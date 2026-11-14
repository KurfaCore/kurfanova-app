package com.kurfacode.kurfamuzik;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.DownloadManager;
import android.content.ActivityNotFoundException;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.media.MediaScannerConnection;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.os.PowerManager;
import android.provider.MediaStore;
import android.text.TextUtils;
import android.util.Base64;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.webkit.CookieManager;
import android.webkit.DownloadListener;
import android.webkit.JavascriptInterface;
import android.webkit.MimeTypeMap;
import android.webkit.URLUtil;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class MainActivity extends Activity {

    private static final String TARGET_URL = "https://kurfanova.github.io/";
    private static final int REQUEST_FILE_CHOOSER = 1001;
    private static final int REQUEST_WRITE_STORAGE = 1002;
    private static final int MAX_DATA_DOWNLOAD_BYTES = 25 * 1024 * 1024;

    private static final Set<String> AD_HOSTS = new HashSet<>(Arrays.asList(
            "doubleclick.net",
            "googlesyndication.com",
            "googleadservices.com",
            "2mdn.net"
    ));

    private static final Set<String> IN_APP_HOSTS = new HashSet<>(Arrays.asList(
            "kurfanova.github.io",
            "kurfanova.kurfa.workers.dev",
            "accounts.google.com",
            "kurfa-ai.firebaseapp.com",
            "youtube.com",
            "youtube-nocookie.com",
            "googlevideo.com",
            "savenow.to",
            "lbserver.xyz"
    ));

    private FrameLayout rootContainer;
    private FrameLayout fullscreenContainer;
    private WebView webView;
    private View customView;
    private WebChromeClient.CustomViewCallback customViewCallback;
    private ValueCallback<Uri[]> fileUploadCallback;
    private PendingDownload pendingDownload;
    private String lastFailedUrl = TARGET_URL;
    private int originalSystemUiVisibility;
    private final ExecutorService nativeExecutor = Executors.newCachedThreadPool();
    private PowerManager.WakeLock playbackWakeLock;

    private static final class PendingDownload {
        final String url;
        final String userAgent;
        final String contentDisposition;
        final String mimetype;

        PendingDownload(String url, String userAgent, String contentDisposition, String mimetype) {
            this.url = url;
            this.userAgent = userAgent;
            this.contentDisposition = contentDisposition;
            this.mimetype = mimetype;
        }
    }

    private final class KurfaAndroidBridge {
        @JavascriptInterface
        public void retryLoad() {
            runOnUiThread(() -> {
                if (webView != null) {
                    String target = isHttpUrl(lastFailedUrl) ? lastFailedUrl : TARGET_URL;
                    webView.loadUrl(target);
                }
            });
        }

        @JavascriptInterface
        public void saveDataUrl(String dataUrl, String fileName, String mimeType) {
            saveDataUrlToDownloads(dataUrl, fileName, mimeType);
        }

        @JavascriptInterface
        public void onBlobDownloadFailed() {
            runOnUiThread(() -> Toast.makeText(
                    MainActivity.this,
                    "Dosya indirilemedi, l\u00fctfen tekrar deneyin.",
                    Toast.LENGTH_SHORT
            ).show());
        }

        @JavascriptInterface
        public boolean isNativeEngineReady() {
            return true;
        }

        @JavascriptInterface
        public void resolveAudioStreamAsync(String videoId, String requestId) {
            final String safeId = requestId == null ? "" : requestId.replaceAll("[^A-Za-z0-9_-]", "");
            if (safeId.isEmpty()) return;
            nativeExecutor.execute(() -> {
                JSONObject res = new JSONObject();
                try {
                    KurfaNativeEngine.ResolvedAudio resolved = KurfaNativeEngine.resolveAudioStream(videoId);
                    if (resolved != null) {
                        res.put("success", true);
                        res.put("videoId", resolved.videoId);
                        res.put("streamUrl", "https://kurfanova.github.io"
                                + KurfaNativeEngine.NATIVE_AUDIO_PATH_PREFIX + resolved.videoId);
                        res.put("mimeType", resolved.mimeType);
                        res.put("title", resolved.title);
                        res.put("artist", resolved.artist);
                        res.put("duration", resolved.durationSec);
                    } else {
                        res.put("success", false);
                        res.put("error", "Audio stream unavailable");
                    }
                } catch (Exception e) {
                    try {
                        res.put("success", false);
                        res.put("error", e.getMessage() != null ? e.getMessage() : "Resolve error");
                    } catch (Exception ignored) {}
                }
                dispatchJsCallback("__kurfaNativeStreamCallback", safeId, res.toString());
            });
        }

        @JavascriptInterface
        public void searchTracksAsync(String query, boolean filterSingles, String requestId) {
            final String safeId = requestId == null ? "" : requestId.replaceAll("[^A-Za-z0-9_-]", "");
            if (safeId.isEmpty()) return;
            nativeExecutor.execute(() -> {
                JSONObject res = KurfaNativeEngine.searchYouTubeLocal(query, filterSingles);
                dispatchJsCallback("__kurfaNativeSearchCallback", safeId, res.toString());
            });
        }

        @JavascriptInterface
        public void setNativePlaybackState(boolean playing) {
            runOnUiThread(() -> updatePlaybackWakeLock(playing));
        }

        @JavascriptInterface
        public void checkForUpdates() {
            ApkAutoUpdater.checkInBackground(MainActivity.this, nativeExecutor, webView);
        }
    }

    private void dispatchJsCallback(String fnName, String safeRequestId, String jsonPayload) {
        runOnUiThread(() -> {
            if (webView == null) return;
            String script = "if (typeof window." + fnName + " === 'function') { try { window."
                    + fnName + "(" + JSONObject.quote(safeRequestId) + ", JSON.parse("
                    + JSONObject.quote(jsonPayload) + ")); } catch(e) {} }";
            webView.evaluateJavascript(script, null);
        });
    }

    @SuppressLint("WakelockTimeout")
    private void updatePlaybackWakeLock(boolean active) {
        try {
            if (active) {
                if (playbackWakeLock == null) {
                    PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
                    if (pm != null) {
                        playbackWakeLock = pm.newWakeLock(
                                PowerManager.PARTIAL_WAKE_LOCK,
                                "KurfaMuzik::PlaybackWakeLock"
                        );
                        playbackWakeLock.setReferenceCounted(false);
                    }
                }
                if (playbackWakeLock != null && !playbackWakeLock.isHeld()) {
                    playbackWakeLock.acquire();
                }
            } else if (playbackWakeLock != null && playbackWakeLock.isHeld()) {
                playbackWakeLock.release();
            }
        } catch (Exception ignored) {}
    }

    @SuppressLint("SetJavaScriptEnabled")
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        Window window = getWindow();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
            window.addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS);
            window.setStatusBarColor(Color.parseColor("#07090e"));
            window.setNavigationBarColor(Color.parseColor("#07090e"));
        }

        rootContainer = new FrameLayout(this);
        rootContainer.setBackgroundColor(Color.parseColor("#07090e"));

        webView = new WebView(this);
        rootContainer.addView(webView, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
        ));
        setContentView(rootContainer);

        configureCookies(webView);
        configureWebSettings(webView);

        webView.setScrollBarStyle(View.SCROLLBARS_INSIDE_OVERLAY);
        webView.setBackgroundColor(Color.parseColor("#07090e"));
        webView.setLayerType(View.LAYER_TYPE_HARDWARE, null);
        webView.addJavascriptInterface(new KurfaAndroidBridge(), "KurfaAndroidBridge");

        webView.setWebChromeClient(new KurfaWebChromeClient());
        webView.setWebViewClient(new KurfaWebViewClient());
        webView.setDownloadListener(createDownloadListener());

        webView.loadUrl(TARGET_URL);
        ApkAutoUpdater.checkInBackground(this, nativeExecutor, webView);
    }

    private void configureCookies(WebView view) {
        CookieManager cookieManager = CookieManager.getInstance();
        cookieManager.setAcceptCookie(true);
        cookieManager.setAcceptThirdPartyCookies(view, true);
    }

    @SuppressLint("SetJavaScriptEnabled")
    private void configureWebSettings(WebView view) {
        WebSettings settings = view.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setDatabaseEnabled(true);
        settings.setMediaPlaybackRequiresUserGesture(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setAllowFileAccess(false);
        settings.setAllowContentAccess(false);
        settings.setCacheMode(WebSettings.LOAD_DEFAULT);
        settings.setUseWideViewPort(true);
        settings.setLoadWithOverviewMode(true);
        settings.setSupportZoom(false);
        settings.setBuiltInZoomControls(false);
    }

    private static boolean isHttpUrl(String url) {
        if (url == null) return false;
        String lower = url.toLowerCase(Locale.ROOT);
        return lower.startsWith("http://") || lower.startsWith("https://");
    }

    private static boolean matchesHostOrSubdomain(String host, String domain) {
        return host.equals(domain) || host.endsWith("." + domain);
    }

    private boolean isAdRequest(Uri uri) {
        if (uri == null) return false;
        String scheme = uri.getScheme();
        if (scheme == null) return false;
        String lowerScheme = scheme.toLowerCase(Locale.ROOT);
        if (!"http".equals(lowerScheme) && !"https".equals(lowerScheme)) {
            return false;
        }

        String host = uri.getHost();
        if (host == null || host.isEmpty()) return false;
        String lowerHost = host.toLowerCase(Locale.ROOT);

        // Never interfere with media streams, app origin, or Google Auth
        if (matchesHostOrSubdomain(lowerHost, "googlevideo.com")
                || matchesHostOrSubdomain(lowerHost, "kurfanova.github.io")
                || matchesHostOrSubdomain(lowerHost, "kurfanova.kurfa.workers.dev")
                || matchesHostOrSubdomain(lowerHost, "accounts.google.com")) {
            return false;
        }

        for (String adHost : AD_HOSTS) {
            if (matchesHostOrSubdomain(lowerHost, adHost)) {
                return true;
            }
        }

        if (lowerHost.startsWith("adservice.google.")) {
            return true;
        }

        if (matchesHostOrSubdomain(lowerHost, "youtube.com")
                || matchesHostOrSubdomain(lowerHost, "youtube-nocookie.com")) {
            String path = uri.getPath();
            if (path != null) {
                String lowerPath = path.toLowerCase(Locale.ROOT);
                return lowerPath.equals("/pagead")
                        || lowerPath.startsWith("/pagead/")
                        || lowerPath.startsWith("/api/stats/ads")
                        || lowerPath.equals("/ptracking")
                        || lowerPath.startsWith("/ptracking/")
                        || lowerPath.equals("/get_midroll_info")
                        || lowerPath.startsWith("/get_midroll_info/");
            }
        }

        return false;
    }

    private boolean isInAppHost(String host) {
        if (host == null || host.isEmpty()) return false;
        String lowerHost = host.toLowerCase(Locale.ROOT);
        for (String allowed : IN_APP_HOSTS) {
            if (matchesHostOrSubdomain(lowerHost, allowed)) {
                return true;
            }
        }
        return false;
    }

    private boolean isApkDownloadUri(Uri uri) {
        String host = uri.getHost() != null ? uri.getHost().toLowerCase(Locale.ROOT) : "";
        String path = uri.getPath() != null ? uri.getPath().toLowerCase(Locale.ROOT) : "";
        if (path.endsWith(".apk")) {
            return true;
        }
        if (matchesHostOrSubdomain(host, "github.com")
                || matchesHostOrSubdomain(host, "githubusercontent.com")) {
            return uri.toString().toLowerCase(Locale.ROOT).contains(".apk");
        }
        return false;
    }

    private boolean handleUrlNavigation(Uri uri, boolean isMainFrame, boolean hasGesture) {
        if (uri == null) return false;
        String scheme = uri.getScheme() != null ? uri.getScheme().toLowerCase(Locale.ROOT) : "";

        if ("kurfanova".equals(scheme) && "retry".equalsIgnoreCase(uri.getHost())) {
            String target = isHttpUrl(lastFailedUrl) ? lastFailedUrl : TARGET_URL;
            if (webView != null) {
                webView.loadUrl(target);
            }
            return true;
        }

        if ("about".equals(scheme) || "blob".equals(scheme) || "data".equals(scheme) || "javascript".equals(scheme)) {
            return false;
        }

        if (!"http".equals(scheme) && !"https".equals(scheme)) {
            if ("intent".equals(scheme)) {
                handleIntentScheme(uri.toString());
            } else {
                openExternalUri(uri);
            }
            return true;
        }

        if (isAdRequest(uri)) {
            return true;
        }

        if (isApkDownloadUri(uri)) {
            openExternalUri(uri);
            return true;
        }

        String host = uri.getHost();
        if (isInAppHost(host)) {
            return false;
        }

        // Allow internal subframe initialization unless the user explicitly tapped a link
        if (!isMainFrame && !hasGesture) {
            return false;
        }

        openExternalUri(uri);
        return true;
    }

    private void handleIntentScheme(String rawUrl) {
        try {
            Intent intent = Intent.parseUri(rawUrl, Intent.URI_INTENT_SCHEME);
            if (intent == null) return;
            intent.addCategory(Intent.CATEGORY_BROWSABLE);
            intent.setComponent(null);
            intent.setSelector(null);
            try {
                startActivity(intent);
            } catch (ActivityNotFoundException | SecurityException e) {
                String fallbackUrl = intent.getStringExtra("browser_fallback_url");
                if (fallbackUrl != null && isHttpUrl(fallbackUrl)) {
                    handleUrlNavigation(Uri.parse(fallbackUrl), true, true);
                }
            }
        } catch (URISyntaxException ignored) {
        }
    }

    private void openExternalUri(Uri uri) {
        if (uri == null) return;
        try {
            Intent intent = new Intent(Intent.ACTION_VIEW, uri);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            startActivity(intent);
        } catch (ActivityNotFoundException | SecurityException ignored) {
        }
    }

    private WebResourceResponse createEmptyResponse() {
        return new WebResourceResponse(
                "text/plain",
                "UTF-8",
                new ByteArrayInputStream(new byte[0])
        );
    }

    private DownloadListener createDownloadListener() {
        return (url, userAgent, contentDisposition, mimetype, contentLength) -> {
            if (url == null || url.trim().isEmpty()) {
                return;
            }
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q
                    && checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE)
                    != PackageManager.PERMISSION_GRANTED) {
                pendingDownload = new PendingDownload(url, userAgent, contentDisposition, mimetype);
                requestPermissions(
                        new String[]{Manifest.permission.WRITE_EXTERNAL_STORAGE},
                        REQUEST_WRITE_STORAGE
                );
                return;
            }
            executeDownload(url, userAgent, contentDisposition, mimetype);
        };
    }

    private void executeDownload(String url, String userAgent, String contentDisposition, String mimetype) {
        String trimmed = url.trim();
        if (trimmed.startsWith("blob:")) {
            downloadBlobUrl(trimmed, contentDisposition, mimetype);
            return;
        }
        if (trimmed.startsWith("data:")) {
            String fileName = guessSafeFileName(trimmed, contentDisposition, mimetype, "kurfanova-dosya");
            saveDataUrlToDownloads(trimmed, fileName, mimetype);
            return;
        }
        if (!isHttpUrl(trimmed)) {
            openExternalUri(Uri.parse(trimmed));
            return;
        }

        String fileName = guessSafeFileName(trimmed, contentDisposition, mimetype, "kurfanova-indirilen");
        try {
            DownloadManager downloadManager = (DownloadManager) getSystemService(DOWNLOAD_SERVICE);
            if (downloadManager == null) {
                openExternalUri(Uri.parse(trimmed));
                return;
            }
            DownloadManager.Request request = new DownloadManager.Request(Uri.parse(trimmed));
            if (mimetype != null && !mimetype.trim().isEmpty()) {
                request.setMimeType(mimetype);
            }
            String cookies = CookieManager.getInstance().getCookie(trimmed);
            if (cookies != null && !cookies.isEmpty()) {
                request.addRequestHeader("Cookie", cookies);
            }
            if (userAgent != null && !userAgent.isEmpty()) {
                request.addRequestHeader("User-Agent", userAgent);
            }
            request.setTitle(fileName);
            request.setDescription("Kurfanova M\u00fczik indiriliyor");
            request.setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED);
            request.setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, fileName);
            downloadManager.enqueue(request);
            Toast.makeText(this, "\u0130ndirme ba\u015flat\u0131ld\u0131: " + fileName, Toast.LENGTH_SHORT).show();
        } catch (Exception e) {
            openExternalUri(Uri.parse(trimmed));
        }
    }

    private void downloadBlobUrl(String blobUrl, String contentDisposition, String mimetype) {
        if (webView == null) return;
        String fileName = guessSafeFileName(blobUrl, contentDisposition, mimetype, "kurfamuzik-yedek.json");
        String safeMime = (mimetype != null && !mimetype.trim().isEmpty()) ? mimetype.trim() : "application/octet-stream";
        String script = "(function(){"
                + "fetch(" + JSONObject.quote(blobUrl) + ")"
                + ".then(function(r){if(!r.ok){throw new Error('fetch');}return r.blob();})"
                + ".then(function(b){"
                + "var fr=new FileReader();"
                + "fr.onloadend=function(){"
                + "if(window.KurfaAndroidBridge&&typeof fr.result==='string'){"
                + "window.KurfaAndroidBridge.saveDataUrl(fr.result," + JSONObject.quote(fileName) + "," + JSONObject.quote(safeMime) + ");"
                + "}"
                + "};"
                + "fr.readAsDataURL(b);"
                + "})"
                + ".catch(function(){"
                + "if(window.KurfaAndroidBridge){window.KurfaAndroidBridge.onBlobDownloadFailed();}"
                + "});"
                + "})();";
        webView.evaluateJavascript(script, null);
    }

    private String guessSafeFileName(String url, String contentDisposition, String mimetype, String fallbackBase) {
        String guessed = URLUtil.guessFileName(url, contentDisposition, mimetype);
        if (TextUtils.isEmpty(guessed)
                || guessed.startsWith("downloadfile")
                || guessed.equals(".bin")) {
            String ext = null;
            if (mimetype != null && !mimetype.trim().isEmpty()) {
                ext = MimeTypeMap.getSingleton().getExtensionFromMimeType(mimetype.trim().toLowerCase(Locale.ROOT));
            }
            if ("application/json".equalsIgnoreCase(mimetype) || "json".equalsIgnoreCase(ext)) {
                guessed = "kurfamuzik-yedek.json";
            } else if (!TextUtils.isEmpty(ext) && !fallbackBase.endsWith("." + ext)) {
                guessed = fallbackBase + "." + ext;
            } else {
                guessed = fallbackBase;
            }
        }
        String sanitized = sanitizeFileName(guessed);
        return sanitized.isEmpty() ? "kurfanova-dosya" : sanitized;
    }

    private void saveDataUrlToDownloads(String dataUrl, String rawFileName, String fallbackMime) {
        if (dataUrl == null || !dataUrl.startsWith("data:")) {
            return;
        }
        int commaIndex = dataUrl.indexOf(',');
        if (commaIndex <= 5) {
            return;
        }

        String header = dataUrl.substring(5, commaIndex);
        String payload = dataUrl.substring(commaIndex + 1);
        String lowerHeader = header.toLowerCase(Locale.ROOT);
        boolean isBase64 = lowerHeader.endsWith(";base64") || lowerHeader.equals("base64");

        String extractedMime = header;
        int semiIndex = header.indexOf(';');
        if (semiIndex >= 0) {
            extractedMime = header.substring(0, semiIndex);
        }
        String resolvedMime = !TextUtils.isEmpty(extractedMime)
                ? extractedMime
                : (!TextUtils.isEmpty(fallbackMime) ? fallbackMime : "application/octet-stream");

        byte[] bytes;
        try {
            if (isBase64) {
                bytes = Base64.decode(payload, Base64.DEFAULT);
            } else {
                bytes = Uri.decode(payload).getBytes(StandardCharsets.UTF_8);
            }
        } catch (IllegalArgumentException e) {
            return;
        }

        if (bytes.length == 0 || bytes.length > MAX_DATA_DOWNLOAD_BYTES) {
            return;
        }

        String fileName = sanitizeFileName(rawFileName);
        if (TextUtils.isEmpty(fileName)) {
            fileName = "kurfanova-dosya";
        }
        if ("application/json".equalsIgnoreCase(resolvedMime) && !fileName.toLowerCase(Locale.ROOT).endsWith(".json")) {
            fileName = fileName + ".json";
        } else if (!fileName.contains(".")) {
            String ext = MimeTypeMap.getSingleton().getExtensionFromMimeType(resolvedMime.toLowerCase(Locale.ROOT));
            if (!TextUtils.isEmpty(ext)) {
                fileName = fileName + "." + ext;
            }
        }

        boolean saved;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            saved = writeDownloadMediaStore(bytes, fileName, resolvedMime);
        } else {
            saved = writeDownloadLegacy(bytes, fileName, resolvedMime);
        }

        final String finalName = fileName;
        final boolean wasSaved = saved;
        runOnUiThread(() -> Toast.makeText(
                MainActivity.this,
                wasSaved ? ("\u0130ndirildi: " + finalName) : "Dosya kaydedilemedi.",
                Toast.LENGTH_SHORT
        ).show());
    }

    private static String sanitizeFileName(String raw) {
        if (raw == null) return "";
        String sanitized = raw.replaceAll("[\\\\/:*?\"<>|\\r\\n\\t]", "_").trim();
        while (sanitized.startsWith(".")) {
            sanitized = sanitized.substring(1);
        }
        return sanitized;
    }

    private boolean writeDownloadMediaStore(byte[] bytes, String fileName, String mimeType) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            return false;
        }
        ContentResolver resolver = getContentResolver();
        ContentValues values = new ContentValues();
        values.put(MediaStore.Downloads.DISPLAY_NAME, fileName);
        values.put(MediaStore.Downloads.MIME_TYPE, mimeType);
        values.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS);
        values.put(MediaStore.Downloads.IS_PENDING, 1);

        Uri itemUri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
        if (itemUri == null) {
            return false;
        }
        try (OutputStream out = resolver.openOutputStream(itemUri)) {
            if (out == null) {
                resolver.delete(itemUri, null, null);
                return false;
            }
            out.write(bytes);
            out.flush();
            values.clear();
            values.put(MediaStore.Downloads.IS_PENDING, 0);
            resolver.update(itemUri, values, null, null);
            return true;
        } catch (IOException | SecurityException e) {
            resolver.delete(itemUri, null, null);
            return false;
        }
    }

    @SuppressWarnings("deprecation")
    private boolean writeDownloadLegacy(byte[] bytes, String fileName, String mimeType) {
        if (checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE) != PackageManager.PERMISSION_GRANTED) {
            return false;
        }
        File downloadsDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS);
        if (downloadsDir == null) {
            return false;
        }
        if (!downloadsDir.exists() && !downloadsDir.mkdirs()) {
            return false;
        }
        File targetFile = resolveUniqueFile(downloadsDir, fileName);
        try (FileOutputStream fos = new FileOutputStream(targetFile)) {
            fos.write(bytes);
            fos.flush();
        } catch (IOException | SecurityException e) {
            return false;
        }
        MediaScannerConnection.scanFile(
                this,
                new String[]{targetFile.getAbsolutePath()},
                new String[]{mimeType},
                null
        );
        return true;
    }

    private static File resolveUniqueFile(File directory, String fileName) {
        File candidate = new File(directory, fileName);
        if (!candidate.exists()) {
            return candidate;
        }
        int dot = fileName.lastIndexOf('.');
        String base = dot > 0 ? fileName.substring(0, dot) : fileName;
        String ext = dot > 0 ? fileName.substring(dot) : "";
        for (int i = 1; i < 1000; i++) {
            File next = new File(directory, base + " (" + i + ")" + ext);
            if (!next.exists()) {
                return next;
            }
        }
        return new File(directory, base + "_" + System.currentTimeMillis() + ext);
    }

    private void showOfflineErrorPage(WebView view, int errorCode, String description) {
        String safeCode = escapeHtml(buildErrorSummary(errorCode, description));
        String html = "<!DOCTYPE html>"
                + "<html lang=\"tr\">"
                + "<head>"
                + "<meta charset=\"UTF-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1.0,maximum-scale=1.0,user-scalable=no\">"
                + "<title>Baglanti Kurulamadi</title>"
                + "<style>"
                + "*{box-sizing:border-box;margin:0;padding:0}"
                + "html,body{height:100%;background:#07090e;color:#f3f4f6;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;-webkit-font-smoothing:antialiased}"
                + ".wrap{min-height:100%;display:flex;align-items:center;justify-content:center;padding:24px}"
                + ".card{width:100%;max-width:380px;background:#0e121a;border:1px solid rgba(255,255,255,0.10);border-radius:24px;padding:32px 24px;text-align:center;box-shadow:0 24px 60px rgba(0,0,0,0.55)}"
                + ".icon-box{width:64px;height:64px;margin:0 auto 20px;border-radius:18px;background:rgba(255,32,86,0.12);border:1px solid rgba(255,32,86,0.28);display:flex;align-items:center;justify-content:center}"
                + "h1{font-size:20px;font-weight:700;letter-spacing:-0.01em;margin-bottom:10px;color:#ffffff}"
                + "p{font-size:14px;line-height:1.55;color:#9ca3af;margin-bottom:18px}"
                + ".err-code{display:inline-block;padding:5px 10px;border-radius:99px;background:rgba(255,255,255,0.05);border:1px solid rgba(255,255,255,0.08);font-family:monospace;font-size:11px;color:#9ca3af;margin-bottom:22px}"
                + ".btn{width:100%;height:48px;border:none;border-radius:14px;background:#ff2056;color:#ffffff;font-size:15px;font-weight:700;cursor:pointer;letter-spacing:0.01em}"
                + ".btn:active{opacity:0.88;transform:scale(0.98)}"
                + "</style>"
                + "</head>"
                + "<body>"
                + "<div class=\"wrap\">"
                + "<div class=\"card\">"
                + "<div class=\"icon-box\">"
                + "<svg width=\"30\" height=\"30\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"#ff2056\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">"
                + "<line x1=\"1\" y1=\"1\" x2=\"23\" y2=\"23\"></line>"
                + "<path d=\"M16.72 11.06A10.94 10.94 0 0 1 19 12.55\"></path>"
                + "<path d=\"M5 12.55a10.94 10.94 0 0 1 5.17-2.39\"></path>"
                + "<path d=\"M10.71 5.05A16 16 0 0 1 22.58 9\"></path>"
                + "<path d=\"M1.42 9a15.91 15.91 0 0 1 4.7-2.88\"></path>"
                + "<path d=\"M8.53 16.11a6 6 0 0 1 6.95 0\"></path>"
                + "<line x1=\"12\" y1=\"20\" x2=\"12.01\" y2=\"20\"></line>"
                + "</svg>"
                + "</div>"
                + "<h1>Ba\u011flant\u0131 Kurulamad\u0131</h1>"
                + "<p>\u0130nternet ba\u011flant\u0131n\u0131z kesilmi\u015f veya Kurfanova sunucusuna \u015fu anda ula\u015f\u0131lam\u0131yor. Ba\u011flant\u0131n\u0131z\u0131 kontrol edip tekrar deneyin.</p>"
                + "<div class=\"err-code\">" + safeCode + "</div>"
                + "<button type=\"button\" class=\"btn\" onclick=\"retryConnection()\">Tekrar Dene</button>"
                + "</div>"
                + "</div>"
                + "<script>"
                + "function retryConnection(){"
                + "if(window.KurfaAndroidBridge&&typeof window.KurfaAndroidBridge.retryLoad==='function'){"
                + "window.KurfaAndroidBridge.retryLoad();"
                + "}else{"
                + "window.location.href='kurfanova://retry';"
                + "}"
                + "}"
                + "</script>"
                + "</body>"
                + "</html>";
        view.loadDataWithBaseURL(null, html, "text/html", "UTF-8", null);
    }

    private static String buildErrorSummary(int errorCode, String description) {
        if (description != null && !description.trim().isEmpty()) {
            return description.trim() + " (" + errorCode + ")";
        }
        return "Hata Kodu: " + errorCode;
    }

    private static String escapeHtml(String input) {
        if (input == null) return "";
        return input.replace("&", "&amp;")
                .replace("<", "&lt;")
                .replace(">", "&gt;")
                .replace("\"", "&quot;")
                .replace("'", "&#39;");
    }

    private void hideCustomView() {
        if (customView == null) {
            return;
        }
        if (fullscreenContainer != null) {
            fullscreenContainer.removeAllViews();
            if (rootContainer != null) {
                rootContainer.removeView(fullscreenContainer);
            }
            fullscreenContainer = null;
        }
        exitImmersiveMode();
        if (customViewCallback != null) {
            try {
                customViewCallback.onCustomViewHidden();
            } catch (Exception ignored) {
            }
            customViewCallback = null;
        }
        customView = null;
    }

    private void enterImmersiveMode() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            WindowInsetsController controller = getWindow().getInsetsController();
            if (controller != null) {
                controller.hide(WindowInsets.Type.statusBars() | WindowInsets.Type.navigationBars());
                controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            }
        } else {
            enterImmersiveLegacy();
        }
    }

    private void exitImmersiveMode() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            WindowInsetsController controller = getWindow().getInsetsController();
            if (controller != null) {
                controller.show(WindowInsets.Type.statusBars() | WindowInsets.Type.navigationBars());
            }
        } else {
            exitImmersiveLegacy();
        }
    }

    @SuppressWarnings("deprecation")
    private void enterImmersiveLegacy() {
        View decor = getWindow().getDecorView();
        originalSystemUiVisibility = decor.getSystemUiVisibility();
        decor.setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                        | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
        );
    }

    @SuppressWarnings("deprecation")
    private void exitImmersiveLegacy() {
        getWindow().getDecorView().setSystemUiVisibility(originalSystemUiVisibility);
    }

    private final class KurfaWebChromeClient extends WebChromeClient {
        @Override
        public void onShowCustomView(View view, CustomViewCallback callback) {
            if (customView != null) {
                hideCustomView();
            }
            customView = view;
            customViewCallback = callback;

            fullscreenContainer = new FrameLayout(MainActivity.this);
            fullscreenContainer.setBackgroundColor(Color.BLACK);
            fullscreenContainer.setClickable(true);
            fullscreenContainer.setFocusable(true);
            fullscreenContainer.addView(view, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    Gravity.CENTER
            ));
            rootContainer.addView(fullscreenContainer, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.MATCH_PARENT
            ));
            enterImmersiveMode();
        }

        @Override
        public void onHideCustomView() {
            hideCustomView();
        }

        @Override
        public Bitmap getDefaultVideoPoster() {
            return Bitmap.createBitmap(1, 1, Bitmap.Config.ARGB_8888);
        }

        @Override
        public boolean onShowFileChooser(
                WebView view,
                ValueCallback<Uri[]> filePathCallback,
                FileChooserParams fileChooserParams
        ) {
            if (fileUploadCallback != null) {
                fileUploadCallback.onReceiveValue(null);
            }
            fileUploadCallback = filePathCallback;
            try {
                Intent intent = fileChooserParams.createIntent();
                startActivityForResult(intent, REQUEST_FILE_CHOOSER);
                return true;
            } catch (ActivityNotFoundException | SecurityException e) {
                fileUploadCallback = null;
                return false;
            }
        }
    }

    private final class KurfaWebViewClient extends WebViewClient {
        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
            if (request == null || request.getUrl() == null) {
                return false;
            }
            return handleUrlNavigation(request.getUrl(), request.isForMainFrame(), request.hasGesture());
        }

        @SuppressWarnings("deprecation")
        @Override
        public boolean shouldOverrideUrlLoading(WebView view, String url) {
            if (url == null || url.isEmpty()) {
                return false;
            }
            return handleUrlNavigation(Uri.parse(url), true, true);
        }

        @Override
        public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
            if (request != null && request.getUrl() != null) {
                String nativeVid = KurfaNativeEngine.extractVideoIdFromNativeAudioUri(request.getUrl());
                if (nativeVid != null) {
                    return KurfaNativeEngine.handleNativeAudioIntercept(
                            nativeVid,
                            request.getMethod(),
                            request.getRequestHeaders()
                    );
                }
                if (isAdRequest(request.getUrl())) {
                    return createEmptyResponse();
                }
            }
            return super.shouldInterceptRequest(view, request);
        }

        @SuppressWarnings("deprecation")
        @Override
        public WebResourceResponse shouldInterceptRequest(WebView view, String url) {
            if (isHttpUrl(url)) {
                Uri parsed = Uri.parse(url);
                String nativeVid = KurfaNativeEngine.extractVideoIdFromNativeAudioUri(parsed);
                if (nativeVid != null) {
                    return KurfaNativeEngine.handleNativeAudioIntercept(nativeVid, "GET", null);
                }
                if (isAdRequest(parsed)) {
                    return createEmptyResponse();
                }
            }
            return super.shouldInterceptRequest(view, url);
        }

        @Override
        public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
            if (request == null || !request.isForMainFrame() || request.getUrl() == null) {
                return;
            }
            Uri failedUri = request.getUrl();
            String urlString = failedUri.toString();
            if (!isHttpUrl(urlString)) {
                return;
            }
            String desc = (error != null && error.getDescription() != null)
                    ? error.getDescription().toString() : "";
            if ("net::ERR_ABORTED".equalsIgnoreCase(desc)) {
                return;
            }
            lastFailedUrl = urlString;
            hideCustomView();
            int code = error != null ? error.getErrorCode() : ERROR_UNKNOWN;
            showOfflineErrorPage(view, code, desc);
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQUEST_FILE_CHOOSER && fileUploadCallback != null) {
            Uri[] results = WebChromeClient.FileChooserParams.parseResult(resultCode, data);
            fileUploadCallback.onReceiveValue(results);
            fileUploadCallback = null;
        }
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == REQUEST_WRITE_STORAGE && pendingDownload != null) {
            PendingDownload queued = pendingDownload;
            pendingDownload = null;
            if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
                executeDownload(queued.url, queued.userAgent, queued.contentDisposition, queued.mimetype);
            } else if (isHttpUrl(queued.url)) {
                openExternalUri(Uri.parse(queued.url));
            } else {
                Toast.makeText(this, "\u0130ndirme i\u00e7in depolama izni verilmedi.", Toast.LENGTH_SHORT).show();
            }
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (webView != null) {
            webView.onResume();
            webView.resumeTimers();
        }
        ApkAutoUpdater.resumePendingInstallIfPermitted(this);
    }

    @Override
    protected void onPause() {
        CookieManager.getInstance().flush();
        // Intentionally avoid webView.onPause() and webView.pauseTimers() so background playback continues
        super.onPause();
    }

    @Override
    public void onBackPressed() {
        if (customView != null) {
            hideCustomView();
            return;
        }
        if (webView != null && webView.canGoBack()) {
            webView.goBack();
        } else {
            super.onBackPressed();
        }
    }

    @Override
    protected void onDestroy() {
        updatePlaybackWakeLock(false);
        nativeExecutor.shutdownNow();
        hideCustomView();
        CookieManager.getInstance().flush();
        if (webView != null) {
            if (rootContainer != null) {
                rootContainer.removeView(webView);
            }
            webView.removeJavascriptInterface("KurfaAndroidBridge");
            webView.setWebChromeClient(null);
            webView.setWebViewClient(null);
            webView.setDownloadListener(null);
            webView.destroy();
            webView = null;
        }
        super.onDestroy();
    }
}
