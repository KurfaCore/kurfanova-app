package com.kurfacode.kurfamuzik;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.media.AudioAttributes;
import android.media.AudioFocusRequest;
import android.media.AudioManager;
import android.media.MediaMetadata;
import android.media.session.MediaSession;
import android.media.session.PlaybackState;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;
import android.webkit.WebView;

import java.io.InputStream;
import java.lang.ref.WeakReference;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * True MYT-style Foreground MediaPlayback Service with lockscreen MediaSession,
 * notification playback controls, CPU/Wi-Fi locks, and AudioFocus management.
 */
public class KurfaPlaybackService extends Service {

    private static final String CHANNEL_ID = "kurfa_media_channel";
    private static final int NOTIFICATION_ID = 7001;

    private static final String ACTION_SYNC = "com.kurfacode.kurfamuzik.action.SYNC";
    private static final String ACTION_TOGGLE = "com.kurfacode.kurfamuzik.action.TOGGLE";
    private static final String ACTION_NEXT = "com.kurfacode.kurfamuzik.action.NEXT";
    private static final String ACTION_PREV = "com.kurfacode.kurfamuzik.action.PREV";
    private static final String ACTION_STOP = "com.kurfacode.kurfamuzik.action.STOP";

    public static volatile boolean isPlaying = false;
    private static volatile String currentTitle = "Kurfanova Müzik";
    private static volatile String currentArtist = "Reklamsız Müzik Çalar";
    private static volatile String currentThumbUrl = "";
    private static volatile long currentDurationMs = -1L;
    private static volatile long currentPositionMs = PlaybackState.PLAYBACK_POSITION_UNKNOWN;
    private static volatile Bitmap currentArtwork = null;
    private static WeakReference<WebView> activeWebView = new WeakReference<>(null);

    private MediaSession mediaSession;
    private NotificationManager notificationManager;
    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;
    private final ExecutorService artExecutor = Executors.newSingleThreadExecutor();
    private volatile String lastLoadedArtUrl = "";

    public static void attachWebView(WebView view) {
        activeWebView = new WeakReference<>(view);
    }

    public static void updateState(Context ctx, boolean playing) {
        isPlaying = playing;
        KurfaWebView.keepAliveInBackground = playing;
        if (ctx == null) return;
        Context app = ctx.getApplicationContext();
        try {
            Intent intent = new Intent(app, KurfaPlaybackService.class);
            intent.setAction(ACTION_SYNC);
            if (playing) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    app.startForegroundService(intent);
                } else {
                    app.startService(intent);
                }
            } else {
                app.startService(intent);
            }
        } catch (Throwable ignored) {
        }
    }

    public static void updateMetadata(Context ctx, String title, String artist, String thumbUrl) {
        currentTitle = (title != null && !title.trim().isEmpty()) ? title.trim() : "Kurfanova Müzik";
        currentArtist = (artist != null && !artist.trim().isEmpty()) ? artist.trim() : "Kurfanova";
        currentThumbUrl = thumbUrl != null ? thumbUrl.trim() : "";
        if (ctx != null && isPlaying) {
            updateState(ctx, true);
        }
    }

    public static void updateProgress(Context ctx, long positionSec, long durationSec) {
        currentPositionMs = positionSec >= 0 ? positionSec * 1000L : PlaybackState.PLAYBACK_POSITION_UNKNOWN;
        currentDurationMs = durationSec > 0 ? durationSec * 1000L : -1L;
        if (ctx != null && isPlaying) {
            updateState(ctx, true);
        }
    }

    public static void stopService(Context ctx) {
        isPlaying = false;
        KurfaWebView.keepAliveInBackground = false;
        if (ctx == null) return;
        try {
            ctx.getApplicationContext().stopService(new Intent(ctx.getApplicationContext(), KurfaPlaybackService.class));
        } catch (Throwable ignored) {
        }
    }

    @Override
    public void onCreate() {
        super.onCreate();
        notificationManager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        ensureNotificationChannel();
        initMediaSession();
        initLocks();
    }

    private void ensureNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && notificationManager != null) {
            NotificationChannel ch = new NotificationChannel(
                    CHANNEL_ID,
                    "Müzik Oynatma",
                    NotificationManager.IMPORTANCE_LOW
            );
            ch.setDescription("Arka plan ve kilit ekranı müzik kontrolleri");
            ch.setShowBadge(false);
            ch.setSound(null, null);
            notificationManager.createNotificationChannel(ch);
        }
    }

    private void initMediaSession() {
        mediaSession = new MediaSession(this, "KurfanovaMediaSession");
        mediaSession.setCallback(new MediaSession.Callback() {
            @Override
            public void onPlay() {
                if (!isPlaying) dispatchToJs("kurfaNativeTogglePlay");
            }

            @Override
            public void onPause() {
                if (isPlaying) dispatchToJs("kurfaNativeTogglePlay");
            }

            @Override
            public void onSkipToNext() {
                dispatchToJs("kurfaNativeNextTrack");
            }

            @Override
            public void onSkipToPrevious() {
                dispatchToJs("kurfaNativePrevTrack");
            }

            @Override
            public void onSeekTo(long posMs) {
                long targetSec = Math.max(0L, posMs / 1000L);
                currentPositionMs = targetSec * 1000L;
                updateSessionAndNotification(isPlaying);
                dispatchToJsArg("kurfaNativeSeekToSeconds", targetSec);
            }

            @Override
            public void onStop() {
                if (isPlaying) dispatchToJs("kurfaNativeTogglePlay");
                releaseLocks();
                stopForegroundCompat(true);
                stopSelf();
            }
        });
        mediaSession.setActive(true);
    }

    private void initLocks() {
        try {
            PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
            if (pm != null) {
                wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Kurfanova::BgPlaybackCpu");
                wakeLock.setReferenceCounted(false);
            }
        } catch (Throwable ignored) {}

        try {
            WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wm != null) {
                int mode = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
                        ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY
                        : WifiManager.WIFI_MODE_FULL_HIGH_PERF;
                wifiLock = wm.createWifiLock(mode, "Kurfanova::BgPlaybackWifi");
                wifiLock.setReferenceCounted(false);
            }
        } catch (Throwable ignored) {}
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent != null ? intent.getAction() : ACTION_SYNC;

        if (ACTION_TOGGLE.equals(action)) {
            dispatchToJs("kurfaNativeTogglePlay");
        } else if (ACTION_NEXT.equals(action)) {
            dispatchToJs("kurfaNativeNextTrack");
        } else if (ACTION_PREV.equals(action)) {
            dispatchToJs("kurfaNativePrevTrack");
        } else if (ACTION_STOP.equals(action)) {
            isPlaying = false;
            KurfaWebView.keepAliveInBackground = false;
            releaseLocks();
            stopForegroundCompat(true);
            stopSelf();
            return START_NOT_STICKY;
        }

        if (isPlaying) {
            acquireLocks();
        } else {
            releaseLocks();
        }

        updateSessionAndNotification(ACTION_SYNC.equals(action) || isPlaying);
        if (!isPlaying && (ACTION_SYNC.equals(action) || action == null)) {
            stopForegroundCompat(false);
        }
        loadArtworkAsyncIfNeeded();
        return START_STICKY;
    }

    private void updateSessionAndNotification(boolean asForeground) {
        if (mediaSession != null) {
            PlaybackState state = new PlaybackState.Builder()
                    .setActions(
                            PlaybackState.ACTION_PLAY |
                            PlaybackState.ACTION_PAUSE |
                            PlaybackState.ACTION_PLAY_PAUSE |
                            PlaybackState.ACTION_SKIP_TO_NEXT |
                            PlaybackState.ACTION_SKIP_TO_PREVIOUS |
                            PlaybackState.ACTION_SEEK_TO |
                            PlaybackState.ACTION_STOP
                    )
                    .setState(
                            isPlaying ? PlaybackState.STATE_PLAYING : PlaybackState.STATE_PAUSED,
                            currentPositionMs,
                            isPlaying ? 1.0f : 0.0f
                    )
                    .build();
            mediaSession.setPlaybackState(state);

            MediaMetadata.Builder meta = new MediaMetadata.Builder()
                    .putString(MediaMetadata.METADATA_KEY_TITLE, currentTitle)
                    .putString(MediaMetadata.METADATA_KEY_ARTIST, currentArtist);
            if (currentDurationMs > 0) {
                meta.putLong(MediaMetadata.METADATA_KEY_DURATION, currentDurationMs);
            }
            if (currentArtwork != null) {
                meta.putBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART, currentArtwork);
            }
            mediaSession.setMetadata(meta.build());
        }

        Notification notif = buildNotification();
        if (notif == null) return;

        if (asForeground && isPlaying) {
            try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    startForeground(NOTIFICATION_ID, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK);
                } else {
                    startForeground(NOTIFICATION_ID, notif);
                }
            } catch (Throwable t) {
                try {
                    if (notificationManager != null) notificationManager.notify(NOTIFICATION_ID, notif);
                } catch (Throwable ignored) {}
            }
        } else if (notificationManager != null) {
            try {
                notificationManager.notify(NOTIFICATION_ID, notif);
            } catch (Throwable ignored) {}
        }
    }

    private Notification buildNotification() {
        Intent launchIntent = new Intent(this, MainActivity.class);
        launchIntent.setFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        int pFlags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M ? PendingIntent.FLAG_IMMUTABLE : 0);
        PendingIntent contentPi = PendingIntent.getActivity(this, 0, launchIntent, pFlags);

        PendingIntent prevPi = serviceActionPendingIntent(ACTION_PREV, 1, pFlags);
        PendingIntent togglePi = serviceActionPendingIntent(ACTION_TOGGLE, 2, pFlags);
        PendingIntent nextPi = serviceActionPendingIntent(ACTION_NEXT, 3, pFlags);
        PendingIntent stopPi = serviceActionPendingIntent(ACTION_STOP, 4, pFlags);

        Notification.Builder builder = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);

        Bitmap largeIcon = currentArtwork != null
                ? currentArtwork
                : BitmapFactory.decodeResource(getResources(), R.mipmap.ic_launcher);

        Notification.MediaStyle style = new Notification.MediaStyle()
                .setMediaSession(mediaSession != null ? mediaSession.getSessionToken() : null)
                .setShowActionsInCompactView(0, 1, 2);

        builder.setSmallIcon(R.mipmap.ic_launcher)
                .setLargeIcon(largeIcon)
                .setContentTitle(currentTitle)
                .setContentText(currentArtist)
                .setContentIntent(contentPi)
                .setOngoing(isPlaying)
                .setShowWhen(false)
                .setVisibility(Notification.VISIBILITY_PUBLIC)
                .addAction(new Notification.Action.Builder(
                        android.R.drawable.ic_media_previous, "Önceki", prevPi).build())
                .addAction(new Notification.Action.Builder(
                        isPlaying ? android.R.drawable.ic_media_pause : android.R.drawable.ic_media_play,
                        isPlaying ? "Duraklat" : "Oynat", togglePi).build())
                .addAction(new Notification.Action.Builder(
                        android.R.drawable.ic_media_next, "Sonraki", nextPi).build())
                .addAction(new Notification.Action.Builder(
                        android.R.drawable.ic_menu_close_clear_cancel, "Kapat", stopPi).build())
                .setStyle(style);

        return builder.build();
    }

    private PendingIntent serviceActionPendingIntent(String action, int reqCode, int pFlags) {
        Intent i = new Intent(this, KurfaPlaybackService.class);
        i.setAction(action);
        return PendingIntent.getService(this, reqCode, i, pFlags);
    }

    private void loadArtworkAsyncIfNeeded() {
        final String targetUrl = currentThumbUrl;
        if (targetUrl.isEmpty() || !targetUrl.startsWith("http") || targetUrl.equals(lastLoadedArtUrl)) {
            return;
        }
        lastLoadedArtUrl = targetUrl;
        artExecutor.execute(() -> {
            HttpURLConnection conn = null;
            try {
                conn = (HttpURLConnection) new URL(targetUrl).openConnection();
                conn.setConnectTimeout(3500);
                conn.setReadTimeout(3500);
                conn.setDoInput(true);
                conn.connect();
                try (InputStream is = conn.getInputStream()) {
                    BitmapFactory.Options opts = new BitmapFactory.Options();
                    opts.inSampleSize = 2;
                    Bitmap bmp = BitmapFactory.decodeStream(is, null, opts);
                    if (bmp != null && targetUrl.equals(currentThumbUrl)) {
                        currentArtwork = bmp;
                        updateSessionAndNotification(isPlaying);
                    }
                }
            } catch (Throwable ignored) {
            } finally {
                if (conn != null) conn.disconnect();
            }
        });
    }

    private void dispatchToJs(String fnName) {
        WebView wv = activeWebView.get();
        if (wv == null) return;
        wv.post(() -> {
            try {
                wv.evaluateJavascript("if(typeof " + fnName + "==='function'){" + fnName + "();}", null);
            } catch (Throwable ignored) {}
        });
    }

    private void dispatchToJsArg(String fnName, long arg) {
        WebView wv = activeWebView.get();
        if (wv == null) return;
        wv.post(() -> {
            try {
                wv.evaluateJavascript("if(typeof " + fnName + "==='function'){" + fnName + "(" + arg + ");}", null);
            } catch (Throwable ignored) {}
        });
    }

    private void acquireLocks() {
        try {
            if (wakeLock != null && !wakeLock.isHeld()) wakeLock.acquire(4 * 60 * 60 * 1000L);
        } catch (Throwable ignored) {}
        try {
            if (wifiLock != null && !wifiLock.isHeld()) wifiLock.acquire();
        } catch (Throwable ignored) {}
    }

    private void releaseLocks() {
        try {
            if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        } catch (Throwable ignored) {}
        try {
            if (wifiLock != null && wifiLock.isHeld()) wifiLock.release();
        } catch (Throwable ignored) {}
    }

    @SuppressWarnings("deprecation")
    private void stopForegroundCompat(boolean removeNotification) {
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                stopForeground(removeNotification ? STOP_FOREGROUND_REMOVE : STOP_FOREGROUND_DETACH);
            } else {
                stopForeground(removeNotification);
            }
        } catch (Throwable ignored) {}
    }

    @Override
    public void onTaskRemoved(Intent rootIntent) {
        super.onTaskRemoved(rootIntent);
        isPlaying = false;
        KurfaWebView.keepAliveInBackground = false;
        releaseLocks();
        stopForegroundCompat(true);
        stopSelf();
    }

    @Override
    public void onDestroy() {
        releaseLocks();
        if (mediaSession != null) {
            try {
                mediaSession.setActive(false);
                mediaSession.release();
            } catch (Throwable ignored) {}
        }
        artExecutor.shutdownNow();
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
