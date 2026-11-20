package com.kurfacode.kurfamuzik;

import android.annotation.SuppressLint;
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
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.webkit.WebView;

import androidx.annotation.NonNull;
import androidx.media3.common.C;
import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.database.StandaloneDatabaseProvider;
import androidx.media3.datasource.HttpDataSource;
import androidx.media3.datasource.cache.CacheDataSource;
import androidx.media3.datasource.cache.LeastRecentlyUsedCacheEvictor;
import androidx.media3.datasource.cache.SimpleCache;
import androidx.media3.datasource.okhttp.OkHttpDataSource;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.exoplayer.source.MediaSource;
import androidx.media3.exoplayer.source.ProgressiveMediaSource;
import androidx.media3.exoplayer.upstream.DefaultLoadErrorHandlingPolicy;
import androidx.media3.exoplayer.upstream.LoadErrorHandlingPolicy;

import org.json.JSONObject;

import java.io.File;
import java.io.InputStream;
import java.lang.ref.WeakReference;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Foreground MediaPlayback Service powered by native Media3 ExoPlayer, 128 MB SimpleCache,
 * resilient LoadErrorHandlingPolicy, lockscreen MediaSession, and screen-off AudioFocus recovery.
 */
@SuppressLint("UnsafeOptInUsageError")
public class KurfaPlaybackService extends Service {

    private static final String CHANNEL_ID = "kurfa_media_channel";
    private static final int NOTIFICATION_ID = 7001;
    private static final long EXO_CACHE_MAX_BYTES = 128L * 1024L * 1024L;
    private static final long SCREEN_OFF_FOCUS_RECOVERY_DELAY_MS = 800L;
    private static final long TIME_TICK_INTERVAL_MS = 500L;

    private static final String ACTION_SYNC = "com.kurfacode.kurfamuzik.action.SYNC";
    private static final String ACTION_TOGGLE = "com.kurfacode.kurfamuzik.action.TOGGLE";
    private static final String ACTION_NEXT = "com.kurfacode.kurfamuzik.action.NEXT";
    private static final String ACTION_PREV = "com.kurfacode.kurfamuzik.action.PREV";
    private static final String ACTION_STOP = "com.kurfacode.kurfamuzik.action.STOP";
    private static final String ACTION_PLAY_NATIVE = "com.kurfacode.kurfamuzik.action.PLAY_NATIVE";

    private static final String EXTRA_VIDEO_ID = "video_id";
    private static final String EXTRA_TITLE = "title";
    private static final String EXTRA_ARTIST = "artist";
    private static final String EXTRA_COVER_URL = "cover_url";
    private static final String EXTRA_START_SEC = "start_sec";

    public static volatile boolean isPlaying = false;
    private static volatile boolean nativeExoMode = false;
    private static volatile String activeVideoId = "";
    private static volatile String currentTitle = "Kurfanova Müzik";
    private static volatile String currentArtist = "Reklamsız Müzik Çalar";
    private static volatile String currentThumbUrl = "";
    private static volatile long currentDurationMs = -1L;
    private static volatile long currentPositionMs = PlaybackState.PLAYBACK_POSITION_UNKNOWN;
    private static volatile float desiredVolume = 1.0f;
    private static volatile Bitmap currentArtwork = null;

    private static WeakReference<WebView> activeWebView = new WeakReference<>(null);
    private static WeakReference<KurfaPlaybackService> activeService = new WeakReference<>(null);
    private static final Handler MAIN_HANDLER = new Handler(Looper.getMainLooper());
    private static final Object CACHE_LOCK = new Object();
    private static SimpleCache sharedExoCache;
    private static StandaloneDatabaseProvider sharedDbProvider;

    private MediaSession mediaSession;
    private NotificationManager notificationManager;
    private AudioManager audioManager;
    private PowerManager powerManager;
    private AudioFocusRequest audioFocusRequest;
    private volatile boolean hasAudioFocus = false;
    private volatile boolean pausedByAudioFocus = false;
    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;
    private final ExecutorService artExecutor = Executors.newSingleThreadExecutor();
    private volatile String lastLoadedArtUrl = "";

    private ExoPlayer exoPlayer;
    private ProgressiveMediaSource.Factory mediaSourceFactory;
    private boolean userRequestedPlayback = false;
    private boolean endedEmittedForTrack = false;
    private boolean errorRecoveryAttemptedForTrack = false;
    private Runnable pendingFocusRecovery;
    private boolean timeTickScheduled = false;

    private final Runnable timeTickRunnable = new Runnable() {
        @Override
        public void run() {
            timeTickScheduled = false;
            if (!nativeExoMode || exoPlayer == null) {
                return;
            }
            int state = exoPlayer.getPlaybackState();
            boolean shouldTick = exoPlayer.isPlaying()
                    || (exoPlayer.getPlayWhenReady() && state == Player.STATE_BUFFERING);
            long posMs = Math.max(0L, exoPlayer.getCurrentPosition());
            long durMs = exoPlayer.getDuration();
            currentPositionMs = posMs;
            currentDurationMs = durMs > 0L ? durMs : -1L;
            updateSessionPlaybackStateOnly();
            emitNativeExoEvent("time", null);
            if (shouldTick) {
                timeTickScheduled = true;
                MAIN_HANDLER.postDelayed(this, TIME_TICK_INTERVAL_MS);
            }
        }
    };

    private final AudioManager.OnAudioFocusChangeListener audioFocusListener = focusChange -> {
        if (nativeExoMode) return;
        if (focusChange == AudioManager.AUDIOFOCUS_LOSS) {
            hasAudioFocus = false;
            pausedByAudioFocus = false;
            if (isPlaying) {
                dispatchPlayPauseJs(false);
            }
        } else if (focusChange == AudioManager.AUDIOFOCUS_LOSS_TRANSIENT) {
            if (isPlaying) {
                pausedByAudioFocus = true;
                dispatchPlayPauseJs(false);
            }
        } else if (focusChange == AudioManager.AUDIOFOCUS_GAIN) {
            hasAudioFocus = true;
            if (pausedByAudioFocus && !isPlaying) {
                pausedByAudioFocus = false;
                dispatchPlayPauseJs(true);
            }
        }
    };

    private static SimpleCache getOrCreateExoCache(Context context) {
        synchronized (CACHE_LOCK) {
            if (sharedExoCache == null) {
                Context appCtx = context.getApplicationContext();
                File cacheDir = new File(appCtx.getCacheDir(), "kurfa_exo_media_cache");
                if (sharedDbProvider == null) {
                    sharedDbProvider = new StandaloneDatabaseProvider(appCtx);
                }
                sharedExoCache = new SimpleCache(
                        cacheDir,
                        new LeastRecentlyUsedCacheEvictor(EXO_CACHE_MAX_BYTES),
                        sharedDbProvider
                );
            }
            return sharedExoCache;
        }
    }

    /**
     * Metrolist-style resilient error handling: keeps buffered media intact across transient
     * network dropouts while failing fast on hard 401/403/404/410 responses.
     */
    private static final class PlaybackLoadErrorHandlingPolicy extends DefaultLoadErrorHandlingPolicy {
        @Override
        public int getMinimumLoadableRetryCount(int dataType) {
            return Integer.MAX_VALUE;
        }

        @Override
        public long getRetryDelayMsFor(@NonNull LoadErrorHandlingPolicy.LoadErrorInfo loadErrorInfo) {
            int statusCode = extractHttpStatusCode(loadErrorInfo.exception);
            if (statusCode == 401 || statusCode == 403 || statusCode == 404 || statusCode == 410) {
                return C.TIME_UNSET;
            }
            return Math.min(Math.max(1, loadErrorInfo.errorCount) * 1_000L, 10_000L);
        }

        private static int extractHttpStatusCode(Throwable error) {
            Throwable cur = error;
            for (int depth = 0; cur != null && depth < 6; depth++) {
                if (cur instanceof HttpDataSource.InvalidResponseCodeException) {
                    return ((HttpDataSource.InvalidResponseCodeException) cur).responseCode;
                }
                cur = cur.getCause();
            }
            return -1;
        }
    }

    public static void attachWebView(WebView view) {
        activeWebView = new WeakReference<>(view);
    }

    public static void playNative(
            Context ctx,
            String rawVideoId,
            String title,
            String artist,
            String coverUrl,
            double startSec
    ) {
        final String videoId = KurfaNativeEngine.extractVideoId(rawVideoId);
        if (ctx == null || videoId == null) return;

        nativeExoMode = true;
        activeVideoId = videoId;
        currentTitle = (title != null && !title.trim().isEmpty()) ? title.trim() : "Kurfanova Müzik";
        currentArtist = (artist != null && !artist.trim().isEmpty()) ? artist.trim() : "Kurfanova";
        currentThumbUrl = coverUrl != null ? coverUrl.trim() : "";
        currentPositionMs = startSec > 0 ? Math.round(startSec * 1000.0) : 0L;
        currentDurationMs = -1L;
        isPlaying = true;
        KurfaWebView.keepAliveInBackground = true;

        final Context app = ctx.getApplicationContext();
        MAIN_HANDLER.post(() -> {
            KurfaPlaybackService svc = activeService.get();
            if (svc != null) {
                svc.startOrUpdateNativePlayback(videoId, currentTitle, currentArtist, currentThumbUrl, startSec);
                try {
                    Intent syncIntent = new Intent(app, KurfaPlaybackService.class);
                    syncIntent.setAction(ACTION_SYNC);
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                        app.startForegroundService(syncIntent);
                    } else {
                        app.startService(syncIntent);
                    }
                } catch (Throwable ignored) {}
                return;
            }
            try {
                Intent intent = new Intent(app, KurfaPlaybackService.class);
                intent.setAction(ACTION_PLAY_NATIVE);
                intent.putExtra(EXTRA_VIDEO_ID, videoId);
                intent.putExtra(EXTRA_TITLE, currentTitle);
                intent.putExtra(EXTRA_ARTIST, currentArtist);
                intent.putExtra(EXTRA_COVER_URL, currentThumbUrl);
                intent.putExtra(EXTRA_START_SEC, startSec);
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    app.startForegroundService(intent);
                } else {
                    app.startService(intent);
                }
            } catch (Throwable ignored) {}
        });
    }

    public static void pauseNative(Context ctx) {
        MAIN_HANDLER.post(() -> {
            KurfaPlaybackService svc = activeService.get();
            if (svc != null) {
                svc.pauseExoInternal();
            }
        });
    }

    public static void resumeNative(Context ctx) {
        MAIN_HANDLER.post(() -> {
            KurfaPlaybackService svc = activeService.get();
            if (svc != null) {
                svc.resumeExoInternal();
            }
        });
    }

    public static void stopNative(Context ctx) {
        nativeExoMode = false;
        isPlaying = false;
        KurfaWebView.keepAliveInBackground = false;
        MAIN_HANDLER.post(() -> {
            KurfaPlaybackService svc = activeService.get();
            if (svc != null) {
                svc.stopExoInternal(true);
            }
        });
    }

    public static void seekNative(Context ctx, double sec) {
        MAIN_HANDLER.post(() -> {
            KurfaPlaybackService svc = activeService.get();
            if (svc != null) {
                svc.seekExoInternal(sec);
            }
        });
    }

    public static void setVolumeNative(Context ctx, float vol) {
        final float clamped = Math.max(0f, Math.min(1f, vol));
        desiredVolume = clamped;
        MAIN_HANDLER.post(() -> {
            KurfaPlaybackService svc = activeService.get();
            if (svc != null && svc.exoPlayer != null) {
                svc.exoPlayer.setVolume(clamped);
            }
        });
    }

    public static void updateState(Context ctx, boolean playing) {
        if (nativeExoMode) return;
        isPlaying = playing;
        KurfaWebView.keepAliveInBackground = playing;
        if (ctx == null) return;
        Context app = ctx.getApplicationContext();
        try {
            Intent intent = new Intent(app, KurfaPlaybackService.class);
            intent.setAction(ACTION_SYNC);
            if (playing && Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                app.startForegroundService(intent);
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
        KurfaPlaybackService svc = activeService.get();
        if (svc != null) {
            MAIN_HANDLER.post(() -> {
                if (activeService.get() == svc) {
                    svc.updateSessionAndNotification(isPlaying);
                    svc.loadArtworkAsyncIfNeeded();
                }
            });
            return;
        }
        if (ctx != null && isPlaying && !nativeExoMode) {
            updateState(ctx, true);
        }
    }

    public static void updateProgress(Context ctx, long positionSec, long durationSec) {
        if (nativeExoMode) return;
        currentPositionMs = positionSec >= 0 ? positionSec * 1000L : PlaybackState.PLAYBACK_POSITION_UNKNOWN;
        currentDurationMs = durationSec > 0 ? durationSec * 1000L : -1L;
        KurfaPlaybackService svc = activeService.get();
        if (svc != null) {
            MAIN_HANDLER.post(() -> {
                if (activeService.get() == svc) {
                    svc.updateSessionPlaybackStateOnly();
                }
            });
            return;
        }
        if (ctx != null && isPlaying) {
            updateState(ctx, true);
        }
    }

    public static void stopService(Context ctx) {
        nativeExoMode = false;
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
        activeService = new WeakReference<>(this);
        notificationManager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        audioManager = (AudioManager) getSystemService(Context.AUDIO_SERVICE);
        powerManager = (PowerManager) getSystemService(Context.POWER_SERVICE);
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
                if (nativeExoMode && exoPlayer != null) {
                    resumeExoInternal();
                } else if (!isPlaying) {
                    dispatchToJs("kurfaNativeTogglePlay");
                }
            }

            @Override
            public void onPause() {
                if (nativeExoMode && exoPlayer != null) {
                    pauseExoInternal();
                } else if (isPlaying) {
                    dispatchToJs("kurfaNativeTogglePlay");
                }
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
                long safePosMs = Math.max(0L, posMs);
                if (nativeExoMode && exoPlayer != null) {
                    seekExoInternal(safePosMs / 1000.0);
                    return;
                }
                long targetSec = safePosMs / 1000L;
                currentPositionMs = targetSec * 1000L;
                updateSessionAndNotification(isPlaying);
                dispatchToJsArg("kurfaNativeSeekToSeconds", targetSec);
            }

            @Override
            public void onStop() {
                if (nativeExoMode && exoPlayer != null) {
                    stopExoInternal(true);
                } else if (isPlaying) {
                    dispatchToJs("kurfaNativeTogglePlay");
                }
                releaseLocks();
                stopForegroundCompat(true);
                stopSelf();
            }
        });
        mediaSession.setActive(true);
    }

    private void initLocks() {
        try {
            if (powerManager != null) {
                wakeLock = powerManager.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Kurfanova::BgPlaybackCpu");
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

    private void ensureExoPlayer() {
        if (exoPlayer != null) {
            return;
        }
        androidx.media3.common.AudioAttributes exoAudioAttrs =
                new androidx.media3.common.AudioAttributes.Builder()
                        .setUsage(C.USAGE_MEDIA)
                        .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                        .build();

        OkHttpDataSource.Factory upstreamFactory =
                new OkHttpDataSource.Factory(KurfaNativeEngine.getSharedHttpClient())
                        .setUserAgent("KurfanovaMuzik-ExoPlayer/1.0");
        CacheDataSource.Factory cacheDataSourceFactory = new CacheDataSource.Factory()
                .setCache(getOrCreateExoCache(this))
                .setUpstreamDataSourceFactory(upstreamFactory)
                .setFlags(CacheDataSource.FLAG_IGNORE_CACHE_ON_ERROR);

        mediaSourceFactory = new ProgressiveMediaSource.Factory(cacheDataSourceFactory)
                .setContinueLoadingCheckIntervalBytes(65_536)
                .setLoadErrorHandlingPolicy(new PlaybackLoadErrorHandlingPolicy());

        exoPlayer = new ExoPlayer.Builder(this)
                .setAudioAttributes(exoAudioAttrs, true)
                .setWakeMode(C.WAKE_MODE_NETWORK)
                .setHandleAudioBecomingNoisy(true)
                .build();
        exoPlayer.setVolume(desiredVolume);
        exoPlayer.addListener(new Player.Listener() {
            @Override
            public void onPlaybackStateChanged(int playbackState) {
                if (!nativeExoMode || exoPlayer == null) return;
                if (playbackState == Player.STATE_ENDED) {
                    if (!endedEmittedForTrack) {
                        endedEmittedForTrack = true;
                        userRequestedPlayback = false;
                        isPlaying = false;
                        KurfaWebView.keepAliveInBackground = false;
                        stopTimeTick();
                        releaseLocks();
                        updateSessionAndNotification(false);
                        emitNativeExoEvent("ended", null);
                    }
                    return;
                }
                if (playbackState == Player.STATE_READY) {
                    long dur = exoPlayer.getDuration();
                    if (dur > 0L) currentDurationMs = dur;
                    long pos = Math.max(0L, exoPlayer.getCurrentPosition());
                    currentPositionMs = pos;
                }
                syncNativeForegroundState();
                emitNativeExoEvent("state", null);
            }

            @Override
            public void onIsPlayingChanged(boolean playing) {
                if (!nativeExoMode || exoPlayer == null) return;
                isPlaying = playing;
                if (playing) {
                    errorRecoveryAttemptedForTrack = false;
                    startTimeTick();
                } else if (exoPlayer.getPlaybackState() != Player.STATE_BUFFERING || !exoPlayer.getPlayWhenReady()) {
                    stopTimeTick();
                }
                syncNativeForegroundState();
                emitNativeExoEvent("state", null);
            }

            @Override
            public void onPlayWhenReadyChanged(boolean playWhenReady, int reason) {
                if (!nativeExoMode || exoPlayer == null) return;
                if (!playWhenReady && reason == Player.PLAY_WHEN_READY_CHANGE_REASON_AUDIO_FOCUS_LOSS && userRequestedPlayback) {
                    scheduleScreenOffFocusRecovery();
                } else if (playWhenReady) {
                    cancelScreenOffFocusRecovery();
                }
                syncNativeForegroundState();
                emitNativeExoEvent("state", null);
            }

            @Override
            public void onPlayerError(@NonNull PlaybackException error) {
                if (!nativeExoMode || exoPlayer == null) return;
                final String failingVideoId = activeVideoId;
                if (!errorRecoveryAttemptedForTrack && failingVideoId != null && !failingVideoId.isEmpty()) {
                    errorRecoveryAttemptedForTrack = true;
                    final long resumePosMs = Math.max(0L, exoPlayer.getCurrentPosition());
                    new Thread(() -> {
                        KurfaNativeEngine.invalidateStream(failingVideoId);
                        MAIN_HANDLER.post(() -> {
                            if (exoPlayer == null || !nativeExoMode || !failingVideoId.equals(activeVideoId)) {
                                return;
                            }
                            prepareAndPlayOnExo(failingVideoId, resumePosMs / 1000.0);
                        });
                    }, "KurfaExoErrorRecover").start();
                    return;
                }

                userRequestedPlayback = false;
                isPlaying = false;
                KurfaWebView.keepAliveInBackground = false;
                stopTimeTick();
                releaseLocks();
                updateSessionAndNotification(false);
                String msg = error.getMessage() != null ? error.getMessage() : "ExoPlayer playback error";
                emitNativeExoEvent("error", msg);
            }
        });
    }

    private void startOrUpdateNativePlayback(
            String videoId,
            String title,
            String artist,
            String coverUrl,
            double startSec
    ) {
        cancelScreenOffFocusRecovery();
        abandonAudioFocus();
        nativeExoMode = true;
        activeVideoId = videoId;
        currentTitle = (title != null && !title.trim().isEmpty()) ? title.trim() : "Kurfanova Müzik";
        currentArtist = (artist != null && !artist.trim().isEmpty()) ? artist.trim() : "Kurfanova";
        currentThumbUrl = coverUrl != null ? coverUrl.trim() : "";
        userRequestedPlayback = true;
        endedEmittedForTrack = false;
        errorRecoveryAttemptedForTrack = false;

        ensureExoPlayer();
        prepareAndPlayOnExo(videoId, startSec);
        acquireLocks();
        updateSessionAndNotification(true);
        loadArtworkAsyncIfNeeded();
        startTimeTick();
        emitNativeExoEvent("state", null);
    }

    private void prepareAndPlayOnExo(String videoId, double startSec) {
        if (exoPlayer == null || mediaSourceFactory == null) return;
        String localUrl = KurfaNativeEngine.getLocalNativeAudioUrl(videoId);
        if (localUrl == null) {
            emitNativeExoEvent("error", "Local proxy unavailable");
            return;
        }
        MediaItem mediaItem = new MediaItem.Builder()
                .setUri(localUrl)
                .setCustomCacheKey("kurfa_audio_" + videoId)
                .build();
        MediaSource source = mediaSourceFactory.createMediaSource(mediaItem);
        long startMs = startSec > 0 ? Math.round(startSec * 1000.0) : 0L;
        exoPlayer.setMediaSource(source, startMs);
        exoPlayer.prepare();
        exoPlayer.setPlayWhenReady(true);
    }

    private void pauseExoInternal() {
        cancelScreenOffFocusRecovery();
        userRequestedPlayback = false;
        if (exoPlayer != null) {
            exoPlayer.pause();
        }
        isPlaying = false;
        KurfaWebView.keepAliveInBackground = false;
        stopTimeTick();
        releaseLocks();
        updateSessionAndNotification(false);
        emitNativeExoEvent("state", null);
    }

    private void resumeExoInternal() {
        cancelScreenOffFocusRecovery();
        userRequestedPlayback = true;
        if (exoPlayer != null) {
            if (exoPlayer.getPlaybackState() == Player.STATE_ENDED) {
                endedEmittedForTrack = false;
                exoPlayer.seekTo(0L);
            }
            exoPlayer.play();
            startTimeTick();
        }
        acquireLocks();
        isPlaying = true;
        KurfaWebView.keepAliveInBackground = true;
        updateSessionAndNotification(true);
        emitNativeExoEvent("state", null);
    }

    private void stopExoInternal(boolean clearTrack) {
        cancelScreenOffFocusRecovery();
        userRequestedPlayback = false;
        stopTimeTick();
        if (exoPlayer != null) {
            exoPlayer.stop();
            if (clearTrack) {
                exoPlayer.clearMediaItems();
            }
        }
        if (clearTrack) {
            activeVideoId = "";
        }
        isPlaying = false;
        KurfaWebView.keepAliveInBackground = false;
        releaseLocks();
        updateSessionAndNotification(false);
        emitNativeExoEvent("state", null);
    }

    private void seekExoInternal(double sec) {
        if (exoPlayer == null) return;
        long targetMs = Math.max(0L, Math.round(sec * 1000.0));
        exoPlayer.seekTo(targetMs);
        currentPositionMs = targetMs;
        updateSessionPlaybackStateOnly();
        emitNativeExoEvent("time", null);
    }

    private void syncNativeForegroundState() {
        if (exoPlayer == null) return;
        int state = exoPlayer.getPlaybackState();
        boolean active = exoPlayer.isPlaying() || (exoPlayer.getPlayWhenReady() && state == Player.STATE_BUFFERING);
        isPlaying = exoPlayer.isPlaying();
        KurfaWebView.keepAliveInBackground = active;
        if (active) {
            acquireLocks();
        } else {
            releaseLocks();
        }
        updateSessionAndNotification(active);
    }

    private void scheduleScreenOffFocusRecovery() {
        cancelScreenOffFocusRecovery();
        final ExoPlayer targetPlayer = exoPlayer;
        final String targetVideoId = activeVideoId;
        pendingFocusRecovery = () -> {
            pendingFocusRecovery = null;
            if (!nativeExoMode || exoPlayer != targetPlayer || exoPlayer == null) return;
            if (targetVideoId == null || !targetVideoId.equals(activeVideoId)) return;
            if (!userRequestedPlayback || exoPlayer.getPlayWhenReady()) return;

            boolean screenInteractive = powerManager != null && powerManager.isInteractive();
            int audioMode = audioManager != null ? audioManager.getMode() : AudioManager.MODE_NORMAL;
            boolean inCall = audioMode == AudioManager.MODE_IN_CALL
                    || audioMode == AudioManager.MODE_IN_COMMUNICATION
                    || audioMode == AudioManager.MODE_RINGTONE;
            boolean musicActive = audioManager != null && audioManager.isMusicActive();

            if (!screenInteractive && !inCall && !musicActive) {
                exoPlayer.play();
            }
        };
        MAIN_HANDLER.postDelayed(pendingFocusRecovery, SCREEN_OFF_FOCUS_RECOVERY_DELAY_MS);
    }

    private void cancelScreenOffFocusRecovery() {
        if (pendingFocusRecovery != null) {
            MAIN_HANDLER.removeCallbacks(pendingFocusRecovery);
            pendingFocusRecovery = null;
        }
    }

    private void startTimeTick() {
        if (timeTickScheduled) {
            MAIN_HANDLER.removeCallbacks(timeTickRunnable);
        }
        timeTickScheduled = true;
        MAIN_HANDLER.postDelayed(timeTickRunnable, TIME_TICK_INTERVAL_MS);
    }

    private void stopTimeTick() {
        timeTickScheduled = false;
        MAIN_HANDLER.removeCallbacks(timeTickRunnable);
    }

    private void emitNativeExoEvent(String type, String errorMsg) {
        WebView wv = activeWebView.get();
        if (wv == null) return;
        try {
            boolean playingNow = exoPlayer != null && exoPlayer.isPlaying();
            boolean bufferingNow = exoPlayer != null && exoPlayer.getPlaybackState() == Player.STATE_BUFFERING;
            long posMs = exoPlayer != null ? Math.max(0L, exoPlayer.getCurrentPosition()) : 0L;
            long durMs = exoPlayer != null ? exoPlayer.getDuration() : -1L;

            JSONObject ev = new JSONObject();
            ev.put("type", type);
            ev.put("playing", playingNow);
            ev.put("buffering", bufferingNow);
            ev.put("currentTime", posMs / 1000.0);
            ev.put("duration", durMs > 0L ? (durMs / 1000.0) : 0.0);
            ev.put("videoId", activeVideoId != null ? activeVideoId : "");
            if (errorMsg != null) {
                ev.put("error", errorMsg);
            }
            final String script = "if(typeof window.__kurfaNativeExoEvent==='function'){try{window.__kurfaNativeExoEvent("
                    + ev.toString() + ");}catch(_){}}";
            wv.post(() -> {
                try {
                    wv.evaluateJavascript(script, null);
                } catch (Throwable ignored) {}
            });
        } catch (Throwable ignored) {}
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent != null ? intent.getAction() : ACTION_SYNC;

        if (ACTION_PLAY_NATIVE.equals(action) && intent != null) {
            String vid = intent.getStringExtra(EXTRA_VIDEO_ID);
            String title = intent.getStringExtra(EXTRA_TITLE);
            String artist = intent.getStringExtra(EXTRA_ARTIST);
            String cover = intent.getStringExtra(EXTRA_COVER_URL);
            double startSec = intent.getDoubleExtra(EXTRA_START_SEC, 0.0);
            updateSessionAndNotification(true);
            if (vid != null && !vid.isEmpty()) {
                startOrUpdateNativePlayback(vid, title, artist, cover, startSec);
            }
            return START_STICKY;
        }

        if (ACTION_TOGGLE.equals(action)) {
            if (nativeExoMode && exoPlayer != null) {
                if (exoPlayer.isPlaying() || exoPlayer.getPlayWhenReady()) {
                    pauseExoInternal();
                } else {
                    resumeExoInternal();
                }
                return START_STICKY;
            }
            dispatchToJs("kurfaNativeTogglePlay");
        } else if (ACTION_NEXT.equals(action)) {
            dispatchToJs("kurfaNativeNextTrack");
        } else if (ACTION_PREV.equals(action)) {
            dispatchToJs("kurfaNativePrevTrack");
        } else if (ACTION_STOP.equals(action)) {
            if (nativeExoMode && exoPlayer != null) {
                stopExoInternal(true);
            }
            nativeExoMode = false;
            isPlaying = false;
            KurfaWebView.keepAliveInBackground = false;
            releaseLocks();
            stopForegroundCompat(true);
            stopSelf();
            return START_NOT_STICKY;
        }

        if (!nativeExoMode) {
            if (isPlaying) {
                pausedByAudioFocus = false;
                requestAudioFocus();
                acquireLocks();
            } else {
                releaseLocks();
                if (!pausedByAudioFocus) {
                    abandonAudioFocus();
                }
            }
        }

        boolean asForeground = ACTION_SYNC.equals(action) || isPlaying;
        updateSessionAndNotification(asForeground);
        if (!isPlaying && (ACTION_SYNC.equals(action) || action == null)) {
            stopForegroundCompat(false);
        }
        loadArtworkAsyncIfNeeded();
        return START_STICKY;
    }

    private void updateSessionPlaybackStateOnly() {
        if (mediaSession == null) return;
        try {
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
        } catch (Throwable ignored) {}
    }

    private void requestAudioFocus() {
        if (audioManager == null || hasAudioFocus) return;
        try {
            int res;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                if (audioFocusRequest == null) {
                    AudioAttributes attrs = new AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                            .build();
                    audioFocusRequest = new AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
                            .setAudioAttributes(attrs)
                            .setAcceptsDelayedFocusGain(true)
                            .setOnAudioFocusChangeListener(audioFocusListener, MAIN_HANDLER)
                            .build();
                }
                res = audioManager.requestAudioFocus(audioFocusRequest);
            } else {
                @SuppressWarnings("deprecation")
                int legacyRes = audioManager.requestAudioFocus(
                        audioFocusListener,
                        AudioManager.STREAM_MUSIC,
                        AudioManager.AUDIOFOCUS_GAIN
                );
                res = legacyRes;
            }
            hasAudioFocus = (res == AudioManager.AUDIOFOCUS_REQUEST_GRANTED);
        } catch (Throwable ignored) {}
    }

    private void abandonAudioFocus() {
        if (audioManager == null || !hasAudioFocus) return;
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && audioFocusRequest != null) {
                audioManager.abandonAudioFocusRequest(audioFocusRequest);
            } else {
                @SuppressWarnings("deprecation")
                int ignored = audioManager.abandonAudioFocus(audioFocusListener);
            }
        } catch (Throwable ignored) {}
        hasAudioFocus = false;
    }

    private void dispatchPlayPauseJs(boolean play) {
        WebView wv = activeWebView.get();
        if (wv == null) return;
        String specificFn = play ? "kurfaNativePlay" : "kurfaNativePause";
        wv.post(() -> {
            try {
                wv.evaluateJavascript(
                        "if(typeof " + specificFn + "==='function'){" + specificFn + "();}else if(typeof kurfaNativeTogglePlay==='function'){kurfaNativeTogglePlay();}",
                        null
                );
            } catch (Throwable ignored) {}
        });
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

        if (asForeground && (isPlaying || (nativeExoMode && userRequestedPlayback))) {
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
                        MAIN_HANDLER.post(() -> {
                            if (activeService.get() == this) {
                                updateSessionAndNotification(isPlaying);
                            }
                        });
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
        nativeExoMode = false;
        isPlaying = false;
        KurfaWebView.keepAliveInBackground = false;
        cancelScreenOffFocusRecovery();
        stopTimeTick();
        releaseLocks();
        stopForegroundCompat(true);
        stopSelf();
    }

    @Override
    public void onDestroy() {
        if (activeService.get() == this) {
            activeService = new WeakReference<>(null);
        }
        cancelScreenOffFocusRecovery();
        stopTimeTick();
        if (exoPlayer != null) {
            try {
                exoPlayer.release();
            } catch (Throwable ignored) {}
            exoPlayer = null;
        }
        releaseLocks();
        abandonAudioFocus();
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
