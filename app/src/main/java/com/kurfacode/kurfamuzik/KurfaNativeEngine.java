package com.kurfacode.kurfamuzik;

import android.net.Uri;
import android.webkit.WebResourceResponse;

import org.json.JSONArray;
import org.json.JSONObject;
import org.schabi.newpipe.extractor.InfoItem;
import org.schabi.newpipe.extractor.MediaFormat;
import org.schabi.newpipe.extractor.NewPipe;
import org.schabi.newpipe.extractor.ServiceList;
import org.schabi.newpipe.extractor.downloader.Downloader;
import org.schabi.newpipe.extractor.downloader.Request;
import org.schabi.newpipe.extractor.downloader.Response;
import org.schabi.newpipe.extractor.exceptions.ReCaptchaException;
import org.schabi.newpipe.extractor.localization.Localization;
import org.schabi.newpipe.extractor.search.SearchInfo;
import org.schabi.newpipe.extractor.stream.AudioStream;
import org.schabi.newpipe.extractor.stream.DeliveryMethod;
import org.schabi.newpipe.extractor.stream.StreamInfo;
import org.schabi.newpipe.extractor.stream.StreamInfoItem;
import org.schabi.newpipe.extractor.stream.StreamType;
import org.schabi.newpipe.extractor.stream.VideoStream;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.TimeUnit;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import androidx.annotation.NonNull;

import okhttp3.ConnectionPool;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.RequestBody;
import okhttp3.ResponseBody;

/**
 * On-device zero-ad audio stream and local search engine for Kurfanova Android APK.
 * Executes directly on the user's device IP so zero traffic hits Cloudflare Worker.
 */
final class KurfaNativeEngine {

    static final String NATIVE_AUDIO_PATH_PREFIX = "/__kurfa_native_audio__/";
    private static final Pattern YOUTUBE_ID_PATTERN = Pattern.compile("^[A-Za-z0-9_-]{11}$");
    private static final Pattern WATCH_ID_PATTERN = Pattern.compile("(?:v=|youtu\\.be/|shorts/|embed/)([A-Za-z0-9_-]{11})");
    private static final long STREAM_CACHE_TTL_MS = 35L * 60L * 1000L;
    private static final int MAX_STREAM_CACHE = 80;

    private static final String DOWNLOADER_UA =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0";
    private static final String WEB_UA =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36";
    private static final String VISIONOS_UA =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
    private static final String IOS_UA =
            "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)";
    private static final String ANDROID_UA =
            "com.google.android.youtube/20.10.38 (Linux; U; Android 14; en_US; Pixel 8) gzip";
    private static final String ANDROID_VR_UA =
            "com.google.android.apps.youtube.vr.oculus/1.57.29 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip";
    private static final long VISITOR_DATA_TTL_MS = 30L * 60L * 1000L;
    private static final long MAX_ADAPTIVE_CHUNK_BYTES = 512L * 1024L;
    private static final long MAX_PROGRESSIVE_CHUNK_BYTES = 2L * 1024L * 1024L;
    private static final long HEAD_PROBE_END_BYTE = 1023L;
    private static final long TAIL_PROBE_START_BYTE = 1_048_576L;
    private static final long TAIL_PROBE_END_BYTE = 1_310_719L;
    private static final MediaType JSON_MEDIA_TYPE = MediaType.get("application/json; charset=utf-8");

    static final class ResolvedAudio {
        final String videoId;
        final String streamUrl;
        final String mimeType;
        final String title;
        final String artist;
        final long durationSec;
        final long contentLength;
        final String requestUserAgent;
        final String clientKind;
        final long expiresAt;

        ResolvedAudio(
                String videoId,
                String streamUrl,
                String mimeType,
                String title,
                String artist,
                long durationSec,
                long contentLength,
                String requestUserAgent,
                String clientKind
        ) {
            this.videoId = videoId;
            this.streamUrl = streamUrl;
            this.mimeType = mimeType;
            this.title = title;
            this.artist = artist;
            this.durationSec = durationSec;
            this.contentLength = contentLength;
            this.requestUserAgent = requestUserAgent;
            this.clientKind = clientKind;
            this.expiresAt = System.currentTimeMillis() + STREAM_CACHE_TTL_MS;
        }
    }

    private static final OkHttpClient HTTP_CLIENT = new OkHttpClient.Builder()
            .connectTimeout(6, TimeUnit.SECONDS)
            .readTimeout(10, TimeUnit.SECONDS)
            .writeTimeout(10, TimeUnit.SECONDS)
            .connectionPool(new ConnectionPool(10, 2, TimeUnit.MINUTES))
            .retryOnConnectionFailure(true)
            .followRedirects(true)
            .build();

    private static final ConcurrentHashMap<String, ResolvedAudio> STREAM_CACHE = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, Object> RESOLVE_LOCKS = new ConcurrentHashMap<>();
    private static final Object VISITOR_LOCK = new Object();
    private static volatile String cachedVisitorData = null;
    private static volatile long cachedVisitorDataExpiresAt = 0L;
    private static volatile boolean initialized = false;

    private KurfaNativeEngine() {
    }

    static synchronized void ensureInitialized() {
        if (initialized) {
            return;
        }
        NewPipe.init(new KurfaOkHttpDownloader(HTTP_CLIENT), new Localization("tr", "TR"));
        initialized = true;
    }

    static boolean isValidVideoId(String videoId) {
        return videoId != null && YOUTUBE_ID_PATTERN.matcher(videoId).matches();
    }

    static String extractVideoId(String raw) {
        if (raw == null) return null;
        String trimmed = raw.trim();
        if (isValidVideoId(trimmed)) return trimmed;
        Matcher m = WATCH_ID_PATTERN.matcher(trimmed);
        return m.find() ? m.group(1) : null;
    }

    static String extractVideoIdFromNativeAudioUri(Uri uri) {
        if (uri == null || uri.getHost() == null) {
            return null;
        }
        String host = uri.getHost().toLowerCase(Locale.ROOT);
        if (!"kurfanova.github.io".equals(host) && !"kurfanova.kurfa.workers.dev".equals(host)) {
            return null;
        }
        String path = uri.getPath();
        if (path == null || !path.startsWith(NATIVE_AUDIO_PATH_PREFIX)) {
            return null;
        }
        String candidate = path.substring(NATIVE_AUDIO_PATH_PREFIX.length()).trim();
        return isValidVideoId(candidate) ? candidate : null;
    }

    static void invalidateStream(String videoId) {
        if (videoId != null) {
            STREAM_CACHE.remove(videoId);
        }
    }

    static ResolvedAudio resolveAudioStream(String videoId) {
        return resolveAudioStream(videoId, false, false);
    }

    private static long resolveChunkCap(ResolvedAudio audio) {
        if (audio != null && audio.mimeType != null && audio.mimeType.startsWith("video/")) {
            return MAX_PROGRESSIVE_CHUNK_BYTES;
        }
        return MAX_ADAPTIVE_CHUNK_BYTES;
    }

    static ResolvedAudio resolveAudioStream(String videoId, boolean forceRefresh, boolean preferAlternate) {
        if (!isValidVideoId(videoId)) {
            return null;
        }
        ensureInitialized();

        Object lock = RESOLVE_LOCKS.computeIfAbsent(videoId, k -> new Object());
        synchronized (lock) {
            if (!forceRefresh) {
                ResolvedAudio cached = STREAM_CACHE.get(videoId);
                if (cached != null && cached.expiresAt > System.currentTimeMillis()) {
                    return cached;
                }
            }
            STREAM_CACHE.remove(videoId);

            ResolvedAudio resolved = null;
            if (preferAlternate) {
                resolved = resolveViaAndroid(videoId);
                if (resolved == null) resolved = resolveViaIos(videoId);
                if (resolved == null) resolved = tryVerifiedAudioCandidate(resolveViaNewPipe(videoId));
                if (resolved == null) resolved = resolveViaAndroidVr(videoId);
                if (resolved == null) resolved = resolveViaVisionOs(videoId, true);
            } else {
                // Prefer NewPipe first, then verified ANDROID (with itag=18 fallback), then IOS / ANDROID_VR / VISIONOS
                resolved = tryVerifiedAudioCandidate(resolveViaNewPipe(videoId));
                if (resolved == null) resolved = resolveViaAndroid(videoId);
                if (resolved == null) resolved = resolveViaIos(videoId);
                if (resolved == null) resolved = resolveViaAndroidVr(videoId);
                if (resolved == null) resolved = resolveViaVisionOs(videoId, forceRefresh);
            }

            if (resolved != null) {
                pruneCacheIfNeeded();
                STREAM_CACHE.put(videoId, resolved);
            }
            return resolved;
        }
    }

    private static ResolvedAudio tryVerifiedAudioCandidate(ResolvedAudio candidate) {
        if (candidate == null || candidate.streamUrl == null || candidate.streamUrl.isEmpty()) {
            return null;
        }
        okhttp3.Request req = new okhttp3.Request.Builder()
                .url(candidate.streamUrl)
                .get()
                .header("User-Agent", candidate.requestUserAgent)
                .header("Range", "bytes=0-" + HEAD_PROBE_END_BYTE)
                .build();
        try (okhttp3.Response res = HTTP_CLIENT.newCall(req).execute()) {
            int code = res.code();
            if (code < 200 || code >= 300) {
                return null;
            }
            long totalLen = parseTotalLengthFromContentRange(res.header("Content-Range"));
            if (totalLen <= 0L && code == 200) totalLen = parseLongSafe(res.header("Content-Length"));
            if (totalLen <= 0L) totalLen = candidate.contentLength;

            if (totalLen > TAIL_PROBE_START_BYTE) {
                long tailEnd = Math.min(TAIL_PROBE_END_BYTE, totalLen - 1L);
                okhttp3.Request tailReq = new okhttp3.Request.Builder()
                        .url(candidate.streamUrl)
                        .get()
                        .header("User-Agent", candidate.requestUserAgent)
                        .header("Range", "bytes=" + TAIL_PROBE_START_BYTE + "-" + tailEnd)
                        .build();
                try (okhttp3.Response tailRes = HTTP_CLIENT.newCall(tailReq).execute()) {
                    int tailCode = tailRes.code();
                    if (tailCode < 200 || tailCode >= 300) {
                        return null;
                    }
                }
            }

            if (totalLen > 0L && candidate.contentLength <= 0L) {
                return new ResolvedAudio(
                        candidate.videoId,
                        candidate.streamUrl,
                        candidate.mimeType,
                        candidate.title,
                        candidate.artist,
                        candidate.durationSec,
                        totalLen,
                        candidate.requestUserAgent,
                        candidate.clientKind
                );
            }
            return candidate;
        } catch (Exception ignored) {
            return null;
        }
    }

    private static void pruneCacheIfNeeded() {
        if (STREAM_CACHE.size() < MAX_STREAM_CACHE) {
            return;
        }
        long now = System.currentTimeMillis();
        Iterator<Map.Entry<String, ResolvedAudio>> it = STREAM_CACHE.entrySet().iterator();
        while (it.hasNext()) {
            Map.Entry<String, ResolvedAudio> entry = it.next();
            if (entry.getValue().expiresAt <= now) {
                it.remove();
            }
        }
        if (STREAM_CACHE.size() >= MAX_STREAM_CACHE) {
            STREAM_CACHE.clear();
        }
    }

    private static ResolvedAudio resolveViaNewPipe(String videoId) {
        try {
            String watchUrl = "https://www.youtube.com/watch?v=" + videoId;
            StreamInfo info = StreamInfo.getInfo(ServiceList.YouTube, watchUrl);
            List<AudioStream> audioStreams = info.getAudioStreams();
            AudioStream best = pickBestAudioStream(audioStreams);
            if (best == null || best.getContent() == null || best.getContent().isEmpty()) {
                return null;
            }
            MediaFormat fmt = best.getFormat();
            String mime = (fmt != null && fmt.mimeType != null && !fmt.mimeType.isEmpty())
                    ? fmt.mimeType
                    : "audio/mp4";
            return new ResolvedAudio(
                    videoId,
                    best.getContent(),
                    mime,
                    info.getName() != null ? info.getName() : "",
                    info.getUploaderName() != null ? info.getUploaderName() : "",
                    info.getDuration(),
                    0L,
                    DOWNLOADER_UA,
                    "NEWPIPE"
            );
        } catch (Exception ignored) {
            return null;
        }
    }

    private static AudioStream pickBestAudioStream(List<AudioStream> streams) {
        if (streams == null || streams.isEmpty()) {
            return null;
        }
        AudioStream best = null;
        int bestScore = Integer.MIN_VALUE;

        for (AudioStream s : streams) {
            if (s == null || !s.isUrl() || s.getDeliveryMethod() != DeliveryMethod.PROGRESSIVE_HTTP) {
                continue;
            }
            String content = s.getContent();
            if (content == null || content.isEmpty()) {
                continue;
            }
            MediaFormat fmt = s.getFormat();
            int bitrate = Math.max(s.getAverageBitrate(), s.getBitrate());
            int score = 0;

            if (fmt == MediaFormat.M4A) {
                score += 500;
            } else if (fmt == MediaFormat.WEBMA || fmt == MediaFormat.WEBMA_OPUS) {
                score += 300;
            }

            // Prefer reliable ~96-192kbps mobile streams for zero buffering
            if (bitrate >= 96_000 && bitrate <= 192_000) {
                score += 200;
            } else if (bitrate > 48_000 && bitrate < 96_000) {
                score += 100;
            } else if (bitrate > 192_000) {
                score += 150;
            }

            if (score > bestScore) {
                bestScore = score;
                best = s;
            }
        }
        return best;
    }

    private static String resolveVisitorData(boolean forceRefresh) {
        long now = System.currentTimeMillis();
        if (!forceRefresh && cachedVisitorData != null && cachedVisitorDataExpiresAt > now) {
            return cachedVisitorData;
        }
        synchronized (VISITOR_LOCK) {
            now = System.currentTimeMillis();
            if (!forceRefresh && cachedVisitorData != null && cachedVisitorDataExpiresAt > now) {
                return cachedVisitorData;
            }
            try {
                okhttp3.Request req = new okhttp3.Request.Builder()
                        .url("https://www.youtube.com/sw.js_data")
                        .get()
                        .header("User-Agent", WEB_UA)
                        .build();
                try (okhttp3.Response res = HTTP_CLIENT.newCall(req).execute()) {
                    if (!res.isSuccessful() || res.body() == null) {
                        return cachedVisitorData;
                    }
                    String raw = res.body().string();
                    if (raw.startsWith(")]}'")) {
                        raw = raw.substring(4).trim();
                    }
                    JSONArray root = new JSONArray(raw);
                    JSONArray n0 = root.optJSONArray(0);
                    JSONArray n2 = n0 != null ? n0.optJSONArray(2) : null;
                    JSONArray n20 = n2 != null ? n2.optJSONArray(0) : null;
                    JSONArray n200 = n20 != null ? n20.optJSONArray(0) : null;
                    String vd = n200 != null ? n200.optString(13, "").trim() : "";
                    if (!vd.isEmpty()) {
                        cachedVisitorData = vd;
                        cachedVisitorDataExpiresAt = System.currentTimeMillis() + VISITOR_DATA_TTL_MS;
                        return vd;
                    }
                }
            } catch (Exception ignored) {
            }
        }
        return cachedVisitorData;
    }

    private static ResolvedAudio resolveViaVisionOs(String videoId, boolean forceRefreshVisitor) {
        try {
            String visitorData = resolveVisitorData(forceRefreshVisitor);
            JSONObject client = new JSONObject()
                    .put("clientName", "VISIONOS")
                    .put("clientVersion", "1.02")
                    .put("deviceMake", "Apple")
                    .put("deviceModel", "RealityDevice17,1")
                    .put("osName", "visionOS")
                    .put("osVersion", "26.5.23O471")
                    .put("hl", "tr")
                    .put("gl", "TR");
            if (visitorData != null && !visitorData.isEmpty()) {
                client.put("visitorData", visitorData);
            }
            JSONObject payload = new JSONObject()
                    .put("context", new JSONObject().put("client", client))
                    .put("videoId", videoId)
                    .put("contentCheckOk", true)
                    .put("racyCheckOk", true);
            return executePlayerClient(videoId, payload, VISIONOS_UA, null, null, visitorData, "VISIONOS");
        } catch (Exception ignored) {
            return null;
        }
    }

    private static ResolvedAudio resolveViaIos(String videoId) {
        try {
            JSONObject client = new JSONObject()
                    .put("clientName", "IOS")
                    .put("clientVersion", "20.10.4")
                    .put("deviceMake", "Apple")
                    .put("deviceModel", "iPhone16,2")
                    .put("osName", "iPhone")
                    .put("osVersion", "18.3.2.22D82")
                    .put("hl", "tr")
                    .put("gl", "TR");
            JSONObject payload = new JSONObject()
                    .put("context", new JSONObject().put("client", client))
                    .put("videoId", videoId);
            return executePlayerClient(videoId, payload, IOS_UA, "5", "20.10.4", null, "IOS");
        } catch (Exception ignored) {
            return null;
        }
    }

    private static ResolvedAudio resolveViaAndroid(String videoId) {
        try {
            JSONObject client = new JSONObject()
                    .put("clientName", "ANDROID")
                    .put("clientVersion", "20.10.38")
                    .put("androidSdkVersion", 34)
                    .put("osName", "Android")
                    .put("osVersion", "14")
                    .put("hl", "tr")
                    .put("gl", "TR");
            JSONObject payload = new JSONObject()
                    .put("context", new JSONObject().put("client", client))
                    .put("videoId", videoId)
                    .put("contentCheckOk", true)
                    .put("racyCheckOk", true);
            return executePlayerClient(videoId, payload, ANDROID_UA, "3", "20.10.38", null, "ANDROID");
        } catch (Exception ignored) {
            return null;
        }
    }

    private static ResolvedAudio resolveViaAndroidVr(String videoId) {
        try {
            JSONObject client = new JSONObject()
                    .put("clientName", "ANDROID_VR")
                    .put("clientVersion", "1.57.29")
                    .put("androidSdkVersion", 32)
                    .put("hl", "tr")
                    .put("gl", "TR");
            JSONObject payload = new JSONObject()
                    .put("context", new JSONObject().put("client", client))
                    .put("videoId", videoId);
            return executePlayerClient(videoId, payload, ANDROID_VR_UA, "28", "1.57.29", null, "ANDROID_VR");
        } catch (Exception ignored) {
            return null;
        }
    }

    private static ResolvedAudio executePlayerClient(
            String videoId,
            JSONObject payload,
            String userAgent,
            String clientNameId,
            String clientVersion,
            String visitorData,
            String clientKind
    ) throws IOException {
        okhttp3.Request.Builder rb = new okhttp3.Request.Builder()
                .url("https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
                .post(RequestBody.create(payload.toString(), JSON_MEDIA_TYPE))
                .header("User-Agent", userAgent);
        if (clientNameId != null && !clientNameId.isEmpty()) {
            rb.header("X-YouTube-Client-Name", clientNameId);
        }
        if (clientVersion != null && !clientVersion.isEmpty()) {
            rb.header("X-YouTube-Client-Version", clientVersion);
        }
        if (visitorData != null && !visitorData.isEmpty()) {
            rb.header("X-Goog-Visitor-Id", visitorData);
        }

        try (okhttp3.Response res = HTTP_CLIENT.newCall(rb.build()).execute()) {
            if (!res.isSuccessful() || res.body() == null) {
                return null;
            }
            JSONObject json = new JSONObject(res.body().string());
            JSONObject streamingData = json.optJSONObject("streamingData");
            if (streamingData == null) {
                return null;
            }

            JSONObject details = json.optJSONObject("videoDetails");
            String title = details != null ? details.optString("title", "") : "";
            String author = details != null ? details.optString("author", "") : "";
            long dur = details != null ? details.optLong("lengthSeconds", 0L) : 0L;

            JSONArray adaptive = streamingData.optJSONArray("adaptiveFormats");
            if (adaptive != null) {
                String bestUrl = null;
                String bestMime = "audio/mp4";
                long bestContentLength = 0L;
                int bestScore = Integer.MIN_VALUE;

                for (int i = 0; i < adaptive.length(); i++) {
                    JSONObject f = adaptive.optJSONObject(i);
                    if (f == null) continue;
                    String mime = f.optString("mimeType", "");
                    String url = f.optString("url", "");
                    if (!mime.startsWith("audio/") || url.isEmpty()) continue;

                    int itag = f.optInt("itag", 0);
                    int bitrate = f.optInt("bitrate", 0);
                    long contentLen = parseLongSafe(f.optString("contentLength", ""));
                    int score = 0;
                    if (itag == 140) score += 1000;
                    else if (mime.startsWith("audio/mp4")) score += 500;
                    else if (mime.startsWith("audio/webm")) score += 300;
                    if (bitrate >= 96_000 && bitrate <= 192_000) score += 200;

                    if (score > bestScore) {
                        bestScore = score;
                        bestUrl = url;
                        bestContentLength = contentLen;
                        bestMime = mime.contains(";") ? mime.substring(0, mime.indexOf(';')).trim() : mime;
                    }
                }

                if (bestUrl != null) {
                    ResolvedAudio verifiedAdaptive = tryVerifiedAudioCandidate(
                            new ResolvedAudio(videoId, bestUrl, bestMime, title, author, dur, bestContentLength, userAgent, clientKind));
                    if (verifiedAdaptive != null) {
                        return verifiedAdaptive;
                    }
                }
            }

            JSONArray formats = streamingData.optJSONArray("formats");
            if (formats != null) {
                String progUrl = null;
                String progMime = "video/mp4";
                long progContentLength = 0L;
                int progScore = Integer.MIN_VALUE;

                for (int i = 0; i < formats.length(); i++) {
                    JSONObject f = formats.optJSONObject(i);
                    if (f == null) continue;
                    String mime = f.optString("mimeType", "");
                    String url = f.optString("url", "");
                    if ((!mime.startsWith("video/mp4") && !mime.startsWith("video/webm")) || url.isEmpty()) continue;

                    int itag = f.optInt("itag", 0);
                    int height = f.optInt("height", 0);
                    long contentLen = parseLongSafe(f.optString("contentLength", ""));
                    int score = mime.startsWith("video/mp4") ? 500 : 0;
                    if (itag == 18) score += 2000;
                    else if (itag == 22) score += 1000;
                    else score += Math.max(0, 800 - height);

                    if (score > progScore) {
                        progScore = score;
                        progUrl = url;
                        progContentLength = contentLen;
                        progMime = mime.contains(";") ? mime.substring(0, mime.indexOf(';')).trim() : "video/mp4";
                    }
                }

                if (progUrl != null) {
                    return tryVerifiedAudioCandidate(
                            new ResolvedAudio(videoId, progUrl, progMime, title, author, dur, progContentLength, userAgent, clientKind));
                }
            }

            return null;
        } catch (Exception ignored) {
            return null;
        }
    }

    private static long parseLongSafe(String raw) {
        if (raw == null || raw.isEmpty()) return 0L;
        try {
            long val = Long.parseLong(raw.trim());
            return val > 0L ? val : 0L;
        } catch (NumberFormatException ignored) {
            return 0L;
        }
    }

    private static String resolveAllowedOrigin(Map<String, String> requestHeaders) {
        if (requestHeaders != null) {
            for (Map.Entry<String, String> entry : requestHeaders.entrySet()) {
                if ("origin".equalsIgnoreCase(entry.getKey()) && entry.getValue() != null) {
                    String origin = entry.getValue().trim().toLowerCase(Locale.ROOT);
                    if ("https://kurfanova.kurfa.workers.dev".equals(origin)) {
                        return "https://kurfanova.kurfa.workers.dev";
                    }
                    if ("https://kurfanova.github.io".equals(origin)) {
                        return "https://kurfanova.github.io";
                    }
                }
            }
        }
        return "https://kurfanova.github.io";
    }

    private static long[] parseByteRangeHeader(String header) {
        if (header == null) return null;
        String trimmed = header.trim();
        if (!trimmed.toLowerCase(Locale.ROOT).startsWith("bytes=")) return null;
        String spec = trimmed.substring(6).trim();
        int comma = spec.indexOf(',');
        if (comma >= 0) spec = spec.substring(0, comma).trim();
        int dash = spec.indexOf('-');
        if (dash <= 0) return null;
        try {
            long start = Long.parseLong(spec.substring(0, dash).trim());
            if (start < 0L) return null;
            String endStr = spec.substring(dash + 1).trim();
            long end = endStr.isEmpty() ? -1L : Long.parseLong(endStr);
            if (end >= 0L && end < start) return null;
            return new long[]{start, end};
        } catch (NumberFormatException ignored) {
            return null;
        }
    }

    private static long parseTotalLengthFromContentRange(String contentRange) {
        if (contentRange == null) return 0L;
        int slash = contentRange.lastIndexOf('/');
        if (slash < 0 || slash + 1 >= contentRange.length()) return 0L;
        String tail = contentRange.substring(slash + 1).trim();
        if ("*".equals(tail)) return 0L;
        return parseLongSafe(tail);
    }

    static WebResourceResponse handleNativeAudioIntercept(
            String videoId,
            String method,
            Map<String, String> requestHeaders
    ) {
        String allowOrigin = resolveAllowedOrigin(requestHeaders);
        if ("OPTIONS".equalsIgnoreCase(method)) {
            Map<String, String> headers = new HashMap<>();
            headers.put("Access-Control-Allow-Origin", allowOrigin);
            headers.put("Vary", "Origin");
            headers.put("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
            headers.put("Access-Control-Allow-Headers", "Range");
            headers.put("Access-Control-Max-Age", "3600");
            return new WebResourceResponse(
                    "text/plain",
                    "utf-8",
                    204,
                    "No Content",
                    headers,
                    new ByteArrayInputStream(new byte[0])
            );
        }

        String rangeHeader = null;
        if (requestHeaders != null) {
            for (Map.Entry<String, String> entry : requestHeaders.entrySet()) {
                if ("range".equalsIgnoreCase(entry.getKey())) {
                    rangeHeader = entry.getValue();
                    break;
                }
            }
        }
        long[] parsedRange = parseByteRangeHeader(rangeHeader);
        boolean isHead = "HEAD".equalsIgnoreCase(method);
        String expectedMime = null;
        long expectedLength = -1L;

        for (int attempt = 0; attempt < 3; attempt++) {
            boolean forceRefresh = attempt > 0;
            boolean forceAlternate = attempt >= 2;
            ResolvedAudio audio = resolveAudioStream(videoId, forceRefresh, forceAlternate);
            if (audio == null) {
                break;
            }
            String candidateMime = cleanMimeType(null, audio.mimeType);
            if (attempt > 0 && parsedRange != null && parsedRange[0] > 0L) {
                // Avoid feeding bytes from a differently-encoded stream mid-file into Chromium's decoder
                if (expectedMime != null && !expectedMime.equals(candidateMime)) {
                    continue;
                }
                if (expectedLength > 0L && audio.contentLength > 0L && expectedLength != audio.contentLength) {
                    continue;
                }
            }
            if (attempt == 0) {
                expectedMime = candidateMime;
                expectedLength = audio.contentLength;
            }
            long chunkCap = resolveChunkCap(audio);

            try {
                if (isHead && parsedRange == null) {
                    long probeEnd = audio.contentLength > 0L
                            ? Math.min(HEAD_PROBE_END_BYTE, audio.contentLength - 1L)
                            : HEAD_PROBE_END_BYTE;
                    okhttp3.Request headReq = new okhttp3.Request.Builder()
                            .url(audio.streamUrl)
                            .get()
                            .header("User-Agent", audio.requestUserAgent)
                            .header("Range", "bytes=0-" + probeEnd)
                            .build();
                    try (okhttp3.Response upstream = HTTP_CLIENT.newCall(headReq).execute()) {
                        int code = upstream.code();
                        if (code == 403 || code == 410 || code >= 500) {
                            invalidateStream(videoId);
                            continue;
                        }
                        if (code < 200 || code >= 300) {
                            return buildErrorResponse(code, "Upstream error", allowOrigin);
                        }
                        long totalLen = parseTotalLengthFromContentRange(upstream.header("Content-Range"));
                        if (totalLen <= 0L) totalLen = audio.contentLength;

                        Map<String, String> respHeaders = new HashMap<>();
                        respHeaders.put("Access-Control-Allow-Origin", allowOrigin);
                        respHeaders.put("Vary", "Origin");
                        respHeaders.put("Access-Control-Expose-Headers", "Content-Range, Content-Length, Accept-Ranges");
                        respHeaders.put("Cache-Control", "no-store");
                        respHeaders.put("Accept-Ranges", "bytes");
                        if (totalLen > 0L) {
                            respHeaders.put("Content-Length", String.valueOf(totalLen));
                        }
                        return new WebResourceResponse(cleanMimeType(upstream.header("Content-Type"), audio.mimeType),
                                null, 200, "OK", respHeaders, new ByteArrayInputStream(new byte[0]));
                    }
                }

                if (parsedRange != null) {
                    long start = parsedRange[0];
                    if (audio.contentLength > 0L && start >= audio.contentLength) {
                        Map<String, String> r416 = new HashMap<>();
                        r416.put("Access-Control-Allow-Origin", allowOrigin);
                        r416.put("Vary", "Origin");
                        r416.put("Content-Range", "bytes */" + audio.contentLength);
                        return new WebResourceResponse("text/plain", "utf-8", 416, "Range Not Satisfiable",
                                r416, new ByteArrayInputStream(new byte[0]));
                    }

                    long maxChunkEnd = start + chunkCap - 1L;
                    long fetchEnd = parsedRange[1] >= 0L ? Math.min(parsedRange[1], maxChunkEnd) : maxChunkEnd;
                    if (audio.contentLength > 0L) {
                        fetchEnd = Math.min(fetchEnd, audio.contentLength - 1L);
                    }
                    if (fetchEnd < start) fetchEnd = start;

                    okhttp3.Request req = new okhttp3.Request.Builder()
                            .url(audio.streamUrl)
                            .get()
                            .header("User-Agent", audio.requestUserAgent)
                            .header("Range", "bytes=" + start + "-" + fetchEnd)
                            .build();

                    okhttp3.Response upstream = HTTP_CLIENT.newCall(req).execute();
                    int code = upstream.code();
                    if (code == 403 || code == 410 || code >= 500 || (start > 0L && code == 200)) {
                        upstream.close();
                        invalidateStream(videoId);
                        continue;
                    }
                    if (code < 200 || code >= 300) {
                        upstream.close();
                        return buildErrorResponse(code, "Upstream error", allowOrigin);
                    }

                    Map<String, String> respHeaders = new HashMap<>();
                    respHeaders.put("Access-Control-Allow-Origin", allowOrigin);
                    respHeaders.put("Vary", "Origin");
                    respHeaders.put("Access-Control-Expose-Headers", "Content-Range, Content-Length, Accept-Ranges");
                    respHeaders.put("Cache-Control", "no-store");
                    respHeaders.put("Accept-Ranges", "bytes");

                    String contentRange = upstream.header("Content-Range");
                    long crTotal = parseTotalLengthFromContentRange(contentRange);
                    long knownTotal = crTotal > 0L ? crTotal : audio.contentLength;
                    if (knownTotal > 0L && expectedLength <= 0L) {
                        expectedLength = knownTotal;
                    }
                    if (code == 206) {
                        if (contentRange != null && !contentRange.isEmpty()) {
                            respHeaders.put("Content-Range", contentRange);
                        } else if (knownTotal > 0L) {
                            long rangeEnd = Math.min(fetchEnd, knownTotal - 1L);
                            respHeaders.put("Content-Range", "bytes " + start + "-" + rangeEnd + "/" + knownTotal);
                        }
                    }

                    String contentLength = upstream.header("Content-Length");
                    long declaredLen = parseLongSafe(contentLength);
                    if (declaredLen <= 0L && code == 206) {
                        declaredLen = Math.max(0L, fetchEnd - start + 1L);
                    }
                    if (contentLength != null && !contentLength.isEmpty()) {
                        respHeaders.put("Content-Length", contentLength);
                    } else if (declaredLen > 0L) {
                        respHeaders.put("Content-Length", String.valueOf(declaredLen));
                    }

                    String cleanMime = cleanMimeType(upstream.header("Content-Type"), audio.mimeType);
                    if (isHead) {
                        upstream.close();
                        return new WebResourceResponse(cleanMime, null, code == 206 ? 206 : 200,
                                code == 206 ? "Partial Content" : "OK", respHeaders, new ByteArrayInputStream(new byte[0]));
                    }

                    ResponseBody body = upstream.body();
                    long targetEnd = declaredLen > 0L ? (start + declaredLen - 1L) : fetchEnd;
                    InputStream stream = body != null
                            ? new ResilientRangeInputStream(audio, upstream, start, targetEnd, knownTotal, cleanMime)
                            : new ByteArrayInputStream(new byte[0]);
                    return new WebResourceResponse(cleanMime, null, code == 206 ? 206 : 200,
                            code == 206 ? "Partial Content" : "OK", respHeaders, stream);
                }

                // Un-ranged GET: stream bounded chunks sequentially so CDN never rejects open-ended requests
                long firstEnd = audio.contentLength > 0L
                        ? Math.min(chunkCap - 1L, audio.contentLength - 1L)
                        : (chunkCap - 1L);
                okhttp3.Request firstReq = new okhttp3.Request.Builder()
                        .url(audio.streamUrl)
                        .get()
                        .header("User-Agent", audio.requestUserAgent)
                        .header("Range", "bytes=0-" + firstEnd)
                        .build();

                okhttp3.Response firstUpstream = HTTP_CLIENT.newCall(firstReq).execute();
                int code = firstUpstream.code();
                if (code == 403 || code == 410 || code >= 500) {
                    firstUpstream.close();
                    invalidateStream(videoId);
                    continue;
                }
                if (code < 200 || code >= 300) {
                    firstUpstream.close();
                    return buildErrorResponse(code, "Upstream error", allowOrigin);
                }

                long totalLength = parseTotalLengthFromContentRange(firstUpstream.header("Content-Range"));
                if (totalLength <= 0L) totalLength = audio.contentLength;

                Map<String, String> respHeaders = new HashMap<>();
                respHeaders.put("Access-Control-Allow-Origin", allowOrigin);
                respHeaders.put("Vary", "Origin");
                respHeaders.put("Access-Control-Expose-Headers", "Content-Range, Content-Length, Accept-Ranges");
                respHeaders.put("Cache-Control", "no-store");
                respHeaders.put("Accept-Ranges", "bytes");
                if (totalLength > 0L) {
                    respHeaders.put("Content-Length", String.valueOf(totalLength));
                }

                String cleanMime = cleanMimeType(firstUpstream.header("Content-Type"), audio.mimeType);
                InputStream stream = new BoundedChunkInputStream(audio, firstUpstream, totalLength, chunkCap);
                return new WebResourceResponse(cleanMime, null, 200, "OK", respHeaders, stream);
            } catch (Exception e) {
                invalidateStream(videoId);
            }
        }

        return buildErrorResponse(502, "Stream unavailable", allowOrigin);
    }

    private static String cleanMimeType(String upstreamContentType, String fallbackMime) {
        if (upstreamContentType != null && !upstreamContentType.isEmpty()) {
            return upstreamContentType.contains(";")
                    ? upstreamContentType.substring(0, upstreamContentType.indexOf(';')).trim()
                    : upstreamContentType.trim();
        }
        return (fallbackMime != null && !fallbackMime.isEmpty()) ? fallbackMime : "audio/mp4";
    }

    private static WebResourceResponse buildErrorResponse(int statusCode, String message, String allowOrigin) {
        Map<String, String> headers = new HashMap<>();
        headers.put("Access-Control-Allow-Origin", allowOrigin);
        headers.put("Vary", "Origin");
        headers.put("Cache-Control", "no-store");
        byte[] bytes = message.getBytes(StandardCharsets.UTF_8);
        return new WebResourceResponse(
                "text/plain",
                "utf-8",
                statusCode,
                "Error",
                headers,
                new ByteArrayInputStream(bytes)
        );
    }

    private static final class BoundedChunkInputStream extends InputStream {
        private ResolvedAudio audio;
        private final long chunkCap;
        private long totalLength;
        private long nextOffset;
        private okhttp3.Response currentResponse;
        private InputStream currentBodyStream;
        private boolean closed;
        private int recoveryCount = 0;

        BoundedChunkInputStream(
                ResolvedAudio audio,
                okhttp3.Response initialResponse,
                long totalLength,
                long chunkCap
        ) {
            this.audio = audio;
            this.chunkCap = chunkCap;
            this.totalLength = totalLength;
            this.currentResponse = initialResponse;
            ResponseBody body = initialResponse.body();
            this.currentBodyStream = body != null ? body.byteStream() : new ByteArrayInputStream(new byte[0]);
            this.nextOffset = 0L;
        }

        @Override
        public int read() throws IOException {
            byte[] single = new byte[1];
            int n = read(single, 0, 1);
            return n == -1 ? -1 : (single[0] & 0xFF);
        }

        @Override
        public int read(@NonNull byte[] b, int off, int len) throws IOException {
            if (closed) return -1;
            if (len == 0) return 0;

            while (true) {
                if (currentBodyStream != null) {
                    try {
                        int n = currentBodyStream.read(b, off, len);
                        if (n > 0) {
                            nextOffset += n;
                            return n;
                        }
                    } catch (IOException readErr) {
                        closeCurrentUpstream();
                        if (!tryRefreshMatchingStream()) {
                            throw readErr;
                        }
                        continue;
                    }
                    closeCurrentUpstream();
                }

                if (totalLength > 0L && nextOffset >= totalLength) {
                    return -1;
                }

                long chunkEnd = nextOffset + chunkCap - 1L;
                if (totalLength > 0L) {
                    chunkEnd = Math.min(chunkEnd, totalLength - 1L);
                }
                okhttp3.Request nextReq = new okhttp3.Request.Builder()
                        .url(audio.streamUrl)
                        .get()
                        .header("User-Agent", audio.requestUserAgent)
                        .header("Range", "bytes=" + nextOffset + "-" + chunkEnd)
                        .build();

                okhttp3.Response nextResp;
                try {
                    nextResp = HTTP_CLIENT.newCall(nextReq).execute();
                } catch (IOException connErr) {
                    if (tryRefreshMatchingStream()) {
                        continue;
                    }
                    throw connErr;
                }
                int code = nextResp.code();
                if (code == 416) {
                    nextResp.close();
                    return -1;
                }
                if (code == 403 || code == 410 || code >= 500) {
                    nextResp.close();
                    if (tryRefreshMatchingStream()) {
                        continue;
                    }
                    return -1;
                }
                if (code < 200 || code >= 300) {
                    nextResp.close();
                    return -1;
                }
                long crTotal = parseTotalLengthFromContentRange(nextResp.header("Content-Range"));
                if (crTotal > 0L) {
                    totalLength = crTotal;
                }
                ResponseBody nextBody = nextResp.body();
                if (nextBody == null) {
                    nextResp.close();
                    return -1;
                }
                currentResponse = nextResp;
                currentBodyStream = nextBody.byteStream();
            }
        }

        private boolean tryRefreshMatchingStream() {
            if (closed || recoveryCount >= 2) return false;
            recoveryCount++;
            invalidateStream(audio.videoId);
            ResolvedAudio fresh = resolveAudioStream(audio.videoId, true, recoveryCount > 1);
            if (fresh == null || fresh.streamUrl == null) return false;
            if (totalLength > 0L && fresh.contentLength > 0L && fresh.contentLength != totalLength) {
                return false;
            }
            this.audio = fresh;
            return true;
        }

        private void closeCurrentUpstream() {
            if (currentBodyStream != null) {
                try { currentBodyStream.close(); } catch (IOException ignored) {}
                currentBodyStream = null;
            }
            if (currentResponse != null) {
                currentResponse.close();
                currentResponse = null;
            }
        }

        @Override
        public void close() {
            closed = true;
            closeCurrentUpstream();
        }
    }

    static JSONObject searchYouTubeLocal(String query, boolean filterSingles) {
        JSONObject out = new JSONObject();
        try {
            if (query == null || query.trim().isEmpty()) {
                out.put("success", false);
                out.put("results", new JSONArray());
                return out;
            }

            ensureInitialized();
            SearchInfo info = SearchInfo.getInfo(ServiceList.YouTube.getSearchExtractor(query.trim()));
            List<InfoItem> items = info.getRelatedItems();
            JSONArray primary = new JSONArray();
            JSONArray secondary = new JSONArray();

            for (InfoItem item : items) {
                if (!(item instanceof StreamInfoItem)) continue;
                StreamInfoItem s = (StreamInfoItem) item;
                StreamType st = s.getStreamType();
                if (st == StreamType.LIVE_STREAM || st == StreamType.AUDIO_LIVE_STREAM) {
                    continue;
                }
                long durationSec = s.getDuration();
                if (durationSec <= 0) continue;

                String vid = extractYoutubeId(s.getUrl());
                if (vid == null) continue;

                String title = s.getName() != null ? s.getName().trim() : "Bilinmeyen \u015eark\u0131";
                String artist = s.getUploaderName() != null ? s.getUploaderName().trim() : "Bilinmeyen Sanat\u00e7\u0131";
                String lowTitle = title.toLowerCase(Locale.ROOT);

                if (filterSingles) {
                    if (durationSec < 60 || durationSec > 539) continue;
                    if (lowTitle.contains("t\u00fcm \u015fark\u0131lar")
                            || lowTitle.contains("kar\u0131\u015f\u0131k \u015fark\u0131lar")
                            || lowTitle.contains("full alb\u00fcm")
                            || lowTitle.contains("full album")
                            || lowTitle.contains("megamix")
                            || lowTitle.contains("1 saat")
                            || lowTitle.contains("1 hour")) {
                        continue;
                    }
                }

                JSONObject track = new JSONObject();
                track.put("id", vid);
                track.put("title", title);
                track.put("artist", artist);
                track.put("duration", formatDuration(durationSec));
                track.put("thumbnail", "https://i.ytimg.com/vi/" + vid + "/hqdefault.jpg");

                if (durationSec >= 75 && durationSec <= 480) {
                    primary.put(track);
                } else {
                    secondary.put(track);
                }

                if (primary.length() >= 25) {
                    break;
                }
            }

            JSONArray merged = new JSONArray();
            for (int i = 0; i < primary.length(); i++) {
                merged.put(primary.get(i));
            }
            for (int i = 0; i < secondary.length() && merged.length() < 25; i++) {
                merged.put(secondary.get(i));
            }

            out.put("success", merged.length() > 0);
            out.put("source", "android-local");
            out.put("results", merged);
            return out;
        } catch (Exception e) {
            try {
                out.put("success", false);
                out.put("error", e.getMessage() != null ? e.getMessage() : "Search failed");
                out.put("results", new JSONArray());
            } catch (Exception ignored) {}
            return out;
        }
    }

    private static String extractYoutubeId(String url) {
        if (url == null) return null;
        Matcher m = WATCH_ID_PATTERN.matcher(url);
        if (m.find()) {
            String candidate = m.group(1);
            if (isValidVideoId(candidate)) {
                return candidate;
            }
        }
        return null;
    }

    private static String formatDuration(long sec) {
        long m = sec / 60;
        long s = sec % 60;
        return String.format(Locale.US, "%d:%02d", m, s);
    }

    interface DownloadProgressListener {
        void onProgress(long bytesDownloaded, long totalBytes, int percent);
    }

    static ResolvedAudio resolveMediaForDownload(String rawVideoId, boolean wantVideo) {
        String videoId = extractVideoId(rawVideoId);
        if (videoId == null) return null;
        if (!wantVideo) {
            return resolveAudioStream(videoId);
        }

        ensureInitialized();
        // Verify each candidate with a quick range probe so 403-gated clients never win blindly
        ResolvedAudio res = tryVerifiedAudioCandidate(resolveVideoViaNewPipe(videoId));
        if (res != null) return res;
        res = tryVerifiedAudioCandidate(resolveVideoViaAndroidVr(videoId));
        if (res != null) return res;
        res = tryVerifiedAudioCandidate(resolveVideoViaIos(videoId));
        if (res != null) return res;
        return tryVerifiedAudioCandidate(resolveVideoViaVisionOs(videoId));
    }

    private static ResolvedAudio resolveVideoViaNewPipe(String videoId) {
        try {
            String watchUrl = "https://www.youtube.com/watch?v=" + videoId;
            StreamInfo info = StreamInfo.getInfo(ServiceList.YouTube, watchUrl);
            List<VideoStream> videoStreams = info.getVideoStreams();
            if (videoStreams == null || videoStreams.isEmpty()) return null;

            VideoStream best = null;
            int bestScore = Integer.MIN_VALUE;
            for (VideoStream v : videoStreams) {
                if (v == null || v.isVideoOnly() || !v.isUrl()
                        || v.getDeliveryMethod() != DeliveryMethod.PROGRESSIVE_HTTP) {
                    continue;
                }
                String url = v.getContent();
                if (url == null || url.isEmpty()) continue;

                MediaFormat fmt = v.getFormat();
                String resStr = v.getResolution() != null ? v.getResolution().toLowerCase(Locale.ROOT) : "";
                int height = 0;
                try {
                    String digits = resStr.replaceAll("[^0-9]", "");
                    if (!digits.isEmpty()) height = Integer.parseInt(digits);
                } catch (Exception ignored) {}

                int score = height;
                if (fmt == MediaFormat.MPEG_4) score += 2000;
                if (height == 720) score += 800;
                else if (height == 360) score += 400;

                if (score > bestScore) {
                    bestScore = score;
                    best = v;
                }
            }
            if (best == null || best.getContent() == null) return null;
            MediaFormat fmt = best.getFormat();
            String mime = (fmt != null && fmt.mimeType != null && !fmt.mimeType.isEmpty())
                    ? fmt.mimeType
                    : "video/mp4";
            return new ResolvedAudio(
                    videoId,
                    best.getContent(),
                    mime,
                    info.getName() != null ? info.getName() : "",
                    info.getUploaderName() != null ? info.getUploaderName() : "",
                    info.getDuration(),
                    0L,
                    DOWNLOADER_UA,
                    "NEWPIPE"
            );
        } catch (Exception ignored) {
            return null;
        }
    }

    static long downloadStreamChunked(
            ResolvedAudio media,
            java.io.OutputStream out,
            DownloadProgressListener listener
    ) throws IOException {
        if (media == null || out == null) throw new IOException("Invalid media");
        long chunkCap = resolveChunkCap(media);
        long totalBytes = media.contentLength;
        long offset = 0L;
        long lastNotify = 0L;
        byte[] buf = new byte[32768];

        if (listener != null) listener.onProgress(0L, totalBytes, 0);

        while (true) {
            long chunkEnd = offset + chunkCap - 1L;
            if (totalBytes > 0L) {
                if (offset >= totalBytes) break;
                chunkEnd = Math.min(chunkEnd, totalBytes - 1L);
            }

            okhttp3.Request req = new okhttp3.Request.Builder()
                    .url(media.streamUrl)
                    .get()
                    .header("User-Agent", media.requestUserAgent)
                    .header("Range", "bytes=" + offset + "-" + chunkEnd)
                    .build();

            try (okhttp3.Response resp = HTTP_CLIENT.newCall(req).execute()) {
                int code = resp.code();
                if (code < 200 || code >= 300) {
                    if (offset > 0L && code == 416) break;
                    throw new IOException("HTTP " + code);
                }
                long probedTotal = parseTotalLengthFromContentRange(resp.header("Content-Range"));
                if (probedTotal > 0L) {
                    totalBytes = probedTotal;
                } else if (totalBytes <= 0L && code == 200) {
                    long cl = parseLongSafe(resp.header("Content-Length"));
                    if (cl > 0L) totalBytes = cl;
                }

                ResponseBody body = resp.body();
                if (body == null) break;
                InputStream in = body.byteStream();
                long readChunk = 0L;
                int n;
                while ((n = in.read(buf)) != -1) {
                    out.write(buf, 0, n);
                    offset += n;
                    readChunk += n;
                    long now = System.currentTimeMillis();
                    if (listener != null && now - lastNotify >= 150L) {
                        lastNotify = now;
                        int pct = totalBytes > 0L ? (int) Math.min(99L, Math.max(1L, (offset * 100L) / totalBytes)) : 15;
                        listener.onProgress(offset, totalBytes, pct);
                    }
                }
                if (readChunk == 0L || code == 200 || (totalBytes > 0L && offset >= totalBytes)) {
                    break;
                }
            }
        }
        out.flush();
        if (listener != null) listener.onProgress(offset, Math.max(totalBytes, offset), 100);
        return offset;
    }

    private static ResolvedAudio resolveVideoViaIos(String videoId) {
        try {
            JSONObject client = new JSONObject()
                    .put("clientName", "IOS")
                    .put("clientVersion", "20.10.4")
                    .put("deviceMake", "Apple")
                    .put("deviceModel", "iPhone16,2")
                    .put("osName", "iPhone")
                    .put("osVersion", "18.3.2.22D82")
                    .put("hl", "tr")
                    .put("gl", "TR");
            JSONObject payload = new JSONObject()
                    .put("context", new JSONObject().put("client", client))
                    .put("videoId", videoId);
            return executeMuxedVideoClient(videoId, payload, IOS_UA, "5", "20.10.4", null, "IOS");
        } catch (Exception ignored) {
            return null;
        }
    }

    private static ResolvedAudio resolveVideoViaVisionOs(String videoId) {
        try {
            String visitorData = resolveVisitorData(false);
            JSONObject client = new JSONObject()
                    .put("clientName", "VISIONOS")
                    .put("clientVersion", "1.02")
                    .put("deviceMake", "Apple")
                    .put("deviceModel", "RealityDevice17,1")
                    .put("osName", "visionOS")
                    .put("osVersion", "26.5.23O471")
                    .put("hl", "tr")
                    .put("gl", "TR");
            if (visitorData != null && !visitorData.isEmpty()) client.put("visitorData", visitorData);
            JSONObject payload = new JSONObject()
                    .put("context", new JSONObject().put("client", client))
                    .put("videoId", videoId)
                    .put("contentCheckOk", true)
                    .put("racyCheckOk", true);
            return executeMuxedVideoClient(videoId, payload, VISIONOS_UA, null, null, visitorData, "VISIONOS");
        } catch (Exception ignored) {
            return null;
        }
    }

    private static ResolvedAudio resolveVideoViaAndroidVr(String videoId) {
        try {
            JSONObject client = new JSONObject()
                    .put("clientName", "ANDROID_VR")
                    .put("clientVersion", "1.57.29")
                    .put("androidSdkVersion", 32)
                    .put("hl", "tr")
                    .put("gl", "TR");
            JSONObject payload = new JSONObject()
                    .put("context", new JSONObject().put("client", client))
                    .put("videoId", videoId);
            return executeMuxedVideoClient(videoId, payload, ANDROID_VR_UA, "28", "1.57.29", null, "ANDROID_VR");
        } catch (Exception ignored) {
            return null;
        }
    }

    private static ResolvedAudio executeMuxedVideoClient(
            String videoId,
            JSONObject payload,
            String userAgent,
            String clientNameId,
            String clientVersion,
            String visitorData,
            String clientKind
    ) throws IOException {
        okhttp3.Request.Builder rb = new okhttp3.Request.Builder()
                .url("https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
                .post(RequestBody.create(payload.toString(), JSON_MEDIA_TYPE))
                .header("User-Agent", userAgent);
        if (clientNameId != null && !clientNameId.isEmpty()) rb.header("X-YouTube-Client-Name", clientNameId);
        if (clientVersion != null && !clientVersion.isEmpty()) rb.header("X-YouTube-Client-Version", clientVersion);
        if (visitorData != null && !visitorData.isEmpty()) rb.header("X-Goog-Visitor-Id", visitorData);

        try (okhttp3.Response res = HTTP_CLIENT.newCall(rb.build()).execute()) {
            if (!res.isSuccessful() || res.body() == null) return null;
            JSONObject json = new JSONObject(res.body().string());
            JSONObject streamingData = json.optJSONObject("streamingData");
            if (streamingData == null) return null;
            JSONArray formats = streamingData.optJSONArray("formats");
            if (formats == null) return null;

            String bestUrl = null;
            String bestMime = "video/mp4";
            long bestContentLength = 0L;
            int bestScore = Integer.MIN_VALUE;

            for (int i = 0; i < formats.length(); i++) {
                JSONObject f = formats.optJSONObject(i);
                if (f == null) continue;
                String mime = f.optString("mimeType", "");
                String url = f.optString("url", "");
                if (!mime.startsWith("video/mp4") || url.isEmpty()) continue;

                int itag = f.optInt("itag", 0);
                int height = f.optInt("height", 0);
                long contentLen = parseLongSafe(f.optString("contentLength", ""));
                int score = height;
                if (itag == 22) score += 2000;
                else if (itag == 18) score += 1000;

                if (score > bestScore) {
                    bestScore = score;
                    bestUrl = url;
                    bestContentLength = contentLen;
                    bestMime = mime.contains(";") ? mime.substring(0, mime.indexOf(';')).trim() : "video/mp4";
                }
            }
            if (bestUrl == null) return null;
            JSONObject details = json.optJSONObject("videoDetails");
            String title = details != null ? details.optString("title", "") : "";
            String author = details != null ? details.optString("author", "") : "";
            long dur = details != null ? details.optLong("lengthSeconds", 0L) : 0L;
            return new ResolvedAudio(videoId, bestUrl, bestMime, title, author, dur, bestContentLength, userAgent, clientKind);
        } catch (Exception ignored) {
            return null;
        }
    }

    private static final class ResilientRangeInputStream extends InputStream {
        private ResolvedAudio audio;
        private final long targetEnd;
        private final long expectedTotalLength;
        private final String expectedMime;
        private long nextOffset;
        private okhttp3.Response currentResponse;
        private InputStream currentBodyStream;
        private boolean closed;
        private int recoveryCount = 0;

        ResilientRangeInputStream(
                ResolvedAudio audio,
                okhttp3.Response initialResponse,
                long startOffset,
                long targetEnd,
                long expectedTotalLength,
                String expectedMime
        ) {
            this.audio = audio;
            this.currentResponse = initialResponse;
            ResponseBody body = initialResponse.body();
            this.currentBodyStream = body != null ? body.byteStream() : new ByteArrayInputStream(new byte[0]);
            this.nextOffset = startOffset;
            this.targetEnd = targetEnd;
            this.expectedTotalLength = expectedTotalLength;
            this.expectedMime = expectedMime;
        }

        @Override
        public int read() throws IOException {
            byte[] single = new byte[1];
            int n = read(single, 0, 1);
            return n == -1 ? -1 : (single[0] & 0xFF);
        }

        @Override
        public int read(@NonNull byte[] b, int off, int len) throws IOException {
            if (closed) return -1;
            if (len == 0) return 0;

            while (true) {
                if (currentBodyStream != null) {
                    try {
                        int n = currentBodyStream.read(b, off, len);
                        if (n > 0) {
                            nextOffset += n;
                            return n;
                        }
                        closeCurrentUpstream();
                        if (targetEnd >= 0L && nextOffset <= targetEnd && recoveryCount < 2) {
                            if (reopenRemainingRange()) {
                                continue;
                            }
                        }
                        return -1;
                    } catch (IOException readErr) {
                        closeCurrentUpstream();
                        if (targetEnd >= 0L && nextOffset <= targetEnd && reopenRemainingRange()) {
                            continue;
                        }
                        throw readErr;
                    }
                }
                return -1;
            }
        }

        private boolean reopenRemainingRange() {
            if (closed || recoveryCount >= 2 || nextOffset > targetEnd) return false;
            recoveryCount++;
            invalidateStream(audio.videoId);
            ResolvedAudio fresh = resolveAudioStream(audio.videoId, true, recoveryCount > 1);
            if (fresh == null || fresh.streamUrl == null) return false;
            if (expectedMime != null && !expectedMime.equals(cleanMimeType(null, fresh.mimeType))) return false;
            if (expectedTotalLength > 0L && fresh.contentLength > 0L && fresh.contentLength != expectedTotalLength) {
                return false;
            }

            okhttp3.Request req = new okhttp3.Request.Builder()
                    .url(fresh.streamUrl)
                    .get()
                    .header("User-Agent", fresh.requestUserAgent)
                    .header("Range", "bytes=" + nextOffset + "-" + targetEnd)
                    .build();
            try {
                okhttp3.Response resp = HTTP_CLIENT.newCall(req).execute();
                int code = resp.code();
                if (code != 206 && !(nextOffset == 0L && code == 200)) {
                    resp.close();
                    return false;
                }
                ResponseBody body = resp.body();
                if (body == null) {
                    resp.close();
                    return false;
                }
                this.audio = fresh;
                this.currentResponse = resp;
                this.currentBodyStream = body.byteStream();
                return true;
            } catch (IOException ignored) {
                return false;
            }
        }

        private void closeCurrentUpstream() {
            if (currentBodyStream != null) {
                try { currentBodyStream.close(); } catch (IOException ignored) {}
                currentBodyStream = null;
            }
            if (currentResponse != null) {
                currentResponse.close();
                currentResponse = null;
            }
        }

        @Override
        public void close() {
            closed = true;
            closeCurrentUpstream();
        }
    }

    private static final class KurfaOkHttpDownloader extends Downloader {
        private final OkHttpClient client;

        KurfaOkHttpDownloader(OkHttpClient client) {
            this.client = client;
        }

        @Override
        public Response execute(@NonNull final Request request) throws IOException, ReCaptchaException {
            final byte[] dataToSend = request.dataToSend();
            RequestBody requestBody = null;
            if (dataToSend != null) {
                requestBody = RequestBody.create(dataToSend);
            } else if ("POST".equalsIgnoreCase(request.httpMethod()) || "PUT".equalsIgnoreCase(request.httpMethod())) {
                requestBody = RequestBody.create(new byte[0]);
            }

            final okhttp3.Request.Builder rb = new okhttp3.Request.Builder()
                    .method(request.httpMethod(), requestBody)
                    .url(request.url())
                    .addHeader("User-Agent", DOWNLOADER_UA);

            final Map<String, List<String>> headers = request.headers();
            if (headers != null) {
                for (Map.Entry<String, List<String>> entry : headers.entrySet()) {
                    String name = entry.getKey();
                    rb.removeHeader(name);
                    List<String> values = entry.getValue();
                    if (values != null) {
                        for (String val : values) {
                            rb.addHeader(name, val);
                        }
                    }
                }
            }

            try (okhttp3.Response response = client.newCall(rb.build()).execute()) {
                if (response.code() == 429) {
                    throw new ReCaptchaException("reCaptcha Challenge requested", request.url());
                }
                ResponseBody body = response.body();
                String bodyStr = body != null ? body.string() : "";
                return new Response(
                        response.code(),
                        response.message(),
                        response.headers() != null ? response.headers().toMultimap() : Collections.emptyMap(),
                        bodyStr,
                        response.request().url().toString()
                );
            }
        }
    }
}
