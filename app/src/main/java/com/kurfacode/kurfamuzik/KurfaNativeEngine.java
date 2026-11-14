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

import java.io.ByteArrayInputStream;
import java.io.FilterInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
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
    private static final String ANDROID_VR_UA =
            "com.google.android.apps.youtube.vr.oculus/1.57.29 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip";
    private static final MediaType JSON_MEDIA_TYPE = MediaType.get("application/json; charset=utf-8");

    static final class ResolvedAudio {
        final String videoId;
        final String streamUrl;
        final String mimeType;
        final String title;
        final String artist;
        final long durationSec;
        final long expiresAt;

        ResolvedAudio(
                String videoId,
                String streamUrl,
                String mimeType,
                String title,
                String artist,
                long durationSec
        ) {
            this.videoId = videoId;
            this.streamUrl = streamUrl;
            this.mimeType = mimeType;
            this.title = title;
            this.artist = artist;
            this.durationSec = durationSec;
            this.expiresAt = System.currentTimeMillis() + STREAM_CACHE_TTL_MS;
        }
    }

    private static final OkHttpClient HTTP_CLIENT = new OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(25, TimeUnit.SECONDS)
            .followRedirects(true)
            .build();

    private static final ConcurrentHashMap<String, ResolvedAudio> STREAM_CACHE = new ConcurrentHashMap<>();
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

    static String extractVideoIdFromNativeAudioUri(Uri uri) {
        if (uri == null || uri.getHost() == null) {
            return null;
        }
        String host = uri.getHost().toLowerCase(Locale.ROOT);
        if (!"kurfanova.github.io".equals(host)) {
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
        if (!isValidVideoId(videoId)) {
            return null;
        }
        ensureInitialized();

        ResolvedAudio cached = STREAM_CACHE.get(videoId);
        if (cached != null && cached.expiresAt > System.currentTimeMillis()) {
            return cached;
        }
        STREAM_CACHE.remove(videoId);

        ResolvedAudio resolved = resolveViaNewPipe(videoId);
        if (resolved == null) {
            resolved = resolveViaAndroidVr(videoId);
        }

        if (resolved != null) {
            pruneCacheIfNeeded();
            STREAM_CACHE.put(videoId, resolved);
        }
        return resolved;
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
                    info.getDuration()
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

    private static ResolvedAudio resolveViaAndroidVr(String videoId) {
        try {
            JSONObject client = new JSONObject()
                    .put("clientName", "ANDROID_VR")
                    .put("clientVersion", "1.57.29")
                    .put("androidSdkVersion", 32)
                    .put("hl", "tr")
                    .put("gl", "TR");
            JSONObject context = new JSONObject().put("client", client);
            JSONObject payload = new JSONObject()
                    .put("context", context)
                    .put("videoId", videoId);

            okhttp3.Request req = new okhttp3.Request.Builder()
                    .url("https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
                    .post(RequestBody.create(payload.toString(), JSON_MEDIA_TYPE))
                    .header("User-Agent", ANDROID_VR_UA)
                    .header("X-YouTube-Client-Name", "28")
                    .header("X-YouTube-Client-Version", "1.57.29")
                    .build();

            try (okhttp3.Response res = HTTP_CLIENT.newCall(req).execute()) {
                if (!res.isSuccessful() || res.body() == null) {
                    return null;
                }
                JSONObject json = new JSONObject(res.body().string());
                JSONObject streamingData = json.optJSONObject("streamingData");
                if (streamingData == null) {
                    return null;
                }
                JSONArray adaptive = streamingData.optJSONArray("adaptiveFormats");
                if (adaptive == null) {
                    return null;
                }

                String bestUrl = null;
                String bestMime = "audio/mp4";
                int bestScore = Integer.MIN_VALUE;

                for (int i = 0; i < adaptive.length(); i++) {
                    JSONObject f = adaptive.optJSONObject(i);
                    if (f == null) continue;
                    String mime = f.optString("mimeType", "");
                    String url = f.optString("url", "");
                    if (!mime.startsWith("audio/") || url.isEmpty()) continue;

                    int itag = f.optInt("itag", 0);
                    int bitrate = f.optInt("bitrate", 0);
                    int score = 0;
                    if (itag == 140) score += 1000;
                    else if (mime.startsWith("audio/mp4")) score += 500;
                    else if (mime.startsWith("audio/webm")) score += 300;
                    if (bitrate >= 96_000 && bitrate <= 192_000) score += 200;

                    if (score > bestScore) {
                        bestScore = score;
                        bestUrl = url;
                        bestMime = mime.contains(";") ? mime.substring(0, mime.indexOf(';')).trim() : mime;
                    }
                }

                if (bestUrl == null) {
                    return null;
                }
                JSONObject details = json.optJSONObject("videoDetails");
                String title = details != null ? details.optString("title", "") : "";
                String author = details != null ? details.optString("author", "") : "";
                long dur = details != null ? details.optLong("lengthSeconds", 0L) : 0L;
                return new ResolvedAudio(videoId, bestUrl, bestMime, title, author, dur);
            }
        } catch (Exception ignored) {
            return null;
        }
    }

    static WebResourceResponse handleNativeAudioIntercept(
            String videoId,
            String method,
            Map<String, String> requestHeaders
    ) {
        if ("OPTIONS".equalsIgnoreCase(method)) {
            Map<String, String> headers = new HashMap<>();
            headers.put("Access-Control-Allow-Origin", "https://kurfanova.github.io");
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

        for (int attempt = 0; attempt < 2; attempt++) {
            ResolvedAudio audio = resolveAudioStream(videoId);
            if (audio == null) {
                break;
            }
            try {
                okhttp3.Request.Builder rb = new okhttp3.Request.Builder()
                        .url(audio.streamUrl)
                        .header("User-Agent", DOWNLOADER_UA);
                if (rangeHeader != null && !rangeHeader.isEmpty()) {
                    rb.header("Range", rangeHeader);
                }
                if ("HEAD".equalsIgnoreCase(method)) {
                    rb.head();
                } else {
                    rb.get();
                }

                okhttp3.Response upstream = HTTP_CLIENT.newCall(rb.build()).execute();
                int code = upstream.code();
                if (code == 403 || code == 410) {
                    upstream.close();
                    invalidateStream(videoId);
                    continue;
                }

                if (code < 200 || code >= 300) {
                    upstream.close();
                    return buildErrorResponse(code, "Upstream error");
                }

                ResponseBody body = upstream.body();
                InputStream stream = body != null
                        ? new ResponseClosingInputStream(body.byteStream(), upstream)
                        : new ByteArrayInputStream(new byte[0]);

                Map<String, String> respHeaders = new HashMap<>();
                respHeaders.put("Access-Control-Allow-Origin", "https://kurfanova.github.io");
                respHeaders.put("Access-Control-Expose-Headers", "Content-Range, Content-Length, Accept-Ranges");
                respHeaders.put("Cache-Control", "no-store");

                String acceptRanges = upstream.header("Accept-Ranges");
                respHeaders.put("Accept-Ranges", acceptRanges != null ? acceptRanges : "bytes");

                String contentRange = upstream.header("Content-Range");
                if (contentRange != null) {
                    respHeaders.put("Content-Range", contentRange);
                }
                String contentLength = upstream.header("Content-Length");
                if (contentLength != null) {
                    respHeaders.put("Content-Length", contentLength);
                }

                String contentType = upstream.header("Content-Type");
                String cleanMime = audio.mimeType;
                if (contentType != null && !contentType.isEmpty()) {
                    cleanMime = contentType.contains(";")
                            ? contentType.substring(0, contentType.indexOf(';')).trim()
                            : contentType.trim();
                }

                String reason = code == 206 ? "Partial Content" : "OK";
                return new WebResourceResponse(cleanMime, null, code, reason, respHeaders, stream);
            } catch (Exception e) {
                invalidateStream(videoId);
            }
        }

        return buildErrorResponse(502, "Stream unavailable");
    }

    private static WebResourceResponse buildErrorResponse(int statusCode, String message) {
        Map<String, String> headers = new HashMap<>();
        headers.put("Access-Control-Allow-Origin", "https://kurfanova.github.io");
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

    private static final class ResponseClosingInputStream extends FilterInputStream {
        private final okhttp3.Response response;

        ResponseClosingInputStream(InputStream in, okhttp3.Response response) {
            super(in);
            this.response = response;
        }

        @Override
        public void close() throws IOException {
            try {
                super.close();
            } finally {
                response.close();
            }
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
