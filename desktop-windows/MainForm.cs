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
    private const string FallbackTargetUrl = "https://kurfanova.kurfa.workers.dev/";
    private const string TestTargetUrl = "https://kurfatest.kurfa.workers.dev/";
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
        "kurfatest.kurfa.workers.dev",
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
    private readonly bool _isTestMode;
    private readonly Uri _initialTargetUri;
    private readonly string? _customTargetHost;
    private readonly NativeDesktopEngine _engine = new();
    private readonly DiscordRpcService _discordRpc = new();
    private readonly AutoUpdater _updater;
    private readonly WebView2 _webView = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SemaphoreSlim _poTokenUiLock = new(1, 1);
    private CoreWebView2Environment? _webViewEnv;
    private WebView2? _poTokenWebView;
    private bool _poTokenPageReady;
    private string? _currentTrackTitle;
    private string? _currentTrackArtist;
    private Icon? _embeddedIcon;
    private NotifyIcon? _trayIcon;
    private bool _restartForUpdate;
    private bool _allowExit;
    private bool _trayHintShown;
    private int _updatePromptShown;
    private volatile bool _webContentLoaded;
    private bool _detachedUpdaterForApply;
    private AutoUpdater.UpdateProgressInfo? _latestUpdateProgress;
    private MiniPlayerForm? _miniPlayer;
    private Icon? _trayCroppedIcon;
    private TrayMenuForm? _trayMenu;
    private bool _darkTitleBar = true;
    private bool _triedFallbackOrigin;
    private bool _isAudioPlaying;

    public MainForm() : this(null, null, false, null)
    {
    }

    public MainForm(AppPaths? paths, DesktopAppConfig? config) : this(paths, config, false, null)
    {
    }

    public MainForm(AppPaths? paths, DesktopAppConfig? config, bool isTestMode, string? customTargetUrl = null)
    {
        _isTestMode = isTestMode;
        _paths = paths ?? new AppPaths(isTestMode ? AppPaths.DefaultTestInstallDir : AppPaths.ResolveActiveInstallDir());
        _config = config ?? AppPaths.LoadOrDefaultConfig(_paths, AppPaths.GetNormalizedCurrentVersion());
        _updater = new AutoUpdater(_paths);

        if (!string.IsNullOrWhiteSpace(customTargetUrl) &&
            Uri.TryCreate(customTargetUrl, UriKind.Absolute, out Uri? customUri) &&
            (customUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
             customUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
        {
            _initialTargetUri = customUri;
            _customTargetHost = customUri.Host.ToLowerInvariant();
        }
        else
        {
            _initialTargetUri = new Uri(_isTestMode ? TestTargetUrl : TargetUrl);
            _customTargetHost = null;
        }

        Color darkBg = Color.FromArgb(11, 11, 20); // #0B0B14

        Text = GetMainWindowTitle(_config.Language);
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
        Resize += (_, _) => UpdateWebViewMemoryMode();
        VisibleChanged += (_, _) => { if (Visible) ApplyTitleBarDarkMode(_darkTitleBar); UpdateWebViewMemoryMode(); };
        FormClosing += OnMainFormClosing;
        FormClosed += OnFormClosed;
    }

    private static string NormalizeLanguage(string? lang) =>
        string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";

    private string GetMainWindowTitle(string? lang)
    {
        if (_isTestMode)
        {
            return "KurfaTest — Önizleme";
        }
        return NormalizeLanguage(lang) == "en" ? "Kurfamüzik — Desktop" : "Kurfamüzik — Masaüstü";
    }

    private void ApplyLanguage(string? lang)
    {
        string normalized = NormalizeLanguage(lang);
        Text = GetMainWindowTitle(normalized);
        _trayMenu?.SetLanguage(normalized);
        _miniPlayer?.SetLanguage(normalized);
    }

    private void UpdateWebViewMemoryMode()
    {
        try
        {
            CoreWebView2? core = _webView.CoreWebView2;
            if (core is null) return;
            bool isBackground = WindowState == FormWindowState.Minimized || !Visible;
            bool keepActiveForAudio = _isAudioPlaying || (_miniPlayer is { Visible: true });
            core.MemoryUsageTargetLevel = (isBackground && !keepActiveForAudio)
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
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static Icon? LoadTrayIconForCurrentDpi()
    {
        try
        {
            int smCx = GetSystemMetrics(49); // SM_CXSMICON (DPI-aware small tray icon size)
            int size = smCx >= 16 && smCx <= 64 ? smCx : 16;
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KurfanovaMuzik.tray.ico")
                ?? Assembly.GetExecutingAssembly().GetManifestResourceStream("KurfanovaMuzik.app.ico");
            if (stream is null) return null;
            return new Icon(stream, size, size);
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
            // Attribute 20 works on Win10 20H1+ & Win11; fallback 19 supports Win10 1809–1909
            if (DwmSetWindowAttribute(Handle, 20, ref val, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(Handle, 19, ref val, sizeof(int));
            }
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int round = 2; // DWMWCP_ROUND
                _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            }
        }
        catch
        {
            // Ignore on older Windows versions
        }
    }

    private void SyncInstalledIconAndShortcuts()
    {
        if (!AppPaths.IsInnoSetupInstalled &&
            !AppPaths.IsProcessRunningFromInstallDir(Environment.ProcessPath, _paths))
        {
            return;
        }

        try
        {
            using Stream? iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KurfanovaMuzik.app.ico");
            if (iconStream is null) return;

            using var ms = new MemoryStream();
            iconStream.CopyTo(ms);
            byte[] icoBytes = ms.ToArray();
            if (icoBytes.Length == 0) return;

            string? iconDir = Path.GetDirectoryName(_paths.IconPath);
            if (!string.IsNullOrWhiteSpace(iconDir))
            {
                Directory.CreateDirectory(iconDir);
            }

            bool iconChanged = !File.Exists(_paths.IconPath) || !File.ReadAllBytes(_paths.IconPath).SequenceEqual(icoBytes);
            if (iconChanged)
            {
                File.WriteAllBytes(_paths.IconPath, icoBytes);
                if (File.Exists(_paths.InstalledExePath))
                {
                    InstallerService.ApplyShortcutsAndRegistry(_paths, _config, AppPaths.GetNormalizedCurrentVersion());
                }
                SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch { }
    }

    private void RegisterToastAppIdentity(string appId)
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDir);
            string toastPngPath = Path.Combine(_paths.DataDir, "toast-logo.png");
            if (!File.Exists(toastPngPath))
            {
                using Stream? iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KurfanovaMuzik.app.ico");
                if (iconStream is not null)
                {
                    using var ico = new Icon(iconStream, 256, 256);
                    using var bmp = ico.ToBitmap();
                    bmp.Save(toastPngPath, System.Drawing.Imaging.ImageFormat.Png);
                }
            }

            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{appId}");
            if (key is not null)
            {
                key.SetValue("DisplayName", _isTestMode ? "KurfaTest" : "Kurfamüzik", Microsoft.Win32.RegistryValueKind.String);
                if (File.Exists(toastPngPath))
                {
                    key.SetValue("IconUri", toastPngPath, Microsoft.Win32.RegistryValueKind.String);
                }
                key.SetValue("IconBackgroundColor", "0", Microsoft.Win32.RegistryValueKind.String);
            }
        }
        catch { }
    }

    private async void OnFormLoad(object? sender, EventArgs e)
    {
        try
        {
            string appId = _isTestMode ? "KurfaCore.KurfaTest" : "KurfaCore.Kurfamuzik";
            _ = SetCurrentProcessExplicitAppUserModelID(appId);
            RegisterToastAppIdentity(appId);
            if (!_isTestMode)
            {
                SyncInstalledIconAndShortcuts();
            }
            ApplyTitleBarDarkMode(true);
            _engine.Start();
            InitializeTrayIcon();

            // Pre-check WebView2 Evergreen Runtime (critical on clean Windows 10 PCs)
            try
            {
                string ver = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (string.IsNullOrWhiteSpace(ver))
                    throw new WebView2RuntimeNotFoundException();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                throw;
            }
            catch
            {
                // Continue to CreateAsync if probe failed ambiguously
            }

            Directory.CreateDirectory(_paths.WebView2DataDir);

            var envOptions = new CoreWebView2EnvironmentOptions(
                "--autoplay-policy=no-user-gesture-required --disk-cache-size=33554432 --media-cache-size=33554432 --disable-background-timer-throttling --disable-renderer-backgrounding"
            );
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, _paths.WebView2DataDir, envOptions);
            _webViewEnv = env;
            _engine.PageBoundPoTokenMinter = MintPageBoundPoTokenViaWebViewAsync;
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
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    if (!_isTestMode && _customTargetHost is null &&
                        !_triedFallbackOrigin && (_webView.Source?.Host.Contains("github.io", StringComparison.OrdinalIgnoreCase) ?? true))
                    {
                        _triedFallbackOrigin = true;
                        core.Navigate(FallbackTargetUrl);
                        return;
                    }
                }
                _webContentLoaded = true;
                ReplayLatestUpdateProgress();
            };
            core.ProcessFailed += (_, _) =>
            {
                try
                {
                    if (!IsDisposed)
                    {
                        BeginInvoke(() => _webView.Reload());
                    }
                }
                catch { }
            };

            await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildBridgeInjectionScript(_customTargetHost));

            core.Navigate(_initialTargetUri.AbsoluteUri);

            if (!_isTestMode)
            {
                _updater.UpdateProgressChanged += OnUpdateProgressChanged;
                _updater.UpdateReady += OnUpdateReady;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3500, _shutdownCts.Token).ConfigureAwait(false);
                        await _updater.CheckManifestAsync(_shutdownCts.Token, force: false, emitProgressEvents: true).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                });
            }
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
                OpenExternalBrowser(new Uri("https://go.microsoft.com/fwlink/p/?LinkId=2124703"));
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

    private static string BuildBridgeInjectionScript(string? customTargetHost = null)
    {
        string escapedCustomHost = string.IsNullOrWhiteSpace(customTargetHost)
            ? string.Empty
            : customTargetHost.Replace("\\", "\\\\").Replace("'", "\\'");
        return """
            (() => {
              try {
                const host = String(window.location && window.location.hostname || '').toLowerCase();
                const customHost = '__KURFA_CUSTOM_HOST__';

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
                if (host !== 'kurfanova.github.io' &&
                    host !== 'kurfanova.kurfa.workers.dev' &&
                    host !== 'kurfatest.kurfa.workers.dev' &&
                    host !== 'localhost' &&
                    (!customHost || host !== customHost)) return;

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
            """.Replace("__KURFA_CUSTOM_HOST__", escapedCustomHost);
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
            else if (IsConfiguredAppHost(host) ||
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
        bool hasMiniPlaying = false;
        bool miniMuted = false;
        double? miniVolume = null;
        double miniCur = 0;
        double miniDur = 0;
        bool themeDarkMode = true;
        bool? settingDesktopShortcut = null;
        bool? settingStartMenuShortcut = null;
        bool? settingRunOnStartup = null;
        bool? settingMinimizeToTray = null;
        string? settingDownloadDir = null;
        bool hasSettingDownloadDir = false;
        string? settingLanguage = null;
        string? saveUserDataJson = null;

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
                    hasMiniPlaying = true;
                }
                if (payload.TryGetProperty("isMuted", out JsonElement mmEl) &&
                    (mmEl.ValueKind == JsonValueKind.True || mmEl.ValueKind == JsonValueKind.False))
                {
                    miniMuted = mmEl.GetBoolean();
                }
                if (payload.TryGetProperty("volume", out JsonElement mvEl) && mvEl.ValueKind == JsonValueKind.Number)
                {
                    miniVolume = Math.Clamp(mvEl.GetDouble(), 0.0, 1.0);
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
                if (payload.TryGetProperty("downloadDir", out JsonElement dlDirEl) &&
                    dlDirEl.ValueKind == JsonValueKind.String)
                {
                    settingDownloadDir = dlDirEl.GetString();
                    hasSettingDownloadDir = true;
                }
                if (payload.TryGetProperty("lang", out JsonElement langEl) && langEl.ValueKind == JsonValueKind.String)
                {
                    settingLanguage = langEl.GetString();
                }
                else if (payload.TryGetProperty("language", out JsonElement langAltEl) && langAltEl.ValueKind == JsonValueKind.String)
                {
                    settingLanguage = langAltEl.GetString();
                }
                if (payload.TryGetProperty("dataJson", out JsonElement udEl))
                {
                    saveUserDataJson = udEl.ValueKind == JsonValueKind.String
                        ? udEl.GetString()
                        : udEl.GetRawText();
                }
                else if (payload.TryGetProperty("data", out JsonElement udObjEl) && udObjEl.ValueKind == JsonValueKind.Object)
                {
                    saveUserDataJson = udObjEl.GetRawText();
                }
                else if (string.Equals(action, "SAVE_USER_DATA", StringComparison.OrdinalIgnoreCase))
                {
                    saveUserDataJson = payload.GetRawText();
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
                    {
                        AutoUpdater.UpdateCheckResult check = await _updater.CheckManifestAsync(ct, force: true, emitProgressEvents: true).ConfigureAwait(false);
                        responseObj = new
                        {
                            success = check.Status != "error",
                            status = check.Status,
                            currentVersion = check.CurrentVersion.ToString(3),
                            remoteVersion = check.RemoteVersion?.ToString(3),
                            sizeBytes = check.SizeBytes,
                            notes = check.Notes ?? string.Empty,
                            errorMessage = check.ErrorMessage,
                            hasUpdate = check.Status == "available" || check.Status == "staged",
                            stagedVersion = _updater.StagedVersion?.ToString(3)
                        };
                        break;
                    }

                    case "START_UPDATE_DOWNLOAD":
                    {
                        _ = Task.Run(() => _updater.DownloadAndStageAsync(_shutdownCts.Token));
                        responseObj = new { success = true, started = true };
                        break;
                    }

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
                            responseObj = new { success = false, error = "Hazır güncelleme paketi bulunamadı" };
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
                                prefetchUrl = _engine.GetLocalPrefetchUrl(resolved.VideoId),
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

                    case "PREFETCH":
                    {
                        if (NativeDesktopEngine.IsValidVideoId(videoId))
                        {
                            _engine.TriggerPrefetch(videoId);
                            responseObj = new
                            {
                                success = true,
                                status = "accepted",
                                videoId,
                                prefetchUrl = _engine.GetLocalPrefetchUrl(videoId!)
                            };
                        }
                        else
                        {
                            responseObj = new { success = false, error = "Invalid video ID" };
                        }
                        break;
                    }

                    case "DOWNLOAD_MEDIA":
                    {
                        string targetDownloadDir;
                        lock (_config)
                        {
                            targetDownloadDir = AppPaths.ResolveDownloadDir(_config);
                        }
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
                            targetDownloadDir,
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
                                string downloadsDir;
                                lock (_config)
                                {
                                    downloadsDir = AppPaths.ResolveDownloadDir(_config);
                                }
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
                        lock (_config)
                        {
                            responseObj = new
                            {
                                success = true,
                                version = _updater.CurrentVersion.ToString(3),
                                installDir = _paths.InstallDir,
                                downloadDir = AppPaths.ResolveDownloadDir(_config),
                                desktopShortcut = _config.DesktopShortcut,
                                startMenuShortcut = _config.StartMenuShortcut,
                                runOnStartup = _config.RunOnStartup,
                                minimizeToTray = _config.MinimizeToTray,
                                language = NormalizeLanguage(_config.Language)
                            };
                        }
                        break;

                    case "SET_DESKTOP_SETTINGS":
                        lock (_config)
                        {
                            if (settingDesktopShortcut.HasValue) _config.DesktopShortcut = settingDesktopShortcut.Value;
                            if (settingStartMenuShortcut.HasValue) _config.StartMenuShortcut = settingStartMenuShortcut.Value;
                            if (settingRunOnStartup.HasValue) _config.RunOnStartup = settingRunOnStartup.Value;
                            if (settingMinimizeToTray.HasValue) _config.MinimizeToTray = settingMinimizeToTray.Value;
                            if (hasSettingDownloadDir)
                            {
                                if (string.IsNullOrWhiteSpace(settingDownloadDir))
                                {
                                    _config.DownloadDir = string.Empty;
                                }
                                else
                                {
                                    try
                                    {
                                        string resolved = Path.GetFullPath(settingDownloadDir.Trim());
                                        Directory.CreateDirectory(resolved);
                                        _config.DownloadDir = resolved;
                                    }
                                    catch
                                    {
                                        // Keep existing DownloadDir if custom path is invalid/unwritable
                                    }
                                }
                            }

                            AppPaths.SaveConfig(_paths, _config);
                            InstallerService.ApplyShortcutsAndRegistry(_paths, _config, _updater.CurrentVersion);
                            responseObj = new
                            {
                                success = true,
                                version = _updater.CurrentVersion.ToString(3),
                                installDir = _paths.InstallDir,
                                downloadDir = AppPaths.ResolveDownloadDir(_config),
                                desktopShortcut = _config.DesktopShortcut,
                                startMenuShortcut = _config.StartMenuShortcut,
                                runOnStartup = _config.RunOnStartup,
                                minimizeToTray = _config.MinimizeToTray,
                                language = NormalizeLanguage(_config.Language)
                            };
                        }
                        break;

                    case "SET_LANGUAGE":
                    {
                        string normalizedLang = NormalizeLanguage(settingLanguage);
                        lock (_config)
                        {
                            _config.Language = normalizedLang;
                            AppPaths.SaveConfig(_paths, _config);
                        }
                        if (!IsDisposed && IsHandleCreated)
                        {
                            BeginInvoke(() => ApplyLanguage(normalizedLang));
                        }
                        responseObj = new { success = true, language = normalizedLang };
                        break;
                    }

                    case "SAVE_USER_DATA":
                    {
                        bool ok = saveUserDataJson is not null && AppPaths.SaveUserDataJson(_paths, saveUserDataJson);
                        responseObj = ok
                            ? new { success = true }
                            : new { success = false, error = "Invalid or unwritable user data" };
                        break;
                    }

                    case "LOAD_USER_DATA":
                    {
                        string rawUserJson = AppPaths.LoadUserDataJson(_paths);
                        responseObj = new { success = true, dataJson = rawUserJson };
                        break;
                    }

                    case "PICK_DOWNLOAD_FOLDER":
                    {
                        string initialDir;
                        lock (_config)
                        {
                            initialDir = AppPaths.ResolveDownloadDir(_config);
                        }
                        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                        BeginInvoke(() =>
                        {
                            try
                            {
                                using var fbd = new FolderBrowserDialog
                                {
                                    Description = "İndirme Klasörü Seç",
                                    UseDescriptionForTitle = true,
                                    SelectedPath = initialDir,
                                    ShowNewFolderButton = true
                                };
                                if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                                {
                                    tcs.TrySetResult(fbd.SelectedPath);
                                }
                                else
                                {
                                    tcs.TrySetResult(null);
                                }
                            }
                            catch
                            {
                                tcs.TrySetResult(null);
                            }
                        });
                        string? selectedPath = await tcs.Task.ConfigureAwait(false);
                        bool picked = !string.IsNullOrWhiteSpace(selectedPath);
                        string effectiveDir;
                        lock (_config)
                        {
                            if (picked)
                            {
                                _config.DownloadDir = selectedPath!;
                                AppPaths.SaveConfig(_paths, _config);
                            }
                            effectiveDir = AppPaths.ResolveDownloadDir(_config);
                        }
                        responseObj = new { success = true, canceled = !picked, downloadDir = effectiveDir };
                        break;
                    }

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
                        if (hasMiniPlaying)
                        {
                            _isAudioPlaying = miniPlaying;
                        }
                        bool nowVisible = ToggleOrUpdateMiniPlayer(true, downloadTitle, miniArtist, miniThumb, miniPlaying, miniMuted, miniCur, miniDur, miniVolume);
                        responseObj = new { success = true, visible = nowVisible };
                        break;
                    }

                    case "UPDATE_MINI_PLAYER":
                    {
                        if (!string.IsNullOrWhiteSpace(downloadTitle)) _currentTrackTitle = downloadTitle.Trim();
                        if (!string.IsNullOrWhiteSpace(miniArtist)) _currentTrackArtist = miniArtist.Trim();
                        if (hasMiniPlaying)
                        {
                            bool changed = _isAudioPlaying != miniPlaying;
                            _isAudioPlaying = miniPlaying;
                            if (changed && !IsDisposed && IsHandleCreated)
                            {
                                BeginInvoke(UpdateWebViewMemoryMode);
                            }
                        }
                        if (_isAudioPlaying && !string.IsNullOrWhiteSpace(_currentTrackTitle))
                        {
                            _discordRpc.UpdatePresence(_currentTrackTitle, _currentTrackArtist, true, miniCur, miniDur);
                        }
                        else if (hasMiniPlaying && !miniPlaying)
                        {
                            _discordRpc.ClearPresence();
                        }
                        if (_miniPlayer is { IsDisposed: false, Visible: true })
                        {
                            ToggleOrUpdateMiniPlayer(false, downloadTitle, miniArtist, miniThumb, miniPlaying, miniMuted, miniCur, miniDur, miniVolume);
                        }
                        responseObj = new { success = true };
                        break;
                    }

                    case "SET_PLAYBACK_STATE":
                    {
                        if (!string.IsNullOrWhiteSpace(downloadTitle)) _currentTrackTitle = downloadTitle.Trim();
                        if (!string.IsNullOrWhiteSpace(miniArtist)) _currentTrackArtist = miniArtist.Trim();
                        if (hasMiniPlaying)
                        {
                            _isAudioPlaying = miniPlaying;
                            if (!IsDisposed && IsHandleCreated)
                            {
                                BeginInvoke(UpdateWebViewMemoryMode);
                            }
                            if (miniPlaying && !string.IsNullOrWhiteSpace(_currentTrackTitle))
                            {
                                _discordRpc.UpdatePresence(_currentTrackTitle, _currentTrackArtist, true, miniCur, miniDur);
                            }
                            else if (!miniPlaying)
                            {
                                _discordRpc.ClearPresence();
                            }
                        }
                        responseObj = new { success = true, isPlaying = _isAudioPlaying };
                        break;
                    }

                    case "SET_THEME_MODE":
                    {
                        BeginInvoke(() => ApplyTitleBarDarkMode(themeDarkMode));
                        responseObj = new { success = true };
                        break;
                    }

                    case "GET_LOCAL_AUDIO_TRACKS":
                    {
                        object[] tracks = _engine.GetRegisteredLocalAudioTracks();
                        responseObj = new { success = true, tracks };
                        break;
                    }

                    case "PICK_LOCAL_AUDIO_FILES":
                    {
                        var tcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                        BeginInvoke(() =>
                        {
                            try
                            {
                                using var dlg = new OpenFileDialog
                                {
                                    Title = "Bilgisayardan Müzik Seç",
                                    Filter = "Ses Dosyaları|*.mp3;*.m4a;*.wav;*.flac;*.ogg;*.aac;*.opus;*.webm|Tüm Dosyalar|*.*",
                                    Multiselect = true,
                                    CheckFileExists = true
                                };
                                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.FileNames.Length > 0)
                                {
                                    tcs.TrySetResult(dlg.FileNames);
                                }
                                else
                                {
                                    tcs.TrySetResult([]);
                                }
                            }
                            catch
                            {
                                tcs.TrySetResult([]);
                            }
                        });
                        string[] picked = await tcs.Task.ConfigureAwait(false);
                        object[] addedTracks = picked.Length > 0 ? _engine.RegisterLocalAudioFiles(picked) : [];
                        object[] allTracks = _engine.GetRegisteredLocalAudioTracks();
                        responseObj = new { success = true, addedCount = addedTracks.Length, tracks = allTracks };
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
        double durSec,
        double? volume = null)
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
                    },
                    onSetVolume: vol => ExecuteMiniPlayerJs($"if(typeof window.setPlayerVolume==='function')window.setPlayerVolume({vol.ToString(System.Globalization.CultureInfo.InvariantCulture)});"));
                string lang;
                lock (_config)
                {
                    lang = NormalizeLanguage(_config.Language);
                }
                _miniPlayer.SetLanguage(lang);
                _miniPlayer.VisibleChanged += (_, _) => UpdateWebViewMemoryMode();
            }

            _miniPlayer.UpdateState(title, artist, thumbUrl, isPlaying, isMuted, curSec, durSec, volume);
            if (toggleVisibility)
            {
                if (_miniPlayer.Visible && !_miniPlayer.IsClosingAnimation)
                {
                    _miniPlayer.CloseAnimated();
                }
                else
                {
                    _miniPlayer.ShowAnimated();
                }
            }
            UpdateWebViewMemoryMode();
            visible = _miniPlayer.Visible && !_miniPlayer.IsClosingAnimation;
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

        if (IsConfiguredAppHost(host) &&
            _webView.CoreWebView2 is not null)
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
        if (e.IsUserInitiated && !MatchesAnyHost(host, InAppHosts) && !IsConfiguredAppHost(host))
        {
            e.Cancel = true;
            OpenExternalBrowser(uri);
        }
    }

    private bool IsConfiguredAppHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        return MatchesHostOrSubdomain(host, "kurfanova.github.io") ||
               MatchesHostOrSubdomain(host, "kurfanova.kurfa.workers.dev") ||
               MatchesHostOrSubdomain(host, "kurfatest.kurfa.workers.dev") ||
               (!string.IsNullOrEmpty(_customTargetHost) && MatchesHostOrSubdomain(host, _customTargetHost));
    }

    private bool IsAllowedBridgeOrigin(string? source, out string targetOrigin)
    {
        targetOrigin = string.Empty;
        if (string.IsNullOrWhiteSpace(source) || !Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        bool isKurfanova = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                           (uri.Host.Equals("kurfanova.github.io", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.Equals("kurfanova.kurfa.workers.dev", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.Equals("kurfatest.kurfa.workers.dev", StringComparison.OrdinalIgnoreCase)) &&
                           uri.IsDefaultPort;

        bool isCustomTarget = !string.IsNullOrEmpty(_customTargetHost) &&
                              (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                               uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) &&
                              uri.Host.Equals(_customTargetHost, StringComparison.OrdinalIgnoreCase);

        bool isLocalhost = (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                            uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) &&
                           uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        if (!isKurfanova && !isCustomTarget && !isLocalhost)
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
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return;
        }

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
        if (_isTestMode) return;
        _latestUpdateProgress = info;
        if (IsDisposed || !IsHandleCreated || !_webContentLoaded) return;

        PushUpdateProgressToWebView(info);
    }

    private void ReplayLatestUpdateProgress()
    {
        if (_isTestMode) return;
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
                currentVersion = _updater.CurrentVersion.ToString(3),
                version = info.Version.ToString(3),
                notes = info.Notes ?? string.Empty,
                bytesDownloaded = info.BytesDownloaded,
                totalBytes = info.TotalBytes,
                percent = info.Percent,
                errorMessage = info.ErrorMessage ?? string.Empty
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
        if (_isTestMode) return;
        if (IsDisposed || !IsHandleCreated) return;
        if (Interlocked.CompareExchange(ref _updatePromptShown, 1, 0) != 0) return;

        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;

                if (_webContentLoaded && _webView.CoreWebView2 is not null)
                {
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
                    $"Kurfamüzik v{version.ToString(3)} arka planda indirildi ve doğrulandı.{noteLine}\nŞimdi güncellenip yeniden başlatılsın mı?",
                    "Kurfamüzik Güncelleme Onayı",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (choice == DialogResult.Yes)
                {
                    _restartForUpdate = true;
                    _allowExit = true;
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
            string verTag = _updater.CurrentVersion.ToString(3);
            _trayCroppedIcon = LoadTrayIconForCurrentDpi();
            _trayIcon = new NotifyIcon
            {
                Text = _isTestMode ? $"KurfaTest v{verTag}" : $"Kurfamüzik v{verTag}",
                Icon = _trayCroppedIcon ?? LoadEmbeddedIcon(32) ?? _embeddedIcon ?? SystemIcons.Application,
                Visible = true
            };

            _trayMenu = new TrayMenuForm(
                $"v{verTag}",
                () =>
                {
                    if (Visible && WindowState != FormWindowState.Minimized)
                        HideToTray();
                    else
                        RestoreWindow();
                },
                async () =>
                {
                    _trayIcon?.ShowBalloonTip(1500, "Kurfamüzik", "Yeni sürüm denetleniyor...", ToolTipIcon.None);
                    AutoUpdater.UpdateCheckResult check = await _updater.CheckManifestAsync(_shutdownCts.Token, force: true, emitProgressEvents: true).ConfigureAwait(false);
                    if (IsDisposed) return;

                    BeginInvoke(() =>
                    {
                        if (IsDisposed) return;
                        if (check.Status == "available" || check.Status == "staged")
                        {
                            RestoreWindow();
                            string phase = check.Status == "staged" ? "ready" : "available";
                            if (_webContentLoaded && _webView.CoreWebView2 is not null && check.RemoteVersion is not null)
                            {
                                PushUpdateProgressToWebView(new AutoUpdater.UpdateProgressInfo(
                                    phase, check.RemoteVersion, check.Notes,
                                    phase == "ready" ? check.SizeBytes : 0, check.SizeBytes, phase == "ready" ? 100 : 0));
                            }
                            else if (check.RemoteVersion is not null)
                            {
                                DialogResult ans = MessageBox.Show(
                                    this,
                                    $"Kurfamüzik v{check.RemoteVersion.ToString(3)} bulundu (Yüklü: v{check.CurrentVersion.ToString(3)}).\nŞimdi indirip güncellemek istiyor musunuz?",
                                    "Yeni Güncelleme Mevcut",
                                    MessageBoxButtons.YesNo,
                                    MessageBoxIcon.Information);
                                if (ans == DialogResult.Yes)
                                {
                                    _ = Task.Run(async () =>
                                    {
                                        bool ok = await _updater.DownloadAndStageAsync(_shutdownCts.Token).ConfigureAwait(false);
                                        if (ok && !IsDisposed)
                                        {
                                            BeginInvoke(() => { _restartForUpdate = true; _allowExit = true; Close(); });
                                        }
                                    });
                                }
                            }
                        }
                        else if (check.Status == "up-to-date")
                        {
                            _trayIcon?.ShowBalloonTip(2200, "Kurfamüzik", $"Uygulamanız güncel (v{check.CurrentVersion.ToString(3)}).", ToolTipIcon.None);
                        }
                        else if (check.Status == "error")
                        {
                            _trayIcon?.ShowBalloonTip(2200, "Kurfamüzik", check.ErrorMessage ?? "Güncelleme denetlenemedi.", ToolTipIcon.Warning);
                        }
                    });
                },
                () =>
                {
                    _allowExit = true;
                    Close();
                });

            lock (_config)
            {
                _trayMenu.SetLanguage(NormalizeLanguage(_config.Language));
            }

            _trayIcon.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Right && _trayMenu is not null && !_trayMenu.IsDisposed)
                {
                    bool isVisible = Visible && WindowState != FormWindowState.Minimized;
                    _trayMenu.ShowAtCursor(isVisible);
                }
            };

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
            Hide();
            UpdateWebViewMemoryMode();
            if (!_trayHintShown && _trayIcon is not null)
            {
                _trayHintShown = true;
                bool isEn;
                lock (_config)
                {
                    isEn = NormalizeLanguage(_config.Language) == "en";
                }
                _trayIcon.ShowBalloonTip(
                    2200,
                    "Kurfamüzik",
                    isEn
                        ? "Still running in background. Double-click the tray icon to reopen."
                        : "Arka planda çalışmaya devam ediyor. Geri açmak için tepsi simgesine çift tıklayın.",
                    ToolTipIcon.None);
            }
        }
        catch { /* ignore tray hide races */ }
    }

    public void BringExistingWindowToFront()
    {
        RestoreWindow();
    }

    public void ForceExitForInstaller()
    {
        _allowExit = true;
        Close();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_QUERYENDSESSION = 0x0011;
        const int WM_ENDSESSION = 0x0016;
        if (m.Msg == WM_QUERYENDSESSION || m.Msg == WM_ENDSESSION)
        {
            _allowExit = true;
        }
        base.WndProc(ref m);
    }

    private void RestoreWindow()
    {
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

    internal AutoUpdater? DetachPendingUpdaterOnExit(out bool relaunch)
    {
        relaunch = _restartForUpdate;
        if (_updater.HasStagedUpdate)
        {
            _detachedUpdaterForApply = true;
            return _updater;
        }
        return null;
    }

    private async Task<string?> MintPageBoundPoTokenViaWebViewAsync(
        string videoId,
        string visitorData,
        NativeDesktopEngine.AttestationChallengeData? challenge,
        CancellationToken ct)
    {
        if (IsDisposed || !IsHandleCreated || _webViewEnv is null || challenge is null ||
            string.IsNullOrWhiteSpace(challenge.InterpreterUrl) ||
            string.IsNullOrWhiteSpace(challenge.Program) ||
            string.IsNullOrWhiteSpace(challenge.GlobalName))
        {
            return null;
        }

        await _poTokenUiLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");

            string interpreterJs = await http.GetStringAsync(challenge.InterpreterUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(interpreterJs)) return null;

            string? botguardResp = await EvaluateOnPoTokenWebViewAsync(async core =>
            {
                string helperScript = """
                    if (!globalThis.__kurfaBgReady) {
                      globalThis.__kurfaBgReady = true;
                      globalThis.runKurfaBotGuard = function(interpreter, program, globalName) {
                        new Function(interpreter)();
                        var bgVm = window[globalName];
                        if (!bgVm || !bgVm.a) throw new Error('BotGuard VM unavailable');
                        var bgFns = null;
                        var vmLoad = bgVm.a(program, function(a, s, p, c) {
                          bgFns = { asyncSnapshotFunction: a, shutdownFunction: s };
                        }, true, undefined, function() {}, [[], []], undefined, false,
                        [function(){},function(){},function(){},function(){},function(){}]);
                        var sigOut = [];
                        return Promise.resolve(vmLoad).then(function() {
                          return new Promise(function(resolve, reject) {
                            var tries = 0;
                            var iv = setInterval(function() {
                              if (bgFns && bgFns.asyncSnapshotFunction) { clearInterval(iv); resolve(bgFns); }
                              else if (++tries > 4000) { clearInterval(iv); reject(new Error('BG timeout')); }
                            }, 2);
                          });
                        }).then(function(fns) {
                          return new Promise(function(resolve, reject) {
                            fns.asyncSnapshotFunction(function(resp) {
                              globalThis.__kurfaWebPoSignalOutput = sigOut;
                              resolve(resp);
                            }, [undefined, undefined, sigOut, undefined]);
                          });
                        });
                      };
                    }
                    """;
                await core.ExecuteScriptAsync(helperScript);

                string challengeCall = $$"""
                    (function() {
                      globalThis.__kurfaBgSnap = { state: 'running' };
                      runKurfaBotGuard({{JsonSerializer.Serialize(interpreterJs)}}, {{JsonSerializer.Serialize(challenge.Program)}}, {{JsonSerializer.Serialize(challenge.GlobalName)}})
                        .then(function(r) { globalThis.__kurfaBgSnap = { state: 'ready', value: r }; })
                        .catch(function(e) { globalThis.__kurfaBgSnap = { state: 'error', value: String(e) }; });
                      return 'ok';
                    })();
                    """;
                await core.ExecuteScriptAsync(challengeCall);
                return await PollWebViewGlobalResultAsync(core, "__kurfaBgSnap", ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(botguardResp)) return null;

            using var genReq = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/api/jnn/v1/GenerateIT")
            {
                Content = new StringContent(
                    $"[\"O43z0dpjhgX20SCx4KAo\",{JsonSerializer.Serialize(botguardResp)}]",
                    System.Text.Encoding.UTF8,
                    "application/json+protobuf")
            };
            genReq.Headers.TryAddWithoutValidation("Accept", "application/json");
            genReq.Headers.TryAddWithoutValidation("x-goog-api-key", "AIzaSyDyT5W0Jh49F30Pqqtyfdf7pDLFKLJoAnw");
            genReq.Headers.TryAddWithoutValidation("x-user-agent", "grpc-web-javascript/0.1");

            using HttpResponseMessage genRes = await http.SendAsync(genReq, ct).ConfigureAwait(false);
            if (!genRes.IsSuccessStatusCode) return null;

            string genBody = await genRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument genDoc = JsonDocument.Parse(genBody);
            if (genDoc.RootElement.ValueKind != JsonValueKind.Array || genDoc.RootElement.GetArrayLength() < 1)
            {
                return null;
            }
            string integrityTokenJson = genDoc.RootElement[0].GetRawText();

            string identifier = !string.IsNullOrWhiteSpace(videoId) ? videoId : visitorData;
            string? tokenCsv = await EvaluateOnPoTokenWebViewAsync(async core =>
            {
                string mintCall = $$"""
                    (function() {
                      globalThis.__kurfaPotRes = { state: 'running' };
                      var getMinter = (globalThis.__kurfaWebPoSignalOutput || [])[0];
                      if (typeof getMinter !== 'function') {
                        globalThis.__kurfaPotRes = { state: 'error', value: 'no minter factory' };
                        return 'err';
                      }
                      var idBytes = new TextEncoder().encode({{JsonSerializer.Serialize(identifier)}});
                      Promise.resolve(getMinter({{integrityTokenJson}}))
                        .then(function(minter) { return minter(idBytes); })
                        .then(function(u8) { globalThis.__kurfaPotRes = { state: 'ready', value: Array.from(u8).join(',') }; })
                        .catch(function(e) { globalThis.__kurfaPotRes = { state: 'error', value: String(e) }; });
                      return 'ok';
                    })();
                    """;
                await core.ExecuteScriptAsync(mintCall);
                return await PollWebViewGlobalResultAsync(core, "__kurfaPotRes", ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(tokenCsv)) return null;
            byte[] tokenBytes = tokenCsv.Split(',').Select(s => byte.Parse(s.Trim())).ToArray();
            return Convert.ToBase64String(tokenBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
        catch
        {
            return null;
        }
        finally
        {
            _poTokenUiLock.Release();
        }
    }

    private async Task<string?> EvaluateOnPoTokenWebViewAsync(Func<CoreWebView2, Task<string?>> action, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(async () =>
        {
            try
            {
                if (IsDisposed || _webViewEnv is null)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                if (_poTokenWebView is null)
                {
                    var wv = new WebView2
                    {
                        Visible = false,
                        Size = new Size(1, 1),
                        Location = new Point(-2000, -2000)
                    };
                    Controls.Add(wv);
                    await wv.EnsureCoreWebView2Async(_webViewEnv);
                    wv.CoreWebView2.Settings.IsScriptEnabled = true;
                    wv.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;

                    var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    wv.CoreWebView2.NavigationCompleted += (_, _) => navTcs.TrySetResult(true);
                    wv.CoreWebView2.Navigate("https://www.youtube.com/robots.txt");
                    using var navTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var reg = navTimeout.Token.Register(() => navTcs.TrySetResult(false));
                    _poTokenPageReady = await navTcs.Task;
                    _poTokenWebView = wv;
                }

                if (!_poTokenPageReady || _poTokenWebView?.CoreWebView2 is null)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                using var regCt = ct.Register(() => tcs.TrySetCanceled(ct));
                string? res = await action(_poTokenWebView.CoreWebView2);
                tcs.TrySetResult(res);
            }
            catch
            {
                tcs.TrySetResult(null);
            }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private static async Task<string?> PollWebViewGlobalResultAsync(CoreWebView2 core, string globalName, CancellationToken ct)
    {
        for (int i = 0; i < 200; i++)
        {
            ct.ThrowIfCancellationRequested();
            string raw = await core.ExecuteScriptAsync($"JSON.stringify(globalThis.{globalName} || null)");
            string? unescaped = JsonSerializer.Deserialize<string>(raw);
            if (!string.IsNullOrWhiteSpace(unescaped) && unescaped != "null")
            {
                using JsonDocument d = JsonDocument.Parse(unescaped);
                string? state = d.RootElement.TryGetProperty("state", out JsonElement st) ? st.GetString() : null;
                if (state == "ready")
                {
                    return d.RootElement.TryGetProperty("value", out JsonElement val) ? val.GetString() : null;
                }
                if (state == "error")
                {
                    return null;
                }
            }
            await Task.Delay(30, ct);
        }
        return null;
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        try { _shutdownCts.Cancel(); } catch { /* ignore */ }
        _discordRpc.Dispose();
        _poTokenUiLock.Dispose();
        _poTokenWebView?.Dispose();
        _trayMenu?.Dispose();
        _trayIcon?.Dispose();
        _trayCroppedIcon?.Dispose();
        _miniPlayer?.Dispose();
        if (!_detachedUpdaterForApply && !_updater.HasStagedUpdate)
        {
            _updater.Dispose();
        }
        _engine.Dispose();
        _webView.Dispose();
        _embeddedIcon?.Dispose();
        _shutdownCts.Dispose();
    }
}
