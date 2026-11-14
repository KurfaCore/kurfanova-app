package com.kurfacode.kurfamuzik;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;
import android.webkit.WebView;
import android.widget.Toast;

import androidx.core.content.FileProvider;

import org.json.JSONObject;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.security.MessageDigest;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.ResponseBody;

public final class ApkAutoUpdater {

    private static final String MANIFEST_URL =
            "https://github.com/KurfaCore/kurfanova-app/releases/latest/download/apk-update.json";

    private static final OkHttpClient UPDATE_CLIENT = new OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(120, TimeUnit.SECONDS)
            .followRedirects(true)
            .followSslRedirects(true)
            .build();

    private static final AtomicBoolean PROMPT_SHOWN = new AtomicBoolean(false);
    private static volatile File stagedApkFile = null;
    private static volatile String stagedVersionName = null;
    private static volatile boolean pendingInstallAfterPermission = false;

    private ApkAutoUpdater() {}

    public static void checkInBackground(Activity activity, ExecutorService executor, WebView webView) {
        if (activity == null || executor == null) return;
        executor.execute(() -> {
            try {
                Thread.sleep(3800);
                if (activity.isFinishing() || activity.isDestroyed()) return;

                long currentCode = getCurrentVersionCode(activity);
                String manifestUrl = MANIFEST_URL + "?t=" + (System.currentTimeMillis() / 1000L);
                Request req = new Request.Builder()
                        .url(manifestUrl)
                        .header("User-Agent", "KurfaMuzik-AndroidUpdater/1.2")
                        .build();

                String rawJson;
                try (Response res = UPDATE_CLIENT.newCall(req).execute()) {
                    if (!res.isSuccessful() || res.body() == null) return;
                    rawJson = res.body().string();
                }

                JSONObject json = new JSONObject(rawJson);
                long remoteCode = json.optLong("versionCode", 0L);
                String remoteName = json.optString("versionName", "").trim();
                String downloadUrl = json.optString("url", "").trim();
                String expectedSha = json.optString("sha256", "").trim().toLowerCase(Locale.ROOT);

                if (remoteCode <= currentCode || expectedSha.length() != 64 || !isTrustedReleaseUrl(downloadUrl)) {
                    return;
                }

                File updatesDir = new File(activity.getCacheDir(), "updates");
                if (!updatesDir.exists() && !updatesDir.mkdirs()) return;

                File targetApk = new File(updatesDir, "Kurfanova-Muzik-" + remoteCode + ".apk");
                File partApk = new File(updatesDir, "Kurfanova-Muzik-" + remoteCode + ".apk.part");

                if (!targetApk.exists() || !verifyApkFile(targetApk, expectedSha)) {
                    Request dlReq = new Request.Builder()
                            .url(downloadUrl)
                            .header("User-Agent", "KurfaMuzik-AndroidUpdater/1.2")
                            .build();
                    try (Response dlRes = UPDATE_CLIENT.newCall(dlReq).execute()) {
                        if (!dlRes.isSuccessful()) return;
                        ResponseBody body = dlRes.body();
                        if (body == null) return;
                        try (InputStream is = body.byteStream();
                             FileOutputStream fos = new FileOutputStream(partApk)) {
                            byte[] buf = new byte[16384];
                            int read;
                            while ((read = is.read(buf)) != -1) {
                                fos.write(buf, 0, read);
                            }
                            fos.flush();
                        }
                    }

                    if (!verifyApkFile(partApk, expectedSha)) {
                        //noinspection ResultOfMethodCallIgnored
                        partApk.delete();
                        return;
                    }
                    if (targetApk.exists()) {
                        //noinspection ResultOfMethodCallIgnored
                        targetApk.delete();
                    }
                    if (!partApk.renameTo(targetApk)) {
                        return;
                    }
                }

                stagedApkFile = targetApk;
                stagedVersionName = remoteName.isEmpty() ? String.valueOf(remoteCode) : remoteName;

                if (PROMPT_SHOWN.compareAndSet(false, true)) {
                    activity.runOnUiThread(() -> showUpdatePrompt(activity, webView, stagedVersionName, stagedApkFile));
                }
            } catch (Throwable ignored) {
                // Keep playback undisturbed when offline or interrupted
            }
        });
    }

    public static void resumePendingInstallIfPermitted(Activity activity) {
        if (!pendingInstallAfterPermission || activity == null || stagedApkFile == null || !stagedApkFile.exists()) {
            return;
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            if (!activity.getPackageManager().canRequestPackageInstalls()) return;
        }
        pendingInstallAfterPermission = false;
        launchPackageInstaller(activity, stagedApkFile);
        stagedApkFile = null;
    }

    private static void showUpdatePrompt(Activity activity, WebView webView, String versionName, File apkFile) {
        if (activity.isFinishing() || activity.isDestroyed() || apkFile == null || !apkFile.exists()) return;

        if (webView != null) {
            String js = "if (typeof window.__kurfaOnNativeUpdateReady === 'function') { window.__kurfaOnNativeUpdateReady({ version: "
                    + JSONObject.quote(versionName) + ", platform: 'android' }); }";
            webView.evaluateJavascript(js, null);
        }

        new AlertDialog.Builder(activity)
                .setTitle("Kurfamüzik Yeni Sürüm Hazır")
                .setMessage("Kurfamüzik v" + versionName + " arka planda indirildi ve doğrulandı.\n\nŞimdi güncellemeyi yüklemek ister misiniz?")
                .setCancelable(true)
                .setPositiveButton("Şimdi Yükle", (dialog, which) -> installApk(activity, apkFile))
                .setNegativeButton("Sonra", null)
                .show();
    }

    private static void installApk(Activity activity, File apkFile) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
                !activity.getPackageManager().canRequestPackageInstalls()) {
            pendingInstallAfterPermission = true;
            Toast.makeText(
                    activity,
                    "Güncelleme için 'Bu kaynaktan izin ver' seçeneğini açın ve geri dönün.",
                    Toast.LENGTH_LONG
            ).show();
            Intent permIntent = new Intent(
                    Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                    Uri.parse("package:" + activity.getPackageName())
            );
            activity.startActivity(permIntent);
            return;
        }
        pendingInstallAfterPermission = false;
        launchPackageInstaller(activity, apkFile);
        stagedApkFile = null;
    }

    private static void launchPackageInstaller(Activity activity, File apkFile) {
        try {
            Uri apkUri = FileProvider.getUriForFile(
                    activity,
                    activity.getPackageName() + ".fileprovider",
                    apkFile
            );
            Intent intent = new Intent(Intent.ACTION_VIEW);
            intent.setDataAndType(apkUri, "application/vnd.android.package-archive");
            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_NEW_TASK);
            activity.startActivity(intent);
        } catch (Throwable ignored) {
            Toast.makeText(activity, "Güncelleme yükleyicisi başlatılamadı.", Toast.LENGTH_SHORT).show();
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
        if (rawUrl == null || !rawUrl.toLowerCase(Locale.ROOT).startsWith("https://")) return false;
        Uri uri = Uri.parse(rawUrl);
        String host = uri.getHost();
        if (host == null) return false;
        String lower = host.toLowerCase(Locale.ROOT);
        return lower.equals("github.com") ||
                lower.endsWith(".github.com") ||
                lower.equals("githubusercontent.com") ||
                lower.endsWith(".githubusercontent.com");
    }

    private static boolean verifyApkFile(File file, String expectedSha256) {
        if (file == null || !file.exists() || file.length() < 512 * 1024) return false;
        try (FileInputStream fis = new FileInputStream(file)) {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[] buf = new byte[16384];
            int total = 0;
            int read;
            byte[] firstTwo = new byte[2];
            while ((read = fis.read(buf)) != -1) {
                if (total == 0 && read >= 2) {
                    firstTwo[0] = buf[0];
                    firstTwo[1] = buf[1];
                }
                total += read;
                md.update(buf, 0, read);
            }
            // APK is a ZIP archive starting with 'P','K'
            if (firstTwo[0] != 'P' || firstTwo[1] != 'K') return false;

            byte[] digest = md.digest();
            StringBuilder hex = new StringBuilder(64);
            for (byte b : digest) {
                hex.append(String.format(Locale.US, "%02x", b));
            }
            return MessageDigest.isEqual(
                    hex.toString().getBytes(java.nio.charset.StandardCharsets.US_ASCII),
                    expectedSha256.getBytes(java.nio.charset.StandardCharsets.US_ASCII)
            );
        } catch (Throwable ignored) {
            return false;
        }
    }
}
