using System.Diagnostics;
using System.Reflection;
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
    private int _updatePromptShown;

    public MainForm() : this(null, null)
    {
    }

    public MainForm(AppPaths? paths, DesktopAppConfig? config)
    {
        _paths = paths ?? new AppPaths(AppPaths.ResolveActiveInstallDir());
        _config = config ?? AppPaths.LoadOrDefaultConfig(_paths, AppPaths.GetNormalizedCurrentVersion());
        _updater = new AutoUpdater(_paths);

        Color darkBg = Color.FromArgb(11, 11, 20); // #0B0B14

        Text = "Kurfamüzik — Reklamsız Masaüstü";
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
        FormClosed += OnFormClosed;
    }

    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("KurfanovaMuzik.app.ico");
            return stream is not null ? new Icon(stream) : null;
        }
        catch
        {
            return null;
        }
    }

    private async void OnFormLoad(object? sender, EventArgs e)
    {
        try
        {
            _engine.Start();
            InitializeTrayIcon();

            Directory.CreateDirectory(_paths.WebView2DataDir);

            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, _paths.WebView2DataDir);
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

            await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildBridgeInjectionScript());

            core.Navigate(TargetUrl);

            _updater.UpdateReady += OnUpdateReady;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(12000, _shutdownCts.Token).ConfigureAwait(false);
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
                      const player = document.querySelector('.html5-video-player');
                      if (player && player.classList.contains('ad-showing')) {
                        const v = player.querySelector('video');
                        if (v) {
                          v.muted = true;
                          if (Number.isFinite(v.duration) && v.duration > 0 && v.currentTime < v.duration - 0.15) {
                            v.currentTime = v.duration;
                          }
                        }
                        const skipBtn = document.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern');
                        if (skipBtn) skipBtn.click();
                      }
                    } catch (_) {}
                  };
                  setInterval(guardAdDom, 150);
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
        bool? settingDesktopShortcut = null;
        bool? settingStartMenuShortcut = null;
        bool? settingRunOnStartup = null;

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

                    case "GET_DESKTOP_SETTINGS":
                        responseObj = new
                        {
                            success = true,
                            version = _updater.CurrentVersion.ToString(3),
                            installDir = _paths.InstallDir,
                            desktopShortcut = _config.DesktopShortcut,
                            startMenuShortcut = _config.StartMenuShortcut,
                            runOnStartup = _config.RunOnStartup
                        };
                        break;

                    case "SET_DESKTOP_SETTINGS":
                        lock (_config)
                        {
                            if (settingDesktopShortcut.HasValue) _config.DesktopShortcut = settingDesktopShortcut.Value;
                            if (settingStartMenuShortcut.HasValue) _config.StartMenuShortcut = settingStartMenuShortcut.Value;
                            if (settingRunOnStartup.HasValue) _config.RunOnStartup = settingRunOnStartup.Value;

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
                            runOnStartup = _config.RunOnStartup
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

    private void OnUpdateReady(Version version, string? notes)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (Interlocked.CompareExchange(ref _updatePromptShown, 1, 0) != 0) return;

        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;

                if (_webView.CoreWebView2 is not null)
                {
                    string verJson = JsonSerializer.Serialize(version.ToString());
                    _ = _webView.CoreWebView2.ExecuteScriptAsync(
                        $"if (typeof window.__kurfaOnNativeUpdateReady === 'function') {{ window.__kurfaOnNativeUpdateReady({{ version: {verJson}, platform: 'windows' }}); }}");
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
            _trayIcon = new NotifyIcon
            {
                Text = "Kurfamüzik",
                Icon = _embeddedIcon ?? SystemIcons.Application,
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            menu.BackColor = Color.FromArgb(20, 22, 37);
            menu.ForeColor = Color.White;

            var itemShow = new ToolStripMenuItem("Kurfamüzik'i Göster", null, (_, _) => RestoreWindow());
            itemShow.Font = new Font(Font, FontStyle.Bold);
            itemShow.ForeColor = Color.White;

            var itemShortcut = new ToolStripMenuItem("Masaüstü Kısayolu Oluştur / Yenile", null, (_, _) =>
            {
                lock (_config)
                {
                    _config.DesktopShortcut = true;
                    AppPaths.SaveConfig(_paths, _config);
                    InstallerService.ApplyShortcutsAndRegistry(_paths, _config, _updater.CurrentVersion);
                }
                _trayIcon.ShowBalloonTip(2000, "Kurfamüzik", "Masaüstü kısayolu oluşturuldu.", ToolTipIcon.Info);
            })
            {
                ForeColor = Color.White
            };

            var itemOpenDir = new ToolStripMenuItem("Kurulum Klasörünü Aç", null, (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.InstallDir}\"") { UseShellExecute = true });
                }
                catch { }
            })
            {
                ForeColor = Color.White
            };

            var itemCheckUpdate = new ToolStripMenuItem("Güncellemeleri Denetle", null, async (_, _) =>
            {
                _trayIcon.ShowBalloonTip(1500, "Kurfamüzik", "Güncellemeler denetleniyor...", ToolTipIcon.Info);
                await _updater.CheckAndStageAsync(_shutdownCts.Token, force: true);
                if (!_updater.HasStagedUpdate)
                {
                    _trayIcon.ShowBalloonTip(2000, "Kurfamüzik", "Uygulamanız güncel.", ToolTipIcon.Info);
                }
            })
            {
                ForeColor = Color.White
            };

            var itemSetup = new ToolStripMenuItem("Kurulum Sihirbazını Aç", null, (_, _) =>
            {
                LaunchSetup();
            })
            {
                ForeColor = Color.White
            };

            var itemUninstall = new ToolStripMenuItem("Uygulamayı Kaldır...", null, (_, _) =>
            {
                BeginInvoke(() => InstallerService.RunUninstall(silent: false));
            })
            {
                ForeColor = Color.White
            };

            var itemExit = new ToolStripMenuItem("Çıkış", null, (_, _) =>
            {
                Close();
            })
            {
                ForeColor = Color.White
            };

            menu.Items.Add(itemShow);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(itemShortcut);
            menu.Items.Add(itemOpenDir);
            menu.Items.Add(itemCheckUpdate);
            menu.Items.Add(itemSetup);
            menu.Items.Add(itemUninstall);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(itemExit);

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => RestoreWindow();
        }
        catch
        {
            // Ignore tray initialization errors
        }
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
    }

    private void LaunchSetup()
    {
        try
        {
            ExistingInstallInfo existing = InstallerService.DetectExistingInstallation();
            using var setup = new SetupForm(existing);
            if (setup.ShowDialog(this) == DialogResult.OK)
            {
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

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private readonly Color _bg = Color.FromArgb(20, 22, 37);
        private readonly Color _selected = Color.FromArgb(38, 42, 65);
        private readonly Color _border = Color.FromArgb(48, 54, 80);

        public override Color ToolStripDropDownBackground => _bg;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
        public override Color MenuBorder => _border;
        public override Color MenuItemBorder => _border;
        public override Color MenuItemSelected => _selected;
        public override Color MenuItemSelectedGradientBegin => _selected;
        public override Color MenuItemSelectedGradientEnd => _selected;
        public override Color MenuItemPressedGradientBegin => _selected;
        public override Color MenuItemPressedGradientEnd => _selected;
        public override Color SeparatorDark => _border;
        public override Color SeparatorLight => _border;
    }
}
