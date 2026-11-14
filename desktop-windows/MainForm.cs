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
        "*://youtube-nocookie.com/pagead/*",
        "*://*.youtube-nocookie.com/pagead/*",
        "*://youtube-nocookie.com/api/stats/ads*",
        "*://*.youtube-nocookie.com/api/stats/ads*",
        "*://youtube-nocookie.com/ptracking*",
        "*://*.youtube-nocookie.com/ptracking*",
        "*://youtube-nocookie.com/get_midroll_info*",
        "*://*.youtube-nocookie.com/get_midroll_info*"
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

    private readonly NativeDesktopEngine _engine = new();
    private readonly WebView2 _webView = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private Icon? _embeddedIcon;

    public MainForm()
    {
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

            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KurfanovaMuzik",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);

            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
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
                if (host !== 'kurfanova.github.io' && host !== 'localhost') return;

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
            if (MatchesHostOrSubdomain(host, "kurfanova.github.io") ||
                MatchesHostOrSubdomain(host, "googlevideo.com") ||
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
                        responseObj = new { success = true, mode = DesktopBridgeMode };
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
                           uri.Host.Equals("kurfanova.github.io", StringComparison.OrdinalIgnoreCase) &&
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

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        try { _shutdownCts.Cancel(); } catch { /* ignore */ }
        _engine.Dispose();
        _webView.Dispose();
        _embeddedIcon?.Dispose();
        _shutdownCts.Dispose();
    }
}
