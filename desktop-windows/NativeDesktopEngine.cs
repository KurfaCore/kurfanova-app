using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KurfanovaMuzik;

/// <summary>
/// Local zero-ad audio proxy and YouTube InnerTube resolver/search engine for Kurfanova Desktop.
/// Executes strictly on loopback 127.0.0.1 and validates origin + YouTube IDs.
/// </summary>
public sealed partial class NativeDesktopEngine : IDisposable
{
    public const string AllowedOrigin = "https://kurfanova.github.io";
    public const string NativeAudioPathPrefix = "/__kurfa_native_audio__/";

    private const string AndroidVrUserAgent =
        "com.google.android.apps.youtube.vr.oculus/1.57.29 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip";
    private const string IosUserAgent =
        "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)";
    private const string WebClientVersion = "2.20250101.00.00";
    private const string WebUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36";

    private static readonly TimeSpan StreamCacheTtl = TimeSpan.FromMinutes(35);
    private const int MaxStreamCacheEntries = 100;
    private const int MaxSearchResults = 25;

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdRegex();

    public sealed record ResolvedAudio(
        string VideoId,
        string StreamUrl,
        string MimeType,
        int Itag,
        string Title,
        string Artist,
        long DurationSec,
        string RequestUserAgent,
        DateTimeOffset ExpiresAtUtc
    );

    public sealed record SearchTrackItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("artist")] string Artist,
        [property: JsonPropertyName("duration")] string Duration,
        [property: JsonPropertyName("thumbnail")] string Thumbnail
    );

    public sealed record SearchResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("results")] IReadOnlyList<SearchTrackItem> Results,
        [property: JsonPropertyName("error")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null
    );

    private readonly ConcurrentDictionary<string, ResolvedAudio> _streamCache = new(StringComparer.Ordinal);
    private readonly HttpClient _apiHttpClient;
    private readonly HttpClient _streamHttpClient;
    private readonly CancellationTokenSource _cts = new();

    private HttpListener? _listener;
    private Task? _acceptLoopTask;
    private int _disposed;

    public int Port { get; private set; }

    public NativeDesktopEngine()
    {
        var apiHandler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _apiHttpClient = new HttpClient(apiHandler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        var streamHandler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None
        };
        _streamHttpClient = new HttpClient(streamHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static bool IsValidVideoId(string? videoId)
    {
        return !string.IsNullOrEmpty(videoId) && VideoIdRegex().IsMatch(videoId);
    }

    public string GetLocalAudioUrl(string videoId)
    {
        if (!IsValidVideoId(videoId))
        {
            throw new ArgumentException("Invalid YouTube video ID.", nameof(videoId));
        }
        if (Port <= 0)
        {
            throw new InvalidOperationException("Local audio HTTP server has not been started.");
        }
        return $"http://127.0.0.1:{Port}{NativeAudioPathPrefix}{videoId}";
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_listener is not null)
        {
            return;
        }

        Exception? lastError = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int port = FindFreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
                return;
            }
            catch (HttpListenerException ex)
            {
                lastError = ex;
                try { listener.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException("Failed to bind loopback audio HttpListener.", lastError);
    }

    private static int FindFreeLoopbackPort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true } listener)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }

            _ = Task.Run(() => HandleHttpContextAsync(context, ct), CancellationToken.None);
        }
    }

    private async Task HandleHttpContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            string? origin = context.Request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) &&
                !string.Equals(origin, AllowedOrigin, StringComparison.OrdinalIgnoreCase))
            {
                await WritePlainErrorAsync(context.Response, 403, "Forbidden origin", ct).ConfigureAwait(false);
                return;
            }

            string method = context.Request.HttpMethod?.ToUpperInvariant() ?? "GET";
            if (method == "OPTIONS")
            {
                ApplyCorsHeaders(context.Response, context.Request);
                context.Response.StatusCode = 204;
                context.Response.StatusDescription = "No Content";
                context.Response.ContentLength64 = 0;
                return;
            }

            if (method != "GET" && method != "HEAD")
            {
                ApplyCorsHeaders(context.Response, context.Request);
                context.Response.AddHeader("Allow", "GET, HEAD, OPTIONS");
                await WritePlainErrorAsync(context.Response, 405, "Method Not Allowed", ct).ConfigureAwait(false);
                return;
            }

            string path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (!path.StartsWith(NativeAudioPathPrefix, StringComparison.Ordinal))
            {
                await WritePlainErrorAsync(context.Response, 404, "Not Found", ct).ConfigureAwait(false);
                return;
            }

            string videoId = path[NativeAudioPathPrefix.Length..].Trim();
            if (!IsValidVideoId(videoId))
            {
                await WritePlainErrorAsync(context.Response, 400, "Invalid video ID", ct).ConfigureAwait(false);
                return;
            }

            await ProxyAudioStreamAsync(context, videoId, isHead: method == "HEAD", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown or client cancellation
        }
        catch (HttpListenerException)
        {
            // Client disconnected mid-response
        }
        catch (IOException)
        {
            // Stream closed by WebView2 during seek/abort
        }
        catch
        {
            try
            {
                if (context.Response.OutputStream.CanWrite)
                {
                    await WritePlainErrorAsync(context.Response, 502, "Proxy error", ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore secondary failure on closed response
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
                // Ignore if already disposed/aborted
            }
        }
    }

    private static void ApplyCorsHeaders(HttpListenerResponse response, HttpListenerRequest request)
    {
        response.Headers["Access-Control-Allow-Origin"] = AllowedOrigin;
        response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Range, Content-Type";
        response.Headers["Access-Control-Expose-Headers"] = "Content-Range, Content-Length, Accept-Ranges";
        response.Headers["Access-Control-Max-Age"] = "3600";
        response.Headers["Vary"] = "Origin";
        response.Headers["Cache-Control"] = "no-store";

        if (string.Equals(request.Headers["Access-Control-Request-Private-Network"], "true", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["Access-Control-Allow-Private-Network"] = "true";
        }
    }

    private static async Task WritePlainErrorAsync(
        HttpListenerResponse response,
        int statusCode,
        string message,
        CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        response.StatusCode = statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        response.Headers["Access-Control-Allow-Origin"] = AllowedOrigin;
        response.Headers["Cache-Control"] = "no-store";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async Task ProxyAudioStreamAsync(
        HttpListenerContext context,
        string videoId,
        bool isHead,
        CancellationToken ct)
    {
        string? rangeHeader = context.Request.Headers["Range"];
        bool hasClientRange = !string.IsNullOrWhiteSpace(rangeHeader) &&
                              rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ResolvedAudio? resolved = await ResolveAudioStreamAsync(videoId, ct).ConfigureAwait(false);
            if (resolved is null)
            {
                break;
            }

            // googlevideo.com rejects HTTP HEAD with 403; probe metadata using GET Range: bytes=0-0 when HEAD is requested.
            using var upstreamReq = new HttpRequestMessage(HttpMethod.Get, resolved.StreamUrl);
            upstreamReq.Headers.TryAddWithoutValidation("User-Agent", resolved.RequestUserAgent);
            if (hasClientRange)
            {
                upstreamReq.Headers.TryAddWithoutValidation("Range", rangeHeader!.Trim());
            }
            else if (isHead)
            {
                upstreamReq.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
            }

            using HttpResponseMessage upstream = await _streamHttpClient
                .SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            int statusCode = (int)upstream.StatusCode;
            if (statusCode == 403 || statusCode == 410)
            {
                _streamCache.TryRemove(videoId, out _);
                if (attempt == 0)
                {
                    continue;
                }
                await WritePlainErrorAsync(context.Response, 502, "Upstream audio forbidden", ct).ConfigureAwait(false);
                return;
            }

            if (statusCode < 200 || statusCode >= 300)
            {
                await WritePlainErrorAsync(context.Response, statusCode, "Upstream audio error", ct).ConfigureAwait(false);
                return;
            }

            HttpListenerResponse resp = context.Response;
            ApplyCorsHeaders(resp, context.Request);

            string contentType = upstream.Content.Headers.ContentType?.MediaType ?? resolved.MimeType;
            resp.ContentType = string.IsNullOrWhiteSpace(contentType) ? "audio/mp4" : contentType;

            string acceptRanges = upstream.Headers.TryGetValues("Accept-Ranges", out var acceptValues)
                ? string.Join(", ", acceptValues)
                : "bytes";
            resp.Headers["Accept-Ranges"] = acceptRanges;

            if (isHead && !hasClientRange)
            {
                resp.StatusCode = 200;
                resp.StatusDescription = "OK";
                if (upstream.Content.Headers.ContentRange?.Length is long totalLen && totalLen >= 0)
                {
                    resp.ContentLength64 = totalLen;
                }
                else if (upstream.Content.Headers.ContentLength is long fallbackLen && fallbackLen >= 0)
                {
                    resp.ContentLength64 = fallbackLen;
                }
                return;
            }

            resp.StatusCode = statusCode == 206 ? 206 : 200;
            resp.StatusDescription = resp.StatusCode == 206 ? "Partial Content" : "OK";

            if (upstream.Content.Headers.TryGetValues("Content-Range", out var rangeValues))
            {
                string? cr = rangeValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(cr))
                {
                    resp.Headers["Content-Range"] = cr;
                }
            }

            if (upstream.Content.Headers.ContentLength is long contentLength && contentLength >= 0)
            {
                resp.ContentLength64 = contentLength;
            }

            if (!isHead)
            {
                await using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await upstreamStream.CopyToAsync(resp.OutputStream, 65536, ct).ConfigureAwait(false);
                await resp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
            }
            return;
        }

        await WritePlainErrorAsync(context.Response, 502, "Audio stream unavailable", ct).ConfigureAwait(false);
    }

    public async Task<ResolvedAudio?> ResolveAudioStreamAsync(string? videoId, CancellationToken ct = default)
    {
        if (!IsValidVideoId(videoId))
        {
            return null;
        }
        string cleanId = videoId!;

        if (_streamCache.TryGetValue(cleanId, out ResolvedAudio? cached))
        {
            if (cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return cached;
            }
            _streamCache.TryRemove(cleanId, out _);
        }

        ResolvedAudio? resolved = await ResolveViaAndroidVrAsync(cleanId, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            resolved = await ResolveViaIosAsync(cleanId, ct).ConfigureAwait(false);
        }

        if (resolved is not null)
        {
            PruneStreamCacheIfNeeded();
            _streamCache[cleanId] = resolved;
        }
        return resolved;
    }

    private void PruneStreamCacheIfNeeded()
    {
        if (_streamCache.Count < MaxStreamCacheEntries)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var kvp in _streamCache)
        {
            if (kvp.Value.ExpiresAtUtc <= now)
            {
                _streamCache.TryRemove(kvp.Key, out _);
            }
        }

        if (_streamCache.Count >= MaxStreamCacheEntries)
        {
            _streamCache.Clear();
        }
    }

    private Task<ResolvedAudio?> ResolveViaAndroidVrAsync(string videoId, CancellationToken ct)
    {
        var payloadObj = new
        {
            context = new
            {
                client = new
                {
                    clientName = "ANDROID_VR",
                    clientVersion = "1.57.29",
                    androidSdkVersion = 34,
                    hl = "tr",
                    gl = "TR"
                }
            },
            videoId
        };

        return ResolveViaPlayerClientAsync(
            videoId,
            payloadObj,
            AndroidVrUserAgent,
            clientNameId: "28",
            clientVersion: "1.57.29",
            ct);
    }

    private Task<ResolvedAudio?> ResolveViaIosAsync(string videoId, CancellationToken ct)
    {
        var payloadObj = new
        {
            context = new
            {
                client = new
                {
                    clientName = "IOS",
                    clientVersion = "20.10.4",
                    deviceMake = "Apple",
                    deviceModel = "iPhone16,2",
                    osName = "iPhone",
                    osVersion = "18.3.2.22D82",
                    hl = "tr",
                    gl = "TR"
                }
            },
            videoId
        };

        return ResolveViaPlayerClientAsync(
            videoId,
            payloadObj,
            IosUserAgent,
            clientNameId: "5",
            clientVersion: "20.10.4",
            ct);
    }

    private async Task<ResolvedAudio?> ResolveViaPlayerClientAsync(
        string videoId,
        object payloadObj,
        string userAgent,
        string clientNameId,
        string clientVersion,
        CancellationToken ct)
    {
        try
        {
            string jsonBody = JsonSerializer.Serialize(payloadObj);
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientNameId);
            req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);

            using HttpResponseMessage res = await _apiHttpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream bodyStream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(bodyStream, cancellationToken: ct).ConfigureAwait(false);

            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("streamingData", out JsonElement streamingData) ||
                streamingData.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!streamingData.TryGetProperty("adaptiveFormats", out JsonElement adaptiveFormats) ||
                adaptiveFormats.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? bestUrl = null;
            string bestMime = "audio/mp4";
            int bestItag = 0;
            int bestScore = int.MinValue;

            foreach (JsonElement format in adaptiveFormats.EnumerateArray())
            {
                if (format.ValueKind != JsonValueKind.Object) continue;

                string mime = GetString(format, "mimeType");
                string url = GetString(format, "url");
                if (!mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(url))
                {
                    continue;
                }

                int itag = GetInt32(format, "itag");
                int bitrate = GetInt32(format, "bitrate");

                int score = 0;
                if (itag == 140) score += 2000;
                else if (itag == 251) score += 1500;
                else if (mime.StartsWith("audio/mp4", StringComparison.OrdinalIgnoreCase)) score += 500;
                else if (mime.StartsWith("audio/webm", StringComparison.OrdinalIgnoreCase)) score += 300;

                if (bitrate is >= 96_000 and <= 192_000) score += 200;
                else if (bitrate > 192_000) score += 120;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestUrl = url;
                    bestItag = itag;
                    int semiIdx = mime.IndexOf(';');
                    bestMime = semiIdx > 0 ? mime[..semiIdx].Trim() : mime.Trim();
                }
            }

            if (string.IsNullOrEmpty(bestUrl))
            {
                return null;
            }

            string title = string.Empty;
            string artist = string.Empty;
            long durationSec = 0;
            if (root.TryGetProperty("videoDetails", out JsonElement details) && details.ValueKind == JsonValueKind.Object)
            {
                title = GetString(details, "title");
                artist = GetString(details, "author");
                string lenStr = GetString(details, "lengthSeconds");
                _ = long.TryParse(lenStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out durationSec);
            }

            return new ResolvedAudio(
                videoId,
                bestUrl,
                bestMime,
                bestItag,
                title,
                artist,
                durationSec,
                userAgent,
                DateTimeOffset.UtcNow.Add(StreamCacheTtl)
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<SearchResponse> SearchYouTubeLocalAsync(
        string? query,
        bool filterSingles,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchResponse(false, "desktop-youtube", Array.Empty<SearchTrackItem>());
        }

        try
        {
            var payloadObj = new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "WEB",
                        clientVersion = WebClientVersion,
                        hl = "tr",
                        gl = "TR"
                    }
                },
                query = query.Trim()
            };

            string jsonBody = JsonSerializer.Serialize(payloadObj);
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                "https://www.youtube.com/youtubei/v1/search?prettyPrint=false")
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
            req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
            req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", WebClientVersion);

            using HttpResponseMessage res = await _apiHttpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return new SearchResponse(
                    false,
                    "desktop-youtube",
                    Array.Empty<SearchTrackItem>(),
                    $"HTTP {(int)res.StatusCode}");
            }

            await using Stream bodyStream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(bodyStream, cancellationToken: ct).ConfigureAwait(false);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var primary = new List<SearchTrackItem>(MaxSearchResults);
            var secondary = new List<SearchTrackItem>(MaxSearchResults);

            CollectVideoRenderers(doc.RootElement, filterSingles, seen, primary, secondary);

            var merged = new List<SearchTrackItem>(MaxSearchResults);
            foreach (var item in primary)
            {
                if (merged.Count >= MaxSearchResults) break;
                merged.Add(item);
            }
            foreach (var item in secondary)
            {
                if (merged.Count >= MaxSearchResults) break;
                merged.Add(item);
            }

            return new SearchResponse(merged.Count > 0, "desktop-youtube", merged);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SearchResponse(false, "desktop-youtube", Array.Empty<SearchTrackItem>(), ex.Message);
        }
    }

    private static void CollectVideoRenderers(
        JsonElement element,
        bool filterSingles,
        HashSet<string> seen,
        List<SearchTrackItem> primary,
        List<SearchTrackItem> secondary)
    {
        if (primary.Count >= MaxSearchResults)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("videoRenderer") && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        TryAddVideoRenderer(prop.Value, filterSingles, seen, primary, secondary);
                        if (primary.Count >= MaxSearchResults) return;
                    }
                    else
                    {
                        CollectVideoRenderers(prop.Value, filterSingles, seen, primary, secondary);
                        if (primary.Count >= MaxSearchResults) return;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    CollectVideoRenderers(item, filterSingles, seen, primary, secondary);
                    if (primary.Count >= MaxSearchResults) return;
                }
                break;
        }
    }

    private static void TryAddVideoRenderer(
        JsonElement vr,
        bool filterSingles,
        HashSet<string> seen,
        List<SearchTrackItem> primary,
        List<SearchTrackItem> secondary)
    {
        string videoId = GetString(vr, "videoId");
        if (!IsValidVideoId(videoId) || seen.Contains(videoId))
        {
            return;
        }

        // Skip upcoming premieres or Shorts reel endpoints
        if (vr.TryGetProperty("upcomingEventData", out _))
        {
            return;
        }
        if (vr.TryGetProperty("navigationEndpoint", out JsonElement nav) &&
            nav.ValueKind == JsonValueKind.Object &&
            nav.TryGetProperty("reelWatchEndpoint", out _))
        {
            return;
        }
        if (IsLiveOrShortRenderer(vr))
        {
            return;
        }

        string durationText = ExtractText(vr, "lengthText");
        if (string.IsNullOrWhiteSpace(durationText))
        {
            return;
        }

        long durationSec = ParseDurationSeconds(durationText);
        if (durationSec <= 0)
        {
            return;
        }

        string title = ExtractText(vr, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Bilinmeyen Şarkı";
        }

        string artist = ExtractText(vr, "ownerText");
        if (string.IsNullOrWhiteSpace(artist))
        {
            artist = ExtractText(vr, "longBylineText");
        }
        if (string.IsNullOrWhiteSpace(artist))
        {
            artist = ExtractText(vr, "shortBylineText");
        }
        if (string.IsNullOrWhiteSpace(artist))
        {
            artist = "Bilinmeyen Sanatçı";
        }

        string lowTitle = title.ToLowerInvariant();
        if (lowTitle.Contains("#shorts", StringComparison.Ordinal))
        {
            return;
        }

        if (filterSingles)
        {
            if (durationSec < 60 || durationSec > 539)
            {
                return;
            }
            if (lowTitle.Contains("tüm şarkılar", StringComparison.Ordinal) ||
                lowTitle.Contains("karışık şarkılar", StringComparison.Ordinal) ||
                lowTitle.Contains("full albüm", StringComparison.Ordinal) ||
                lowTitle.Contains("full album", StringComparison.Ordinal) ||
                lowTitle.Contains("megamix", StringComparison.Ordinal) ||
                lowTitle.Contains("1 saat", StringComparison.Ordinal) ||
                lowTitle.Contains("1 hour", StringComparison.Ordinal))
            {
                return;
            }
        }

        seen.Add(videoId);
        var item = new SearchTrackItem(
            videoId,
            title.Trim(),
            artist.Trim(),
            durationText.Trim(),
            $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg"
        );

        if (durationSec is >= 75 and <= 480)
        {
            primary.Add(item);
        }
        else
        {
            secondary.Add(item);
        }
    }

    private static bool IsLiveOrShortRenderer(JsonElement vr)
    {
        if (vr.TryGetProperty("badges", out JsonElement badges) && badges.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement badge in badges.EnumerateArray())
            {
                if (badge.TryGetProperty("metadataBadgeRenderer", out JsonElement mbr) &&
                    mbr.ValueKind == JsonValueKind.Object)
                {
                    string style = GetString(mbr, "style");
                    if (style.Contains("LIVE", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        if (vr.TryGetProperty("thumbnailOverlays", out JsonElement overlays) && overlays.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement overlay in overlays.EnumerateArray())
            {
                if (overlay.TryGetProperty("thumbnailOverlayTimeStatusRenderer", out JsonElement status) &&
                    status.ValueKind == JsonValueKind.Object)
                {
                    string style = GetString(status, "style");
                    if (style.Equals("LIVE", StringComparison.OrdinalIgnoreCase) ||
                        style.Equals("SHORTS", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string ExtractText(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement prop) || prop.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        string simple = GetString(prop, "simpleText");
        if (!string.IsNullOrEmpty(simple))
        {
            return simple;
        }

        if (prop.TryGetProperty("runs", out JsonElement runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (JsonElement run in runs.EnumerateArray())
            {
                string part = GetString(run, "text");
                if (!string.IsNullOrEmpty(part))
                {
                    sb.Append(part);
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    private static long ParseDurationSeconds(string durationText)
    {
        string[] parts = durationText.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return 0;
        }

        long total = 0;
        foreach (string part in parts)
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long val) || val < 0)
            {
                return 0;
            }
            total = (total * 60) + val;
        }
        return total;
    }

    private static string GetString(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(propertyName, out JsonElement prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static int GetInt32(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(propertyName, out JsonElement prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetInt32(out int val))
        {
            return val;
        }
        return 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }

        try
        {
            _acceptLoopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown wait errors
        }

        _apiHttpClient.Dispose();
        _streamHttpClient.Dispose();
        _cts.Dispose();
    }
}
