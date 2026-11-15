package com.kurfacode.kurfamuzik;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.DownloadManager;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;
import android.provider.Settings;
import android.webkit.WebView;
import android.widget.Toast;

import androidx.core.content.FileProvider;

import org.json.JSONObject;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.security.MessageDigest;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.atomic.AtomicBoolean;

import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.ResponseBody;

/**
 * Silent-first GitHub Release APK updater with automatic public Downloads export
 * so signature-mismatched installs never trap startup and always support manual one-tap reinstall.
 */
public final class ApkAutoUpdater {

    private static final String MANIFEST_URL =
            "https://github.com/KurfaCore/kurfanova-app/releases/latest/download/apk-update.json";

    private static final OkHttpClient UPDATE_CLIENT = new OkHttpClient.Builder()
            .followRedirects(true)
            .followSslRedirects(true)
            .build();

    private static final AtomicBoolean INTERACTIVE_LOCK = new AtomicBoolean(false);
    private static volatile File stagedApkFile = null;
    private static volatile String stagedVersionName = null;
    private static volatile boolean pendingInstallAfterPermission = false;

    private ApkAutoUpdater() {}

    /** Silent startup check: updates Settings label + subtle toast without popping a startup modal. */
    public static void checkInBackground(Activity activity, ExecutorService executor, WebView webView) {
        runUpdateCheck(activity, executor, webView, false);
    }

    /** Manual check from Ayarlar -> Güncelleme Denetle: downloads APK, exports to İndirilenler, prompts cleanly. */
    public static void checkInteractive(Activity activity, ExecutorService executor, WebView webView) {
        if (!INTERACTIVE_LOCK.compareAndSet(false, true)) {
            if (activity != null && !activity.isFinishing()) {
                activity.runOnUiThread(() -> Toast.makeText(activity, "Güncelleme denetimi zaten sürüyor...", Toast.LENGTH_SHORT).show());
            }
            return;
        }
        runUpdateCheck(activity, executor, webView, true);
    }

    private static void runUpdateCheck(Activity activity, ExecutorService executor, WebView webView, boolean interactive) {
        if (activity == null || executor == null || executor.isShutdown()) {
            if (interactive) INTERACTIVE_LOCK.set(false);
            return;
        }

        executor.execute(() -> {
            try {
                if (interactive) {
                    activity.runOnUiThread(() -> Toast.makeText(activity, "Güncelleme kontrol ediliyor...", Toast.LENGTH_SHORT).show());
                }

                String manifestUrl = MANIFEST_URL + "?t=" + (System.currentTimeMillis() / 1000L);
                Request req = new Request.Builder()
                        .url(manifestUrl)
                        .header("Accept", "application/json")
                        .build();

                JSONObject manifest;
                try (Response resp = UPDATE_CLIENT.newCall(req).execute()) {
                    if (!resp.isSuccessful() || resp.body() == null) {
                        if (interactive) {
                            activity.runOnUiThread(() -> Toast.makeText(activity, "Güncelleme sunucusuna ulaşılamadı.", Toast.LENGTH_SHORT).show());
                        }
                        return;
                    }
                    manifest = new JSONObject(resp.body().string());
                }

                int latestCode = manifest.optInt("versionCode", 0);
                String latestName = manifest.optString("versionName", "");
                String apkUrl = manifest.optString("url", "");
                String expectedSha256 = manifest.optString("sha256", "");

                if (latestCode <= 0 || !isTrustedReleaseUrl(apkUrl)) {
                    return;
                }

                long currentCode = getCurrentVersionCode(activity);
                if (latestCode <= currentCode) {
                    notifyWebStatus(webView, "Güncel sürümdesiniz (v" + (latestName.isEmpty() ? currentCode : latestName) + ")", false);
                    if (interactive) {
                        activity.runOnUiThread(() -> Toast.makeText(activity, "Uygulamanız zaten en güncel sürümde.", Toast.LENGTH_SHORT).show());
                    }
                    return;
                }

                notifyWebStatus(webView, "Yeni sürüm mevcut: v" + latestName + " (İndirmek için tıklayın)", !interactive);

                if (!interactive) {
                    // Never ambush user with a startup AlertDialog; let Settings button handle download & install cleanly.
                    return;
                }

                activity.runOnUiThread(() -> Toast.makeText(activity, "Kurfanova v" + latestName + " indiriliyor...", Toast.LENGTH_SHORT).show());

                File updateDir = new File(activity.getCacheDir(), "updates");
                if (!updateDir.exists()) {
                    //noinspection ResultOfMethodCallIgnored
                    updateDir.mkdirs();
                }
                String safeFileName = "KurfanovaMuzik-v" + latestName.replaceAll("[^A-Za-z0-9._-]", "") + ".apk";
                File targetApk = new File(updateDir, safeFileName);
                File tempApk = new File(updateDir, "kurfanova-update.tmp");

                if (!targetApk.exists() || !verifyApkFile(targetApk, expectedSha256)) {
                    Request apkReq = new Request.Builder().url(apkUrl).build();
                    try (Response apkResp = UPDATE_CLIENT.newCall(apkReq).execute()) {
                        if (!apkResp.isSuccessful() || apkResp.body() == null) {
                            activity.runOnUiThread(() -> Toast.makeText(activity, "APK indirilemedi.", Toast.LENGTH_SHORT).show());
                            return;
                        }
                        ResponseBody body = apkResp.body();
                        try (InputStream is = body.byteStream();
                             FileOutputStream fos = new FileOutputStream(tempApk)) {
                            byte[] buf = new byte[16384];
                            int read;
                            while ((read = is.read(buf)) != -1) {
                                fos.write(buf, 0, read);
                            }
                            fos.flush();
                        }
                    }

                    if (!verifyApkFile(tempApk, expectedSha256)) {
                        //noinspection ResultOfMethodCallIgnored
                        tempApk.delete();
                        activity.runOnUiThread(() -> Toast.makeText(activity, "İndirilen paket doğrulanamadı.", Toast.LENGTH_SHORT).show());
                        return;
                    }
                    if (targetApk.exists()) {
                        //noinspection ResultOfMethodCallIgnored
                        targetApk.delete();
                    }
                    if (!tempApk.renameTo(targetApk)) {
                        //noinspection ResultOfMethodCallIgnored
                        tempApk.delete();
                        return;
                    }
                }

                // Always export verified APK to public İndirilenler so user has 1-tap manual install fallback
                exportApkToPublicDownloads(activity, targetApk, safeFileName);

                stagedApkFile = targetApk;
                stagedVersionName = latestName.isEmpty() ? String.valueOf(latestCode) : latestName;
                showManualFriendlyUpdatePrompt(activity, stagedVersionName, targetApk, safeFileName);
            } catch (Throwable ignored) {
                if (interactive) {
                    activity.runOnUiThread(() -> Toast.makeText(activity, "Güncelleme denetlenemedi.", Toast.LENGTH_SHORT).show());
                }
            } finally {
                if (interactive) INTERACTIVE_LOCK.set(false);
            }
        });
    }

    public static void resumePendingInstallIfPermitted(Activity activity) {
        if (!pendingInstallAfterPermission || activity == null || stagedApkFile == null || !stagedApkFile.exists()) {
            return;
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !activity.getPackageManager().canRequestPackageInstalls()) {
            return;
        }
        pendingInstallAfterPermission = false;
        launchPackageInstaller(activity, stagedApkFile);
    }

    private static void showManualFriendlyUpdatePrompt(Activity activity, String versionName, File apkFile, String publicFileName) {
        if (activity == null || activity.isFinishing() || activity.isDestroyed()) return;
        activity.runOnUiThread(() -> {
            if (activity.isFinishing() || activity.isDestroyed()) return;
            try {
                new AlertDialog.Builder(activity)
                        .setTitle("Kurfanova v" + versionName + " İndirildi")
                        .setMessage(
                                "APK dosyası İndirilenler klasörüne kaydedildi:\n"
                                + publicFileName + "\n\n"
                                + "Not: Eski sürümden geçişte Android 'Paket yüklenemedi' uyarısı verirse mevcut uygulamayı kaldırıp İndirilenler'deki bu dosyaya dokunarak temiz kurulum yapabilirsiniz."
                        )
                        .setPositiveButton("Şimdi Kur", (dialog, which) -> installApk(activity, apkFile))
                        .setNeutralButton("İndirilenleri Aç", (dialog, which) -> openDownloadsFolder(activity))
                        .setNegativeButton("Kapat", null)
                        .show();
            } catch (Throwable ignored) {}
        });
    }

    private static void exportApkToPublicDownloads(Activity activity, File sourceApk, String displayName) {
        if (activity == null || sourceApk == null || !sourceApk.exists()) return;
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                ContentResolver resolver = activity.getContentResolver();
                ContentValues values = new ContentValues();
                values.put(MediaStore.Downloads.DISPLAY_NAME, displayName);
                values.put(MediaStore.Downloads.MIME_TYPE, "application/vnd.android.package-archive");
                values.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/Kurfanova");
                Uri uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
                if (uri != null) {
                    try (InputStream is = new FileInputStream(sourceApk);
                         OutputStream os = resolver.openOutputStream(uri, "w")) {
                        if (os != null) {
                            byte[] buf = new byte[16384];
                            int n;
                            while ((n = is.read(buf)) != -1) {
                                os.write(buf, 0, n);
                            }
                        }
                    }
                }
            } else {
                File dir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS);
                if (dir != null) {
                    File out = new File(dir, displayName);
                    try (InputStream is = new FileInputStream(sourceApk);
                         OutputStream os = new FileOutputStream(out)) {
                        byte[] buf = new byte[16384];
                        int n;
                        while ((n = is.read(buf)) != -1) {
                            os.write(buf, 0, n);
                        }
                    }
                }
            }
        } catch (Throwable ignored) {}
    }

    private static void openDownloadsFolder(Activity activity) {
        try {
            activity.startActivity(new Intent(DownloadManager.ACTION_VIEW_DOWNLOADS));
        } catch (Throwable t) {
            Toast.makeText(activity, "Dosya Yöneticisi > İndirilenler > Kurfanova klasörünü açın.", Toast.LENGTH_LONG).show();
        }
    }

    private static void notifyWebStatus(WebView webView, String msg, boolean notifyToast) {
        if (webView == null) return;
        String safeMsg = msg.replace("\\", "\\\\").replace("'", "\\'").replace("\n", " ");
        webView.post(() -> {
            try {
                webView.evaluateJavascript(
                        "if(typeof window.kurfaOnAndroidUpdateEvent==='function'){window.kurfaOnAndroidUpdateEvent('"
                        + safeMsg + "'," + notifyToast + ");}",
                        null
                );
            } catch (Throwable ignored) {}
        });
    }

    private static void installApk(Activity activity, File apkFile) {
        if (activity == null || apkFile == null || !apkFile.exists()) return;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !activity.getPackageManager().canRequestPackageInstalls()) {
            pendingInstallAfterPermission = true;
            try {
                Intent permIntent = new Intent(
                        Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                        Uri.parse("package:" + activity.getPackageName())
                );
                activity.startActivity(permIntent);
            } catch (Throwable t) {
                pendingInstallAfterPermission = false;
            }
            return;
        }
        launchPackageInstaller(activity, apkFile);
    }

    private static void launchPackageInstaller(Activity activity, File apkFile) {
        try {
            Uri apkUri = FileProvider.getUriForFile(
                    activity,
                    activity.getPackageName() + ".fileprovider",
                    apkFile
            );
            Intent installIntent = new Intent(Intent.ACTION_VIEW);
            installIntent.setDataAndType(apkUri, "application/vnd.android.package-archive");
            installIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            installIntent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            activity.startActivity(installIntent);
        } catch (Throwable t) {
            openDownloadsFolder(activity);
        }
    }

    @SuppressWarnings("deprecation")
    private static long getCurrentVersionCode(Activity activity) {
        try {
            PackageInfo pi = activity.getPackageManager().getPackageInfo(activity.getPackageName(), 0);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                return pi.getLongVersionCode();
            }
            return pi.versionCode;
        } catch (PackageManager.NameNotFoundException e) {
            return 0L;
        }
    }

    private static boolean isTrustedReleaseUrl(String rawUrl) {
        if (rawUrl == null || rawUrl.isEmpty()) return false;
        Uri uri = Uri.parse(rawUrl);
        if (!"https".equalsIgnoreCase(uri.getScheme())) return false;
        String host = uri.getHost();
        if (host == null) return false;
        String lower = host.toLowerCase(Locale.US);
        return lower.equals("github.com")
                || lower.endsWith(".github.com")
                || lower.equals("githubusercontent.com")
                || lower.endsWith(".githubusercontent.com");
    }

    private static boolean verifyApkFile(File file, String expectedSha256) {
        if (file == null || !file.exists() || file.length() < 1024 * 1024) {
            return false;
        }
        if (expectedSha256 == null || expectedSha256.trim().isEmpty()) {
            return true;
        }
        try {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            try (InputStream is = new FileInputStream(file)) {
                byte[] buf = new byte[16384];
                int n;
                while ((n = is.read(buf)) != -1) {
                    md.update(buf, 0, n);
                }
            }
            byte[] digest = md.digest();
            StringBuilder sb = new StringBuilder(digest.length * 2);
            for (byte b : digest) {
                sb.append(String.format(Locale.US, "%02x", b));
            }
            return sb.toString().equalsIgnoreCase(expectedSha256.trim());
        } catch (Throwable t) {
            return false;
        }
    }
}
