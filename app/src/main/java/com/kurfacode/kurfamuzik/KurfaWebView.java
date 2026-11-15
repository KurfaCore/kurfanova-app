package com.kurfacode.kurfamuzik;

import android.content.Context;
import android.util.AttributeSet;
import android.view.View;
import android.webkit.WebView;

import androidx.annotation.NonNull;

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
        if (visibility != View.VISIBLE && keepAliveInBackground && isAttachedToWindow()) {
            super.onWindowVisibilityChanged(View.VISIBLE);
            return;
        }
        super.onWindowVisibilityChanged(visibility);
    }

    @Override
    public void dispatchWindowVisibilityChanged(int visibility) {
        if (visibility != View.VISIBLE && keepAliveInBackground && isAttachedToWindow()) {
            super.dispatchWindowVisibilityChanged(View.VISIBLE);
            return;
        }
        super.dispatchWindowVisibilityChanged(visibility);
    }

    @Override
    protected void onVisibilityChanged(@NonNull View changedView, int visibility) {
        if (visibility != View.VISIBLE && keepAliveInBackground && isAttachedToWindow()) {
            super.onVisibilityChanged(changedView, View.VISIBLE);
            return;
        }
        super.onVisibilityChanged(changedView, visibility);
    }

    @Override
    public void onVisibilityAggregated(boolean isVisible) {
        if (!isVisible && keepAliveInBackground && isAttachedToWindow()) {
            super.onVisibilityAggregated(true);
            return;
        }
        super.onVisibilityAggregated(isVisible);
    }
}
