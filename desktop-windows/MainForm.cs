using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KurfanovaMuzik;

public sealed class MainForm : Form
{
    private const string TargetUrl = "https://kurfanova.github.io/";
    private const string DesktopBridgeMode = "desktop-v1";

    private static readonly string[] AdFilterPatterns =
    [
        "*://doubleclick.net/*",
        "*://*.doubleclick.net/*",
        "*://googlesyndication.com/*",
        "*://*.googlesyndication.com/*",
        "*://googleadservices.com/*",
        "*://*.googleadservices.com/*",
        "*://2mdn.net/*",
        "*://*.2mdn.net/*",
        "*://adservice.google.*/*",
        "*://youtube.com/pagead/*",
        "*://*.youtube.com/pagead/*",
        "*://youtube.com/api/stats/ads*",
        "*://*.youtube.com/api/stats/ads*",
        "*://youtube.com/ptracking*",
        "*://*.youtube.com/ptracking*",
        "*://youtube.com/get_midroll_info*",
        "*://*.youtube.com/get_midroll_info*",
        "*://youtube.com/api/stats/qoe*",
        "*://*.youtube.com/api/stats/qoe*",
        "*://youtube-nocookie.com/pagead/*",
        "*://*.youtube-nocookie.com/pagead/*",
        "*://youtube-nocookie.com/api/stats/ads*",
        "*://*.youtube-nocookie.com/api/stats/ads*",
        "*://youtube-nocookie.com/ptracking*",
        "*://*.youtube-nocookie.com/ptracking*",
        "*://youtube-nocookie.com/get_midroll_info*",
        "*://*.youtube-nocookie.com/get_midroll_info*",
        "*://*.googlevideo.com/*oad=*",
        "*://*.googlevideo.com/*adformat=*"
    ];

    private static readonly string[] AuthPopupHosts =
    [
        "accounts.google.com",
        "kurfa-ai.firebaseapp.com",
        "appleid.apple.com",
        "github.com"
    ];

    private static readonly string[] InAppHosts =
    [
        "kurfanova.github.io",
        "kurfanova.kurfa.workers.dev",
        "accounts.google.com",
        "kurfa-ai.firebaseapp.com",
        "youtube.com",
        "youtube-nocookie.com",
        "googlevideo.com",
        "127.0.0.1",
        "localhost"
    ];

    private readonly AppPaths _paths;
    private readonly DesktopAppConfig _config;
    private readonly NativeDesktopEngine _engine = new();
    private readonly AutoUpdater _updater;
    private readonly WebView2 _webView = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private Icon? _embeddedIcon;
    private NotifyIcon? _trayIcon;
    private bool _restartForUpdate;
    private bool _allowExit;
    private bool _trayHintShown;
    private int _updatePromptShown;
    private volatile bool _webContentLoaded;
    private AutoUpdater.UpdateProgressInfo? _latestUpdateProgress;
    private MiniPlayerForm? _miniPlayer;
    private Icon? _trayCroppedIcon;
    private bool _darkTitleBar = true;

    public MainForm() : this(null, null)
    {
    }

    public MainForm(AppPaths? paths, DesktopAppConfig? config)
    {
        _paths = paths ?? new AppPaths(AppPaths.ResolveActiveInstallDir());
        _config = config ?? AppPaths.LoadOrDefaultConfig(_paths, AppPaths.GetNormalizedCurrentVersion());
        _updater = new AutoUpdater(_paths);

        Color darkBg = Color.FromArgb(11, 11, 20); // #0B0B14

        Text = "Kurfamüzik — Masaüstü";
        BackColor = darkBg;
        ForeColor = Color.White;
        Size = new Size(1280, 820);
        MinimumSize = new Size(860, 560);
        StartPosition = FormStartPosition.CenterScreen;

        _embeddedIcon = LoadEmbeddedIcon();
        if (_embeddedIcon is not null)
        {
            Icon = _embeddedIcon;
        }

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = darkBg;
        Controls.Add(_webView);

        Load += OnFormLoad;
        Activated += (_, _) => ApplyTitleBarDarkMode(_darkTitleBar);
        Resize += (_, _) => { ApplyTitleBarDarkMode(_darkTitleBar); UpdateWebViewMemoryMode(); };
        VisibleChanged += (_, _) => { if (Visible) ApplyTitleBarDarkMode(_darkTitleBar); UpdateWebViewMemoryMode(); };
        FormClosing += OnMainFormClosing;
        FormClosed += OnFormClosed;
    }

    private void UpdateWebViewMemoryMode()
    {
        try
        {
            CoreWebView2? core = _webView.CoreWebView2;
            if (core is null) return;
            bool isBackground = WindowState == FormWindowState.Minimized || !Visible;
            core.MemoryUsageTargetLevel = isBackground
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch { /* ignore if disposing */ }
    }

    private static Icon? LoadEmbeddedIcon(int preferredSize = 0)
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("KurfanovaMuzik.app.ico");
            if (stream is null) return null;
            return preferredSize > 0 ? new Icon(stream, preferredSize, preferredSize) : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    private static Icon? CreateCroppedTrayIcon()
    {
        try
        {
            using Icon? baseIco = LoadEmbeddedIcon(128) ?? LoadEmbeddedIcon(64);
            if (baseIco is null) return null;
            using Bitmap src = baseIco.ToBitmap();
            int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < src.Height; y++)
            {
                for (int x = 0; x < src.Width; x++)
                {
                    if (src.GetPixel(x, y).A > 85)
                    {
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX <= minX || maxY <= minY) return null;
            int side = Math.Max(maxX - minX + 1, maxY - minY + 1);
            int cx = (minX + maxX) / 2;
            int cy = (minY + maxY) / 2;
            var crop = new Rectangle(
                Math.Max(0, cx - side / 2),
                Math.Max(0, cy - side / 2),
                Math.Min(src.Width, side),
                Math.Min(src.Height, side));

            using var dest = new Bitmap(48, 48);
            using (Graphics g = Graphics.FromImage(dest))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, new Rectangle(0, 0, 48, 48), crop, GraphicsUnit.Pixel);
            }
            IntPtr hIcon = dest.GetHicon();
            using Icon temp = Icon.FromHandle(hIcon);
            var cloned = (Icon)temp.Clone();
            DestroyIcon(hIcon);
            return cloned;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyTitleBarDarkMode(bool dark)
    {
        _darkTitleBar = dark;
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            int val = dark ? 1 : 0;
            if (DwmSetWindowAttribute(Handle, 20, ref val, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(Handle, 19, ref val, sizeof(int));
            }
        }
        catch
        {
            // Ignore on older Windows versions
        }
    }

    private async void OnFormLoad(object? sender, EventArgs e)
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID("KurfaCore.Kurfamuzik");
            ApplyTitleBarDarkMode(true);
            _engine.Start();
            InitializeTrayIcon();

            Directory.CreateDirectory(_paths.WebView2DataDir);

            var envOptions = new CoreWebView2EnvironmentOptions(
                "--autoplay-policy=no-user-gesture-required --disk-cache-size=33554432 --media-cache-size=33554432 --disable-features=CalculateNativeWinOcclusion"
            );
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, _paths.WebView2DataDir, envOptions);
            await _webView.EnsureCoreWebView2Async(env);

            CoreWebView2 core = _webView.CoreWebView2;
            core.Settings.IsScriptEnabled = true;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDefaultScriptDialogsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = true;

            foreach (string pattern in AdFilterPatterns)
            {
                core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
            }
            core.WebResourceRequested += OnWebResourceRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += (_, _) =>
            {
                _webContentLoaded = true;
                ReplayLatestUpdateProgress();
            };

            await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildBridgeInjectionScript());

            core.Navigate(TargetUrl);

            _updater.UpdateProgressChanged += OnUpdateProgressChanged;
            _updater.UpdateReady += OnUpdateReady;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(4500, _shutdownCts.Token).ConfigureAwait(false);
                    await _updater.CheckAndStageAsync(_shutdownCts.Token, force: false).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            });
        }
        catch (WebView2RuntimeNotFoundException)
        {
            DialogResult choice = MessageBox.Show(
                this,
                "Microsoft Edge WebView2 Çalışma Zamanı bulunamadı.\n\nKurfamüzik Masaüstü uygulamasını çalıştırmak için WebView2 Runtime gereklidir. İndirme sayfasını açmak ister misiniz?",
                "WebView2 Gerekli",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (choice == DialogResult.Yes)
            {
                OpenExternalBrowser(new Uri("https://developer.microsoft.com/en-us/microsoft-edge/webview2/"));
            }
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Uygulama başlatılırken beklenmeyen bir hata oluştu:\n{ex.Message}",
                "Kurfamüzik Başlatma Hatası",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    private static string BuildBridgeInjectionScript()
    {
        return """
            (() => {
              try {
                const host = String(window.location && window.location.hostname || '').toLowerCase();

                // 1. YouTube Embed Zero-Ad Shield (protects Video Klip Modu / any fallback embed inside WebView2)
                if (host === 'www.youtube.com' || host === 'youtube.com' || host === 'www.youtube-nocookie.com' || host === 'youtube-nocookie.com') {
                  const stripAds = (obj) => {
                    if (!obj || typeof obj !== 'object') return obj;
                    try {
                      delete obj.adPlacements;
                      delete obj.playerAds;
                      delete obj.adSlots;
                      delete obj.adBreakHeartbeatParams;
                    } catch (_) {}
                    return obj;
                  };

                  try {
                    let cachedInitial = undefined;
                    Object.defineProperty(window, 'ytInitialPlayerResponse', {
                      configurable: true,
                      enumerable: true,
                      get() { return cachedInitial; },
                      set(v) { cachedInitial = stripAds(v); }
                    });
                  } catch (_) {}

                  try {
                    const origFetch = window.fetch;
                    if (typeof origFetch === 'function') {
                      window.fetch = async function(...args) {
                        const res = await origFetch.apply(this, args);
                        try {
                          const reqUrl = String((args[0] && args[0].url) || args[0] || '');
                          if (reqUrl.includes('/youtubei/v1/player') || reqUrl.includes('/youtubei/v1/next')) {
                            const txt = await res.clone().text();
                            const data = stripAds(JSON.parse(txt));
                            const hdrs = new Headers(res.headers);
                            hdrs.delete('content-length');
                            return new Response(JSON.stringify(data), { status: res.status, statusText: res.statusText, headers: hdrs });
                          }
                        } catch (_) {}
                        return res;
                      };
                    }
                  } catch (_) {}

                  const guardAdDom = () => {
                    try {
                      if (document.hidden) return;
                      const player = document.querySelector('.html5-video-player');
                      const hasRealAdUi = Boolean(document.querySelector('.ytp-ad-player-overlay, .ytp-ad-text, .ytp-ad-preview-container, .ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern'));
                      if (player && player.classList.contains('ad-showing') && hasRealAdUi) {
                        const v = player.querySelector('video');
                        if (v) {
                          v.muted = true;
                          if (Number.isFinite(v.duration) && v.duration > 0 && v.duration <= 90 && v.currentTime < v.duration - 0.15) {
                            v.currentTime = v.duration;
                          }
                        }
                        const skipBtn = document.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern');
                        if (skipBtn) skipBtn.click();
                      }
                    } catch (_) {}
                  };
                  setInterval(guardAdDom, 750);
                  return;
                }

                // 2. Kurfanova Host Native Desktop Bridge
                if (host !== 'kurfanova.github.io' && host !== 'kurfanova.kurfa.workers.dev' && host !== 'localhost') return;

                const activateBridge = () => {
                  try {
                    if (document.documentElement) {
                      document.documentElement.dataset.kurfaExt = 'desktop-v1';
                    }
                    window.dispatchEvent(new CustomEvent('kurfa-ext-ready', { detail: { mode: 'desktop-v1' } }));
                  } catch (_) {}
                };

                activateBridge();
                if (document.readyState === 'loading') {
                  document.addEventListener('DOMContentLoaded', activateBridge, { once: true });
                }

                window.addEventListener('message', (event) => {
                  if (event.source !== window || !event.data || event.data.__kurfaExt !== 'REQ') return;
                  if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(JSON.stringify(event.data));
                  }
                });

                // 3. Windows Desktop EXE Exclusive Update Overlay (never loaded on normal web)
                const ensureUpdaterStyles = () => {
                  if (document.getElementById('kurfa-win-upd-style')) return;
                  const s = document.createElement('style');
                  s.id = 'kurfa-win-upd-style';
                  s.textContent = `
                    @keyframes kurfaUpdSlideIn {
                      0% { opacity: 0; transform: translateY(14px) scale(0.97); }
                      100% { opacity: 1; transform: translateY(0) scale(1); }
                    }
                    @keyframes kurfaUpdSlideOut {
                      0% { opacity: 1; transform: translateY(0) scale(1); }
                      100% { opacity: 0; transform: translateY(10px) scale(0.97); }
                    }
                    @keyframes kurfaUpdSpin {
                      to { transform: rotate(360deg); }
                    }
                    @keyframes kurfaUpdCheckDraw {
                      0% { stroke-dashoffset: 24; opacity: 0; }
                      100% { stroke-dashoffset: 0; opacity: 1; }
                    }
                    #kurfa-win-updater-ui {
                      --k-upd-bg: rgba(255, 255, 255, 0.90);
                      --k-upd-border: rgba(15, 23, 42, 0.10);
                      --k-upd-fg: #0f172a;
                      --k-upd-muted: rgba(15, 23, 42, 0.62);
                      --k-upd-track: rgba(15, 23, 42, 0.08);
                      --k-upd-pill-bg: rgba(15, 23, 42, 0.05);
                      --k-upd-shadow: 0 18px 42px rgba(15, 23, 42, 0.14), 0 2px 6px rgba(15, 23, 42, 0.06);
                      position: fixed;
                      bottom: 98px;
                      right: 20px;
                      width: min(332px, calc(100vw - 28px));
                      z-index: 2147483000;
                      pointer-events: auto;
                      background: var(--k-upd-bg);
                      backdrop-filter: blur(24px) saturate(180%);
                      -webkit-backdrop-filter: blur(24px) saturate(180%);
                      border: 1px solid var(--k-upd-border);
                      border-radius: 16px;
                      padding: 13px 14px;
                      box-shadow: var(--k-upd-shadow);
                      color: var(--k-upd-fg);
                      font-family: 'Manrope', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
                      animation: kurfaUpdSlideIn 320ms cubic-bezier(0.16, 1, 0.3, 1) both;
                    }
                    html.dark #kurfa-win-updater-ui,
                    html.dark-mode #kurfa-win-updater-ui,
                    body.dark-mode #kurfa-win-updater-ui {
                      --k-upd-bg: rgba(20, 22, 34, 0.88);
                      --k-upd-border: rgba(255, 255, 255, 0.12);
                      --k-upd-fg: #f8fafc;
                      --k-upd-muted: rgba(226, 232, 240, 0.66);
                      --k-upd-track: rgba(255, 255, 255, 0.10);
                      --k-upd-pill-bg: rgba(255, 255, 255, 0.07);
                      --k-upd-shadow: 0 20px 48px rgba(0, 0, 0, 0.48), inset 0 1px 0 rgba(255, 255, 255, 0.12);
                    }
                    #kurfa-win-updater-ui.kurfa-upd-closing {
                      animation: kurfaUpdSlideOut 190ms cubic-bezier(0.4, 0, 1, 1) forwards;
                    }
                    .kurfa-upd-row {
                      display: flex;
                      align-items: center;
                      gap: 11px;
                    }
                    .kurfa-upd-icon {
                      width: 34px;
                      height: 34px;
                      border-radius: 10px;
                      flex-shrink: 0;
                      display: flex;
                      align-items: center;
                      justify-content: center;
                      background: rgba(225, 29, 72, 0.10);
                      color: #e11d48;
                    }
                    html.dark .kurfa-upd-icon,
                    html.dark-mode .kurfa-upd-icon,
                    body.dark-mode .kurfa-upd-icon {
                      background: rgba(255, 32, 86, 0.14);
                      color: #ff4778;
                    }
                    .kurfa-upd-icon.is-ready {
                      background: rgba(16, 185, 129, 0.12);
                      color: #059669;
                    }
                    html.dark .kurfa-upd-icon.is-ready,
                    html.dark-mode .kurfa-upd-icon.is-ready,
                    body.dark-mode .kurfa-upd-icon.is-ready {
                      background: rgba(16, 185, 129, 0.16);
                      color: #34d399;
                    }
                    .kurfa-upd-spin {
                      animation: kurfaUpdSpin 0.9s linear infinite;
                      transform-origin: center;
                    }
                    .kurfa-upd-check {
                      stroke-dasharray: 24;
                      stroke-dashoffset: 0;
                      animation: kurfaUpdCheckDraw 360ms cubic-bezier(0.16, 1, 0.3, 1) both;
                    }
                    .kurfa-upd-meta {
                      flex: 1;
                      min-width: 0;
                    }
                    .kurfa-upd-head {
                      display: flex;
                      align-items: center;
                      gap: 6px;
                    }
                    .kurfa-upd-title {
                      font-size: 12.5px;
                      font-weight: 700;
                      letter-spacing: -0.01em;
                      color: var(--k-upd-fg);
                      white-space: nowrap;
                      overflow: hidden;
                      text-overflow: ellipsis;
                    }
                    .kurfa-upd-badge {
                      font-size: 10px;
                      font-weight: 700;
                      padding: 1px 6px;
                      border-radius: 6px;
                      background: var(--k-upd-pill-bg);
                      color: var(--k-upd-muted);
                      flex-shrink: 0;
                      font-variant-numeric: tabular-nums;
                    }
                    .kurfa-upd-sub {
                      margin-top: 2px;
                      font-size: 11px;
                      color: var(--k-upd-muted);
                      font-variant-numeric: tabular-nums;
                      white-space: nowrap;
                      overflow: hidden;
                      text-overflow: ellipsis;
                    }
                    .kurfa-upd-close {
                      width: 24px;
                      height: 24px;
                      border-radius: 8px;
                      border: none;
                      background: transparent;
                      color: var(--k-upd-muted);
                      cursor: pointer;
                      display: flex;
                      align-items: center;
                      justify-content: center;
                      flex-shrink: 0;
                      transition: background 140ms, color 140ms;
                    }
                    .kurfa-upd-close:hover {
                      background: var(--k-upd-pill-bg);
                      color: var(--k-upd-fg);
                    }
                    .kurfa-upd-track {
                      margin-top: 10px;
                      height: 3px;
                      border-radius: 99px;
                      background: var(--k-upd-track);
                      overflow: hidden;
                    }
                    .kurfa-upd-fill {
                      height: 100%;
                      width: 0%;
                      border-radius: 99px;
                      background: linear-gradient(90deg, #e11d48, #fb7185);
                      transition: width 220ms cubic-bezier(0.22, 1, 0.36, 1);
                    }
                    .kurfa-upd-actions {
                      margin-top: 10px;
                      display: flex;
                      align-items: center;
                      gap: 7px;
                    }
                    .kurfa-upd-btn-primary {
                      flex: 1;
                      border: none;
                      border-radius: 10px;
                      padding: 7px 10px;
                      font-size: 11.5px;
                      font-weight: 700;
                      color: #ffffff;
                      background: #e11d48;
                      cursor: pointer;
                      transition: transform 120ms, background 140ms;
                    }
                    .kurfa-upd-btn-primary:hover { background: #be123c; }
                    .kurfa-upd-btn-primary:active { transform: scale(0.98); }
                    .kurfa-upd-btn-secondary {
                      border: 1px solid var(--k-upd-border);
                      border-radius: 10px;
                      padding: 7px 11px;
                      font-size: 11.5px;
                      font-weight: 600;
                      color: var(--k-upd-fg);
                      background: var(--k-upd-pill-bg);
                      cursor: pointer;
                      transition: background 140ms;
                    }
                    .kurfa-upd-btn-secondary:hover { background: var(--k-upd-track); }
                  `;
                  (document.head || document.documentElement).appendChild(s);
                };

                let __kurfaUpdDismissedForVersion = null;
                const formatMb = (bytes) => (Math.max(0, Number(bytes) || 0) / (1024 * 1024)).toFixed(1);
                const closeUpdaterCard = () => {
                  const el = document.getElementById('kurfa-win-updater-ui');
                  if (!el) return;
                  el.classList.add('kurfa-upd-closing');
                  setTimeout(() => { try { el.remove(); } catch(_) {} }, 200);
                };

                window.__kurfaDesktopUpdateUI = (info) => {
                  try {
                    if (!info || info.phase === 'hide') {
                      closeUpdaterCard();
                      return;
                    }
                    const phase = String(info.phase || 'downloading');
                    const ver = String(info.version || '');
                    if (__kurfaUpdDismissedForVersion && __kurfaUpdDismissedForVersion === ver + ':' + phase) {
                      return;
                    }
                    if (!document.body) return;
                    ensureUpdaterStyles();

                    let card = document.getElementById('kurfa-win-updater-ui');
                    if (!card) {
                      card = document.createElement('div');
                      card.id = 'kurfa-win-updater-ui';
                      card.setAttribute('role', 'status');
                      card.innerHTML = `
                        <div class="kurfa-upd-row">
                          <div class="kurfa-upd-icon" id="kurfa-upd-icon-box"></div>
                          <div class="kurfa-upd-meta">
                            <div class="kurfa-upd-head">
                              <span class="kurfa-upd-title" id="kurfa-upd-title"></span>
                              <span class="kurfa-upd-badge" id="kurfa-upd-ver"></span>
                            </div>
                            <div class="kurfa-upd-sub" id="kurfa-upd-sub"></div>
                          </div>
                          <button type="button" class="kurfa-upd-close" id="kurfa-upd-close-btn" title="Gizle">
                            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round"><path d="M18 6L6 18M6 6l12 12"/></svg>
                          </button>
                        </div>
                        <div class="kurfa-upd-track" id="kurfa-upd-track"><div class="kurfa-upd-fill" id="kurfa-upd-fill"></div></div>
                        <div class="kurfa-upd-actions" id="kurfa-upd-actions" style="display:none">
                          <button type="button" class="kurfa-upd-btn-primary" id="kurfa-upd-apply-btn">Yeniden Başlat</button>
                          <button type="button" class="kurfa-upd-btn-secondary" id="kurfa-upd-later-btn">Kapanışta</button>
                        </div>
                      `;
                      document.body.appendChild(card);

                      card.querySelector('#kurfa-upd-close-btn').addEventListener('click', () => {
                        __kurfaUpdDismissedForVersion = ver + ':' + (card.dataset.phase || phase);
                        closeUpdaterCard();
                      });
                      card.querySelector('#kurfa-upd-later-btn').addEventListener('click', () => {
                        __kurfaUpdDismissedForVersion = ver + ':ready';
                        closeUpdaterCard();
                      });
                      card.querySelector('#kurfa-upd-apply-btn').addEventListener('click', () => {
                        const sub = card.querySelector('#kurfa-upd-sub');
                        const applyBtn = card.querySelector('#kurfa-upd-apply-btn');
                        if (applyBtn) {
                          applyBtn.disabled = true;
                          applyBtn.textContent = 'Güncelleniyor...';
                        }
                        if (sub) sub.textContent = 'Yeni sürüm uygulanıyor...';
                        if (window.chrome && window.chrome.webview) {
                          window.chrome.webview.postMessage(JSON.stringify({
                            __kurfaExt: 'REQ',
                            requestId: 'upd_' + Date.now(),
                            action: 'APPLY_UPDATE'
                          }));
                        }
                      });
                    }

                    card.classList.remove('kurfa-upd-closing');
                    const prevPhase = card.dataset.phase;
                    card.dataset.phase = phase;

                    const iconBox = card.querySelector('#kurfa-upd-icon-box');
                    const titleEl = card.querySelector('#kurfa-upd-title');
                    const verEl = card.querySelector('#kurfa-upd-ver');
                    const subEl = card.querySelector('#kurfa-upd-sub');
                    const trackEl = card.querySelector('#kurfa-upd-track');
                    const fillEl = card.querySelector('#kurfa-upd-fill');
                    const actionsEl = card.querySelector('#kurfa-upd-actions');

                    if (verEl) verEl.textContent = ver ? 'v' + ver : 'Yeni';

                    if (phase === 'ready') {
                      if (prevPhase !== 'ready') {
                        iconBox.classList.add('is-ready');
                        iconBox.innerHTML = '<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path class="kurfa-upd-check" d="M5 13l4 4L19 7"/></svg>';
                      }
                      if (titleEl) titleEl.textContent = 'Güncelleme hazır';
                      if (subEl) subEl.textContent = 'Kurulum için hazır';
                      if (trackEl) trackEl.style.display = 'none';
                      if (actionsEl) actionsEl.style.display = 'flex';
                    } else {
                      if (prevPhase !== 'downloading' && prevPhase !== 'verifying') {
                        iconBox.classList.remove('is-ready');
                        iconBox.innerHTML = '<svg class="kurfa-upd-spin" width="17" height="17" viewBox="0 0 24 24" fill="none"><circle cx="12" cy="12" r="9" stroke="currentColor" stroke-opacity="0.20" stroke-width="2.4"/><path d="M12 3a9 9 0 0 1 9 9" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"/></svg>';
                      }
                      if (trackEl) trackEl.style.display = 'block';
                      if (actionsEl) actionsEl.style.display = 'none';
                      const pct = Math.max(0, Math.min(100, Number(info.percent) || 0));
                      if (fillEl) fillEl.style.width = pct + '%';

                      if (phase === 'verifying') {
                        if (titleEl) titleEl.textContent = 'Doğrulanıyor';
                        if (subEl) subEl.textContent = 'Güvenlik imza denetimi';
                      } else {
                        if (titleEl) titleEl.textContent = 'Güncelleme indiriliyor';
                        const total = Number(info.totalBytes) || 0;
                        const dl = Number(info.bytesDownloaded) || 0;
                        if (subEl) {
                          subEl.textContent = total > 0
                            ? `${formatMb(dl)} / ${formatMb(total)} MB • %${pct}`
                            : `Hazırlanıyor • %${pct}`;
                        }
                      }
                    }
                  } catch (_) {}
                };
              } catch (_) {}
            })();
            """;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_webView.CoreWebView2 is null) return;

        if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out Uri? uri))
        {
            string host = uri.Host.ToLowerInvariant();
            string pathAndQuery = uri.PathAndQuery;

            if (MatchesHostOrSubdomain(host, "googlevideo.com"))
            {
                bool isAdVideo = pathAndQuery.Contains("oad=", StringComparison.OrdinalIgnoreCase) ||
                                 pathAndQuery.Contains("adformat=", StringComparison.OrdinalIgnoreCase);
                if (!isAdVideo)
                {
                    return;
                }
            }
            else if (MatchesHostOrSubdomain(host, "youtube.com") || MatchesHostOrSubdomain(host, "youtube-nocookie.com"))
            {
                bool isNormalQoe = pathAndQuery.Contains("/api/stats/qoe", StringComparison.OrdinalIgnoreCase) &&
                                   !pathAndQuery.Contains("ad", StringComparison.OrdinalIgnoreCase);
                if (isNormalQoe)
                {
                    return;
                }
            }
            else if (MatchesHostOrSubdomain(host, "kurfanova.github.io") ||
                     MatchesHostOrSubdomain(host, "kurfanova.kurfa.workers.dev") ||
                     MatchesHostOrSubdomain(host, "accounts.google.com") ||
                     host == "127.0.0.1")
            {
                return;
            }
        }

        e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
            Stream.Null,
            403,
            "Blocked",
            "Content-Type: text/plain");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsAllowedBridgeOrigin(e.Source, out string targetOrigin))
        {
            return;
        }

        string? rawJson;
        try
        {
            rawJson = e.TryGetWebMessageAsString();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return;
        }

        string requestId;
        string action;
        string? query = null;
        bool filterSingles = false;
        string? videoId = null;
        string? downloadFormat = null;
        string? downloadTitle = null;
        string? revealFilePath = null;
        string? miniArtist = null;
        string? miniThumb = null;
        bool miniPlaying = false;
        bool miniMuted = false;
        double miniCur = 0;
        double miniDur = 0;
        bool themeDarkMode = true;
        bool? settingDesktopShortcut = null;
        bool? settingStartMenuShortcut = null;
        bool? settingRunOnStartup = null;
        bool? settingMinimizeToTray = null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            requestId = root.TryGetProperty("requestId", out JsonElement reqEl) && reqEl.ValueKind == JsonValueKind.String
                ? (reqEl.GetString() ?? string.Empty)
                : string.Empty;
            action = root.TryGetProperty("action", out JsonElement actEl) && actEl.ValueKind == JsonValueKind.String
                ? (actEl.GetString() ?? string.Empty)
                : string.Empty;

            if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            if (root.TryGetProperty("payload", out JsonElement payload) && payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("query", out JsonElement qEl) && qEl.ValueKind == JsonValueKind.String)
                {
                    query = qEl.GetString();
                }
                if (payload.TryGetProperty("filterSingles", out JsonElement fsEl) &&
                    (fsEl.ValueKind == JsonValueKind.True || fsEl.ValueKind == JsonValueKind.False))
                {
                    filterSingles = fsEl.GetBoolean();
                }
                if (payload.TryGetProperty("videoId", out JsonElement vEl) && vEl.ValueKind == JsonValueKind.String)
                {
                    videoId = vEl.GetString();
                }
                if (payload.TryGetProperty("format", out JsonElement fmtEl) && fmtEl.ValueKind == JsonValueKind.String)
                {
                    downloadFormat = fmtEl.GetString();
                }
                if (payload.TryGetProperty("title", out JsonElement titleEl) && titleEl.ValueKind == JsonValueKind.String)
                {
                    downloadTitle = titleEl.GetString();
                }
                if (payload.TryGetProperty("filePath", out JsonElement fpEl) && fpEl.ValueKind == JsonValueKind.String)
                {
                    revealFilePath = fpEl.GetString();
                }
                if (payload.TryGetProperty("artist", out JsonElement maEl) && maEl.ValueKind == JsonValueKind.String)
                {
                    miniArtist = maEl.GetString();
                }
                if (payload.TryGetProperty("thumbnail", out JsonElement mtThumbEl) && mtThumbEl.ValueKind == JsonValueKind.String)
                {
                    miniThumb = mtThumbEl.GetString();
                }
                if (payload.TryGetProperty("isPlaying", out JsonElement mpEl) &&
                    (mpEl.ValueKind == JsonValueKind.True || mpEl.ValueKind == JsonValueKind.False))
                {
                    miniPlaying = mpEl.GetBoolean();
                }
                if (payload.TryGetProperty("isMuted", out JsonElement mmEl) &&
                    (mmEl.ValueKind == JsonValueKind.True || mmEl.ValueKind == JsonValueKind.False))
                {
                    miniMuted = mmEl.GetBoolean();
                }
                if (payload.TryGetProperty("cur", out JsonElement mcEl) && mcEl.ValueKind == JsonValueKind.Number)
                {
                    miniCur = mcEl.GetDouble();
                }
                if (payload.TryGetProperty("dur", out JsonElement mdEl) && mdEl.ValueKind == JsonValueKind.Number)
                {
                    miniDur = mdEl.GetDouble();
                }
                if (payload.TryGetProperty("dark", out JsonElement darkEl) &&
                    (darkEl.ValueKind == JsonValueKind.True || darkEl.ValueKind == JsonValueKind.False))
                {
                    themeDarkMode = darkEl.GetBoolean();
                }
                if (payload.TryGetProperty("desktopShortcut", out JsonElement dsEl) &&
                    (dsEl.ValueKind == JsonValueKind.True || dsEl.ValueKind == JsonValueKind.False))
                {
                    settingDesktopShortcut = dsEl.GetBoolean();
                }
                if (payload.TryGetProperty("startMenuShortcut", out JsonElement smEl) &&
                    (smEl.ValueKind == JsonValueKind.True || smEl.ValueKind == JsonValueKind.False))
                {
                    settingStartMenuShortcut = smEl.GetBoolean();
                }
                if (payload.TryGetProperty("runOnStartup", out JsonElement rsEl) &&
                    (rsEl.ValueKind == JsonValueKind.True || rsEl.ValueKind == JsonValueKind.False))
                {
                    settingRunOnStartup = rsEl.GetBoolean();
                }
                if (payload.TryGetProperty("minimizeToTray", out JsonElement mtEl) &&
                    (mtEl.ValueKind == JsonValueKind.True || mtEl.ValueKind == JsonValueKind.False))
                {
                    settingMinimizeToTray = mtEl.GetBoolean();
                }
                if (payload.TryGetProperty("key", out JsonElement keyEl) &&
                    keyEl.ValueKind == JsonValueKind.String &&
                    payload.TryGetProperty("value", out JsonElement valEl) &&
                    (valEl.ValueKind == JsonValueKind.True || valEl.ValueKind == JsonValueKind.False))
                {
                    bool v = valEl.GetBoolean();
                    switch (keyEl.GetString())
                    {
                        case "desktopShortcut":
                        case "createDesktopShortcut":
                            settingDesktopShortcut = v;
                            break;
                        case "startMenuShortcut":
                        case "createStartMenuShortcut":
                            settingStartMenuShortcut = v;
                            break;
                        case "runOnStartup":
                        case "launchOnStartup":
                            settingRunOnStartup = v;
                            break;
                        case "minimizeToTray":
                            settingMinimizeToTray = v;
                            break;
                    }
                }
            }
        }
        catch
        {
            return;
        }

        CancellationToken ct = _shutdownCts.Token;
        _ = Task.Run(async () =>
        {
            object responseObj;
            try
            {
                switch (action.ToUpperInvariant())
                {
                    case "PING":
                        responseObj = new { success = true, mode = DesktopBridgeMode, version = _updater.CurrentVersion.ToString() };
                        break;

                    case "CHECK_UPDATE":
                        await _updater.CheckAndStageAsync(ct, force: true).ConfigureAwait(false);
                        responseObj = new
                        {
                            success = true,
                            version = _updater.CurrentVersion.ToString(),
                            hasUpdate = _updater.HasStagedUpdate,
                            stagedVersion = _updater.StagedVersion?.ToString()
                        };
                        break;

                    case "APPLY_UPDATE":
                        if (_updater.HasStagedUpdate)
                        {
                            _restartForUpdate = true;
                            _allowExit = true;
                            BeginInvoke(Close);
                            responseObj = new { success = true };
                        }
                        else
                        {
                            responseObj = new { success = false, error = "No staged update" };
                        }
                        break;

                    case "SEARCH":
                        responseObj = await _engine.SearchYouTubeLocalAsync(query, filterSingles, ct).ConfigureAwait(false);
                        break;

                    case "STREAM":
                    {
                        NativeDesktopEngine.ResolvedAudio? resolved =
                            await _engine.ResolveAudioStreamAsync(videoId, ct).ConfigureAwait(false);
                        if (resolved is not null)
                        {
                            responseObj = new
                            {
                                success = true,
                                videoId = resolved.VideoId,
                                streamUrl = _engine.GetLocalAudioUrl(resolved.VideoId),
                                mimeType = resolved.MimeType,
                                itag = resolved.Itag,
                                title = resolved.Title
                            };
                        }
                        else
                        {
                            responseObj = new { success = false, error = "Audio stream unavailable" };
                        }
                        break;
                    }

                    case "DOWNLOAD_MEDIA":
                    {
                        NativeDesktopEngine.DownloadMediaResult dlRes = await _engine.DownloadMediaToDownloadsAsync(
                            videoId,
                            downloadFormat ?? "audio",
                            downloadTitle,
                            (downloaded, total, percent) =>
                            {
                                PostBridgeEvent(requestId, "DOWNLOAD_PROGRESS", new
                                {
                                    bytesDownloaded = downloaded,
                                    totalBytes = total,
                                    percent
                                }, targetOrigin);
                            },
                            ct).ConfigureAwait(false);

                        responseObj = new
                        {
                            success = dlRes.Success,
                            fileName = dlRes.FileName,
                            filePath = dlRes.FilePath,
                            mimeType = dlRes.MimeType,
                            bytesDownloaded = dlRes.BytesDownloaded,
                            error = dlRes.Error
                        };
                        break;
                    }

                    case "OPEN_DOWNLOAD_FOLDER":
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(revealFilePath) && File.Exists(revealFilePath))
                            {
                                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{revealFilePath}\"") { UseShellExecute = true });
                            }
                            else
                            {
                                string downloadsDir = Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                    "Downloads");
                                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{downloadsDir}\"") { UseShellExecute = true });
                            }
                            responseObj = new { success = true };
                        }
                        catch (Exception ex)
                        {
                            responseObj = new { success = false, error = ex.Message };
                        }
                        break;

                    case "MINIMIZE_TO_TRAY":
                        BeginInvoke(HideToTray);
                        responseObj = new { success = true };
                        break;

                    case "GET_DESKTOP_SETTINGS":
                        responseObj = new
                        {
                            success = true,
                            version = _updater.CurrentVersion.ToString(3),
                            installDir = _paths.InstallDir,
                            desktopShortcut = _config.DesktopShortcut,
                            startMenuShortcut = _config.StartMenuShortcut,
                            runOnStartup = _config.RunOnStartup,
                            minimizeToTray = _config.MinimizeToTray
                        };
                        break;

                    case "SET_DESKTOP_SETTINGS":
                        lock (_config)
                        {
                            if (settingDesktopShortcut.HasValue) _config.DesktopShortcut = settingDesktopShortcut.Value;
                            if (settingStartMenuShortcut.HasValue) _config.StartMenuShortcut = settingStartMenuShortcut.Value;
                            if (settingRunOnStartup.HasValue) _config.RunOnStartup = settingRunOnStartup.Value;
                            if (settingMinimizeToTray.HasValue) _config.MinimizeToTray = settingMinimizeToTray.Value;

                            AppPaths.SaveConfig(_paths, _config);
                            InstallerService.ApplyShortcutsAndRegistry(_paths, _config, _updater.CurrentVersion);
                        }
                        responseObj = new
                        {
                            success = true,
                            version = _updater.CurrentVersion.ToString(3),
                            installDir = _paths.InstallDir,
                            desktopShortcut = _config.DesktopShortcut,
                            startMenuShortcut = _config.StartMenuShortcut,
                            runOnStartup = _config.RunOnStartup,
                            minimizeToTray = _config.MinimizeToTray
                        };
                        break;

                    case "OPEN_INSTALL_FOLDER":
                        try
                        {
                            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.InstallDir}\"") { UseShellExecute = true });
                            responseObj = new { success = true };
                        }
                        catch (Exception ex)
                        {
                            responseObj = new { success = false, error = ex.Message };
                        }
                        break;

                    case "CREATE_DESKTOP_SHORTCUT":
                        lock (_config)
                        {
                            _config.DesktopShortcut = true;
                            AppPaths.SaveConfig(_paths, _config);
                            InstallerService.ApplyShortcutsAndRegistry(_paths, _config, _updater.CurrentVersion);
                        }
                        responseObj = new { success = true };
                        break;

                    case "TOGGLE_MINI_PLAYER":
                    {
                        bool nowVisible = ToggleOrUpdateMiniPlayer(true, downloadTitle, miniArtist, miniThumb, miniPlaying, miniMuted, miniCur, miniDur);
                        responseObj = new { success = true, visible = nowVisible };
                        break;
                    }

                    case "UPDATE_MINI_PLAYER":
                    {
                        ToggleOrUpdateMiniPlayer(false, downloadTitle, miniArtist, miniThumb, miniPlaying, miniMuted, miniCur, miniDur);
                        responseObj = new { success = true };
                        break;
                    }

                    case "SET_THEME_MODE":
                    {
                        BeginInvoke(() => ApplyTitleBarDarkMode(themeDarkMode));
                        responseObj = new { success = true };
                        break;
                    }

                    default:
                        responseObj = new { success = false, error = "Unsupported action" };
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                responseObj = new { success = false, error = ex.Message };
            }

            PostBridgeResponse(requestId, responseObj, targetOrigin);
        }, ct);
    }

    private bool ToggleOrUpdateMiniPlayer(
        bool toggleVisibility,
        string? title,
        string? artist,
        string? thumbUrl,
        bool isPlaying,
        bool isMuted,
        double curSec,
        double durSec)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        bool visible = false;
        void RunOnUi()
        {
            if (IsDisposed) return;
            if (_miniPlayer is null || _miniPlayer.IsDisposed)
            {
                _miniPlayer = new MiniPlayerForm(
                    onPrev: () => ExecuteMiniPlayerJs("if(typeof window.playPrevTrack==='function')window.playPrevTrack();"),
                    onTogglePlay: () => ExecuteMiniPlayerJs("if(typeof window.togglePlay==='function')window.togglePlay();"),
                    onNext: () => ExecuteMiniPlayerJs("if(typeof window.playNextTrack==='function')window.playNextTrack();"),
                    onToggleMute: () => ExecuteMiniPlayerJs("if(typeof window.toggleMute==='function')window.toggleMute();"),
                    onSeekFraction: frac => ExecuteMiniPlayerJs($"if(typeof window.seekPlaybackToFraction==='function')window.seekPlaybackToFraction({frac.ToString(System.Globalization.CultureInfo.InvariantCulture)});"),
                    onRestoreMain: () =>
                    {
                        Show();
                        WindowState = FormWindowState.Normal;
                        Activate();
                    });
            }

            _miniPlayer.UpdateState(title, artist, thumbUrl, isPlaying, isMuted, curSec, durSec);
            if (toggleVisibility)
            {
                if (_miniPlayer.Visible)
                {
                    _miniPlayer.Hide();
                }
                else
                {
                    _miniPlayer.Show();
                }
            }
            visible = _miniPlayer.Visible;
        }

        if (InvokeRequired)
        {
            Invoke(RunOnUi);
        }
        else
        {
            RunOnUi();
        }
        return visible;
    }

    private void ExecuteMiniPlayerJs(string script)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(async () =>
        {
            try
            {
                if (_webView.CoreWebView2 is not null)
                {
                    await _webView.CoreWebView2.ExecuteScriptAsync(script);
                }
            }
            catch
            {
                // Ignore transient WebView errors
            }
        });
    }

    private void PostBridgeResponse(string requestId, object responseObj, string targetOrigin)
    {
        if (IsDisposed || !IsHandleCreated) return;

        string messageJson = JsonSerializer.Serialize(new
        {
            __kurfaExt = "RES",
            requestId,
            response = responseObj
        });
        string originJson = JsonSerializer.Serialize(targetOrigin);
        string script = $"window.postMessage({messageJson}, {originJson});";

        ExecuteBridgeScript(script);
    }

    private void PostBridgeEvent(string requestId, string eventName, object payloadObj, string targetOrigin)
    {
        if (IsDisposed || !IsHandleCreated) return;

        string messageJson = JsonSerializer.Serialize(new
        {
            __kurfaExt = "EVT",
            requestId,
            @event = eventName,
            payload = payloadObj
        });
        string originJson = JsonSerializer.Serialize(targetOrigin);
        ExecuteBridgeScript($"window.postMessage({messageJson}, {originJson});");
    }

    private void ExecuteBridgeScript(string script)
    {
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && _webView.CoreWebView2 is not null)
                {
                    _ = _webView.CoreWebView2.ExecuteScriptAsync(script);
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // Form closing
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed during shutdown
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        if (IsExternalDownloadUri(uri))
        {
            e.Handled = true;
            OpenExternalBrowser(uri);
            return;
        }

        string host = uri.Host.ToLowerInvariant();
        if (MatchesAnyHost(host, AuthPopupHosts))
        {
            // Allow Google / Firebase / GitHub OAuth popups cleanly
            e.Handled = false;
            return;
        }

        if (MatchesHostOrSubdomain(host, "kurfanova.github.io") && _webView.CoreWebView2 is not null)
        {
            e.Handled = true;
            _webView.CoreWebView2.Navigate(uri.AbsoluteUri);
            return;
        }

        e.Handled = true;
        OpenExternalBrowser(uri);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsExternalDownloadUri(uri))
        {
            e.Cancel = true;
            OpenExternalBrowser(uri);
            return;
        }

        string host = uri.Host.ToLowerInvariant();
        if (e.IsUserInitiated && !MatchesAnyHost(host, InAppHosts))
        {
            e.Cancel = true;
            OpenExternalBrowser(uri);
        }
    }

    private static bool IsAllowedBridgeOrigin(string? source, out string targetOrigin)
    {
        targetOrigin = string.Empty;
        if (string.IsNullOrWhiteSpace(source) || !Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        bool isKurfanova = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                           (uri.Host.Equals("kurfanova.github.io", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.Equals("kurfanova.kurfa.workers.dev", StringComparison.OrdinalIgnoreCase)) &&
                           uri.IsDefaultPort;

        bool isLocalhost = (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                            uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) &&
                           uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        if (!isKurfanova && !isLocalhost)
        {
            return false;
        }

        targetOrigin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static bool IsExternalDownloadUri(Uri uri)
    {
        string path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".exe", StringComparison.Ordinal) ||
            path.EndsWith(".apk", StringComparison.Ordinal) ||
            path.EndsWith(".zip", StringComparison.Ordinal) ||
            path.EndsWith(".msi", StringComparison.Ordinal))
        {
            return true;
        }

        string host = uri.Host.ToLowerInvariant();
        if (MatchesHostOrSubdomain(host, "github.com") ||
            MatchesHostOrSubdomain(host, "githubusercontent.com"))
        {
            return path.Contains("/releases/", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool MatchesAnyHost(string host, string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (MatchesHostOrSubdomain(host, candidate))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesHostOrSubdomain(string host, string domain)
    {
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenExternalBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore shell launch errors
        }
    }

    private void OnUpdateProgressChanged(AutoUpdater.UpdateProgressInfo info)
    {
        _latestUpdateProgress = info;
        if (IsDisposed || !IsHandleCreated || !_webContentLoaded) return;

        PushUpdateProgressToWebView(info);
    }

    private void ReplayLatestUpdateProgress()
    {
        AutoUpdater.UpdateProgressInfo? info = _latestUpdateProgress;
        if (info is null || IsDisposed || !IsHandleCreated) return;

        PushUpdateProgressToWebView(info);
    }

    private void PushUpdateProgressToWebView(AutoUpdater.UpdateProgressInfo info)
    {
        try
        {
            string payloadJson = JsonSerializer.Serialize(new
            {
                phase = info.Phase,
                version = info.Version.ToString(),
                notes = info.Notes ?? string.Empty,
                bytesDownloaded = info.BytesDownloaded,
                totalBytes = info.TotalBytes,
                percent = info.Percent
            });

            BeginInvoke(() =>
            {
                if (!IsDisposed && _webView.CoreWebView2 is not null)
                {
                    _ = _webView.CoreWebView2.ExecuteScriptAsync(
                        $"if (typeof window.__kurfaDesktopUpdateUI === 'function') {{ window.__kurfaDesktopUpdateUI({payloadJson}); }}");
                }
            });
        }
        catch
        {
            // Ignore shutdown races
        }
    }

    private void OnUpdateReady(Version version, string? notes)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (Interlocked.CompareExchange(ref _updatePromptShown, 1, 0) != 0) return;

        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;

                if (_webContentLoaded && _webView.CoreWebView2 is not null)
                {
                    string verJson = JsonSerializer.Serialize(version.ToString());
                    _ = _webView.CoreWebView2.ExecuteScriptAsync(
                        $"if (typeof window.__kurfaOnNativeUpdateReady === 'function') {{ window.__kurfaOnNativeUpdateReady({{ version: {verJson}, platform: 'windows', uiHandled: true }}); }}");
                    PushUpdateProgressToWebView(new AutoUpdater.UpdateProgressInfo(
                        "ready",
                        version,
                        notes,
                        0,
                        0,
                        100));
                    return;
                }

                string noteLine = string.IsNullOrWhiteSpace(notes) ? string.Empty : $"\nYenilikler: {notes}\n";
                DialogResult choice = MessageBox.Show(
                    this,
                    $"Kurfamüzik v{version} arka planda indirildi ve doğrulandı.{noteLine}\nŞimdi güncellenip yeniden başlatılsın mı?\n(\"Hayır\" derseniz uygulamayı kapattığınızda otomatik güncellenecek.)",
                    "Kurfamüzik Otomatik Güncelleme",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (choice == DialogResult.Yes)
                {
                    _restartForUpdate = true;
                    Close();
                }
            });
        }
        catch
        {
            // Ignore form shutdown races
        }
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayCroppedIcon = CreateCroppedTrayIcon();
            _trayIcon = new NotifyIcon
            {
                Text = $"Kurfamüzik v{_updater.CurrentVersion.ToString(3)}",
                Icon = _trayCroppedIcon ?? LoadEmbeddedIcon(48) ?? _embeddedIcon ?? SystemIcons.Application,
                Visible = true
            };

            Color darkBg = Color.FromArgb(24, 24, 27);
            Color textLight = Color.FromArgb(244, 244, 245);
            Color textMuted = Color.FromArgb(161, 161, 170);
            Color dangerRed = Color.FromArgb(251, 113, 133);

            var menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                ShowCheckMargin = false,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
                BackColor = darkBg,
                ForeColor = textLight,
                Padding = new Padding(6, 6, 6, 6)
            };
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkTrayColorTable());

            var itemHeader = new ToolStripMenuItem($"Kurfamüzik v{_updater.CurrentVersion.ToString(3)}")
            {
                Enabled = false,
                ForeColor = textMuted,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                Padding = new Padding(10, 5, 16, 5)
            };

            var itemToggleWindow = new ToolStripMenuItem("Pencereyi Aç", null, (_, _) =>
            {
                if (Visible && WindowState != FormWindowState.Minimized)
                    HideToTray();
                else
                    RestoreWindow();
            })
            {
                ForeColor = textLight,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10, 7, 16, 7)
            };

            var itemCheckUpdate = new ToolStripMenuItem("Güncelleme Denetle", null, async (_, _) =>
            {
                _trayIcon.ShowBalloonTip(1500, "Kurfamüzik", "Güncellemeler denetleniyor...", ToolTipIcon.Info);
                await _updater.CheckAndStageAsync(_shutdownCts.Token, force: true);
                if (!_updater.HasStagedUpdate)
                {
                    _trayIcon.ShowBalloonTip(2000, "Kurfamüzik", "Uygulamanız güncel.", ToolTipIcon.Info);
                }
            })
            {
                ForeColor = textLight,
                Padding = new Padding(10, 6, 16, 6)
            };

            var itemExit = new ToolStripMenuItem("Çıkış", null, (_, _) =>
            {
                _allowExit = true;
                Close();
            })
            {
                ForeColor = dangerRed,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10, 7, 16, 7)
            };

            menu.Opening += (_, _) =>
            {
                bool isVisible = Visible && WindowState != FormWindowState.Minimized;
                itemToggleWindow.Text = isVisible ? "Küçült" : "Pencereyi Aç";
            };

            menu.Items.Add(itemHeader);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(itemToggleWindow);
            menu.Items.Add(itemCheckUpdate);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(itemExit);

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) =>
            {
                if (Visible && WindowState != FormWindowState.Minimized)
                    HideToTray();
                else
                    RestoreWindow();
            };
        }
        catch
        {
            // Ignore tray initialization errors
        }
    }

    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowExit || _restartForUpdate || e.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        bool minimizeToTray;
        lock (_config)
        {
            minimizeToTray = _config.MinimizeToTray;
        }

        if (minimizeToTray && _trayIcon is not null)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private void HideToTray()
    {
        try
        {
            ShowInTaskbar = false;
            Hide();
            UpdateWebViewMemoryMode();
            if (!_trayHintShown && _trayIcon is not null)
            {
                _trayHintShown = true;
                _trayIcon.ShowBalloonTip(
                    2200,
                    "Kurfamüzik",
                    "Arka planda çalışmaya devam ediyor. Geri açmak için tepsi simgesine çift tıklayın.",
                    ToolTipIcon.Info);
            }
        }
        catch { /* ignore tray hide races */ }
    }

    public void BringExistingWindowToFront()
    {
        RestoreWindow();
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        BringToFront();
        Activate();
        UpdateWebViewMemoryMode();
    }

    private void LaunchSetup()
    {
        try
        {
            ExistingInstallInfo existing = InstallerService.DetectExistingInstallation();
            using var setup = new SetupForm(existing);
            if (setup.ShowDialog(this) == DialogResult.OK)
            {
                _allowExit = true;
                Close();
            }
        }
        catch { }
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        try { _shutdownCts.Cancel(); } catch { /* ignore */ }
        if (_updater.HasStagedUpdate)
        {
            _updater.TryApplyPendingUpdate(relaunch: _restartForUpdate);
        }
        _trayIcon?.Dispose();
        _updater.Dispose();
        _engine.Dispose();
        _webView.Dispose();
        _embeddedIcon?.Dispose();
        _shutdownCts.Dispose();
    }

    private sealed class DarkTrayColorTable : ProfessionalColorTable
    {
        private readonly Color _bg = Color.FromArgb(24, 24, 27);
        private readonly Color _selected = Color.FromArgb(39, 39, 42);
        private readonly Color _border = Color.FromArgb(46, 46, 52);

        public override Color ToolStripDropDownBackground => _bg;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
        public override Color MenuBorder => _border;
        public override Color MenuItemBorder => _selected;
        public override Color MenuItemSelected => _selected;
        public override Color MenuItemSelectedGradientBegin => _selected;
        public override Color MenuItemSelectedGradientEnd => _selected;
        public override Color MenuItemPressedGradientBegin => _selected;
        public override Color MenuItemPressedGradientEnd => _selected;
        public override Color SeparatorDark => _border;
        public override Color SeparatorLight => _bg;
    }
}
