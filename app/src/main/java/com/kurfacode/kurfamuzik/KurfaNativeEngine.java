package com.kurfacode.kurfamuzik;

import android.net.Uri;
import android.util.Base64;
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

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
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
    static final String PREFETCH_PATH_PREFIX = "/__kurfa_prefetch__/";
    private static final Pattern YOUTUBE_ID_PATTERN = Pattern.compile("^[A-Za-z0-9_-]{11}$");
    private static final Pattern WATCH_ID_PATTERN = Pattern.compile("(?:v=|youtu\\.be/|shorts/|embed/)([A-Za-z0-9_-]{11})");
    private static final long STREAM_CACHE_TTL_MS = 35L * 60L * 1000L;
    private static final long CLIENT_BLACKLIST_TTL_MS = 5L * 60L * 1000L;
    private static final int MAX_STREAM_CACHE = 80;

    private static final String DOWNLOADER_UA =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0";
    private static final String WEB_UA =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36";
    private static final String VISIONOS_UA =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15";
    private static final String IOS_UA =
            "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)";
    private static final String ANDROID_UA =
            "com.google.android.youtube/20.10.38 (Linux; U; Android 14; en_US; Pixel 8) gzip";
    private static final String TVHTML5_SIMPLY_UA =
            "Mozilla/5.0 (ChromiumStylePlatform) Cobalt/25.lts.30.1034943-gold (unlike Gecko), Unknown_TV_Unknown_0/Unknown (Unknown, Unknown)";
    private static final String ANDROID_VR_UA =
            "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip";
    private static final String PLAYER_FORMAT_FIELDS =
            "itag,url,mimeType,bitrate,averageBitrate,width,height,contentLength,quality,audioQuality,"
                    + "approxDurationMs,audioSampleRate,audioChannels,loudnessDb,lastModified,xtags,audioTrack,isDrc,signatureCipher,"
                    + "drmFamilies,drmTrackType";
    private static final String PLAYER_RESPONSE_FIELD_MASK =
            "playabilityStatus(status,reason,desktopLegacyAgeGateReason),"
                    + "streamingData(formats(" + PLAYER_FORMAT_FIELDS + "),adaptiveFormats(" + PLAYER_FORMAT_FIELDS + "),"
                    + "expiresInSeconds,hlsManifestUrl,dashManifestUrl,serverAbrStreamingUrl,drmParams,licenseInfos),"
                    + "videoDetails(videoId,title,author,channelId,lengthSeconds,musicVideoType,viewCount,thumbnail,isLiveContent),"
                    + "playbackTracking(videostatsPlaybackUrl,videostatsWatchtimeUrl,atrUrl,"
                    + "videostatsScheduledFlushWalltimeSeconds,videostatsDefaultFlushIntervalSeconds),"
                    + "playerConfig(audioConfig(loudnessDb,perceptualLoudnessDb),"
                    + "mediaCommonConfig(mediaUstreamerRequestConfig(videoPlaybackUstreamerConfig)))";

    private static final String[] PRIMARY_CLIENT_ORDER = new String[]{
            "VISIONOS", "ANDROID", "WEB_REMIX", "NEWPIPE", "IOS", "TVHTML5_SIMPLY", "ANDROID_VR", "SABR"
    };
    private static final String[] ALTERNATE_CLIENT_ORDER = new String[]{
            "ANDROID", "WEB_REMIX", "IOS", "TVHTML5_SIMPLY", "VISIONOS", "NEWPIPE", "ANDROID_VR", "SABR"
    };

    private static final String WEB_REMIX_VERSION = "1.20260707.12.00";
    private static final long VISITOR_DATA_TTL_MS = 30L * 60L * 1000L;
    private static final long PO_TOKEN_TTL_MS = 30L * 60L * 1000L;
    private static final long MAX_ADAPTIVE_CHUNK_BYTES = 512L * 1024L;
    private static final long MAX_PROGRESSIVE_CHUNK_BYTES = 2L * 1024L * 1024L;
    private static final long HEAD_PROBE_END_BYTE = 1023L;
    private static final long TAIL_PROBE_START_BYTE = 1_048_576L;
    private static final long TAIL_PROBE_END_BYTE = 1_310_719L;
    private static final MediaType JSON_MEDIA_TYPE = MediaType.get("application/json; charset=utf-8");

    /**
     * Tracks temporary 5-minute client blacklists after 403/network failures so subsequent
     * resolutions immediately skip degraded clients and switch to healthy ones.
     */
    static final class ClientHealthTracker {
        private static final ConcurrentHashMap<String, Long> BLACKLIST_UNTIL = new ConcurrentHashMap<>();

        static boolean isHealthy(String clientKind) {
            if (clientKind == null || clientKind.isEmpty()) return true;
            Long until = BLACKLIST_UNTIL.get(clientKind);
            if (until == null) return true;
            if (System.currentTimeMillis() >= until) {
                BLACKLIST_UNTIL.remove(clientKind, until);
                return true;
            }
            return false;
        }

        static void markUnhealthy(String clientKind) {
            if (clientKind == null || clientKind.isEmpty()) return;
            BLACKLIST_UNTIL.put(clientKind, System.currentTimeMillis() + CLIENT_BLACKLIST_TTL_MS);
        }

        static void markHealthy(String clientKind) {
            if (clientKind == null || clientKind.isEmpty()) return;
            BLACKLIST_UNTIL.remove(clientKind);
        }
    }

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
            .readTimeout(12, TimeUnit.SECONDS)
            .writeTimeout(10, TimeUnit.SECONDS)
            .connectionPool(new ConnectionPool(12, 2, TimeUnit.MINUTES))
            .retryOnConnectionFailure(true)
            .followRedirects(true)
            .build();

    private static final ConcurrentHashMap<String, ResolvedAudio> STREAM_CACHE = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, PoTokenCacheEntry> PO_TOKEN_CACHE = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, SabrBootstrapContext> SABR_CONTEXT_CACHE = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, byte[]> SABR_AUDIO_BUFFER = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, Object> RESOLVE_LOCKS = new ConcurrentHashMap<>();
    private static final Object VISITOR_LOCK = new Object();
    private static final Object PO_TOKEN_LOCK = new Object();
    private static final Object PROXY_LOCK = new Object();

    private static final class PoTokenCacheEntry {
        final String poToken;
        final String visitorData;
        final long expiresAt;

        PoTokenCacheEntry(String poToken, String visitorData, long expiresAt) {
            this.poToken = poToken;
            this.visitorData = visitorData;
            this.expiresAt = expiresAt;
        }
    }

    private static final class SabrBootstrapContext {
        final String videoId;
        final String serverAbrStreamingUrl;
        final String ustreamerConfig;
        final String visitorData;
        final String poToken;
        final String title;
        final String artist;
        final long durationSec;
        final int itag;
        final String mimeType;
        final long contentLength;
        final long lastModified;

        SabrBootstrapContext(
                String videoId,
                String serverAbrStreamingUrl,
                String ustreamerConfig,
                String visitorData,
                String poToken,
                String title,
                String artist,
                long durationSec,
                int itag,
                String mimeType,
                long contentLength,
                long lastModified
        ) {
            this.videoId = videoId;
            this.serverAbrStreamingUrl = serverAbrStreamingUrl;
            this.ustreamerConfig = ustreamerConfig;
            this.visitorData = visitorData;
            this.poToken = poToken;
            this.title = title;
            this.artist = artist;
            this.durationSec = durationSec;
            this.itag = itag;
            this.mimeType = mimeType;
            this.contentLength = contentLength;
            this.lastModified = lastModified;
        }
    }
    private static final ExecutorService PROXY_EXECUTOR = Executors.newCachedThreadPool(r -> {
        Thread t = new Thread(r, "KurfaLocalProxyWorker");
        t.setDaemon(true);
        return t;
    });
    private static volatile String cachedVisitorData = null;
    private static volatile long cachedVisitorDataExpiresAt = 0L;
    private static volatile boolean initialized = false;
    private static volatile ServerSocket localProxySocket = null;
    private static volatile int localProxyPort = -1;

    private KurfaNativeEngine() {
    }

    static OkHttpClient getSharedHttpClient() {
        return HTTP_CLIENT;
    }

    /**
     * Client used by ExoPlayer to talk to the 127.0.0.1 proxy. The read timeout is longer than the
     * upstream one so a mid-stream client switch inside the proxy (re-resolve + range resume) can
     * complete without ExoPlayer tearing down the connection; the service stall watchdog still
     * guarantees recovery if the proxy itself hangs.
     */
    private static volatile OkHttpClient localProxyClient;

    static OkHttpClient getLocalProxyHttpClient() {
        OkHttpClient c = localProxyClient;
        if (c == null) {
            synchronized (PROXY_LOCK) {
                c = localProxyClient;
                if (c == null) {
                    c = HTTP_CLIENT.newBuilder()
                            .connectTimeout(4, TimeUnit.SECONDS)
                            .readTimeout(20, TimeUnit.SECONDS)
                            .connectionPool(new ConnectionPool(4, 30, TimeUnit.SECONDS))
                            .build();
                    localProxyClient = c;
                }
            }
        }
        return c;
    }

    static synchronized void ensureInitialized() {
        if (initialized) {
            return;
        }
        NewPipe.init(new KurfaOkHttpDownloader(HTTP_CLIENT), new Localization("tr", "TR"));
        initialized = true;
    }

    static int ensureLocalProxyStarted() {
        if (localProxyPort > 0 && localProxySocket != null && !localProxySocket.isClosed()) {
            return localProxyPort;
        }
        synchronized (PROXY_LOCK) {
            if (localProxyPort > 0 && localProxySocket != null && !localProxySocket.isClosed()) {
                return localProxyPort;
            }
            try {
                ServerSocket ss = new ServerSocket();
                ss.setReuseAddress(true);
                ss.bind(new InetSocketAddress(InetAddress.getByName("127.0.0.1"), 0), 32);
                localProxySocket = ss;
                localProxyPort = ss.getLocalPort();
                Thread acceptThread = new Thread(() -> runProxyAcceptLoop(ss), "KurfaLocalProxyAccept");
                acceptThread.setDaemon(true);
                acceptThread.start();
                return localProxyPort;
            } catch (IOException e) {
                return -1;
            }
        }
    }

    static String getLocalNativeAudioUrl(String rawVideoId) {
        String videoId = extractVideoId(rawVideoId);
        if (videoId == null) return null;
        int port = ensureLocalProxyStarted();
        if (port <= 0) return null;
        return "http://127.0.0.1:" + port + NATIVE_AUDIO_PATH_PREFIX + videoId;
    }

    static void prefetchAudioAsync(String rawVideoId) {
        final String videoId = extractVideoId(rawVideoId);
        if (videoId == null) return;
        ResolvedAudio cached = STREAM_CACHE.get(videoId);
        if (cached != null && cached.expiresAt > System.currentTimeMillis()) {
            return;
        }
        PROXY_EXECUTOR.execute(() -> {
            try {
                resolveAudioStream(videoId, false, false);
            } catch (Throwable ignored) {}
        });
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
        invalidateStream(videoId, false);
    }

    /**
     * Byte-level identity (mime + total length) of the stream ExoPlayer is currently decoding per
     * videoId. Mid-file range requests (start &gt; 0) are only ever served from a stream with the same
     * signature, so a client switch can never splice bytes of a different itag into the decoder.
     */
    private static final ConcurrentHashMap<String, String> SERVED_SIGNATURE = new ConcurrentHashMap<>();

    private static String buildSignature(String mime, long length) {
        if (length <= 0L) return null;
        return cleanMimeType(null, mime) + "|" + length;
    }

    /**
     * Returns a cache-key-safe signature of the currently cached (prefetched) stream and pins it for
     * subsequent mid-file requests, or null when nothing valid is cached yet.
     */
    static String peekAndPinStreamSignature(String rawVideoId) {
        String videoId = extractVideoId(rawVideoId);
        if (videoId == null) return null;
        ResolvedAudio cached = STREAM_CACHE.get(videoId);
        if (cached == null || cached.expiresAt <= System.currentTimeMillis()
                || !ClientHealthTracker.isHealthy(cached.clientKind)) {
            return null;
        }
        String sig = buildSignature(cached.mimeType, cached.contentLength);
        if (sig == null) return null;
        SERVED_SIGNATURE.put(videoId, sig);
        return sig.replaceAll("[^A-Za-z0-9]", "_");
    }

    static void clearStreamSignature(String rawVideoId) {
        String videoId = extractVideoId(rawVideoId);
        if (videoId != null) SERVED_SIGNATURE.remove(videoId);
    }

    /**
     * Drops the cached stream URL for {@code videoId}. When {@code blacklistCurrentClient} is true the
     * InnerTube client that produced the stalled URL is temporarily blacklisted so the next proxy
     * request immediately resolves a fresh URL from a different, healthy client.
     */
    static void invalidateStream(String videoId, boolean blacklistCurrentClient) {
        if (videoId == null) return;
        ResolvedAudio stale = STREAM_CACHE.remove(videoId);
        if (blacklistCurrentClient && stale != null) {
            ClientHealthTracker.markUnhealthy(stale.clientKind);
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
        return resolveAudioStream(videoId, forceRefresh, preferAlternate, null, -1L);
    }

    static ResolvedAudio resolveAudioStream(
            String videoId,
            boolean forceRefresh,
            boolean preferAlternate,
            String requiredMime,
            long requiredContentLength
    ) {
        if (!isValidVideoId(videoId)) {
            return null;
        }
        ensureInitialized();

        Object lock = RESOLVE_LOCKS.computeIfAbsent(videoId, k -> new Object());
        synchronized (lock) {
            if (!forceRefresh) {
                ResolvedAudio cached = STREAM_CACHE.get(videoId);
                if (cached != null
                        && cached.expiresAt > System.currentTimeMillis()
                        && ClientHealthTracker.isHealthy(cached.clientKind)
                        && matchesStreamConstraints(cached, requiredMime, requiredContentLength)) {
                    return cached;
                }
            }
            STREAM_CACHE.remove(videoId);

            String[] order = preferAlternate ? ALTERNATE_CLIENT_ORDER : PRIMARY_CLIENT_ORDER;
            ResolvedAudio resolved = resolveWithHealthCatalog(videoId, order, forceRefresh, requiredMime, requiredContentLength, false);
            if (resolved == null) {
                // Fallback pass ignoring blacklist only if every healthy client failed
                resolved = resolveWithHealthCatalog(videoId, order, true, requiredMime, requiredContentLength, true);
            }

            if (resolved != null) {
                pruneCacheIfNeeded();
                STREAM_CACHE.put(videoId, resolved);
            }
            return resolved;
        }
    }

    private static boolean matchesStreamConstraints(ResolvedAudio audio, String requiredMime, long requiredContentLength) {
        if (audio == null || audio.streamUrl == null || audio.streamUrl.isEmpty()) return false;
        if (requiredMime != null && !requiredMime.isEmpty()) {
            String candidateMime = cleanMimeType(null, audio.mimeType);
            if (!requiredMime.equals(candidateMime)) return false;
        }
        if (requiredContentLength > 0L && audio.contentLength > 0L && audio.contentLength != requiredContentLength) {
            return false;
        }
        return true;
    }

    private static ResolvedAudio resolveWithHealthCatalog(
            String videoId,
            String[] order,
            boolean forceRefreshVisitor,
            String requiredMime,
            long requiredContentLength,
            boolean ignoreBlacklist
    ) {
        for (String clientKind : order) {
            if (!ignoreBlacklist && !ClientHealthTracker.isHealthy(clientKind)) {
                continue;
            }
            ResolvedAudio candidate = resolveByClientKind(videoId, clientKind, forceRefreshVisitor);
            if (candidate != null && matchesStreamConstraints(candidate, requiredMime, requiredContentLength)) {
                ClientHealthTracker.markHealthy(clientKind);
                return candidate;
            }
        }
        return null;
    }

    private static ResolvedAudio resolveByClientKind(String videoId, String clientKind, boolean forceRefreshVisitor) {
        if ("VISIONOS".equals(clientKind)) {
            return resolveViaVisionOs(videoId, forceRefreshVisitor);
        }
        if ("ANDROID".equals(clientKind)) {
            return resolveViaAndroid(videoId);
        }
        if ("WEB_REMIX".equals(clientKind)) {
            return resolveViaWebRemix(videoId, forceRefreshVisitor);
        }
        if ("IOS".equals(clientKind)) {
            return resolveViaIos(videoId);
        }
        if ("TVHTML5_SIMPLY".equals(clientKind)) {
            return resolveViaTvHtml5Simply(videoId);
        }
        if ("ANDROID_VR".equals(clientKind)) {
            return resolveViaAndroidVr(videoId);
        }
        if ("NEWPIPE".equals(clientKind)) {
            return tryVerifiedAudioCandidate(resolveViaNewPipe(videoId));
        }
        if ("SABR".equals(clientKind)) {
            return resolveViaSabrFallback(videoId, forceRefreshVisitor);
        }
        return null;
    }

    private static ResolvedAudio tryVerifiedAudioCandidate(ResolvedAudio candidate) {
        if (candidate == null || candidate.streamUrl == null || candidate.streamUrl.isEmpty()) {
            return null;
        }
        if (candidate.streamUrl.startsWith("sabr://")) {
            return candidate;
        }
        okhttp3.Request req = new okhttp3.Request.Builder()
                .url(candidate.streamUrl)
                .get()
                .header("User-Agent", candidate.requestUserAgent)
                .header("Range", "bytes=0-" + HEAD_PROBE_END_BYTE)
                .build();
        try (okhttp3.Response res = HTTP_CLIENT.newCall(req).execute()) {
            int code = res.code();
            if (code == 403 || code == 410 || code == 429) {
                ClientHealthTracker.markUnhealthy(candidate.clientKind);
                return null;
            }
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
                    if (tailCode == 403 || tailCode == 410 || tailCode == 429) {
                        ClientHealthTracker.markUnhealthy(candidate.clientKind);
                        return null;
                    }
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
                JSONObject vBody = new JSONObject().put("context", new JSONObject().put("client",
                        new JSONObject()
                                .put("clientName", "WEB_REMIX")
                                .put("clientVersion", WEB_REMIX_VERSION)
                                .put("hl", "tr")
                                .put("gl", "TR")));
                okhttp3.Request vReq = new okhttp3.Request.Builder()
                        .url("https://music.youtube.com/youtubei/v1/visitor_id?prettyPrint=false")
                        .post(RequestBody.create(vBody.toString(), JSON_MEDIA_TYPE))
                        .header("User-Agent", WEB_UA)
                        .header("Origin", "https://music.youtube.com")
                        .header("Referer", "https://music.youtube.com/")
                        .build();
                try (okhttp3.Response vRes = HTTP_CLIENT.newCall(vReq).execute()) {
                    if (vRes.isSuccessful() && vRes.body() != null) {
                        JSONObject vJson = new JSONObject(vRes.body().string());
                        JSONObject rc = vJson.optJSONObject("responseContext");
                        String vd = rc != null ? rc.optString("visitorData", "").trim() : "";
                        if (!vd.isEmpty()) {
                            cachedVisitorData = vd;
                            cachedVisitorDataExpiresAt = System.currentTimeMillis() + VISITOR_DATA_TTL_MS;
                            return vd;
                        }
                    }
                }
            } catch (Exception ignored) {
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

    private static String resolvePoTokenForWebRemix(String videoId, String visitorData, boolean forceRefresh) {
        if (visitorData == null || visitorData.isEmpty()) {
            return null;
        }
        String cacheKey = visitorData + ":" + videoId;
        long now = System.currentTimeMillis();
        PoTokenCacheEntry cached = PO_TOKEN_CACHE.get(cacheKey);
        if (!forceRefresh && cached != null && cached.expiresAt > now) {
            return cached.poToken;
        }
        synchronized (PO_TOKEN_LOCK) {
            now = System.currentTimeMillis();
            cached = PO_TOKEN_CACHE.get(cacheKey);
            if (!forceRefresh && cached != null && cached.expiresAt > now) {
                return cached.poToken;
            }
            try {
                JSONObject attBody = new JSONObject()
                        .put("context", new JSONObject().put("client",
                                new JSONObject()
                                        .put("clientName", "WEB_REMIX")
                                        .put("clientVersion", WEB_REMIX_VERSION)
                                        .put("hl", "tr")
                                        .put("gl", "TR")
                                        .put("visitorData", visitorData)))
                        .put("engagementType", "ENGAGEMENT_TYPE_UNBOUND")
                        .put("ids", new JSONArray().put(new JSONObject().put("encryptedVideoId", videoId)));

                okhttp3.Request attReq = new okhttp3.Request.Builder()
                        .url("https://music.youtube.com/youtubei/v1/att/get?prettyPrint=false")
                        .post(RequestBody.create(attBody.toString(), JSON_MEDIA_TYPE))
                        .header("User-Agent", WEB_UA)
                        .header("Origin", "https://music.youtube.com")
                        .header("Referer", "https://music.youtube.com/")
                        .header("X-Goog-Visitor-Id", visitorData)
                        .build();

                try (okhttp3.Response attRes = HTTP_CLIENT.newCall(attReq).execute()) {
                    if (!attRes.isSuccessful() || attRes.body() == null) {
                        return null;
                    }
                    JSONObject attJson = new JSONObject(attRes.body().string());
                    String directToken = attJson.optString("challenge", "").trim();
                    if (directToken.isEmpty()) {
                        JSONObject bg = attJson.optJSONObject("bgChallenge");
                        if (bg != null) {
                            String challengeToken = bg.optString("challenge", "").trim();
                            if (!challengeToken.isEmpty()) {
                                directToken = challengeToken;
                            }
                        }
                    }
                    if (!directToken.isEmpty()) {
                        PO_TOKEN_CACHE.put(cacheKey, new PoTokenCacheEntry(
                                directToken, visitorData, System.currentTimeMillis() + PO_TOKEN_TTL_MS));
                        return directToken;
                    }
                }
            } catch (Exception ignored) {
            }
        }
        return null;
    }

    private static ResolvedAudio resolveViaWebRemix(String videoId, boolean forceRefreshVisitor) {
        try {
            String visitorData = resolveVisitorData(forceRefreshVisitor);
            if (visitorData == null || visitorData.isEmpty()) {
                visitorData = resolveVisitorData(true);
            }
            if (visitorData == null || visitorData.isEmpty()) {
                return null;
            }
            String poToken = resolvePoTokenForWebRemix(videoId, visitorData, forceRefreshVisitor);
            JSONObject client = new JSONObject()
                    .put("clientName", "WEB_REMIX")
                    .put("clientVersion", WEB_REMIX_VERSION)
                    .put("osName", "Windows")
                    .put("osVersion", "10.0")
                    .put("platform", "DESKTOP")
                    .put("visitorData", visitorData)
                    .put("hl", "tr")
                    .put("gl", "TR");
            JSONObject payload = new JSONObject()
                    .put("context", new JSONObject().put("client", client))
                    .put("videoId", videoId)
                    .put("contentCheckOk", true)
                    .put("racyCheckOk", true);
            if (poToken != null && !poToken.isEmpty()) {
                payload.put("serviceIntegrityDimensions", new JSONObject().put("poToken", poToken));
            }
            return executePlayerClientInternal(
                    videoId, payload, WEB_UA, "67", WEB_REMIX_VERSION, visitorData, "WEB_REMIX", true, true, poToken);
        } catch (Exception ignored) {
            return null;
        }
    }

    private static String extractUstreamerConfig(JSONObject root) {
        if (root == null) return "";
        JSONObject pc = root.optJSONObject("playerConfig");
        JSONObject mcc = pc != null ? pc.optJSONObject("mediaCommonConfig") : null;
        JSONObject murc = mcc != null ? mcc.optJSONObject("mediaUstreamerRequestConfig") : null;
        return murc != null ? murc.optString("videoPlaybackUstreamerConfig", "").trim() : "";
    }

    private static ResolvedAudio resolveViaSabrFallback(String videoId, boolean forceRefreshVisitor) {
        try {
            SabrBootstrapContext ctx = SABR_CONTEXT_CACHE.get(videoId);
            if (ctx == null || ctx.serverAbrStreamingUrl == null || ctx.serverAbrStreamingUrl.isEmpty()) {
                resolveViaWebRemix(videoId, forceRefreshVisitor);
                ctx = SABR_CONTEXT_CACHE.get(videoId);
            }
            if (ctx == null || ctx.serverAbrStreamingUrl == null || ctx.serverAbrStreamingUrl.isEmpty()) {
                return null;
            }

            String url = ctx.serverAbrStreamingUrl;
            url += (url.contains("?") ? "&" : "?") + "rn=0";
            if (!url.contains("c=")) {
                url += "&c=WEB_REMIX";
            }
            if (ctx.poToken != null && !ctx.poToken.isEmpty() && !url.contains("pot=")) {
                url += "&pot=" + Uri.encode(ctx.poToken);
            }

            byte[] reqBody = buildSabrAbrRequest(ctx);
            MediaType protoType = MediaType.get("application/x-protobuf");
            for (int hop = 0; hop < 3; hop++) {
                okhttp3.Request req = new okhttp3.Request.Builder()
                        .url(url)
                        .post(RequestBody.create(reqBody, protoType))
                        .header("User-Agent", WEB_UA)
                        .header("Origin", "https://music.youtube.com")
                        .header("Referer", "https://music.youtube.com/")
                        .build();
                try (okhttp3.Response res = HTTP_CLIENT.newCall(req).execute()) {
                    if (!res.isSuccessful() || res.body() == null) {
                        ClientHealthTracker.markUnhealthy("SABR");
                        return null;
                    }
                    byte[] raw = res.body().bytes();
                    if (raw.length == 0) {
                        ClientHealthTracker.markUnhealthy("SABR");
                        return null;
                    }
                    String[] redirectOut = new String[1];
                    byte[] audioBytes = parseUmpAudioBytes(raw, redirectOut);
                    if (redirectOut[0] != null && !redirectOut[0].isEmpty()) {
                        url = redirectOut[0];
                        continue;
                    }
                    if (audioBytes != null && audioBytes.length >= 256) {
                        if (SABR_AUDIO_BUFFER.size() >= 2) {
                            SABR_AUDIO_BUFFER.clear();
                        }
                        SABR_AUDIO_BUFFER.put(videoId, audioBytes);
                        ClientHealthTracker.markHealthy("SABR");
                        return new ResolvedAudio(
                                videoId,
                                "sabr://" + videoId,
                                ctx.mimeType != null && !ctx.mimeType.isEmpty() ? ctx.mimeType : "audio/mp4",
                                ctx.title != null ? ctx.title : "",
                                ctx.artist != null ? ctx.artist : "",
                                ctx.durationSec,
                                audioBytes.length,
                                WEB_UA,
                                "SABR"
                        );
                    }
                    ClientHealthTracker.markUnhealthy("SABR");
                    return null;
                }
            }
        } catch (Exception ignored) {
            ClientHealthTracker.markUnhealthy("SABR");
        }
        return null;
    }

    private static ProxyResponse serveSabrBufferedResponse(
            String videoId,
            ResolvedAudio audio,
            boolean isHead,
            long[] parsedRange,
            String allowOrigin
    ) {
        byte[] data = SABR_AUDIO_BUFFER.get(videoId);
        if (data == null || data.length == 0) {
            return null;
        }
        long totalLen = data.length;
        String mime = cleanMimeType(null, audio.mimeType);
        Map<String, String> headers = new HashMap<>();
        headers.put("Access-Control-Allow-Origin", allowOrigin);
        headers.put("Vary", "Origin");
        headers.put("Access-Control-Expose-Headers", "Content-Range, Content-Length, Accept-Ranges");
        headers.put("Cache-Control", "no-store");
        headers.put("Accept-Ranges", "bytes");

        if (parsedRange != null) {
            long start = parsedRange[0];
            if (start >= totalLen) {
                headers.put("Content-Range", "bytes */" + totalLen);
                return new ProxyResponse(416, "Range Not Satisfiable", "text/plain",
                        headers, new ByteArrayInputStream(new byte[0]));
            }
            long end = parsedRange[1] >= 0L ? Math.min(parsedRange[1], totalLen - 1L) : (totalLen - 1L);
            int len = (int) Math.max(0L, end - start + 1L);
            headers.put("Content-Range", "bytes " + start + "-" + end + "/" + totalLen);
            headers.put("Content-Length", String.valueOf(len));
            InputStream body = isHead
                    ? new ByteArrayInputStream(new byte[0])
                    : new ByteArrayInputStream(data, (int) start, len);
            return new ProxyResponse(206, "Partial Content", mime, headers, body);
        }

        headers.put("Content-Length", String.valueOf(totalLen));
        InputStream body = isHead
                ? new ByteArrayInputStream(new byte[0])
                : new ByteArrayInputStream(data);
        return new ProxyResponse(200, "OK", mime, headers, body);
    }

    private static byte[] buildSabrAbrRequest(SabrBootstrapContext ctx) {
        ByteArrayOutputStream root = new ByteArrayOutputStream();
        ByteArrayOutputStream abrState = new ByteArrayOutputStream();
        writeProtoVarIntField(abrState, 1, 0L);
        writeProtoVarIntField(abrState, 25, 1L);
        writeProtoBytesField(root, 1, abrState.toByteArray());

        if (ctx.itag > 0) {
            ByteArrayOutputStream fmt = new ByteArrayOutputStream();
            writeProtoVarIntField(fmt, 1, ctx.itag);
            if (ctx.lastModified > 0L) {
                writeProtoVarIntField(fmt, 2, ctx.lastModified);
            }
            writeProtoBytesField(root, 2, fmt.toByteArray());
        }
        if (ctx.ustreamerConfig != null && !ctx.ustreamerConfig.isEmpty()) {
            byte[] cfg = tryDecodeBase64Flexible(ctx.ustreamerConfig);
            if (cfg != null && cfg.length > 0) {
                writeProtoBytesField(root, 5, cfg);
            }
        }
        if (ctx.poToken != null && !ctx.poToken.isEmpty()) {
            byte[] pot = tryDecodeBase64Flexible(ctx.poToken);
            if (pot != null && pot.length > 0) {
                writeProtoBytesField(root, 16, pot);
            }
        }
        return root.toByteArray();
    }

    private static byte[] parseUmpAudioBytes(byte[] raw, String[] redirectOut) {
        ByteArrayOutputStream audioOut = new ByteArrayOutputStream();
        int[] offset = new int[]{0};
        while (offset[0] < raw.length) {
            long partType = readUmpVarInt(raw, offset);
            long partSize = readUmpVarInt(raw, offset);
            if (partType < 0 || partSize < 0 || offset[0] + partSize > raw.length) {
                break;
            }
            int pStart = offset[0];
            int pLen = (int) partSize;
            offset[0] += pLen;

            if (partType == 21L && pLen > 1) { // MEDIA
                int[] segOff = new int[]{pStart};
                long hdrId = readUmpVarInt(raw, segOff);
                if (hdrId >= 0 && segOff[0] < pStart + pLen) {
                    audioOut.write(raw, segOff[0], (pStart + pLen) - segOff[0]);
                }
            } else if (partType == 58L && pLen > 0) { // SABR_REDIRECT
                String url = readFirstProtoString(raw, pStart, pLen, 1);
                if (url != null && !url.isEmpty()) {
                    redirectOut[0] = url;
                }
            }
        }
        return audioOut.toByteArray();
    }

    private static long readUmpVarInt(byte[] data, int[] offset) {
        int pos = offset[0];
        if (pos >= data.length) return -1L;
        int b0 = data[pos] & 0xFF;
        if (b0 < 0x80) {
            offset[0] = pos + 1;
            return b0;
        }
        if (b0 < 0xC0) {
            if (pos + 2 > data.length) return -1L;
            offset[0] = pos + 2;
            return (b0 & 0x3F) | ((long) (data[pos + 1] & 0xFF) << 6);
        }
        if (b0 < 0xE0) {
            if (pos + 3 > data.length) return -1L;
            offset[0] = pos + 3;
            return (b0 & 0x1F) | ((long) (data[pos + 1] & 0xFF) << 5) | ((long) (data[pos + 2] & 0xFF) << 13);
        }
        if (b0 < 0xF0) {
            if (pos + 4 > data.length) return -1L;
            offset[0] = pos + 4;
            return (b0 & 0x0F) | ((long) (data[pos + 1] & 0xFF) << 4)
                    | ((long) (data[pos + 2] & 0xFF) << 12) | ((long) (data[pos + 3] & 0xFF) << 20);
        }
        if (pos + 5 > data.length) return -1L;
        offset[0] = pos + 5;
        return (data[pos + 1] & 0xFFL)
                | ((data[pos + 2] & 0xFFL) << 8)
                | ((data[pos + 3] & 0xFFL) << 16)
                | ((data[pos + 4] & 0xFFL) << 24);
    }

    private static String readFirstProtoString(byte[] data, int start, int len, int targetField) {
        int pos = start;
        int end = start + len;
        while (pos < end) {
            long tag = 0L;
            int shift = 0;
            while (pos < end && shift < 64) {
                int b = data[pos++] & 0xFF;
                tag |= (long) (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            int field = (int) (tag >>> 3);
            int wire = (int) (tag & 0x07);
            if (wire == 0) {
                while (pos < end && (data[pos++] & 0x80) != 0) {}
            } else if (wire == 2) {
                long sLen = 0L;
                int sShift = 0;
                while (pos < end && sShift < 64) {
                    int b = data[pos++] & 0xFF;
                    sLen |= (long) (b & 0x7F) << sShift;
                    if ((b & 0x80) == 0) break;
                    sShift += 7;
                }
                if (sLen < 0 || pos + sLen > end) break;
                if (field == targetField) {
                    return new String(data, pos, (int) sLen, StandardCharsets.UTF_8);
                }
                pos += (int) sLen;
            } else if (wire == 1) {
                pos += 8;
            } else if (wire == 5) {
                pos += 4;
            } else {
                break;
            }
        }
        return null;
    }

    private static byte[] tryDecodeBase64Flexible(String raw) {
        try {
            return Base64.decode(raw.trim(), Base64.URL_SAFE | Base64.NO_WRAP);
        } catch (IllegalArgumentException e) {
            try {
                return Base64.decode(raw.trim(), Base64.DEFAULT);
            } catch (IllegalArgumentException ignored) {
                return null;
            }
        }
    }

    private static void writeProtoVarIntField(ByteArrayOutputStream s, int fieldNumber, long value) {
        writeProtoVarInt(s, ((long) fieldNumber << 3));
        writeProtoVarInt(s, value);
    }

    private static void writeProtoBytesField(ByteArrayOutputStream s, int fieldNumber, byte[] bytes) {
        writeProtoVarInt(s, ((long) fieldNumber << 3) | 2L);
        writeProtoVarInt(s, bytes.length);
        s.write(bytes, 0, bytes.length);
    }

    private static void writeProtoVarInt(ByteArrayOutputStream s, long value) {
        long v = value;
        while ((v & ~0x7FL) != 0L) {
            s.write((int) ((v & 0x7FL) | 0x80L));
            v >>>= 7;
        }
        s.write((int) v);
    }

    private static ResolvedAudio resolveViaVisionOs(String videoId, boolean forceRefreshVisitor) {
        try {
            String visitorData = resolveVisitorData(forceRefreshVisitor);
            if (visitorData == null || visitorData.isEmpty()) {
                visitorData = resolveVisitorData(true);
            }
            if (visitorData == null || visitorData.isEmpty()) {
                return null;
            }
            JSONObject client = new JSONObject()
                    .put("clientName", "VISIONOS")
                    .put("clientVersion", "0.1")
                    .put("deviceMake", "Apple")
                    .put("deviceModel", "RealityDevice14,1")
                    .put("osName", "VISION_OS")
                    .put("osVersion", "1.3")
                    .put("platform", "MOBILE")
                    .put("visitorData", visitorData)
                    .put("hl", "tr")
                    .put("gl", "TR");
            JSONObject payload = new JSONObject()
                    .put("context", new JSONObject().put("client", client))
                    .put("videoId", videoId)
                    .put("contentCheckOk", true)
                    .put("racyCheckOk", true);
            ResolvedAudio res = executePlayerClient(
                    videoId, payload, VISIONOS_UA, "101", "0.1", visitorData, "VISIONOS", true);
            if (res == null) {
                res = executePlayerClient(
                        videoId, payload, VISIONOS_UA, "101", "0.1", visitorData, "VISIONOS", false);
            }
            return res;
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
            return executePlayerClient(videoId, payload, IOS_UA, "5", "20.10.4", null, "IOS", false);
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
            return executePlayerClient(videoId, payload, ANDROID_UA, "3", "20.10.38", null, "ANDROID", false);
        } catch (Exception ignored) {
            return null;
        }
    }

    private static ResolvedAudio resolveViaTvHtml5Simply(String videoId) {
        try {
            String visitorData = resolveVisitorData(false);
            JSONObject client = new JSONObject()
                    .put("clientName", "TVHTML5_SIMPLY")
                    .put("clientVersion", "1.0")
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
            return executePlayerClient(
                    videoId, payload, TVHTML5_SIMPLY_UA, "75", "1.0", visitorData, "TVHTML5_SIMPLY", true);
        } catch (Exception ignored) {
            return null;
        }
    }

    private static ResolvedAudio resolveViaAndroidVr(String videoId) {
        try {
            JSONObject client = new JSONObject()
                    .put("clientName", "ANDROID_VR")
                    .put("clientVersion", "1.65.10")
                    .put("androidSdkVersion", 32)
                    .put("osName", "Android")
                    .put("osVersion", "12L")
                    .put("hl", "tr")
                    .put("gl", "TR");
            JSONObject payload = new JSONObject()
                    .put("context", new JSONObject().put("client", client))
                    .put("videoId", videoId)
                    .put("contentCheckOk", true)
                    .put("racyCheckOk", true);
            return executePlayerClient(videoId, payload, ANDROID_VR_UA, "28", "1.65.10", null, "ANDROID_VR", false);
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
            String clientKind,
            boolean useMusicEndpoint
    ) throws IOException {
        return executePlayerClientInternal(
                videoId, payload, userAgent, clientNameId, clientVersion, visitorData, clientKind, useMusicEndpoint, true, null);
    }

    private static ResolvedAudio executePlayerClientInternal(
            String videoId,
            JSONObject payload,
            String userAgent,
            String clientNameId,
            String clientVersion,
            String visitorData,
            String clientKind,
            boolean useMusicEndpoint,
            boolean includeFieldMask,
            String poToken
    ) throws IOException {
        String baseUrl = useMusicEndpoint
                ? "https://music.youtube.com/youtubei/v1/player?prettyPrint=false"
                : "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
        okhttp3.Request.Builder rb = new okhttp3.Request.Builder()
                .url(baseUrl)
                .post(RequestBody.create(payload.toString(), JSON_MEDIA_TYPE))
                .header("User-Agent", userAgent);
        if (includeFieldMask) {
            rb.header("X-Goog-FieldMask", PLAYER_RESPONSE_FIELD_MASK);
        }
        if (useMusicEndpoint) {
            rb.header("Origin", "https://music.youtube.com")
                    .header("Referer", "https://music.youtube.com/");
        }
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
            int statusCode = res.code();
            if (statusCode == 400 && includeFieldMask) {
                return executePlayerClientInternal(
                        videoId, payload, userAgent, clientNameId, clientVersion, visitorData, clientKind, useMusicEndpoint, false, poToken);
            }
            if (statusCode == 403 || statusCode == 429 || statusCode >= 500) {
                ClientHealthTracker.markUnhealthy(clientKind);
                return null;
            }
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

            String serverAbrUrl = streamingData.optString("serverAbrStreamingUrl", "").trim();
            String ustreamerCfg = extractUstreamerConfig(json);
            int sabrItag = 140;
            String sabrMime = "audio/mp4";
            long sabrClen = 0L;
            long sabrLmt = 0L;
            int sabrBestMetaScore = Integer.MIN_VALUE;

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
                    if (!mime.startsWith("audio/")) continue;

                    int itag = f.optInt("itag", 0);
                    int bitrate = f.optInt("bitrate", 0);
                    long contentLen = parseLongSafe(f.optString("contentLength", ""));
                    long lmt = parseLongSafe(f.optString("lastModified", ""));
                    int score = 0;
                    if (itag == 140) score += 1000;
                    else if (mime.startsWith("audio/mp4")) score += 500;
                    else if (mime.startsWith("audio/webm")) score += 300;
                    if (bitrate >= 96_000 && bitrate <= 192_000) score += 200;

                    String cleanMime = mime.contains(";") ? mime.substring(0, mime.indexOf(';')).trim() : mime;
                    if (score > sabrBestMetaScore) {
                        sabrBestMetaScore = score;
                        sabrItag = itag > 0 ? itag : 140;
                        sabrMime = cleanMime;
                        sabrClen = contentLen;
                        sabrLmt = lmt;
                    }

                    String url = f.optString("url", "");
                    if (!url.isEmpty() && score > bestScore) {
                        bestScore = score;
                        bestUrl = url;
                        bestContentLength = contentLen;
                        bestMime = cleanMime;
                    }
                }

                if (!serverAbrUrl.isEmpty()) {
                    SABR_CONTEXT_CACHE.put(videoId, new SabrBootstrapContext(
                            videoId, serverAbrUrl, ustreamerCfg, visitorData, poToken,
                            title, author, dur, sabrItag, sabrMime, sabrClen, sabrLmt));
                }

                if (bestUrl != null) {
                    if (poToken != null && !poToken.isEmpty() && !bestUrl.contains("pot=")) {
                        bestUrl += (bestUrl.contains("?") ? "&pot=" : "?pot=") + Uri.encode(poToken);
                    }
                    ResolvedAudio verifiedAdaptive = tryVerifiedAudioCandidate(
                            new ResolvedAudio(videoId, bestUrl, bestMime, title, author, dur, bestContentLength, userAgent, clientKind));
                    if (verifiedAdaptive != null) {
                        return verifiedAdaptive;
                    }
                }
            } else if (!serverAbrUrl.isEmpty()) {
                SABR_CONTEXT_CACHE.put(videoId, new SabrBootstrapContext(
                        videoId, serverAbrUrl, ustreamerCfg, visitorData, poToken,
                        title, author, dur, sabrItag, sabrMime, sabrClen, sabrLmt));
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

    private static final class ProxyResponse {
        final int statusCode;
        final String reasonPhrase;
        final String mimeType;
        final Map<String, String> headers;
        final InputStream bodyStream;

        ProxyResponse(
                int statusCode,
                String reasonPhrase,
                String mimeType,
                Map<String, String> headers,
                InputStream bodyStream
        ) {
            this.statusCode = statusCode;
            this.reasonPhrase = reasonPhrase;
            this.mimeType = mimeType;
            this.headers = headers;
            this.bodyStream = bodyStream;
        }
    }

    private static void runProxyAcceptLoop(ServerSocket serverSocket) {
        while (!serverSocket.isClosed()) {
            try {
                final Socket client = serverSocket.accept();
                PROXY_EXECUTOR.execute(() -> handleProxyClient(client));
            } catch (IOException e) {
                if (serverSocket.isClosed()) {
                    break;
                }
            } catch (Throwable ignored) {
            }
        }
    }

    private static void handleProxyClient(Socket socket) {
        InputStream responseBody = null;
        try (Socket s = socket) {
            s.setSoTimeout(20_000);
            BufferedInputStream in = new BufferedInputStream(s.getInputStream(), 8192);
            String requestLine = readHttpLine(in);
            if (requestLine == null || requestLine.isEmpty()) {
                return;
            }
            String[] parts = requestLine.split("\\s+");
            if (parts.length < 2) {
                return;
            }
            String method = parts[0].toUpperCase(Locale.ROOT);
            String rawTarget = parts[1];
            int qIdx = rawTarget.indexOf('?');
            String path = qIdx >= 0 ? rawTarget.substring(0, qIdx) : rawTarget;

            Map<String, String> reqHeaders = new HashMap<>();
            for (int i = 0; i < 64; i++) {
                String line = readHttpLine(in);
                if (line == null || line.isEmpty()) {
                    break;
                }
                int colon = line.indexOf(':');
                if (colon > 0) {
                    String k = line.substring(0, colon).trim().toLowerCase(Locale.ROOT);
                    String v = line.substring(colon + 1).trim();
                    reqHeaders.put(k, v);
                }
            }

            OutputStream rawOut = s.getOutputStream();
            BufferedOutputStream out = new BufferedOutputStream(rawOut, 32768);

            if (path.startsWith(PREFETCH_PATH_PREFIX)) {
                String candidate = path.substring(PREFETCH_PATH_PREFIX.length()).trim();
                String videoId = extractVideoId(candidate);
                if (videoId == null) {
                    writeSimpleHttpResponse(out, 400, "Bad Request", "application/json; charset=utf-8",
                            "{\"success\":false,\"error\":\"Invalid videoId\"}".getBytes(StandardCharsets.UTF_8),
                            "HEAD".equals(method));
                    return;
                }
                ResolvedAudio prefetched = resolveAudioStream(videoId, false, false);
                JSONObject json = new JSONObject();
                try {
                    json.put("success", prefetched != null);
                    json.put("videoId", videoId);
                    if (prefetched != null) {
                        json.put("client", prefetched.clientKind);
                    }
                } catch (Exception ignored) {}
                writeSimpleHttpResponse(out, 200, "OK", "application/json; charset=utf-8",
                        json.toString().getBytes(StandardCharsets.UTF_8),
                        "HEAD".equals(method));
                return;
            }

            if (path.startsWith(NATIVE_AUDIO_PATH_PREFIX)) {
                String candidate = path.substring(NATIVE_AUDIO_PATH_PREFIX.length()).trim();
                String videoId = extractVideoId(candidate);
                if (videoId == null) {
                    writeSimpleHttpResponse(out, 400, "Bad Request", "text/plain; charset=utf-8",
                            "Invalid videoId".getBytes(StandardCharsets.UTF_8),
                            "HEAD".equals(method));
                    return;
                }
                ProxyResponse resp = openNativeAudioProxyResponse(videoId, method, reqHeaders, true);
                responseBody = resp.bodyStream;

                StringBuilder sb = new StringBuilder();
                sb.append("HTTP/1.1 ").append(resp.statusCode).append(' ').append(resp.reasonPhrase).append("\r\n");
                sb.append("Content-Type: ").append(resp.mimeType).append("\r\n");
                sb.append("Connection: close\r\n");
                if (resp.headers != null) {
                    for (Map.Entry<String, String> e : resp.headers.entrySet()) {
                        sb.append(e.getKey()).append(": ").append(e.getValue()).append("\r\n");
                    }
                }
                sb.append("\r\n");
                out.write(sb.toString().getBytes(StandardCharsets.US_ASCII));
                out.flush();

                if (!"HEAD".equals(method) && !"OPTIONS".equals(method) && responseBody != null) {
                    byte[] buf = new byte[32768];
                    int n;
                    while ((n = responseBody.read(buf)) != -1) {
                        out.write(buf, 0, n);
                    }
                    out.flush();
                }
                return;
            }

            writeSimpleHttpResponse(out, 404, "Not Found", "text/plain; charset=utf-8",
                    "Not Found".getBytes(StandardCharsets.UTF_8),
                    "HEAD".equals(method));
        } catch (IOException ignored) {
        } finally {
            if (responseBody != null) {
                try { responseBody.close(); } catch (IOException ignored) {}
            }
        }
    }

    private static String readHttpLine(InputStream in) throws IOException {
        StringBuilder sb = new StringBuilder(128);
        int b;
        boolean any = false;
        while ((b = in.read()) != -1) {
            any = true;
            if (b == '\n') {
                break;
            }
            if (b != '\r') {
                sb.append((char) b);
                if (sb.length() > 8192) {
                    break;
                }
            }
        }
        if (!any && sb.length() == 0) {
            return null;
        }
        return sb.toString();
    }

    private static void writeSimpleHttpResponse(
            OutputStream out,
            int statusCode,
            String reason,
            String contentType,
            byte[] body,
            boolean headOnly
    ) throws IOException {
        StringBuilder sb = new StringBuilder();
        sb.append("HTTP/1.1 ").append(statusCode).append(' ').append(reason).append("\r\n");
        sb.append("Content-Type: ").append(contentType).append("\r\n");
        sb.append("Content-Length: ").append(body.length).append("\r\n");
        sb.append("Cache-Control: no-store\r\n");
        sb.append("Connection: close\r\n\r\n");
        out.write(sb.toString().getBytes(StandardCharsets.US_ASCII));
        if (!headOnly && body.length > 0) {
            out.write(body);
        }
        out.flush();
    }

    static WebResourceResponse handleNativeAudioIntercept(
            String videoId,
            String method,
            Map<String, String> requestHeaders
    ) {
        ProxyResponse resp = openNativeAudioProxyResponse(videoId, method, requestHeaders, false);
        String encoding = (resp.statusCode >= 400 || resp.statusCode == 204) ? "utf-8" : null;
        return new WebResourceResponse(
                resp.mimeType,
                encoding,
                resp.statusCode,
                resp.reasonPhrase,
                resp.headers,
                resp.bodyStream
        );
    }

    private static ProxyResponse openNativeAudioProxyResponse(
            String videoId,
            String method,
            Map<String, String> requestHeaders,
            boolean streamFullOpenRange
    ) {
        String allowOrigin = resolveAllowedOrigin(requestHeaders);
        if ("OPTIONS".equalsIgnoreCase(method)) {
            Map<String, String> headers = new HashMap<>();
            headers.put("Access-Control-Allow-Origin", allowOrigin);
            headers.put("Vary", "Origin");
            headers.put("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
            headers.put("Access-Control-Allow-Headers", "Range");
            headers.put("Access-Control-Max-Age", "3600");
            return new ProxyResponse(
                    204,
                    "No Content",
                    "text/plain",
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
        boolean midFileRequest = parsedRange != null && parsedRange[0] > 0L;
        boolean pinnedConstraints = false;
        if (streamFullOpenRange && midFileRequest) {
            String pinned = SERVED_SIGNATURE.get(videoId);
            int bar = pinned != null ? pinned.lastIndexOf('|') : -1;
            if (bar > 0) {
                expectedMime = pinned.substring(0, bar);
                expectedLength = parseLongSafe(pinned.substring(bar + 1));
                pinnedConstraints = expectedLength > 0L;
            }
        }

        for (int attempt = 0; attempt < 4; attempt++) {
            boolean forceRefresh = attempt > 0;
            boolean forceAlternate = attempt >= 1;
            ResolvedAudio audio = pinnedConstraints
                    ? resolveAudioStream(videoId, forceRefresh, forceAlternate, expectedMime, expectedLength)
                    : resolveAudioStream(videoId, forceRefresh, forceAlternate);
            if (audio == null) {
                break;
            }
            String candidateMime = cleanMimeType(null, audio.mimeType);
            if (attempt > 0 && midFileRequest) {
                // Avoid feeding bytes from a differently-encoded stream mid-file into decoder
                if (expectedMime != null && !expectedMime.equals(candidateMime)) {
                    continue;
                }
                if (expectedLength > 0L && audio.contentLength > 0L && expectedLength != audio.contentLength) {
                    continue;
                }
            }
            if (attempt == 0 && !pinnedConstraints) {
                expectedMime = candidateMime;
                expectedLength = audio.contentLength;
            }
            if (streamFullOpenRange && !midFileRequest && !isHead) {
                String sig = buildSignature(audio.mimeType, audio.contentLength);
                if (sig != null) SERVED_SIGNATURE.put(videoId, sig);
            }
            if (audio.streamUrl != null && audio.streamUrl.startsWith("sabr://")) {
                ProxyResponse sabrResp = serveSabrBufferedResponse(videoId, audio, isHead, parsedRange, allowOrigin);
                if (sabrResp != null) {
                    return sabrResp;
                }
                ClientHealthTracker.markUnhealthy("SABR");
                invalidateStream(videoId);
                continue;
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
                            ClientHealthTracker.markUnhealthy(audio.clientKind);
                            invalidateStream(videoId);
                            continue;
                        }
                        if (code < 200 || code >= 300) {
                            return buildProxyErrorResponse(code, "Upstream error", allowOrigin);
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
                        return new ProxyResponse(200, "OK",
                                cleanMimeType(upstream.header("Content-Type"), audio.mimeType),
                                respHeaders, new ByteArrayInputStream(new byte[0]));
                    }
                }

                if (parsedRange != null) {
                    long start = parsedRange[0];
                    if (audio.contentLength > 0L && start >= audio.contentLength) {
                        Map<String, String> r416 = new HashMap<>();
                        r416.put("Access-Control-Allow-Origin", allowOrigin);
                        r416.put("Vary", "Origin");
                        r416.put("Content-Range", "bytes */" + audio.contentLength);
                        return new ProxyResponse(416, "Range Not Satisfiable", "text/plain",
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
                        ClientHealthTracker.markUnhealthy(audio.clientKind);
                        invalidateStream(videoId);
                        continue;
                    }
                    if (code < 200 || code >= 300) {
                        upstream.close();
                        return buildProxyErrorResponse(code, "Upstream error", allowOrigin);
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

                    boolean isOpenEndedFull = streamFullOpenRange && parsedRange[1] < 0L;
                    String contentLength = upstream.header("Content-Length");
                    long declaredChunkLen = parseLongSafe(contentLength);
                    if (declaredChunkLen <= 0L && code == 206) {
                        declaredChunkLen = Math.max(0L, fetchEnd - start + 1L);
                    }

                    long targetEnd;
                    if (isOpenEndedFull) {
                        if (knownTotal > 0L) {
                            targetEnd = knownTotal - 1L;
                            long fullRemaining = Math.max(0L, knownTotal - start);
                            respHeaders.put("Content-Range", "bytes " + start + "-" + targetEnd + "/" + knownTotal);
                            respHeaders.put("Content-Length", String.valueOf(fullRemaining));
                        } else {
                            targetEnd = Long.MAX_VALUE;
                        }
                    } else {
                        if (parsedRange[1] >= 0L && streamFullOpenRange) {
                            long requestedEnd = parsedRange[1];
                            if (knownTotal > 0L) {
                                requestedEnd = Math.min(requestedEnd, knownTotal - 1L);
                            }
                            targetEnd = requestedEnd;
                            long respLen = Math.max(0L, targetEnd - start + 1L);
                            if (code == 206 && knownTotal > 0L) {
                                respHeaders.put("Content-Range", "bytes " + start + "-" + targetEnd + "/" + knownTotal);
                            } else if (code == 206 && contentRange != null && !contentRange.isEmpty()) {
                                respHeaders.put("Content-Range", contentRange);
                            }
                            respHeaders.put("Content-Length", String.valueOf(respLen));
                        } else {
                            if (code == 206) {
                                if (contentRange != null && !contentRange.isEmpty()) {
                                    respHeaders.put("Content-Range", contentRange);
                                } else if (knownTotal > 0L) {
                                    long rangeEnd = Math.min(fetchEnd, knownTotal - 1L);
                                    respHeaders.put("Content-Range", "bytes " + start + "-" + rangeEnd + "/" + knownTotal);
                                }
                            }
                            if (contentLength != null && !contentLength.isEmpty()) {
                                respHeaders.put("Content-Length", contentLength);
                            } else if (declaredChunkLen > 0L) {
                                respHeaders.put("Content-Length", String.valueOf(declaredChunkLen));
                            }
                            targetEnd = declaredChunkLen > 0L ? (start + declaredChunkLen - 1L) : fetchEnd;
                        }
                    }

                    String cleanMime = cleanMimeType(upstream.header("Content-Type"), audio.mimeType);
                    if (isHead) {
                        upstream.close();
                        return new ProxyResponse(code == 206 ? 206 : 200,
                                code == 206 ? "Partial Content" : "OK",
                                cleanMime, respHeaders, new ByteArrayInputStream(new byte[0]));
                    }

                    ResponseBody body = upstream.body();
                    InputStream stream = body != null
                            ? new ResilientRangeInputStream(audio, upstream, start, targetEnd, knownTotal, cleanMime, chunkCap)
                            : new ByteArrayInputStream(new byte[0]);
                    return new ProxyResponse(code == 206 ? 206 : 200,
                            code == 206 ? "Partial Content" : "OK",
                            cleanMime, respHeaders, stream);
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
                    ClientHealthTracker.markUnhealthy(audio.clientKind);
                    invalidateStream(videoId);
                    continue;
                }
                if (code < 200 || code >= 300) {
                    firstUpstream.close();
                    return buildProxyErrorResponse(code, "Upstream error", allowOrigin);
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
                if (isHead) {
                    firstUpstream.close();
                    return new ProxyResponse(200, "OK", cleanMime, respHeaders, new ByteArrayInputStream(new byte[0]));
                }
                InputStream stream = new BoundedChunkInputStream(audio, firstUpstream, totalLength, chunkCap);
                return new ProxyResponse(200, "OK", cleanMime, respHeaders, stream);
            } catch (Exception e) {
                ClientHealthTracker.markUnhealthy(audio.clientKind);
                invalidateStream(videoId);
            }
        }

        return buildProxyErrorResponse(502, "Stream unavailable", allowOrigin);
    }

    private static String cleanMimeType(String upstreamContentType, String fallbackMime) {
        if (upstreamContentType != null && !upstreamContentType.isEmpty()) {
            return upstreamContentType.contains(";")
                    ? upstreamContentType.substring(0, upstreamContentType.indexOf(';')).trim()
                    : upstreamContentType.trim();
        }
        return (fallbackMime != null && !fallbackMime.isEmpty()) ? fallbackMime : "audio/mp4";
    }

    private static ProxyResponse buildProxyErrorResponse(int statusCode, String message, String allowOrigin) {
        Map<String, String> headers = new HashMap<>();
        headers.put("Access-Control-Allow-Origin", allowOrigin);
        headers.put("Vary", "Origin");
        headers.put("Cache-Control", "no-store");
        byte[] bytes = message.getBytes(StandardCharsets.UTF_8);
        return new ProxyResponse(
                statusCode,
                "Error",
                "text/plain",
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
            if (closed || recoveryCount >= 4) return false;
            recoveryCount++;
            ClientHealthTracker.markUnhealthy(audio.clientKind);
            invalidateStream(audio.videoId);
            String requiredMime = cleanMimeType(null, audio.mimeType);
            ResolvedAudio fresh = resolveAudioStream(
                    audio.videoId,
                    true,
                    true,
                    nextOffset > 0L ? requiredMime : null,
                    nextOffset > 0L ? totalLength : -1L
            );
            if (fresh == null || fresh.streamUrl == null) return false;
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
        private long targetEnd;
        private long expectedTotalLength;
        private final String expectedMime;
        private final long chunkCap;
        private long nextOffset;
        private long currentChunkBytesRead = 0L;
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
                String expectedMime,
                long chunkCap
        ) {
            this.audio = audio;
            this.currentResponse = initialResponse;
            ResponseBody body = initialResponse.body();
            this.currentBodyStream = body != null ? body.byteStream() : new ByteArrayInputStream(new byte[0]);
            this.nextOffset = startOffset;
            this.targetEnd = targetEnd;
            this.expectedTotalLength = expectedTotalLength;
            this.expectedMime = expectedMime;
            this.chunkCap = chunkCap > 0L ? chunkCap : MAX_ADAPTIVE_CHUNK_BYTES;
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
                            currentChunkBytesRead += n;
                            return n;
                        }
                        boolean chunkProducedData = currentChunkBytesRead > 0L;
                        closeCurrentUpstream();
                        if (nextOffset <= targetEnd) {
                            if (targetEnd == Long.MAX_VALUE && !chunkProducedData) {
                                return -1;
                            }
                            if (openNextChunkOrRecover(false)) {
                                continue;
                            }
                        }
                        return -1;
                    } catch (IOException readErr) {
                        closeCurrentUpstream();
                        // A dropped/idle upstream socket is not proof the URL is dead: first retry the
                        // same URL from nextOffset, and only blacklist + re-resolve if that fails.
                        if (nextOffset <= targetEnd && openNextChunkOrRecover(false)) {
                            continue;
                        }
                        throw readErr;
                    }
                }
                return -1;
            }
        }

        private boolean openNextChunkOrRecover(boolean forceRecover) {
            if (closed || nextOffset > targetEnd) return false;

            if (!forceRecover) {
                if (tryOpenChunkOnCurrentAudio()) {
                    return true;
                }
            }

            // Mid-stream 403 / IOException: blacklist failing client and resume from exact nextOffset
            while (!closed && recoveryCount < 4) {
                recoveryCount++;
                ClientHealthTracker.markUnhealthy(audio.clientKind);
                invalidateStream(audio.videoId);
                ResolvedAudio fresh = resolveAudioStream(
                        audio.videoId,
                        true,
                        true,
                        nextOffset > 0L ? expectedMime : null,
                        nextOffset > 0L ? expectedTotalLength : -1L
                );
                if (fresh == null || fresh.streamUrl == null) {
                    return false;
                }
                this.audio = fresh;
                if (tryOpenChunkOnCurrentAudio()) {
                    return true;
                }
            }
            return false;
        }

        private boolean tryOpenChunkOnCurrentAudio() {
            if (closed || nextOffset > targetEnd) return false;
            long maxChunkEnd = nextOffset + chunkCap - 1L;
            long reqEnd = targetEnd == Long.MAX_VALUE ? maxChunkEnd : Math.min(targetEnd, maxChunkEnd);
            if (expectedTotalLength > 0L) {
                reqEnd = Math.min(reqEnd, expectedTotalLength - 1L);
            }
            if (reqEnd < nextOffset) {
                return false;
            }

            okhttp3.Request req = new okhttp3.Request.Builder()
                    .url(audio.streamUrl)
                    .get()
                    .header("User-Agent", audio.requestUserAgent)
                    .header("Range", "bytes=" + nextOffset + "-" + reqEnd)
                    .build();
            try {
                okhttp3.Response resp = HTTP_CLIENT.newCall(req).execute();
                int code = resp.code();
                if (code == 416) {
                    resp.close();
                    targetEnd = nextOffset - 1L;
                    return false;
                }
                if (code == 403 || code == 410 || code >= 500 || (nextOffset > 0L && code == 200)) {
                    resp.close();
                    return false;
                }
                if (code != 206 && !(nextOffset == 0L && code == 200)) {
                    resp.close();
                    return false;
                }
                long crTotal = parseTotalLengthFromContentRange(resp.header("Content-Range"));
                if (crTotal > 0L && expectedTotalLength <= 0L) {
                    expectedTotalLength = crTotal;
                    if (targetEnd == Long.MAX_VALUE) {
                        targetEnd = crTotal - 1L;
                    }
                }
                ResponseBody body = resp.body();
                if (body == null) {
                    resp.close();
                    return false;
                }
                this.currentResponse = resp;
                this.currentBodyStream = body.byteStream();
                this.currentChunkBytesRead = 0L;
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
