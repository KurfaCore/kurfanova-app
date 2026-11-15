package com.kurfacode.kurfamuzik;

import android.content.Context;
import android.util.AttributeSet;
import android.view.View;
import android.webkit.WebView;

/**
 * Background-resilient WebView that prevents Chromium from suspending media timers
 * and HTMLAudioElement/YouTube playback when the user presses the Home button or
 * switches apps while KurfaPlaybackService is actively playing audio.
 */
public class KurfaWebView extends WebView {

    public static volatile boolean keepAliveInBackground = false;

    public KurfaWebView(Context context) {
        super(context);
    }

    public KurfaWebView(Context context, AttributeSet attrs) {
        super(context, attrs);
    }

    public KurfaWebView(Context context, AttributeSet attrs, int defStyleAttr) {
        super(context, attrs, defStyleAttr);
    }

    @Override
    protected void onWindowVisibilityChanged(int visibility) {
        if (visibility != View.VISIBLE && keepAliveInBackground) {
            super.onWindowVisibilityChanged(View.VISIBLE);
            return;
        }
        super.onWindowVisibilityChanged(visibility);
    }
}
