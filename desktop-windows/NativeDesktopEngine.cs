using System.Buffers;
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
    private static readonly string[] AllowedOrigins =
    [
        "https://kurfanova.github.io",
        "https://kurfanova.kurfa.workers.dev"
    ];
    public const string NativeAudioPathPrefix = "/__kurfa_native_audio__/";
    public const string LocalFilePathPrefix = "/__kurfa_local_file__/";

    private readonly ConcurrentDictionary<string, string> _localFileRegistry = new(StringComparer.Ordinal);
    private readonly object _localAudioLock = new();
    private bool _localAudioLoadedFromDisk;

    private const long MaxIosUpstreamChunkBytes = 2_097_152; // 2 MB bounded chunk (reduces HTTP churn on IOS streams)
    private const long MaxGeneralUpstreamChunkBytes = 10_485_760; // 10 MB bounded chunk for ANDROID / ANDROID_VR / VISIONOS
    private const long HeadProbeEndByte = 1023;
    private static readonly TimeSpan VisitorDataTtl = TimeSpan.FromMinutes(30);

    private const string AndroidUserAgent =
        "com.google.android.youtube/20.10.38 (Linux; U; Android 14; en_US; Pixel 8) gzip";
    private const string VisionOsUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
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
        long ContentLength,
        string Title,
        string Artist,
        long DurationSec,
        string RequestUserAgent,
        string ClientKind,
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
    private readonly ConcurrentDictionary<string, Task<ResolvedAudio?>> _inflightResolutions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _visitorSemaphore = new(1, 1);
    private readonly HttpClient _apiHttpClient;
    private readonly HttpClient _streamHttpClient;
    private readonly CancellationTokenSource _cts = new();

    private string? _visitorData;
    private DateTimeOffset _visitorDataExpiresAtUtc = DateTimeOffset.MinValue;
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
            if (!IsAllowedOriginHeader(origin, out _))
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
            if (path.StartsWith(LocalFilePathPrefix, StringComparison.Ordinal))
            {
                string localId = path[LocalFilePathPrefix.Length..].Trim();
                await ServeLocalAudioFileAsync(context, localId, isHead: method == "HEAD", ct).ConfigureAwait(false);
                return;
            }

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

    private static bool IsAllowedOriginHeader(string? origin, out string matchedOrigin)
    {
        matchedOrigin = AllowedOrigin;
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        foreach (string candidate in AllowedOrigins)
        {
            if (string.Equals(origin, candidate, StringComparison.OrdinalIgnoreCase))
            {
                matchedOrigin = candidate;
                return true;
            }
        }

        if (Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)))
        {
            matchedOrigin = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        return false;
    }

    private static void ApplyCorsHeaders(HttpListenerResponse response, HttpListenerRequest request)
    {
        IsAllowedOriginHeader(request.Headers["Origin"], out string corsOrigin);
        response.Headers["Access-Control-Allow-Origin"] = corsOrigin;
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
        bool hasClientRange = TryParseByteRangeHeader(rangeHeader, out long clientStart, out long? clientRequestedEnd);
        bool forceAlternateClient = false;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ResolvedAudio? resolved = await ResolveAudioStreamAsync(
                videoId,
                ct,
                forceRefresh: attempt > 0,
                preferAlternateClient: forceAlternateClient).ConfigureAwait(false);
            if (resolved is null)
            {
                break;
            }

            if (isHead && !hasClientRange)
            {
                using var headProbeReq = new HttpRequestMessage(HttpMethod.Get, resolved.StreamUrl);
                headProbeReq.Headers.TryAddWithoutValidation("User-Agent", resolved.RequestUserAgent);
                long probeEnd = resolved.ContentLength > 0
                    ? Math.Min(HeadProbeEndByte, resolved.ContentLength - 1)
                    : HeadProbeEndByte;
                headProbeReq.Headers.TryAddWithoutValidation("Range", $"bytes=0-{probeEnd}");

                using HttpResponseMessage headUpstream = await _streamHttpClient
                    .SendAsync(headProbeReq, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                int headStatus = (int)headUpstream.StatusCode;
                if (headStatus == 403 || headStatus == 410)
                {
                    _streamCache.TryRemove(videoId, out _);
                    forceAlternateClient = resolved.ClientKind is "VISIONOS" or "IOS";
                    if (attempt == 0) continue;
                    await WritePlainErrorAsync(context.Response, 502, "Upstream audio forbidden", ct).ConfigureAwait(false);
                    return;
                }
                if (headStatus < 200 || headStatus >= 300)
                {
                    await WritePlainErrorAsync(context.Response, headStatus, "Upstream audio error", ct).ConfigureAwait(false);
                    return;
                }

                HttpListenerResponse headResp = context.Response;
                ApplyCorsHeaders(headResp, context.Request);
                string headMime = headUpstream.Content.Headers.ContentType?.MediaType ?? resolved.MimeType;
                headResp.ContentType = string.IsNullOrWhiteSpace(headMime) ? "audio/mp4" : headMime;
                headResp.Headers["Accept-Ranges"] = "bytes";
                headResp.StatusCode = 200;
                headResp.StatusDescription = "OK";

                if (headUpstream.Content.Headers.ContentRange?.Length is long probedTotal && probedTotal > 0)
                {
                    headResp.ContentLength64 = probedTotal;
                }
                else if (resolved.ContentLength > 0)
                {
                    headResp.ContentLength64 = resolved.ContentLength;
                }
                else if (headUpstream.Content.Headers.ContentLength is long fallbackHeadLen && fallbackHeadLen >= 0)
                {
                    headResp.ContentLength64 = fallbackHeadLen;
                }
                return;
            }

            if (hasClientRange)
            {
                if (resolved.ContentLength > 0 && clientStart >= resolved.ContentLength)
                {
                    HttpListenerResponse r416 = context.Response;
                    ApplyCorsHeaders(r416, context.Request);
                    r416.StatusCode = 416;
                    r416.StatusDescription = "Range Not Satisfiable";
                    r416.Headers["Content-Range"] = $"bytes */{resolved.ContentLength}";
                    r416.ContentLength64 = 0;
                    return;
                }

                long chunkCap = string.Equals(resolved.ClientKind, "IOS", StringComparison.Ordinal)
                    ? MaxIosUpstreamChunkBytes
                    : MaxGeneralUpstreamChunkBytes;
                long maxChunkEnd = clientStart + chunkCap - 1;
                long fetchEnd = clientRequestedEnd.HasValue
                    ? Math.Min(clientRequestedEnd.Value, maxChunkEnd)
                    : maxChunkEnd;
                if (resolved.ContentLength > 0)
                {
                    fetchEnd = Math.Min(fetchEnd, resolved.ContentLength - 1);
                }
                if (fetchEnd < clientStart)
                {
                    fetchEnd = clientStart;
                }

                using var upstreamReq = new HttpRequestMessage(HttpMethod.Get, resolved.StreamUrl);
                upstreamReq.Headers.TryAddWithoutValidation("User-Agent", resolved.RequestUserAgent);
                upstreamReq.Headers.TryAddWithoutValidation("Range", $"bytes={clientStart}-{fetchEnd}");

                using HttpResponseMessage upstream = await _streamHttpClient
                    .SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                int statusCode = (int)upstream.StatusCode;
                if (statusCode == 403 || statusCode == 410)
                {
                    _streamCache.TryRemove(videoId, out _);
                    forceAlternateClient = resolved.ClientKind is "VISIONOS" or "IOS";
                    if (attempt == 0) continue;
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
                resp.Headers["Accept-Ranges"] = "bytes";
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

            // Full GET without Range header: stream in sequential bounded chunks so CDN never returns 403
            long fullChunkCap = string.Equals(resolved.ClientKind, "IOS", StringComparison.Ordinal)
                ? MaxIosUpstreamChunkBytes
                : MaxGeneralUpstreamChunkBytes;
            long offset = 0;
            long totalLength = resolved.ContentLength;
            bool headersSent = false;
            bool retryFromScratch = false;

            while (true)
            {
                long chunkEnd = offset + fullChunkCap - 1;
                if (totalLength > 0)
                {
                    chunkEnd = Math.Min(chunkEnd, totalLength - 1);
                }

                using var chunkReq = new HttpRequestMessage(HttpMethod.Get, resolved.StreamUrl);
                chunkReq.Headers.TryAddWithoutValidation("User-Agent", resolved.RequestUserAgent);
                chunkReq.Headers.TryAddWithoutValidation("Range", $"bytes={offset}-{chunkEnd}");

                using HttpResponseMessage chunkUpstream = await _streamHttpClient
                    .SendAsync(chunkReq, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                int statusCode = (int)chunkUpstream.StatusCode;
                if ((statusCode == 403 || statusCode == 410) && !headersSent)
                {
                    _streamCache.TryRemove(videoId, out _);
                    forceAlternateClient = resolved.ClientKind is "VISIONOS" or "IOS";
                    retryFromScratch = attempt == 0;
                    break;
                }
                if (statusCode < 200 || statusCode >= 300)
                {
                    if (!headersSent)
                    {
                        await WritePlainErrorAsync(context.Response, statusCode == 403 || statusCode == 410 ? 502 : statusCode, "Upstream audio error", ct).ConfigureAwait(false);
                        return;
                    }
                    break;
                }

                if (chunkUpstream.Content.Headers.ContentRange?.Length is long crTotal && crTotal > 0)
                {
                    totalLength = crTotal;
                }

                if (!headersSent)
                {
                    HttpListenerResponse resp = context.Response;
                    ApplyCorsHeaders(resp, context.Request);
                    string contentType = chunkUpstream.Content.Headers.ContentType?.MediaType ?? resolved.MimeType;
                    resp.ContentType = string.IsNullOrWhiteSpace(contentType) ? "audio/mp4" : contentType;
                    resp.Headers["Accept-Ranges"] = "bytes";
                    resp.StatusCode = 200;
                    resp.StatusDescription = "OK";
                    if (totalLength > 0)
                    {
                        resp.ContentLength64 = totalLength;
                    }
                    headersSent = true;
                }

                long writtenInChunk = 0;
                await using (Stream upstreamStream = await chunkUpstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                    try
                    {
                        int read;
                        while ((read = await upstreamStream.ReadAsync(buffer.AsMemory(0, 65536), ct).ConfigureAwait(false)) > 0)
                        {
                            await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            writtenInChunk += read;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                await context.Response.OutputStream.FlushAsync(ct).ConfigureAwait(false);

                if (writtenInChunk <= 0 || statusCode == 200)
                {
                    break;
                }
                offset += writtenInChunk;
                if (totalLength > 0 && offset >= totalLength)
                {
                    break;
                }
            }

            if (retryFromScratch)
            {
                continue;
            }
            if (headersSent)
            {
                return;
            }
        }

        await WritePlainErrorAsync(context.Response, 502, "Audio stream unavailable", ct).ConfigureAwait(false);
    }

    private static bool TryParseByteRangeHeader(string? rangeHeader, out long start, out long? end)
    {
        start = 0;
        end = null;
        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return false;
        }
        string trimmed = rangeHeader.Trim();
        if (!trimmed.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string spec = trimmed["bytes=".Length..].Trim();
        int commaIdx = spec.IndexOf(',');
        if (commaIdx >= 0)
        {
            spec = spec[..commaIdx].Trim();
        }
        int dashIdx = spec.IndexOf('-');
        if (dashIdx <= 0)
        {
            return false;
        }
        if (!long.TryParse(spec[..dashIdx].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start < 0)
        {
            return false;
        }
        string endPart = spec[(dashIdx + 1)..].Trim();
        if (!string.IsNullOrEmpty(endPart))
        {
            if (!long.TryParse(endPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedEnd) || parsedEnd < start)
            {
                return false;
            }
            end = parsedEnd;
        }
        return true;
    }

    public async Task<ResolvedAudio?> ResolveAudioStreamAsync(
        string? videoId,
        CancellationToken ct = default,
        bool forceRefresh = false,
        bool preferAlternateClient = false)
    {
        if (!IsValidVideoId(videoId))
        {
            return null;
        }
        string cleanId = videoId!;

        if (!forceRefresh && _streamCache.TryGetValue(cleanId, out ResolvedAudio? cached))
        {
            if (cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return cached;
            }
            _streamCache.TryRemove(cleanId, out _);
        }

        // Coalesce concurrent resolutions for the same videoId so parallel range probes don't hit YouTube multiple times
        string inflightKey = $"{cleanId}:{(forceRefresh ? '1' : '0')}:{(preferAlternateClient ? '1' : '0')}";
        Task<ResolvedAudio?> task = _inflightResolutions.GetOrAdd(
            inflightKey,
            _ => ResolveAudioStreamCoreAsync(cleanId, forceRefresh, preferAlternateClient, ct)
        );
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            _inflightResolutions.TryRemove(new KeyValuePair<string, Task<ResolvedAudio?>>(inflightKey, task));
        }
    }

    private async Task<ResolvedAudio?> ResolveAudioStreamCoreAsync(
        string cleanId,
        bool forceRefresh,
        bool preferAlternateClient,
        CancellationToken ct)
    {
        // Range-verify every candidate before caching so 403/PoToken-locked URLs never break playback.
        ResolvedAudio? resolved = null;
        if (preferAlternateClient)
        {
            resolved = await TryVerifyStreamRangeAsync(await ResolveViaIosAsync(cleanId, ct).ConfigureAwait(false), ct).ConfigureAwait(false)
                ?? await TryVerifyStreamRangeAsync(await ResolveViaAndroidAsync(cleanId, forceRefreshVisitor: true, ct).ConfigureAwait(false), ct).ConfigureAwait(false)
                ?? await TryVerifyStreamRangeAsync(await ResolveViaAndroidVrAsync(cleanId, ct).ConfigureAwait(false), ct).ConfigureAwait(false)
                ?? await TryVerifyStreamRangeAsync(await ResolveViaVisionOsAsync(cleanId, forceRefreshVisitor: true, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        else
        {
            resolved = await TryVerifyStreamRangeAsync(await ResolveViaAndroidAsync(cleanId, forceRefreshVisitor: forceRefresh, ct).ConfigureAwait(false), ct).ConfigureAwait(false)
                ?? await TryVerifyStreamRangeAsync(await ResolveViaIosAsync(cleanId, ct).ConfigureAwait(false), ct).ConfigureAwait(false)
                ?? await TryVerifyStreamRangeAsync(await ResolveViaAndroidVrAsync(cleanId, ct).ConfigureAwait(false), ct).ConfigureAwait(false)
                ?? await TryVerifyStreamRangeAsync(await ResolveViaVisionOsAsync(cleanId, forceRefreshVisitor: forceRefresh, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }

        if (resolved is not null)
        {
            PruneStreamCacheIfNeeded();
            _streamCache[cleanId] = resolved;
        }
        return resolved;
    }

    private async Task<ResolvedAudio?> ResolveViaAndroidAsync(
        string videoId,
        bool forceRefreshVisitor,
        CancellationToken ct)
    {
        string? visitorData = await ResolveVisitorDataAsync(forceRefreshVisitor, ct).ConfigureAwait(false);
        var payloadObj = new
        {
            context = new
            {
                client = new
                {
                    clientName = "ANDROID",
                    clientVersion = "20.10.38",
                    androidSdkVersion = 34,
                    osName = "Android",
                    osVersion = "14",
                    visitorData,
                    hl = "tr",
                    gl = "TR"
                }
            },
            videoId,
            contentCheckOk = true,
            racyCheckOk = true
        };

        return await ResolveViaPlayerClientAsync(
            videoId,
            payloadObj,
            AndroidUserAgent,
            clientNameId: "3",
            clientVersion: "20.10.38",
            visitorData: visitorData,
            clientKind: "ANDROID",
            ct).ConfigureAwait(false);
    }

    private async Task<ResolvedAudio?> TryVerifyStreamRangeAsync(ResolvedAudio? candidate, CancellationToken ct)
    {
        if (candidate is null || string.IsNullOrEmpty(candidate.StreamUrl))
        {
            return null;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, candidate.StreamUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", candidate.RequestUserAgent);
            req.Headers.TryAddWithoutValidation("Range", $"bytes=0-{HeadProbeEndByte}");

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(8));

            using HttpResponseMessage resp = await _streamHttpClient
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);

            int code = (int)resp.StatusCode;
            if (code != 200 && code != 206)
            {
                return null;
            }

            long resolvedLength = candidate.ContentLength;
            if (resolvedLength <= 0 && resp.Content.Headers.ContentRange?.Length is long crLen && crLen > 0)
            {
                resolvedLength = crLen;
            }
            else if (resolvedLength <= 0 && code == 200 && resp.Content.Headers.ContentLength is long cl && cl > 0)
            {
                resolvedLength = cl;
            }

            return resolvedLength != candidate.ContentLength
                ? candidate with { ContentLength = resolvedLength }
                : candidate;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
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

    private async Task<string?> ResolveVisitorDataAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && !string.IsNullOrEmpty(_visitorData) && _visitorDataExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return _visitorData;
        }

        await _visitorSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && !string.IsNullOrEmpty(_visitorData) && _visitorDataExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return _visitorData;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data");
            req.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);

            using HttpResponseMessage res = await _apiHttpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return _visitorData;
            }

            string raw = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (raw.StartsWith(")]}'", StringComparison.Ordinal))
            {
                raw = raw[4..].TrimStart();
            }

            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                JsonElement n0 = root[0];
                if (n0.ValueKind == JsonValueKind.Array && n0.GetArrayLength() > 2)
                {
                    JsonElement n2 = n0[2];
                    if (n2.ValueKind == JsonValueKind.Array && n2.GetArrayLength() > 0)
                    {
                        JsonElement n20 = n2[0];
                        if (n20.ValueKind == JsonValueKind.Array && n20.GetArrayLength() > 0)
                        {
                            JsonElement n200 = n20[0];
                            if (n200.ValueKind == JsonValueKind.Array && n200.GetArrayLength() > 13 &&
                                n200[13].ValueKind == JsonValueKind.String)
                            {
                                string? vd = n200[13].GetString();
                                if (!string.IsNullOrWhiteSpace(vd))
                                {
                                    _visitorData = vd;
                                    _visitorDataExpiresAtUtc = DateTimeOffset.UtcNow + VisitorDataTtl;
                                    return _visitorData;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Keep existing visitorData if network fails
        }
        finally
        {
            _visitorSemaphore.Release();
        }
        return _visitorData;
    }

    private async Task<ResolvedAudio?> ResolveViaVisionOsAsync(
        string videoId,
        bool forceRefreshVisitor,
        CancellationToken ct)
    {
        string? visitorData = await ResolveVisitorDataAsync(forceRefreshVisitor, ct).ConfigureAwait(false);
        var payloadObj = new
        {
            context = new
            {
                client = new
                {
                    clientName = "VISIONOS",
                    clientVersion = "1.02",
                    deviceMake = "Apple",
                    deviceModel = "RealityDevice17,1",
                    osName = "visionOS",
                    osVersion = "26.5.23O471",
                    visitorData,
                    hl = "tr",
                    gl = "TR"
                }
            },
            videoId,
            contentCheckOk = true,
            racyCheckOk = true
        };

        return await ResolveViaPlayerClientAsync(
            videoId,
            payloadObj,
            VisionOsUserAgent,
            clientNameId: null,
            clientVersion: null,
            visitorData: visitorData,
            clientKind: "VISIONOS",
            ct).ConfigureAwait(false);
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
            visitorData: null,
            clientKind: "ANDROID_VR",
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
            visitorData: null,
            clientKind: "IOS",
            ct);
    }

    private async Task<ResolvedAudio?> ResolveViaPlayerClientAsync(
        string videoId,
        object payloadObj,
        string userAgent,
        string? clientNameId,
        string? clientVersion,
        string? visitorData,
        string clientKind,
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
            if (!string.IsNullOrEmpty(clientNameId))
            {
                req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientNameId);
            }
            if (!string.IsNullOrEmpty(clientVersion))
            {
                req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            }
            if (!string.IsNullOrEmpty(visitorData))
            {
                req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);
            }

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
            long bestContentLength = 0;
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
                long clen = 0;
                string clenStr = GetString(format, "contentLength");
                if (!string.IsNullOrEmpty(clenStr))
                {
                    _ = long.TryParse(clenStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out clen);
                }

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
                    bestContentLength = clen;
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
                bestContentLength,
                title,
                artist,
                durationSec,
                userAgent,
                clientKind,
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

    public sealed record DownloadMediaResult(
        bool Success,
        string? FileName,
        string? FilePath,
        string? MimeType,
        long BytesDownloaded,
        string? Error);

    public async Task<DownloadMediaResult> DownloadMediaToDownloadsAsync(
        string? videoId,
        string format,
        string? preferredTitle,
        Action<long, long, int>? onProgress,
        string? customDownloadDir = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return new DownloadMediaResult(false, null, null, null, 0, "Geçersiz video kimliği");
        }

        string safeId = videoId.Trim();
        bool wantVideo = string.Equals(format, "mp4", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(format, "video", StringComparison.OrdinalIgnoreCase);

        ResolvedAudio? media = wantVideo
            ? await ResolveMuxedVideoAsync(safeId, skipClient: null, ct).ConfigureAwait(false)
            : await ResolveAudioStreamAsync(safeId, ct: ct).ConfigureAwait(false);

        if (media is null || string.IsNullOrEmpty(media.StreamUrl))
        {
            return new DownloadMediaResult(
                false,
                null,
                null,
                null,
                0,
                wantVideo ? "MP4 video akışı bu parça için bulunamadı" : "Ses akışı çözümlenemedi");
        }

        string ext = wantVideo
            ? (media.MimeType.Contains("webm", StringComparison.OrdinalIgnoreCase) ? ".webm" : ".mp4")
            : (media.MimeType.Contains("webm", StringComparison.OrdinalIgnoreCase) ? ".webm" : ".m4a");
        string baseTitle = !string.IsNullOrWhiteSpace(preferredTitle)
            ? preferredTitle!
            : (!string.IsNullOrWhiteSpace(media.Title) ? media.Title : $"Kurfamuzik_{safeId}");
        string safeFileName = SanitizeDownloadFileName(baseTitle) + ext;

        string? downloadsDir = null;
        if (!string.IsNullOrWhiteSpace(customDownloadDir))
        {
            try
            {
                string candidate = Path.GetFullPath(customDownloadDir.Trim());
                Directory.CreateDirectory(candidate);
                downloadsDir = candidate;
            }
            catch
            {
                downloadsDir = null;
            }
        }
        if (string.IsNullOrWhiteSpace(downloadsDir))
        {
            downloadsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (string.IsNullOrWhiteSpace(downloadsDir) || !Directory.Exists(downloadsDir))
            {
                downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            }
            Directory.CreateDirectory(downloadsDir);
        }

        string targetPath = ResolveUniqueDownloadPath(downloadsDir, safeFileName);
        string tempPath = targetPath + ".part";
        string finalFileName = Path.GetFileName(targetPath);

        long lastReportedTicks = 0;
        long offset = 0;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        bool movedToFinal = false;

        try
        {
            onProgress?.Invoke(0, media.ContentLength, 0);

            bool retriedAlternate = false;
            ResolvedAudio activeMedia = media;

            while (true)
            {
                long chunkCap = string.Equals(activeMedia.ClientKind, "IOS", StringComparison.Ordinal)
                    ? MaxIosUpstreamChunkBytes
                    : MaxGeneralUpstreamChunkBytes;

                long totalBytes = activeMedia.ContentLength;
                offset = 0;
                bool needsAlternateRetry = false;
                int failureStatusCode = 0;

                await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        long chunkEnd = offset + chunkCap - 1;
                        if (totalBytes > 0)
                        {
                            if (offset >= totalBytes) break;
                            chunkEnd = Math.Min(chunkEnd, totalBytes - 1);
                        }

                        using var req = new HttpRequestMessage(HttpMethod.Get, activeMedia.StreamUrl);
                        req.Headers.TryAddWithoutValidation("User-Agent", activeMedia.RequestUserAgent);
                        req.Headers.TryAddWithoutValidation("Range", $"bytes={offset}-{chunkEnd}");

                        using HttpResponseMessage resp = await _streamHttpClient
                            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                            .ConfigureAwait(false);

                        int code = (int)resp.StatusCode;
                        if (code < 200 || code >= 300)
                        {
                            if (offset > 0 && code == 416) break;
                            if (!retriedAlternate && (code == 401 || code == 403 || code == 410))
                            {
                                needsAlternateRetry = true;
                                break;
                            }
                            failureStatusCode = code;
                            break;
                        }

                        if (resp.Content.Headers.ContentRange?.Length is long crTotal && crTotal > 0)
                        {
                            totalBytes = crTotal;
                        }
                        else if (totalBytes <= 0 && resp.Content.Headers.ContentLength is long cl && cl > 0 && code == 200)
                        {
                            totalBytes = cl;
                        }

                        await using Stream upstream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        long readInChunk = 0;
                        int read;
                        while ((read = await upstream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            offset += read;
                            readInChunk += read;

                            long nowTicks = Environment.TickCount64;
                            if (nowTicks - lastReportedTicks >= 110)
                            {
                                lastReportedTicks = nowTicks;
                                int pct = totalBytes > 0
                                    ? Math.Clamp((int)((offset * 100L) / totalBytes), 1, 99)
                                    : 15;
                                onProgress?.Invoke(offset, totalBytes, pct);
                            }
                        }

                        if (readInChunk == 0 || code == 200 || (totalBytes > 0 && offset >= totalBytes))
                        {
                            break;
                        }
                    }

                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }

                if (needsAlternateRetry)
                {
                    retriedAlternate = true;
                    TryDeleteTempFile(tempPath);
                    ResolvedAudio? fallback = wantVideo
                        ? await ResolveMuxedVideoAsync(safeId, skipClient: activeMedia.ClientKind, ct).ConfigureAwait(false)
                        : await ResolveAudioStreamCoreAsync(safeId, forceRefresh: true, preferAlternateClient: true, ct).ConfigureAwait(false);
                    if (fallback is not null && !string.IsNullOrEmpty(fallback.StreamUrl))
                    {
                        activeMedia = fallback;
                        continue;
                    }
                    return new DownloadMediaResult(false, null, null, null, 0, "Sunucu erişimi reddedildi (403)");
                }

                if (failureStatusCode != 0)
                {
                    TryDeleteTempFile(tempPath);
                    return new DownloadMediaResult(false, null, null, null, offset, $"Sunucu hatası ({failureStatusCode})");
                }

                File.Move(tempPath, targetPath, overwrite: true);
                movedToFinal = true;
                onProgress?.Invoke(offset, Math.Max(totalBytes, offset), 100);
                return new DownloadMediaResult(true, finalFileName, targetPath, activeMedia.MimeType, offset, null);
            }
        }
        catch (OperationCanceledException)
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteTempFile(tempPath);
            return new DownloadMediaResult(false, null, null, null, offset, ex.Message);
        }
        finally
        {
            if (!movedToFinal)
            {
                TryDeleteTempFile(tempPath);
            }
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<ResolvedAudio?> ResolveMuxedVideoAsync(
        string videoId,
        string? skipClient,
        CancellationToken ct)
    {
        string? visitorData = await ResolveVisitorDataAsync(false, ct).ConfigureAwait(false);

        if (!string.Equals(skipClient, "ANDROID", StringComparison.Ordinal))
        {
            var androidPayload = new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "ANDROID",
                        clientVersion = "20.10.38",
                        androidSdkVersion = 34,
                        osName = "Android",
                        osVersion = "14",
                        visitorData,
                        hl = "tr",
                        gl = "TR"
                    }
                },
                videoId,
                contentCheckOk = true,
                racyCheckOk = true
            };

            ResolvedAudio? androidRes = await TryVerifyStreamRangeAsync(
                await ResolveMuxedVideoFromClientAsync(
                    videoId, androidPayload, AndroidUserAgent, "3", "20.10.38", visitorData, "ANDROID", ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);
            if (androidRes is not null) return androidRes;
        }

        if (!string.Equals(skipClient, "IOS", StringComparison.Ordinal))
        {
            var iosPayload = new
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

            ResolvedAudio? iosRes = await TryVerifyStreamRangeAsync(
                await ResolveMuxedVideoFromClientAsync(
                    videoId, iosPayload, IosUserAgent, "5", "20.10.4", null, "IOS", ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);
            if (iosRes is not null) return iosRes;
        }

        if (!string.Equals(skipClient, "ANDROID_VR", StringComparison.Ordinal))
        {
            var vrPayload = new
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

            ResolvedAudio? vrRes = await TryVerifyStreamRangeAsync(
                await ResolveMuxedVideoFromClientAsync(
                    videoId, vrPayload, AndroidVrUserAgent, "28", "1.57.29", null, "ANDROID_VR", ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);
            if (vrRes is not null) return vrRes;
        }

        if (!string.Equals(skipClient, "VISIONOS", StringComparison.Ordinal))
        {
            var visionPayload = new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "VISIONOS",
                        clientVersion = "1.02",
                        deviceMake = "Apple",
                        deviceModel = "RealityDevice17,1",
                        osName = "visionOS",
                        osVersion = "26.5.23O471",
                        visitorData,
                        hl = "tr",
                        gl = "TR"
                    }
                },
                videoId,
                contentCheckOk = true,
                racyCheckOk = true
            };

            ResolvedAudio? visionRes = await TryVerifyStreamRangeAsync(
                await ResolveMuxedVideoFromClientAsync(
                    videoId, visionPayload, VisionOsUserAgent, null, null, visitorData, "VISIONOS", ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);
            if (visionRes is not null) return visionRes;
        }

        return null;
    }

    private async Task<ResolvedAudio?> ResolveMuxedVideoFromClientAsync(
        string videoId,
        object payloadObj,
        string userAgent,
        string? clientNameId,
        string? clientVersion,
        string? visitorData,
        string clientKind,
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
            if (!string.IsNullOrEmpty(clientNameId)) req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientNameId);
            if (!string.IsNullOrEmpty(clientVersion)) req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            if (!string.IsNullOrEmpty(visitorData)) req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);

            using HttpResponseMessage res = await _apiHttpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            await using Stream bodyStream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(bodyStream, cancellationToken: ct).ConfigureAwait(false);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("streamingData", out JsonElement streamingData) || streamingData.ValueKind != JsonValueKind.Object)
                return null;
            if (!streamingData.TryGetProperty("formats", out JsonElement formats) || formats.ValueKind != JsonValueKind.Array)
                return null;

            string? bestUrl = null;
            string bestMime = "video/mp4";
            int bestItag = 0;
            long bestContentLength = 0;
            int bestScore = int.MinValue;

            foreach (JsonElement format in formats.EnumerateArray())
            {
                if (format.ValueKind != JsonValueKind.Object) continue;
                string mime = GetString(format, "mimeType");
                string url = GetString(format, "url");
                bool isMp4 = mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase);
                bool isWebm = mime.StartsWith("video/webm", StringComparison.OrdinalIgnoreCase);
                if ((!isMp4 && !isWebm) || string.IsNullOrEmpty(url)) continue;

                int itag = GetInt32(format, "itag");
                int height = GetInt32(format, "height");
                long clen = 0;
                string clenStr = GetString(format, "contentLength");
                if (!string.IsNullOrEmpty(clenStr))
                {
                    _ = long.TryParse(clenStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out clen);
                }

                int score = height + (isMp4 ? 500 : 0);
                if (itag == 22) score += 2000; // 720p muxed MP4
                else if (itag == 18) score += 1000; // 360p muxed MP4

                if (score > bestScore)
                {
                    bestScore = score;
                    bestUrl = url;
                    bestItag = itag;
                    bestContentLength = clen;
                    int semiIdx = mime.IndexOf(';');
                    bestMime = semiIdx > 0 ? mime[..semiIdx].Trim() : (isWebm ? "video/webm" : "video/mp4");
                }
            }

            if (string.IsNullOrEmpty(bestUrl)) return null;

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
                videoId, bestUrl, bestMime, bestItag, bestContentLength,
                title, artist, durationSec, userAgent, clientKind, DateTimeOffset.UtcNow.Add(StreamCacheTtl));
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static string SanitizeDownloadFileName(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.Trim())
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c == '\r' || c == '\n' || c == '\t' ? '_' : c);
        }
        string cleaned = sb.ToString().Trim().TrimStart('.');
        if (cleaned.Length > 100) cleaned = cleaned[..100].Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Kurfamuzik" : cleaned;
    }

    private static string ResolveUniqueDownloadPath(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;
        string name = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 1; i < 500; i++)
        {
            string next = Path.Combine(directory, $"{name} ({i}){ext}");
            if (!File.Exists(next)) return next;
        }
        return Path.Combine(directory, $"{name}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{ext}");
    }

    private static string GetLocalAudioStorePath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KurfanovaMuzik");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "local-audio.json");
    }

    private sealed class StoredLocalTrack
    {
        public string id { get; set; } = string.Empty;
        public string path { get; set; } = string.Empty;
        public string title { get; set; } = string.Empty;
        public string artist { get; set; } = string.Empty;
        public string duration { get; set; } = "3:30";
        public string fileName { get; set; } = string.Empty;
        public long fileSize { get; set; }
    }

    private List<StoredLocalTrack> LoadStoredLocalTracksUnlocked()
    {
        string storePath = GetLocalAudioStorePath();
        if (!File.Exists(storePath)) return [];
        try
        {
            string json = File.ReadAllText(storePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<StoredLocalTrack>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveStoredLocalTracksUnlocked(List<StoredLocalTrack> list)
    {
        try
        {
            string storePath = GetLocalAudioStorePath();
            string json = JsonSerializer.Serialize(list);
            File.WriteAllText(storePath, json, Encoding.UTF8);
        }
        catch { /* ignore */ }
    }

    private object ToTrackDto(StoredLocalTrack t) => new
    {
        id = t.id,
        isLocal = true,
        title = t.title,
        artist = t.artist,
        duration = t.duration,
        fileName = t.fileName,
        fileSize = t.fileSize,
        streamUrl = $"http://127.0.0.1:{Port}{LocalFilePathPrefix}{t.id}",
        thumbnail = "./kurfamuzik_logo.png"
    };

    public object[] GetRegisteredLocalAudioTracks()
    {
        lock (_localAudioLock)
        {
            var stored = LoadStoredLocalTracksUnlocked();
            var valid = new List<StoredLocalTrack>(stored.Count);
            bool changed = false;
            foreach (var item in stored)
            {
                if (string.IsNullOrWhiteSpace(item.id) || string.IsNullOrWhiteSpace(item.path) || !File.Exists(item.path))
                {
                    changed = true;
                    continue;
                }
                _localFileRegistry[item.id] = item.path;
                valid.Add(item);
            }
            if (changed) SaveStoredLocalTracksUnlocked(valid);
            _localAudioLoadedFromDisk = true;
            return valid.Select(ToTrackDto).ToArray();
        }
    }

    public object[] RegisterLocalAudioFiles(IEnumerable<string> filePaths)
    {
        lock (_localAudioLock)
        {
            var stored = LoadStoredLocalTracksUnlocked();
            var existingPaths = new HashSet<string>(stored.Select(x => x.path), StringComparer.OrdinalIgnoreCase);
            var newlyAdded = new List<StoredLocalTrack>();

            foreach (string raw in filePaths)
            {
                if (string.IsNullOrWhiteSpace(raw) || !File.Exists(raw)) continue;
                string fullPath = Path.GetFullPath(raw);
                if (existingPaths.Contains(fullPath)) continue;
                existingPaths.Add(fullPath);

                string fileName = Path.GetFileName(fullPath);
                string cleanName = Path.GetFileNameWithoutExtension(fullPath);
                string[] parts = cleanName.Split([" - ", " – ", " — "], 2, StringSplitOptions.None);
                string title = parts.Length == 2 ? parts[1].Trim() : cleanName;
                string artist = parts.Length == 2 ? parts[0].Trim() : "Yerel Dosya";
                long size = 0;
                try { size = new FileInfo(fullPath).Length; } catch { /* ignore */ }

                string id = "local_win_" + Guid.NewGuid().ToString("N")[..12];
                var rec = new StoredLocalTrack
                {
                    id = id,
                    path = fullPath,
                    title = string.IsNullOrWhiteSpace(title) ? cleanName : title,
                    artist = string.IsNullOrWhiteSpace(artist) ? "Yerel Dosya" : artist,
                    duration = "3:30",
                    fileName = fileName,
                    fileSize = size
                };
                _localFileRegistry[id] = fullPath;
                newlyAdded.Add(rec);
            }

            if (newlyAdded.Count > 0)
            {
                stored.InsertRange(0, newlyAdded);
                SaveStoredLocalTracksUnlocked(stored);
            }
            _localAudioLoadedFromDisk = true;
            return newlyAdded.Select(ToTrackDto).ToArray();
        }
    }

    private async Task ServeLocalAudioFileAsync(HttpListenerContext context, string localId, bool isHead, CancellationToken ct)
    {
        ApplyCorsHeaders(context.Response, context.Request);
        if (string.IsNullOrWhiteSpace(localId))
        {
            await WritePlainErrorAsync(context.Response, 400, "Invalid local ID", ct).ConfigureAwait(false);
            return;
        }

        if (!_localFileRegistry.TryGetValue(localId, out string? filePath) && !_localAudioLoadedFromDisk)
        {
            _ = GetRegisteredLocalAudioTracks();
            _localFileRegistry.TryGetValue(localId, out filePath);
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            await WritePlainErrorAsync(context.Response, 404, "Local file not found", ct).ConfigureAwait(false);
            return;
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string mime = ext switch
        {
            ".mp3" => "audio/mpeg",
            ".m4a" or ".aac" => "audio/mp4",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".webm" => "audio/webm",
            _ => "application/octet-stream"
        };

        var fi = new FileInfo(filePath);
        long totalLen = fi.Length;
        string? rangeHeader = context.Request.Headers["Range"];
        long start = 0;
        long end = totalLen - 1;
        bool isPartial = false;

        if (!string.IsNullOrWhiteSpace(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = rangeHeader[6..].Split('-');
            if (long.TryParse(parts[0], out long parsedStart) && parsedStart >= 0 && parsedStart < totalLen)
            {
                start = parsedStart;
                if (parts.Length > 1 && long.TryParse(parts[1], out long parsedEnd) && parsedEnd >= start && parsedEnd < totalLen)
                {
                    end = parsedEnd;
                }
                isPartial = true;
            }
        }

        long contentLen = Math.Max(0, end - start + 1);
        HttpListenerResponse resp = context.Response;
        resp.ContentType = mime;
        resp.AddHeader("Accept-Ranges", "bytes");
        resp.ContentLength64 = contentLen;
        if (isPartial)
        {
            resp.StatusCode = 206;
            resp.AddHeader("Content-Range", $"bytes {start}-{end}/{totalLen}");
        }
        else
        {
            resp.StatusCode = 200;
        }

        if (isHead || contentLen == 0)
        {
            resp.OutputStream.Close();
            return;
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
        fs.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[65536];
        long remaining = contentLen;
        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read <= 0) break;
            await resp.OutputStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }
        resp.OutputStream.Close();
    }

    private static void TryDeleteTempFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
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
        _visitorSemaphore.Dispose();
        _cts.Dispose();
    }
}
