using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
    public const string PrefetchPathPrefix = "/__kurfa_prefetch__/";
    public const string LocalFilePathPrefix = "/__kurfa_local_file__/";

    private readonly ConcurrentDictionary<string, string> _localFileRegistry = new(StringComparer.Ordinal);
    private readonly object _localAudioLock = new();
    private bool _localAudioLoadedFromDisk;

    private const long MaxAdaptiveUpstreamChunkBytes = 524_288; // 512 KB bounded chunk (<= 512 KiB - 1 MiB cap)
    private const long MaxProgressiveUpstreamChunkBytes = 2_097_152; // 2 MB bounded chunk for muxed progressive MP4 (itag 18/22)
    private const long HeadProbeEndByte = 1023;
    private const long TailProbeStartByte = 1_048_576; // 1 MB boundary where un-PoTokenized adaptive streams often 403
    private const long TailProbeEndByte = 1_310_719;
    private const string PlayerFieldMask = "playabilityStatus,streamingData,videoDetails";
    private const int MaxRecoveryAttempts = 5;
    private static readonly TimeSpan ClientCooldownDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ApiRequestTimeout = TimeSpan.FromSeconds(4.5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StreamHeaderTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StreamReadIdleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VisitorDataTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan VisitorDataTtl = TimeSpan.FromMinutes(30);

    private const string VisionOs01UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15";
    private const string AndroidUserAgent =
        "com.google.android.youtube/20.10.38 (Linux; U; Android 14; en_US; Pixel 8) gzip";
    private const string IosProducerUserAgent =
        "com.google.ios.youtubeproducer/0.10 (iPhone16,2; U; CPU iOS 18_6_2 like Mac OS X;)";
    private const string IosUserAgent =
        "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)";
    private const string TvHtml5SimplyUserAgent =
        "Mozilla/5.0 (ChromiumStylePlatform) Cobalt/25.lts.30.1034943-gold (unlike Gecko), Unknown_TV_Unknown_0/Unknown (Unknown, Unknown)";
    private const string AndroidVrUserAgent =
        "com.google.android.apps.youtube.vr.oculus/1.57.29 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip";
    private const string WebRemixClientVersion = "1.20260707.12.00";
    private const string WebClientVersion = "2.20250101.00.00";
    private const string WebUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36";

    private static readonly TimeSpan StreamCacheTtl = TimeSpan.FromMinutes(35);
    private const int MaxStreamCacheEntries = 100;
    private const int MaxSearchResults = 25;

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdRegex();

    public sealed record PlaybackClientProfile(
        string Id,
        string ClientName,
        string ClientVersion,
        string ClientId,
        string UserAgent,
        string? OsName,
        string? OsVersion,
        string? DeviceMake,
        string? DeviceModel,
        string? Platform,
        int? AndroidSdkVersion,
        bool UseMusicPlayerEndpoint,
        bool RequiresVisitorData,
        bool IncludeVisitorDataIfAvailable,
        bool IncludeContentFlags,
        bool IncludeUserAgentInContext,
        bool SupportsAdaptive,
        bool SupportsProgressive
    );

    public sealed record ClientHealthSnapshot(
        string ClientId,
        bool IsHealthy,
        DateTimeOffset CooldownUntilUtc,
        int ConsecutiveFailures,
        string? LastReason
    );

    /// <summary>
    /// Metrolist 2026 live-tested playback client catalog in priority order:
    /// 1) VISIONOS (0.1 + fresh visitorData)
    /// 2) ANDROID (adaptive 1MB+ verified + progressive itag=18/22)
    /// 3) IOS_PRODUCER / IOS + TVHTML5_SIMPLY + ANDROID_VR fallbacks.
    /// </summary>
    private static readonly IReadOnlyList<PlaybackClientProfile> AudioPlaybackClients =
    [
        new PlaybackClientProfile(
            Id: "VISIONOS_0_1",
            ClientName: "VISIONOS",
            ClientVersion: "0.1",
            ClientId: "101",
            UserAgent: VisionOs01UserAgent,
            OsName: "VISION_OS",
            OsVersion: "1.3",
            DeviceMake: "Apple",
            DeviceModel: "RealityDevice14,1",
            Platform: "MOBILE",
            AndroidSdkVersion: null,
            UseMusicPlayerEndpoint: true,
            RequiresVisitorData: true,
            IncludeVisitorDataIfAvailable: true,
            IncludeContentFlags: true,
            IncludeUserAgentInContext: false,
            SupportsAdaptive: true,
            SupportsProgressive: true
        ),
        new PlaybackClientProfile(
            Id: "ANDROID",
            ClientName: "ANDROID",
            ClientVersion: "20.10.38",
            ClientId: "3",
            UserAgent: AndroidUserAgent,
            OsName: "Android",
            OsVersion: "14",
            DeviceMake: null,
            DeviceModel: null,
            Platform: null,
            AndroidSdkVersion: 34,
            UseMusicPlayerEndpoint: false,
            RequiresVisitorData: false,
            IncludeVisitorDataIfAvailable: true,
            IncludeContentFlags: true,
            IncludeUserAgentInContext: false,
            SupportsAdaptive: true,
            SupportsProgressive: true
        ),
        new PlaybackClientProfile(
            Id: "IOS_PRODUCER",
            ClientName: "IOS_PRODUCER",
            ClientVersion: "0.10",
            ClientId: "92",
            UserAgent: IosProducerUserAgent,
            OsName: "iPhone",
            OsVersion: "18.6.2.22G100",
            DeviceMake: "Apple",
            DeviceModel: "iPhone16,2",
            Platform: null,
            AndroidSdkVersion: null,
            UseMusicPlayerEndpoint: false,
            RequiresVisitorData: false,
            IncludeVisitorDataIfAvailable: false,
            IncludeContentFlags: true,
            IncludeUserAgentInContext: false,
            SupportsAdaptive: false,
            SupportsProgressive: true
        ),
        new PlaybackClientProfile(
            Id: "IOS",
            ClientName: "IOS",
            ClientVersion: "20.10.4",
            ClientId: "5",
            UserAgent: IosUserAgent,
            OsName: "iPhone",
            OsVersion: "18.3.2.22D82",
            DeviceMake: "Apple",
            DeviceModel: "iPhone16,2",
            Platform: null,
            AndroidSdkVersion: null,
            UseMusicPlayerEndpoint: false,
            RequiresVisitorData: false,
            IncludeVisitorDataIfAvailable: false,
            IncludeContentFlags: false,
            IncludeUserAgentInContext: true,
            SupportsAdaptive: true,
            SupportsProgressive: true
        ),
        new PlaybackClientProfile(
            Id: "TVHTML5_SIMPLY",
            ClientName: "TVHTML5_SIMPLY",
            ClientVersion: "1.0",
            ClientId: "75",
            UserAgent: TvHtml5SimplyUserAgent,
            OsName: null,
            OsVersion: null,
            DeviceMake: null,
            DeviceModel: null,
            Platform: null,
            AndroidSdkVersion: null,
            UseMusicPlayerEndpoint: true,
            RequiresVisitorData: false,
            IncludeVisitorDataIfAvailable: true,
            IncludeContentFlags: true,
            IncludeUserAgentInContext: false,
            SupportsAdaptive: true,
            SupportsProgressive: true
        ),
        new PlaybackClientProfile(
            Id: "ANDROID_VR",
            ClientName: "ANDROID_VR",
            ClientVersion: "1.57.29",
            ClientId: "28",
            UserAgent: AndroidVrUserAgent,
            OsName: null,
            OsVersion: null,
            DeviceMake: null,
            DeviceModel: null,
            Platform: null,
            AndroidSdkVersion: 34,
            UseMusicPlayerEndpoint: true,
            RequiresVisitorData: false,
            IncludeVisitorDataIfAvailable: false,
            IncludeContentFlags: false,
            IncludeUserAgentInContext: true,
            SupportsAdaptive: true,
            SupportsProgressive: true
        )
    ];

    /// <summary>
    /// Tracks per-client 403 / LOGIN_REQUIRED / stream-drop health with a 5-minute temporary cooldown.
    /// </summary>
    private sealed class ClientHealthTracker
    {
        private sealed class Entry
        {
            public DateTimeOffset CooldownUntilUtc;
            public int ConsecutiveFailures;
            public string? LastReason;
        }

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly TimeSpan _cooldown;

        public ClientHealthTracker(TimeSpan cooldown)
        {
            _cooldown = cooldown;
        }

        public bool IsHealthy(string clientId, DateTimeOffset? nowUtc = null)
        {
            if (!_entries.TryGetValue(clientId, out Entry? entry))
            {
                return true;
            }
            DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
            return now >= entry.CooldownUntilUtc;
        }

        public void MarkFailure(string? clientId, string reason, TimeSpan? overrideCooldown = null)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset until = now + (overrideCooldown ?? _cooldown);
            _entries.AddOrUpdate(
                clientId,
                _ => new Entry
                {
                    CooldownUntilUtc = until,
                    ConsecutiveFailures = 1,
                    LastReason = reason
                },
                (_, existing) =>
                {
                    lock (existing)
                    {
                        existing.CooldownUntilUtc = until;
                        existing.ConsecutiveFailures++;
                        existing.LastReason = reason;
                    }
                    return existing;
                });
        }

        public void MarkSuccess(string? clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return;
            }

            if (_entries.TryGetValue(clientId, out Entry? existing))
            {
                lock (existing)
                {
                    existing.CooldownUntilUtc = DateTimeOffset.MinValue;
                    existing.ConsecutiveFailures = 0;
                    existing.LastReason = null;
                }
            }
        }

        public List<PlaybackClientProfile> GetOrderedCandidates(
            IReadOnlyList<PlaybackClientProfile> catalog,
            IReadOnlySet<string>? excludedClientIds)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var healthy = new List<PlaybackClientProfile>(catalog.Count);
            var cooledDown = new List<(PlaybackClientProfile Profile, DateTimeOffset Until)>(catalog.Count);

            foreach (PlaybackClientProfile profile in catalog)
            {
                if (excludedClientIds is not null && excludedClientIds.Contains(profile.Id))
                {
                    continue;
                }

                if (_entries.TryGetValue(profile.Id, out Entry? entry))
                {
                    DateTimeOffset until;
                    lock (entry)
                    {
                        until = entry.CooldownUntilUtc;
                    }
                    if (now < until)
                    {
                        cooledDown.Add((profile, until));
                        continue;
                    }
                }

                healthy.Add(profile);
            }

            if (cooledDown.Count > 0)
            {
                cooledDown.Sort((a, b) => a.Until.CompareTo(b.Until));
                foreach (var item in cooledDown)
                {
                    healthy.Add(item.Profile);
                }
            }

            return healthy;
        }

        public IReadOnlyList<ClientHealthSnapshot> Snapshot(IReadOnlyList<PlaybackClientProfile> catalog)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var list = new List<ClientHealthSnapshot>(catalog.Count);
            foreach (PlaybackClientProfile profile in catalog)
            {
                if (_entries.TryGetValue(profile.Id, out Entry? entry))
                {
                    lock (entry)
                    {
                        list.Add(new ClientHealthSnapshot(
                            profile.Id,
                            now >= entry.CooldownUntilUtc,
                            entry.CooldownUntilUtc,
                            entry.ConsecutiveFailures,
                            entry.LastReason));
                    }
                }
                else
                {
                    list.Add(new ClientHealthSnapshot(profile.Id, true, DateTimeOffset.MinValue, 0, null));
                }
            }
            return list;
        }
    }

    /// <summary>
    /// Lightweight in-memory LRU chunk and metadata cache for the last 2 active tracks (0ms seek/rewind).
    /// </summary>
    private sealed class ActiveTrackChunkCache
    {
        private const int MaxTracks = 2;
        private const long MaxBytesPerTrack = 16 * 1024 * 1024; // 16 MiB cap per track

        private sealed class Segment
        {
            public long Start;
            public byte[] Data = [];
            public long End => Start + Data.Length - 1;
        }

        private sealed class TrackEntry
        {
            public required string VideoId;
            public required string MimeType;
            public int Itag;
            public long TotalLength;
            public long CachedBytes;
            public readonly List<Segment> Segments = [];
            public LinkedListNode<string>? LruNode;
        }

        private readonly object _sync = new();
        private readonly Dictionary<string, TrackEntry> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _lru = new();

        public void SetMetadata(string videoId, string mimeType, int itag, long totalLength)
        {
            lock (_sync)
            {
                TrackEntry entry = GetOrCreateUnlocked(videoId, mimeType, itag, totalLength);
                if (totalLength > 0 && entry.TotalLength <= 0)
                {
                    entry.TotalLength = totalLength;
                }
            }
        }

        public bool TryGetMetadata(string videoId, out string mimeType, out int itag, out long totalLength)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(videoId, out TrackEntry? entry))
                {
                    TouchUnlocked(entry);
                    mimeType = entry.MimeType;
                    itag = entry.Itag;
                    totalLength = entry.TotalLength;
                    return true;
                }
            }

            mimeType = string.Empty;
            itag = 0;
            totalLength = 0;
            return false;
        }

        public void StoreChunk(
            string videoId,
            string mimeType,
            int itag,
            long totalLength,
            long startOffset,
            ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty || startOffset < 0)
            {
                return;
            }

            lock (_sync)
            {
                TrackEntry entry = GetOrCreateUnlocked(videoId, mimeType, itag, totalLength);
                if (totalLength > 0 && entry.TotalLength <= 0)
                {
                    entry.TotalLength = totalLength;
                }

                long newStart = startOffset;
                long newEnd = startOffset + data.Length - 1;

                // Fast check if completely covered already
                for (int i = 0; i < entry.Segments.Count; i++)
                {
                    Segment seg = entry.Segments[i];
                    if (seg.Start <= newStart && seg.End >= newEnd)
                    {
                        return;
                    }
                }

                if (entry.CachedBytes + data.Length > MaxBytesPerTrack)
                {
                    return;
                }

                int firstOverlap = -1;
                int lastOverlap = -1;
                long mergedStart = newStart;
                long mergedEnd = newEnd;

                for (int i = 0; i < entry.Segments.Count; i++)
                {
                    Segment seg = entry.Segments[i];
                    if (seg.End + 1 >= newStart && seg.Start <= newEnd + 1)
                    {
                        if (firstOverlap < 0) firstOverlap = i;
                        lastOverlap = i;
                        if (seg.Start < mergedStart) mergedStart = seg.Start;
                        if (seg.End > mergedEnd) mergedEnd = seg.End;
                    }
                }

                if (firstOverlap < 0)
                {
                    var seg = new Segment
                    {
                        Start = newStart,
                        Data = data.ToArray()
                    };
                    int insertIdx = 0;
                    while (insertIdx < entry.Segments.Count && entry.Segments[insertIdx].Start < newStart)
                    {
                        insertIdx++;
                    }
                    entry.Segments.Insert(insertIdx, seg);
                    entry.CachedBytes += seg.Data.Length;
                }
                else
                {
                    long mergedLenLong = mergedEnd - mergedStart + 1;
                    if (mergedLenLong <= 0 || mergedLenLong > MaxBytesPerTrack)
                    {
                        return;
                    }
                    int mergedLen = (int)mergedLenLong;
                    byte[] merged = new byte[mergedLen];
                    long removedBytes = 0;
                    for (int i = firstOverlap; i <= lastOverlap; i++)
                    {
                        Segment seg = entry.Segments[i];
                        Buffer.BlockCopy(seg.Data, 0, merged, (int)(seg.Start - mergedStart), seg.Data.Length);
                        removedBytes += seg.Data.Length;
                    }
                    data.CopyTo(merged.AsSpan((int)(newStart - mergedStart)));

                    int removeCount = lastOverlap - firstOverlap + 1;
                    entry.Segments.RemoveRange(firstOverlap, removeCount);
                    entry.Segments.Insert(firstOverlap, new Segment
                    {
                        Start = mergedStart,
                        Data = merged
                    });
                    entry.CachedBytes = entry.CachedBytes - removedBytes + merged.Length;
                }
            }
        }

        public bool TryReadRange(
            string videoId,
            long start,
            long maxEnd,
            out byte[] data,
            out string mimeType,
            out int itag,
            out long totalLength)
        {
            if (start >= 0 && maxEnd >= start)
            {
                lock (_sync)
                {
                    if (_entries.TryGetValue(videoId, out TrackEntry? entry))
                    {
                        TouchUnlocked(entry);
                        for (int i = 0; i < entry.Segments.Count; i++)
                        {
                            Segment seg = entry.Segments[i];
                            if (seg.Start <= start && seg.End >= start)
                            {
                                long availEnd = Math.Min(maxEnd, seg.End);
                                int count = (int)(availEnd - start + 1);
                                if (count > 0)
                                {
                                    data = new byte[count];
                                    Buffer.BlockCopy(seg.Data, (int)(start - seg.Start), data, 0, count);
                                    mimeType = entry.MimeType;
                                    itag = entry.Itag;
                                    totalLength = entry.TotalLength;
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            data = [];
            mimeType = string.Empty;
            itag = 0;
            totalLength = 0;
            return false;
        }

        public (int CachedTracks, long TotalCachedBytes) GetStats()
        {
            lock (_sync)
            {
                long total = 0;
                foreach (TrackEntry e in _entries.Values)
                {
                    total += e.CachedBytes;
                }
                return (_entries.Count, total);
            }
        }

        private TrackEntry GetOrCreateUnlocked(string videoId, string mimeType, int itag, long totalLength)
        {
            if (_entries.TryGetValue(videoId, out TrackEntry? existing))
            {
                bool formatChanged =
                    (!string.IsNullOrEmpty(mimeType) && !string.Equals(existing.MimeType, mimeType, StringComparison.OrdinalIgnoreCase)) ||
                    (itag > 0 && existing.Itag > 0 && existing.Itag != itag) ||
                    (totalLength > 0 && existing.TotalLength > 0 && existing.TotalLength != totalLength);
                if (formatChanged)
                {
                    existing.Segments.Clear();
                    existing.CachedBytes = 0;
                    existing.MimeType = mimeType;
                    existing.Itag = itag;
                    existing.TotalLength = totalLength;
                }
                TouchUnlocked(existing);
                return existing;
            }

            while (_entries.Count >= MaxTracks && _lru.Last is { } oldestNode)
            {
                _entries.Remove(oldestNode.Value);
                _lru.RemoveLast();
            }

            var created = new TrackEntry
            {
                VideoId = videoId,
                MimeType = string.IsNullOrWhiteSpace(mimeType) ? "audio/mp4" : mimeType,
                Itag = itag,
                TotalLength = totalLength
            };
            created.LruNode = _lru.AddFirst(videoId);
            _entries[videoId] = created;
            return created;
        }

        private void TouchUnlocked(TrackEntry entry)
        {
            if (entry.LruNode is not null && entry.LruNode != _lru.First)
            {
                _lru.Remove(entry.LruNode);
                _lru.AddFirst(entry.LruNode);
            }
        }
    }

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
    private readonly ConcurrentDictionary<string, byte> _inflightPrefetches = new(StringComparer.Ordinal);
    private readonly ClientHealthTracker _healthTracker = new(ClientCooldownDuration);
    private readonly ActiveTrackChunkCache _chunkCache = new();
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
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _apiHttpClient = new HttpClient(apiHandler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        var streamHandler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(4),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(45),
            MaxConnectionsPerServer = 16,
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

    public string GetLocalPrefetchUrl(string videoId)
    {
        if (!IsValidVideoId(videoId))
        {
            throw new ArgumentException("Invalid YouTube video ID.", nameof(videoId));
        }
        if (Port <= 0)
        {
            throw new InvalidOperationException("Local audio HTTP server has not been started.");
        }
        return $"http://127.0.0.1:{Port}{PrefetchPathPrefix}{videoId}";
    }

    public IReadOnlyList<ClientHealthSnapshot> GetClientHealthSnapshot() =>
        _healthTracker.Snapshot(AudioPlaybackClients);

    public (int CachedTracks, long TotalCachedBytes) GetChunkCacheStats() =>
        _chunkCache.GetStats();

    public void TriggerPrefetch(string? videoId)
    {
        if (_disposed != 0 || !IsValidVideoId(videoId))
        {
            return;
        }
        string cleanId = videoId!;
        if (_streamCache.TryGetValue(cleanId, out ResolvedAudio? cached) && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            if (!_chunkCache.TryGetMetadata(cleanId, out _, out _, out _))
            {
                _chunkCache.SetMetadata(cleanId, cached.MimeType, cached.Itag, cached.ContentLength);
            }
            return;
        }

        if (!_inflightPrefetches.TryAdd(cleanId, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await PrefetchTrackInternalAsync(cleanId, _cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Fire-and-forget prefetch never surfaces exceptions
            }
            finally
            {
                _inflightPrefetches.TryRemove(cleanId, out _);
            }
        }, CancellationToken.None);
    }

    private async Task PrefetchTrackInternalAsync(string videoId, CancellationToken ct)
    {
        ResolvedAudio? resolved = await ResolveAudioStreamAsync(videoId, ct).ConfigureAwait(false);
        if (resolved is null || string.IsNullOrEmpty(resolved.StreamUrl))
        {
            return;
        }

        _chunkCache.SetMetadata(videoId, resolved.MimeType, resolved.Itag, resolved.ContentLength);

        // Pre-warm initial chunk into LRU cache only if not already cached
        long warmEnd = Math.Min(GetUpstreamChunkCapBytes(resolved) - 1, resolved.ContentLength > 0 ? resolved.ContentLength - 1 : GetUpstreamChunkCapBytes(resolved) - 1);
        if (warmEnd < 0 || _chunkCache.TryReadRange(videoId, 0, Math.Min(warmEnd, 1023), out _, out _, out _, out _))
        {
            return;
        }

        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(StreamHeaderTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, resolved.StreamUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", resolved.RequestUserAgent);
            req.Headers.TryAddWithoutValidation("Range", $"bytes=0-{warmEnd}");

            using HttpResponseMessage resp = await _streamHttpClient
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token)
                .ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (code != 200 && code != 206)
            {
                return;
            }

            long totalLen = resp.Content.Headers.ContentRange?.Length ?? resolved.ContentLength;
            await using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream((int)Math.Min(warmEnd + 1, MaxProgressiveUpstreamChunkBytes));
            byte[] buf = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(StreamReadIdleTimeout);
                    int read = await s.ReadAsync(buf.AsMemory(0, buf.Length), readCts.Token).ConfigureAwait(false);
                    if (read <= 0) break;
                    ms.Write(buf, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            if (ms.Length > 0)
            {
                _chunkCache.StoreChunk(
                    videoId,
                    resolved.MimeType,
                    resolved.Itag,
                    totalLen,
                    0,
                    ms.GetBuffer().AsSpan(0, (int)ms.Length));
            }
        }
        catch
        {
            // Keep resolved URL in _streamCache even if warm-up fails
        }
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

            if (path.StartsWith(PrefetchPathPrefix, StringComparison.Ordinal))
            {
                string prefetchId = path[PrefetchPathPrefix.Length..].Trim();
                await HandlePrefetchRequestAsync(context, prefetchId, isHead: method == "HEAD", ct).ConfigureAwait(false);
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

    private static bool IsMatchingMidStream(ResolvedAudio candidate, string? expectedMime, long expectedLength)
    {
        if (!string.IsNullOrEmpty(expectedMime) &&
            !string.Equals(candidate.MimeType, expectedMime, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (expectedLength > 0 && candidate.ContentLength > 0 && candidate.ContentLength != expectedLength)
        {
            return false;
        }
        return true;
    }

    private async Task HandlePrefetchRequestAsync(
        HttpListenerContext context,
        string videoId,
        bool isHead,
        CancellationToken ct)
    {
        ApplyCorsHeaders(context.Response, context.Request);
        if (!IsValidVideoId(videoId))
        {
            await WritePlainErrorAsync(context.Response, 400, "Invalid video ID", ct).ConfigureAwait(false);
            return;
        }

        bool alreadyCached = _streamCache.TryGetValue(videoId, out ResolvedAudio? existing) &&
                             existing.ExpiresAtUtc > DateTimeOffset.UtcNow;

        if (!alreadyCached)
        {
            TriggerPrefetch(videoId);
        }
        else if (!_chunkCache.TryGetMetadata(videoId, out _, out _, out _))
        {
            _chunkCache.SetMetadata(videoId, existing!.MimeType, existing.Itag, existing.ContentLength);
        }

        HttpListenerResponse resp = context.Response;
        resp.StatusCode = alreadyCached ? 200 : 202;
        resp.StatusDescription = alreadyCached ? "OK" : "Accepted";
        resp.ContentType = "application/json; charset=utf-8";

        if (isHead)
        {
            resp.ContentLength64 = 0;
            return;
        }

        string json = JsonSerializer.Serialize(new
        {
            success = true,
            status = alreadyCached ? "cached" : "accepted",
            videoId,
            client = alreadyCached ? existing!.ClientKind : null
        });
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async Task ProxyAudioStreamAsync(
        HttpListenerContext context,
        string videoId,
        bool isHead,
        CancellationToken ct)
    {
        string? rangeHeader = context.Request.Headers["Range"];
        bool hasClientRange = TryParseByteRangeHeader(rangeHeader, out long clientStart, out long? clientRequestedEnd);
        string? expectedMime = null;
        long expectedLength = -1;

        // 0ms fast path from active 2-track LRU chunk/metadata cache
        if (isHead && !hasClientRange &&
            _streamCache.TryGetValue(videoId, out ResolvedAudio? headCached) &&
            headCached.ExpiresAtUtc > DateTimeOffset.UtcNow &&
            _chunkCache.TryGetMetadata(videoId, out string cachedHeadMime, out _, out long cachedHeadLen) &&
            cachedHeadLen > 0)
        {
            HttpListenerResponse fastHead = context.Response;
            ApplyCorsHeaders(fastHead, context.Request);
            fastHead.ContentType = string.IsNullOrWhiteSpace(cachedHeadMime) ? "audio/mp4" : cachedHeadMime;
            fastHead.Headers["Accept-Ranges"] = "bytes";
            fastHead.StatusCode = 200;
            fastHead.StatusDescription = "OK";
            fastHead.ContentLength64 = cachedHeadLen;
            return;
        }

        if (hasClientRange)
        {
            long maxCacheReadEnd = clientRequestedEnd.HasValue
                ? Math.Min(clientRequestedEnd.Value, clientStart + MaxProgressiveUpstreamChunkBytes - 1)
                : clientStart + MaxAdaptiveUpstreamChunkBytes - 1;

            if (_chunkCache.TryReadRange(
                    videoId,
                    clientStart,
                    maxCacheReadEnd,
                    out byte[] cachedBytes,
                    out string cachedMime,
                    out _,
                    out long cachedTotal))
            {
                if (cachedTotal > 0 && clientStart >= cachedTotal)
                {
                    HttpListenerResponse r416 = context.Response;
                    ApplyCorsHeaders(r416, context.Request);
                    r416.StatusCode = 416;
                    r416.StatusDescription = "Range Not Satisfiable";
                    r416.Headers["Content-Range"] = $"bytes */{cachedTotal}";
                    r416.ContentLength64 = 0;
                    return;
                }

                HttpListenerResponse cacheResp = context.Response;
                ApplyCorsHeaders(cacheResp, context.Request);
                cacheResp.ContentType = string.IsNullOrWhiteSpace(cachedMime) ? "audio/mp4" : cachedMime;
                cacheResp.Headers["Accept-Ranges"] = "bytes";
                cacheResp.Headers["X-Kurfa-Cache"] = "HIT";
                cacheResp.StatusCode = 206;
                cacheResp.StatusDescription = "Partial Content";
                long cacheRangeEnd = clientStart + cachedBytes.Length - 1;
                string totalStr = cachedTotal > 0
                    ? cachedTotal.ToString(CultureInfo.InvariantCulture)
                    : "*";
                cacheResp.Headers["Content-Range"] = $"bytes {clientStart}-{cacheRangeEnd}/{totalStr}";
                cacheResp.ContentLength64 = cachedBytes.Length;

                if (!isHead)
                {
                    await cacheResp.OutputStream.WriteAsync(cachedBytes, ct).ConfigureAwait(false);
                    await cacheResp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                }
                return;
            }
        }

        var excludedClients = new HashSet<string>(StringComparer.Ordinal);

        for (int attempt = 0; attempt < MaxRecoveryAttempts; attempt++)
        {
            bool forceRefresh = attempt > 0;

            ResolvedAudio? resolved = await ResolveAudioStreamAsync(
                videoId,
                ct,
                forceRefresh: forceRefresh,
                excludedClients: excludedClients.Count > 0 ? excludedClients : null).ConfigureAwait(false);
            if (resolved is null)
            {
                break;
            }

            if (expectedMime is null)
            {
                expectedMime = resolved.MimeType;
                expectedLength = resolved.ContentLength;
            }
            else if (hasClientRange && clientStart > 0 && !IsMatchingMidStream(resolved, expectedMime, expectedLength))
            {
                excludedClients.Add(resolved.ClientKind);
                continue;
            }

            if (isHead && !hasClientRange)
            {
                try
                {
                    using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    headCts.CancelAfter(StreamHeaderTimeout);

                    using var headProbeReq = new HttpRequestMessage(HttpMethod.Get, resolved.StreamUrl);
                    headProbeReq.Headers.TryAddWithoutValidation("User-Agent", resolved.RequestUserAgent);
                    long probeEnd = resolved.ContentLength > 0
                        ? Math.Min(HeadProbeEndByte, resolved.ContentLength - 1)
                        : HeadProbeEndByte;
                    headProbeReq.Headers.TryAddWithoutValidation("Range", $"bytes=0-{probeEnd}");

                    using HttpResponseMessage headUpstream = await _streamHttpClient
                        .SendAsync(headProbeReq, HttpCompletionOption.ResponseHeadersRead, headCts.Token)
                        .ConfigureAwait(false);

                    int headStatus = (int)headUpstream.StatusCode;
                    if (headStatus == 403 || headStatus == 410 || headStatus >= 500)
                    {
                        _healthTracker.MarkFailure(resolved.ClientKind, $"HEAD_HTTP_{headStatus}");
                        excludedClients.Add(resolved.ClientKind);
                        _streamCache.TryRemove(videoId, out _);
                        continue;
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

                    long resolvedHeadTotal = 0;
                    if (headUpstream.Content.Headers.ContentRange?.Length is long probedTotal && probedTotal > 0)
                    {
                        headResp.ContentLength64 = probedTotal;
                        resolvedHeadTotal = probedTotal;
                    }
                    else if (resolved.ContentLength > 0)
                    {
                        headResp.ContentLength64 = resolved.ContentLength;
                        resolvedHeadTotal = resolved.ContentLength;
                    }
                    else if (headUpstream.Content.Headers.ContentLength is long fallbackHeadLen && fallbackHeadLen >= 0)
                    {
                        headResp.ContentLength64 = fallbackHeadLen;
                        resolvedHeadTotal = fallbackHeadLen;
                    }
                    _chunkCache.SetMetadata(videoId, headResp.ContentType, resolved.Itag, resolvedHeadTotal);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    _healthTracker.MarkFailure(resolved.ClientKind, "HEAD_EXCEPTION");
                    excludedClients.Add(resolved.ClientKind);
                    _streamCache.TryRemove(videoId, out _);
                    continue;
                }
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

                long chunkCap = GetUpstreamChunkCapBytes(resolved);
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

                HttpResponseMessage? upstream = null;
                try
                {
                    using var hdrCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    hdrCts.CancelAfter(StreamHeaderTimeout);

                    using var upstreamReq = new HttpRequestMessage(HttpMethod.Get, resolved.StreamUrl);
                    upstreamReq.Headers.TryAddWithoutValidation("User-Agent", resolved.RequestUserAgent);
                    upstreamReq.Headers.TryAddWithoutValidation("Range", $"bytes={clientStart}-{fetchEnd}");

                    upstream = await _streamHttpClient
                        .SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, hdrCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    upstream?.Dispose();
                    throw;
                }
                catch
                {
                    upstream?.Dispose();
                    _healthTracker.MarkFailure(resolved.ClientKind, "RANGE_CONNECT_ERROR");
                    excludedClients.Add(resolved.ClientKind);
                    _streamCache.TryRemove(videoId, out _);
                    continue;
                }

                using (upstream)
                {
                    int statusCode = (int)upstream.StatusCode;
                    if (statusCode == 403 || statusCode == 410 || statusCode >= 500 || (clientStart > 0 && statusCode == 200))
                    {
                        _healthTracker.MarkFailure(resolved.ClientKind, $"RANGE_HTTP_{statusCode}");
                        excludedClients.Add(resolved.ClientKind);
                        _streamCache.TryRemove(videoId, out _);
                        continue;
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
                    resp.Headers["X-Kurfa-Cache"] = "MISS";
                    resp.Headers["X-Kurfa-Client"] = resolved.ClientKind;
                    resp.StatusCode = statusCode == 206 ? 206 : 200;
                    resp.StatusDescription = resp.StatusCode == 206 ? "Partial Content" : "OK";

                    long? totalKnownLength = upstream.Content.Headers.ContentRange?.Length;
                    if ((!totalKnownLength.HasValue || totalKnownLength.Value <= 0) && resolved.ContentLength > 0)
                    {
                        totalKnownLength = resolved.ContentLength;
                    }
                    if (totalKnownLength.HasValue && totalKnownLength.Value > 0 && expectedLength <= 0)
                    {
                        expectedLength = totalKnownLength.Value;
                    }

                    long declaredContentLength = upstream.Content.Headers.ContentLength ?? (fetchEnd - clientStart + 1);
                    if (resp.StatusCode == 206)
                    {
                        if (upstream.Content.Headers.TryGetValues("Content-Range", out var rangeValues) &&
                            rangeValues.FirstOrDefault() is { Length: > 0 } crHeader)
                        {
                            resp.Headers["Content-Range"] = crHeader;
                        }
                        else if (totalKnownLength.HasValue && totalKnownLength.Value > 0)
                        {
                            long rangeEnd = Math.Min(fetchEnd, totalKnownLength.Value - 1);
                            resp.Headers["Content-Range"] = $"bytes {clientStart}-{rangeEnd}/{totalKnownLength.Value}";
                        }
                    }
                    if (declaredContentLength >= 0)
                    {
                        resp.ContentLength64 = declaredContentLength;
                    }

                    long metadataTotal = totalKnownLength ?? resolved.ContentLength;
                    _chunkCache.SetMetadata(videoId, resp.ContentType, resolved.Itag, metadataTotal);

                    if (isHead)
                    {
                        return;
                    }

                    long bytesSentThisChunk = 0;
                    long remainingEnd = clientStart + Math.Max(0, declaredContentLength) - 1;
                    if (remainingEnd < clientStart) remainingEnd = fetchEnd;

                    ResolvedAudio activeChunkMedia = resolved;
                    HttpResponseMessage? activeResponse = upstream;
                    bool detachInitial = false;

                    for (int midRecovery = 0; midRecovery < MaxRecoveryAttempts; midRecovery++)
                    {
                        long segmentStartOffset = clientStart + bytesSentThisChunk;
                        using var teeBuffer = new MemoryStream((int)Math.Min(Math.Max(1, remainingEnd - segmentStartOffset + 1), MaxProgressiveUpstreamChunkBytes));
                        try
                        {
                            HttpResponseMessage currentResp;
                            if (midRecovery == 0)
                            {
                                currentResp = activeResponse!;
                            }
                            else
                            {
                                if (segmentStartOffset > remainingEnd) break;

                                _healthTracker.MarkFailure(activeChunkMedia.ClientKind, "MID_CHUNK_RECOVERY");
                                excludedClients.Add(activeChunkMedia.ClientKind);
                                _streamCache.TryRemove(videoId, out _);

                                ResolvedAudio? fresh = await ResolveAudioStreamAsync(
                                    videoId,
                                    ct,
                                    forceRefresh: true,
                                    excludedClients: excludedClients).ConfigureAwait(false);
                                if (fresh is null)
                                {
                                    break;
                                }
                                if (!IsMatchingMidStream(fresh, expectedMime, expectedLength))
                                {
                                    excludedClients.Add(fresh.ClientKind);
                                    continue;
                                }
                                activeChunkMedia = fresh;

                                using var recHdrCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                recHdrCts.CancelAfter(StreamHeaderTimeout);
                                using var recReq = new HttpRequestMessage(HttpMethod.Get, activeChunkMedia.StreamUrl);
                                recReq.Headers.TryAddWithoutValidation("User-Agent", activeChunkMedia.RequestUserAgent);
                                recReq.Headers.TryAddWithoutValidation("Range", $"bytes={segmentStartOffset}-{remainingEnd}");
                                currentResp = await _streamHttpClient
                                    .SendAsync(recReq, HttpCompletionOption.ResponseHeadersRead, recHdrCts.Token)
                                    .ConfigureAwait(false);
                                int recStatus = (int)currentResp.StatusCode;
                                if (recStatus != 206 && !(segmentStartOffset == 0 && recStatus == 200))
                                {
                                    currentResp.Dispose();
                                    _healthTracker.MarkFailure(activeChunkMedia.ClientKind, $"MID_CHUNK_HTTP_{recStatus}");
                                    continue;
                                }
                            }

                            try
                            {
                                await using Stream upstreamStream = await currentResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                                byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                                try
                                {
                                    while (true)
                                    {
                                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                        readCts.CancelAfter(StreamReadIdleTimeout);
                                        int read = await upstreamStream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token).ConfigureAwait(false);
                                        if (read <= 0) break;
                                        await resp.OutputStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                                        if (teeBuffer.Length + read <= MaxProgressiveUpstreamChunkBytes)
                                        {
                                            teeBuffer.Write(buffer, 0, read);
                                        }
                                        bytesSentThisChunk += read;
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(buffer);
                                }
                            }
                            finally
                            {
                                if (teeBuffer.Length > 0)
                                {
                                    _chunkCache.StoreChunk(
                                        videoId,
                                        resp.ContentType ?? activeChunkMedia.MimeType,
                                        activeChunkMedia.Itag,
                                        metadataTotal,
                                        segmentStartOffset,
                                        teeBuffer.GetBuffer().AsSpan(0, (int)teeBuffer.Length));
                                }
                                if (midRecovery > 0)
                                {
                                    currentResp.Dispose();
                                }
                            }

                            detachInitial = true;
                            break;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (HttpListenerException)
                        {
                            throw;
                        }
                        catch
                        {
                            if (midRecovery == MaxRecoveryAttempts - 1)
                            {
                                throw;
                            }
                        }
                    }

                    if (detachInitial)
                    {
                        await resp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                        return;
                    }
                    return;
                }
            }

            // Full GET without Range header: stream in sequential bounded chunks so CDN never returns 403
            long fullChunkCap = GetUpstreamChunkCapBytes(resolved);
            long offset = 0;
            long totalLength = resolved.ContentLength;
            bool headersSent = false;
            bool retryFromScratch = false;
            ResolvedAudio activeStreamMedia = resolved;

            while (true)
            {
                long chunkEnd = offset + fullChunkCap - 1;
                if (totalLength > 0)
                {
                    chunkEnd = Math.Min(chunkEnd, totalLength - 1);
                }

                // Serve full-GET chunk directly from LRU cache when already buffered
                if (_chunkCache.TryReadRange(
                        videoId,
                        offset,
                        chunkEnd,
                        out byte[] cachedChunk,
                        out string cachedChunkMime,
                        out _,
                        out long cachedChunkTotal))
                {
                    if (cachedChunkTotal > 0 && totalLength <= 0)
                    {
                        totalLength = cachedChunkTotal;
                    }
                    if (!headersSent)
                    {
                        HttpListenerResponse resp = context.Response;
                        ApplyCorsHeaders(resp, context.Request);
                        resp.ContentType = string.IsNullOrWhiteSpace(cachedChunkMime) ? "audio/mp4" : cachedChunkMime;
                        resp.Headers["Accept-Ranges"] = "bytes";
                        resp.Headers["X-Kurfa-Cache"] = "HIT";
                        resp.StatusCode = 200;
                        resp.StatusDescription = "OK";
                        if (totalLength > 0)
                        {
                            resp.ContentLength64 = totalLength;
                        }
                        expectedMime = resp.ContentType;
                        headersSent = true;
                    }

                    await context.Response.OutputStream.WriteAsync(cachedChunk, ct).ConfigureAwait(false);
                    offset += cachedChunk.Length;
                    if (totalLength > 0 && offset >= totalLength)
                    {
                        await context.Response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                        return;
                    }
                    continue;
                }

                HttpResponseMessage? chunkUpstream = null;
                bool chunkSuccess = false;
                for (int chunkAttempt = 0; chunkAttempt < MaxRecoveryAttempts; chunkAttempt++)
                {
                    if (chunkAttempt > 0)
                    {
                        _streamCache.TryRemove(videoId, out _);
                        ResolvedAudio? refreshed = await ResolveAudioStreamAsync(
                            videoId,
                            ct,
                            forceRefresh: true,
                            excludedClients: excludedClients).ConfigureAwait(false);
                        if (refreshed is null)
                        {
                            break;
                        }
                        if (headersSent && !IsMatchingMidStream(refreshed, expectedMime, totalLength))
                        {
                            excludedClients.Add(refreshed.ClientKind);
                            continue;
                        }
                        activeStreamMedia = refreshed;
                    }

                    try
                    {
                        using var chunkHdrCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        chunkHdrCts.CancelAfter(StreamHeaderTimeout);
                        using var chunkReq = new HttpRequestMessage(HttpMethod.Get, activeStreamMedia.StreamUrl);
                        chunkReq.Headers.TryAddWithoutValidation("User-Agent", activeStreamMedia.RequestUserAgent);
                        chunkReq.Headers.TryAddWithoutValidation("Range", $"bytes={offset}-{chunkEnd}");

                        chunkUpstream = await _streamHttpClient
                            .SendAsync(chunkReq, HttpCompletionOption.ResponseHeadersRead, chunkHdrCts.Token)
                            .ConfigureAwait(false);

                        int statusCode = (int)chunkUpstream.StatusCode;
                        if (statusCode == 403 || statusCode == 410 || statusCode >= 500 || (offset > 0 && statusCode == 200))
                        {
                            chunkUpstream.Dispose();
                            chunkUpstream = null;
                            _healthTracker.MarkFailure(activeStreamMedia.ClientKind, $"FULL_CHUNK_HTTP_{statusCode}");
                            excludedClients.Add(activeStreamMedia.ClientKind);
                            _streamCache.TryRemove(videoId, out _);
                            continue;
                        }
                        if (statusCode < 200 || statusCode >= 300)
                        {
                            if (!headersSent)
                            {
                                chunkUpstream.Dispose();
                                await WritePlainErrorAsync(context.Response, statusCode, "Upstream audio error", ct).ConfigureAwait(false);
                                return;
                            }
                            chunkUpstream.Dispose();
                            chunkUpstream = null;
                            break;
                        }

                        chunkSuccess = true;
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        chunkUpstream?.Dispose();
                        throw;
                    }
                    catch
                    {
                        chunkUpstream?.Dispose();
                        chunkUpstream = null;
                        _healthTracker.MarkFailure(activeStreamMedia.ClientKind, "FULL_CHUNK_EXCEPTION");
                        excludedClients.Add(activeStreamMedia.ClientKind);
                        _streamCache.TryRemove(videoId, out _);
                    }
                }

                if (!chunkSuccess || chunkUpstream is null)
                {
                    if (!headersSent)
                    {
                        retryFromScratch = attempt == 0;
                    }
                    break;
                }

                using (chunkUpstream)
                {
                    int statusCode = (int)chunkUpstream.StatusCode;
                    if (chunkUpstream.Content.Headers.ContentRange?.Length is long crTotal && crTotal > 0)
                    {
                        totalLength = crTotal;
                    }

                    if (!headersSent)
                    {
                        HttpListenerResponse resp = context.Response;
                        ApplyCorsHeaders(resp, context.Request);
                        string contentType = chunkUpstream.Content.Headers.ContentType?.MediaType ?? activeStreamMedia.MimeType;
                        resp.ContentType = string.IsNullOrWhiteSpace(contentType) ? "audio/mp4" : contentType;
                        resp.Headers["Accept-Ranges"] = "bytes";
                        resp.StatusCode = 200;
                        resp.StatusDescription = "OK";
                        if (totalLength > 0)
                        {
                            resp.ContentLength64 = totalLength;
                        }
                        expectedMime = resp.ContentType;
                        _chunkCache.SetMetadata(videoId, resp.ContentType, activeStreamMedia.Itag, totalLength);
                        headersSent = true;
                    }

                    long writtenInChunk = 0;
                    long chunkStartOffset = offset;
                    using var chunkTee = new MemoryStream((int)Math.Min(fullChunkCap, MaxProgressiveUpstreamChunkBytes));
                    try
                    {
                        await using Stream upstreamStream = await chunkUpstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                        try
                        {
                            while (true)
                            {
                                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                readCts.CancelAfter(StreamReadIdleTimeout);
                                int read = await upstreamStream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token).ConfigureAwait(false);
                                if (read <= 0) break;
                                await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                                if (chunkTee.Length + read <= MaxProgressiveUpstreamChunkBytes)
                                {
                                    chunkTee.Write(buffer, 0, read);
                                }
                                writtenInChunk += read;
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            if (chunkTee.Length > 0)
                            {
                                _chunkCache.StoreChunk(
                                    videoId,
                                    expectedMime ?? activeStreamMedia.MimeType,
                                    activeStreamMedia.Itag,
                                    totalLength,
                                    chunkStartOffset,
                                    chunkTee.GetBuffer().AsSpan(0, (int)chunkTee.Length));
                            }
                        }
                        await context.Response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (HttpListenerException)
                    {
                        throw;
                    }
                    catch
                    {
                        _healthTracker.MarkFailure(activeStreamMedia.ClientKind, "FULL_READ_DROP");
                        excludedClients.Add(activeStreamMedia.ClientKind);
                        _streamCache.TryRemove(videoId, out _);

                        if (writtenInChunk <= 0)
                        {
                            break;
                        }
                        offset += writtenInChunk;
                        if (totalLength > 0 && offset >= totalLength)
                        {
                            break;
                        }
                        ResolvedAudio? recovered = await ResolveAudioStreamAsync(
                            videoId,
                            ct,
                            forceRefresh: true,
                            excludedClients: excludedClients).ConfigureAwait(false);
                        if (recovered is null || !IsMatchingMidStream(recovered, expectedMime, totalLength))
                        {
                            break;
                        }
                        activeStreamMedia = recovered;
                        continue;
                    }

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

    private static readonly JsonSerializerOptions PlayerPayloadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ResolvedAudio?> ResolveAudioStreamAsync(
        string? videoId,
        CancellationToken ct = default,
        bool forceRefresh = false,
        bool preferAlternateClient = false,
        IReadOnlySet<string>? excludedClients = null)
    {
        if (!IsValidVideoId(videoId))
        {
            return null;
        }
        string cleanId = videoId!;

        if (!forceRefresh &&
            (excludedClients is null || excludedClients.Count == 0) &&
            _streamCache.TryGetValue(cleanId, out ResolvedAudio? cached))
        {
            if (cached.ExpiresAtUtc > DateTimeOffset.UtcNow && _healthTracker.IsHealthy(cached.ClientKind))
            {
                return cached;
            }
            _streamCache.TryRemove(cleanId, out _);
        }

        string excludedKey = excludedClients is { Count: > 0 }
            ? string.Join(',', excludedClients.OrderBy(static x => x, StringComparer.Ordinal))
            : string.Empty;
        // Coalesce concurrent resolutions for the same videoId so parallel range probes don't hit YouTube multiple times
        string inflightKey = $"{cleanId}:{(forceRefresh ? '1' : '0')}:{(preferAlternateClient ? '1' : '0')}:{excludedKey}";
        Task<ResolvedAudio?> task = _inflightResolutions.GetOrAdd(
            inflightKey,
            _ => ResolveAudioStreamCoreAsync(cleanId, forceRefresh, preferAlternateClient, excludedClients, _cts.Token)
        );
        try
        {
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _inflightResolutions.TryRemove(new KeyValuePair<string, Task<ResolvedAudio?>>(inflightKey, task));
        }
    }

    private static long GetUpstreamChunkCapBytes(ResolvedAudio media)
    {
        if (media.Itag is 18 or 22 ||
            media.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return MaxProgressiveUpstreamChunkBytes;
        }
        return MaxAdaptiveUpstreamChunkBytes;
    }

    private async Task<ResolvedAudio?> ResolveAudioStreamCoreAsync(
        string cleanId,
        bool forceRefresh,
        bool preferAlternateClient,
        IReadOnlySet<string>? excludedClients,
        CancellationToken ct)
    {
        List<PlaybackClientProfile> candidates = _healthTracker.GetOrderedCandidates(AudioPlaybackClients, excludedClients);
        if (preferAlternateClient && candidates.Count > 1)
        {
            // Rotate first candidate to the back when caller explicitly requests an alternate client
            PlaybackClientProfile first = candidates[0];
            candidates.RemoveAt(0);
            candidates.Add(first);
        }

        ResolvedAudio? resolved = null;
        foreach (PlaybackClientProfile profile in candidates)
        {
            resolved = await ResolveViaClientProfileAsync(cleanId, profile, forceRefreshVisitor: forceRefresh, ct).ConfigureAwait(false);
            if (resolved is not null)
            {
                break;
            }
        }

        if (resolved is not null)
        {
            PruneStreamCacheIfNeeded();
            _streamCache[cleanId] = resolved;
            _chunkCache.SetMetadata(cleanId, resolved.MimeType, resolved.Itag, resolved.ContentLength);
        }
        return resolved;
    }

    private async Task<ResolvedAudio?> ResolveViaClientProfileAsync(
        string videoId,
        PlaybackClientProfile profile,
        bool forceRefreshVisitor,
        CancellationToken ct)
    {
        string? visitorData = null;
        if (profile.RequiresVisitorData)
        {
            visitorData = await ResolveVisitorDataAsync(forceRefreshVisitor, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(visitorData))
            {
                visitorData = await ResolveVisitorDataAsync(forceRefresh: true, ct).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(visitorData))
            {
                _healthTracker.MarkFailure(profile.Id, "NO_VISITOR_DATA");
                return null;
            }
        }
        else if (profile.IncludeVisitorDataIfAvailable)
        {
            visitorData = forceRefreshVisitor
                ? await ResolveVisitorDataAsync(forceRefresh: true, ct).ConfigureAwait(false)
                : (_visitorDataExpiresAtUtc > DateTimeOffset.UtcNow ? _visitorData : null);
        }

        var clientObj = new Dictionary<string, object?>(12)
        {
            ["clientName"] = profile.ClientName,
            ["clientVersion"] = profile.ClientVersion,
            ["hl"] = "tr",
            ["gl"] = "TR"
        };
        if (profile.IncludeUserAgentInContext) clientObj["userAgent"] = profile.UserAgent;
        if (!string.IsNullOrEmpty(profile.OsName)) clientObj["osName"] = profile.OsName;
        if (!string.IsNullOrEmpty(profile.OsVersion)) clientObj["osVersion"] = profile.OsVersion;
        if (!string.IsNullOrEmpty(profile.DeviceMake)) clientObj["deviceMake"] = profile.DeviceMake;
        if (!string.IsNullOrEmpty(profile.DeviceModel)) clientObj["deviceModel"] = profile.DeviceModel;
        if (!string.IsNullOrEmpty(profile.Platform)) clientObj["platform"] = profile.Platform;
        if (profile.AndroidSdkVersion.HasValue) clientObj["androidSdkVersion"] = profile.AndroidSdkVersion.Value;
        if (!string.IsNullOrEmpty(visitorData)) clientObj["visitorData"] = visitorData;

        var payloadObj = new Dictionary<string, object?>(4)
        {
            ["context"] = new Dictionary<string, object?>(1) { ["client"] = clientObj },
            ["videoId"] = videoId
        };
        if (profile.IncludeContentFlags)
        {
            payloadObj["contentCheckOk"] = true;
            payloadObj["racyCheckOk"] = true;
        }

        return await ResolveViaPlayerClientAsync(videoId, profile, payloadObj, visitorData, ct).ConfigureAwait(false);
    }

    private async Task<ResolvedAudio?> TryVerifyStreamRangeAsync(ResolvedAudio? candidate, CancellationToken ct)
    {
        if (candidate is null || string.IsNullOrEmpty(candidate.StreamUrl))
        {
            return null;
        }
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(ProbeTimeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, candidate.StreamUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", candidate.RequestUserAgent);
            req.Headers.TryAddWithoutValidation("Range", $"bytes=0-{HeadProbeEndByte}");

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

            // Un-PoTokenized adaptive audio streams often succeed at bytes=0-1023 but 403 at >= 1 MB.
            // Verify a range past the 1 MB boundary before accepting so playback never fails mid-track or on Range: bytes=0-.
            if (resolvedLength > TailProbeStartByte)
            {
                long tailEnd = Math.Min(TailProbeEndByte, resolvedLength - 1);
                using var tailReq = new HttpRequestMessage(HttpMethod.Get, candidate.StreamUrl);
                tailReq.Headers.TryAddWithoutValidation("User-Agent", candidate.RequestUserAgent);
                tailReq.Headers.TryAddWithoutValidation("Range", $"bytes={TailProbeStartByte}-{tailEnd}");

                using HttpResponseMessage tailResp = await _streamHttpClient
                    .SendAsync(tailReq, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                    .ConfigureAwait(false);

                int tailCode = (int)tailResp.StatusCode;
                if (tailCode != 200 && tailCode != 206)
                {
                    return null;
                }
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

            // 1) Fast path: /youtubei/v1/visitor_id
            string? vd = await FetchVisitorIdEndpointAsync(ct).ConfigureAwait(false);

            // 2) Fallback: www.youtube.com/sw.js_data
            if (string.IsNullOrWhiteSpace(vd))
            {
                vd = await FetchServiceWorkerVisitorDataAsync(ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(vd))
            {
                _visitorData = vd;
                _visitorDataExpiresAtUtc = DateTimeOffset.UtcNow + VisitorDataTtl;
                return _visitorData;
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

    private async Task<string?> FetchVisitorIdEndpointAsync(CancellationToken ct)
    {
        try
        {
            var body = new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "WEB_REMIX",
                        clientVersion = WebRemixClientVersion,
                        hl = "tr",
                        gl = "TR"
                    }
                }
            };
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                "https://music.youtube.com/youtubei/v1/visitor_id?prettyPrint=false")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
            req.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://music.youtube.com/");

            using var visitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            visitorCts.CancelAfter(VisitorDataTimeout);
            using HttpResponseMessage res = await _apiHttpClient.SendAsync(req, visitorCts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream bodyStream = await res.Content.ReadAsStreamAsync(visitorCts.Token).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(bodyStream, cancellationToken: visitorCts.Token).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("responseContext", out JsonElement rc) &&
                rc.ValueKind == JsonValueKind.Object)
            {
                string vd = GetString(rc, "visitorData");
                if (!string.IsNullOrWhiteSpace(vd))
                {
                    return vd;
                }
            }
        }
        catch
        {
            // Fall back to sw.js_data
        }
        return null;
    }

    private async Task<string?> FetchServiceWorkerVisitorDataAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data");
            req.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);

            using var visitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            visitorCts.CancelAfter(VisitorDataTimeout);
            using HttpResponseMessage res = await _apiHttpClient.SendAsync(req, visitorCts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }

            string raw = await res.Content.ReadAsStringAsync(visitorCts.Token).ConfigureAwait(false);
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
                                    return vd;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore sw.js_data error
        }
        return null;
    }

    private async Task<ResolvedAudio?> ResolveViaPlayerClientAsync(
        string videoId,
        PlaybackClientProfile profile,
        object payloadObj,
        string? visitorData,
        CancellationToken ct)
    {
        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(ApiRequestTimeout);

            string endpointUrl = profile.UseMusicPlayerEndpoint
                ? "https://music.youtube.com/youtubei/v1/player?prettyPrint=false"
                : "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
            string jsonBody = JsonSerializer.Serialize(payloadObj, PlayerPayloadJsonOptions);

            using var req = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", profile.UserAgent);
            req.Headers.TryAddWithoutValidation("X-Goog-FieldMask", PlayerFieldMask);
            if (!string.IsNullOrEmpty(profile.ClientId))
            {
                req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", profile.ClientId);
            }
            if (!string.IsNullOrEmpty(profile.ClientVersion))
            {
                req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", profile.ClientVersion);
            }
            if (!string.IsNullOrEmpty(visitorData))
            {
                req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);
            }
            if (profile.UseMusicPlayerEndpoint)
            {
                req.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
                req.Headers.TryAddWithoutValidation("Referer", "https://music.youtube.com/");
            }

            using HttpResponseMessage res = await _apiHttpClient.SendAsync(req, reqCts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _healthTracker.MarkFailure(profile.Id, $"PLAYER_HTTP_{(int)res.StatusCode}");
                return null;
            }

            await using Stream bodyStream = await res.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(bodyStream, cancellationToken: reqCts.Token).ConfigureAwait(false);

            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("playabilityStatus", out JsonElement playability) &&
                playability.ValueKind == JsonValueKind.Object)
            {
                string status = GetString(playability, "status");
                if (!string.IsNullOrEmpty(status) &&
                    !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    _healthTracker.MarkFailure(profile.Id, $"PLAYABILITY_{status}");
                    return null;
                }
            }

            if (!root.TryGetProperty("streamingData", out JsonElement streamingData) ||
                streamingData.ValueKind != JsonValueKind.Object)
            {
                _healthTracker.MarkFailure(profile.Id, "NO_STREAMING_DATA");
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

            ResolvedAudio? adaptiveCandidate = null;
            if (profile.SupportsAdaptive &&
                streamingData.TryGetProperty("adaptiveFormats", out JsonElement adaptiveFormats) &&
                adaptiveFormats.ValueKind == JsonValueKind.Array)
            {
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

                if (!string.IsNullOrEmpty(bestUrl))
                {
                    adaptiveCandidate = new ResolvedAudio(
                        videoId, bestUrl, bestMime, bestItag, bestContentLength,
                        title, artist, durationSec, profile.UserAgent, profile.Id, DateTimeOffset.UtcNow.Add(StreamCacheTtl));
                }
            }

            if (adaptiveCandidate is not null)
            {
                ResolvedAudio? verifiedAdaptive = await TryVerifyStreamRangeAsync(adaptiveCandidate, ct).ConfigureAwait(false);
                if (verifiedAdaptive is not null)
                {
                    _healthTracker.MarkSuccess(profile.Id);
                    return verifiedAdaptive;
                }
            }

            // Fallback to muxed progressive MP4 (itag=18/22) from the same player response when adaptive audio is 1MB/PoToken-locked.
            if (profile.SupportsProgressive &&
                streamingData.TryGetProperty("formats", out JsonElement progressiveFormats) &&
                progressiveFormats.ValueKind == JsonValueKind.Array)
            {
                string? progUrl = null;
                string progMime = "video/mp4";
                int progItag = 0;
                long progContentLength = 0;
                int progScore = int.MinValue;

                foreach (JsonElement format in progressiveFormats.EnumerateArray())
                {
                    if (format.ValueKind != JsonValueKind.Object) continue;
                    string mime = GetString(format, "mimeType");
                    string url = GetString(format, "url");
                    if ((!mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase) &&
                         !mime.StartsWith("video/webm", StringComparison.OrdinalIgnoreCase)) ||
                        string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    int itag = GetInt32(format, "itag");
                    int height = GetInt32(format, "height");
                    long clen = 0;
                    string clenStr = GetString(format, "contentLength");
                    if (!string.IsNullOrEmpty(clenStr))
                    {
                        _ = long.TryParse(clenStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out clen);
                    }

                    // Prefer 360p itag=18 for fastest audio-only playback bandwidth, fall back to 720p itag=22
                    int score = mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase) ? 500 : 0;
                    if (itag == 18) score += 2000;
                    else if (itag == 22) score += 1000;
                    else score += Math.Max(0, 800 - height);

                    if (score > progScore)
                    {
                        progScore = score;
                        progUrl = url;
                        progItag = itag;
                        progContentLength = clen;
                        int semiIdx = mime.IndexOf(';');
                        progMime = semiIdx > 0 ? mime[..semiIdx].Trim() : "video/mp4";
                    }
                }

                if (!string.IsNullOrEmpty(progUrl))
                {
                    var progCandidate = new ResolvedAudio(
                        videoId, progUrl, progMime, progItag, progContentLength,
                        title, artist, durationSec, profile.UserAgent, profile.Id, DateTimeOffset.UtcNow.Add(StreamCacheTtl));
                    ResolvedAudio? verifiedProg = await TryVerifyStreamRangeAsync(progCandidate, ct).ConfigureAwait(false);
                    if (verifiedProg is not null)
                    {
                        _healthTracker.MarkSuccess(profile.Id);
                        return verifiedProg;
                    }
                }
            }

            _healthTracker.MarkFailure(profile.Id, "STREAM_PROBE_403_OR_EMPTY");
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _healthTracker.MarkFailure(profile.Id, "PLAYER_EXCEPTION");
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

            using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            searchCts.CancelAfter(ApiRequestTimeout);
            using HttpResponseMessage res = await _apiHttpClient.SendAsync(req, searchCts.Token).ConfigureAwait(false);
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
            : (media.MimeType.Contains("webm", StringComparison.OrdinalIgnoreCase)
                ? ".webm"
                : (media.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? ".mp4" : ".m4a"));
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
                long chunkCap = GetUpstreamChunkCapBytes(activeMedia);

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
                    _healthTracker.MarkFailure(activeMedia.ClientKind, "DOWNLOAD_403");
                    _streamCache.TryRemove(safeId, out _);
                    TryDeleteTempFile(tempPath);
                    var excluded = new HashSet<string>(StringComparer.Ordinal) { activeMedia.ClientKind };
                    ResolvedAudio? fallback = wantVideo
                        ? await ResolveMuxedVideoAsync(safeId, skipClient: activeMedia.ClientKind, ct).ConfigureAwait(false)
                        : await ResolveAudioStreamCoreAsync(safeId, forceRefresh: true, preferAlternateClient: true, excludedClients: excluded, ct).ConfigureAwait(false);
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

        if (!string.Equals(skipClient, "VISIONOS_0_1", StringComparison.Ordinal) &&
            !string.Equals(skipClient, "VISIONOS", StringComparison.Ordinal))
        {
            var visionPayload = new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "VISIONOS",
                        clientVersion = "0.1",
                        deviceMake = "Apple",
                        deviceModel = "RealityDevice14,1",
                        osName = "VISION_OS",
                        osVersion = "1.3",
                        platform = "MOBILE",
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
                    videoId, visionPayload, VisionOs01UserAgent, "101", "0.1", visitorData, "VISIONOS_0_1", ct).ConfigureAwait(false),
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
            req.Headers.TryAddWithoutValidation("X-Goog-FieldMask", PlayerFieldMask);
            if (!string.IsNullOrEmpty(clientNameId)) req.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientNameId);
            if (!string.IsNullOrEmpty(clientVersion)) req.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            if (!string.IsNullOrEmpty(visitorData)) req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);

            using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            searchCts.CancelAfter(ApiRequestTimeout);
            using HttpResponseMessage res = await _apiHttpClient.SendAsync(req, searchCts.Token).ConfigureAwait(false);
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
        public string duration { get; set; } = "0:00";
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

    public static (string Title, string Artist) CleanLocalAudioMetadata(string fileNameWithoutExt)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithoutExt))
        {
            return ("Bilinmeyen Parça", "Yerel Dosya");
        }

        string s = fileNameWithoutExt.Trim();

        // Strip trailing yt-dlp YouTube IDs such as (_-_9gKhZ5XTmA_), (_JsHbUjOsrCE_), (_62VJm0UGfv8_), ([A-Za-z0-9_-]{11})
        for (int i = 0; i < 3; i++)
        {
            string next = Regex.Replace(
                s,
                @"\s*[\(\[]\s*[_-]{0,4}[A-Za-z0-9_-]{10,14}[_-]{0,4}\s*[\)\]]\s*$",
                string.Empty,
                RegexOptions.CultureInvariant);
            if (next.Length == s.Length) break;
            s = next.Trim();
        }

        // Replace underscores with spaces and clean common yt-dlp quality/video suffixes
        s = s.Replace('_', ' ');
        s = Regex.Replace(
            s,
            @"\s*[\(\[]\s*(?:HIGH QUALITY AUDIO|Official\s+Music\s+Video|Official\s+Video|Official\s+Audio|Video\s+Klip|Lyric\s+Video|Lyrics|Visualizer|Audio|4K|HD)\s*[\)\]]",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        s = Regex.Replace(
            s,
            @"(?:\s*[\|\-–—]\s*|\s+)(?:HIGH QUALITY AUDIO|Official\s+Music\s+Video|Official\s+Video|Official\s+Audio|Video\s+Klip|Lyric\s+Video|Visualizer)\b.*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        s = Regex.Replace(s, @"\s{2,}", " ", RegexOptions.CultureInvariant).Trim(' ', '-', '_', '–', '—', '|');

        string[] parts = s.Split([" - ", " – ", " — "], 2, StringSplitOptions.None);
        static string CleanPart(string p) =>
            Regex.Replace(p, @"\s{2,}", " ", RegexOptions.CultureInvariant)
                 .Trim(' ', '-', '_', '–', '—', '|');

        string artist;
        string title;
        if (parts.Length == 2)
        {
            artist = CleanPart(parts[0]);
            title = CleanPart(parts[1]);
        }
        else
        {
            title = CleanPart(s);
            artist = "Yerel Dosya";
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = CleanPart(fileNameWithoutExt.Replace('_', ' '));
            if (string.IsNullOrWhiteSpace(title)) title = "Bilinmeyen Parça";
        }
        if (string.IsNullOrWhiteSpace(artist))
        {
            artist = "Yerel Dosya";
        }

        return (title, artist);
    }

    public static string ReadLocalAudioDurationFormatted(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return "0:00";
        }

        double seconds = 0;
        try
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            seconds = ext switch
            {
                ".wav" => TryReadWavDurationSeconds(fs),
                ".flac" => TryReadFlacDurationSeconds(fs),
                ".mp3" => TryReadMp3DurationSeconds(fs),
                ".m4a" or ".mp4" or ".aac" => TryReadMp4DurationSeconds(fs),
                _ => 0
            };
        }
        catch
        {
            seconds = 0;
        }

        if (seconds <= 0.1 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            seconds = TryReadShellComDurationSeconds(filePath);
        }

        if (seconds <= 0.1 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return "0:00";
        }

        long totalSec = (long)Math.Round(seconds);
        return $"{totalSec / 60}:{totalSec % 60:D2}";
    }

    private static double TryReadWavDurationSeconds(FileStream fs)
    {
        if (fs.Length < 44) return 0;
        Span<byte> header = stackalloc byte[12];
        if (fs.Read(header) != 12) return 0;
        if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F') return 0;
        if (header[8] != 'W' || header[9] != 'A' || header[10] != 'V' || header[11] != 'E') return 0;

        uint byteRate = 0;
        Span<byte> chunkHdr = stackalloc byte[8];
        Span<byte> fmtBuf = stackalloc byte[16];
        while (fs.Position + 8 <= fs.Length)
        {
            if (fs.Read(chunkHdr) != 8) break;
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr[4..8]);
            long dataStart = fs.Position;

            if (chunkHdr[0] == 'f' && chunkHdr[1] == 'm' && chunkHdr[2] == 't' && chunkHdr[3] == ' ' && chunkSize >= 16)
            {
                if (fs.Read(fmtBuf) == 16)
                {
                    byteRate = BinaryPrimitives.ReadUInt32LittleEndian(fmtBuf[8..12]);
                }
            }
            else if (chunkHdr[0] == 'd' && chunkHdr[1] == 'a' && chunkHdr[2] == 't' && chunkHdr[3] == 'a' && byteRate > 0)
            {
                return chunkSize / (double)byteRate;
            }

            long nextPos = dataStart + chunkSize + (chunkSize & 1);
            if (nextPos <= dataStart || nextPos > fs.Length) break;
            fs.Position = nextPos;
        }
        return 0;
    }

    private static double TryReadFlacDurationSeconds(FileStream fs)
    {
        if (fs.Length < 42) return 0;
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) != 4) return 0;
        if (magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C') return 0;

        Span<byte> blkHdr = stackalloc byte[4];
        Span<byte> streamInfo = stackalloc byte[34];
        while (fs.Position + 4 <= fs.Length)
        {
            if (fs.Read(blkHdr) != 4) break;
            bool isLast = (blkHdr[0] & 0x80) != 0;
            int blkType = blkHdr[0] & 0x7F;
            int blkLen = (blkHdr[1] << 16) | (blkHdr[2] << 8) | blkHdr[3];
            if (blkType == 0 && blkLen >= 18)
            {
                if (fs.Read(streamInfo[..18]) != 18) return 0;
                int sampleRate = (streamInfo[10] << 12) | (streamInfo[11] << 4) | (streamInfo[12] >> 4);
                long totalSamples = ((long)(streamInfo[13] & 0x0F) << 32)
                                  | ((long)streamInfo[14] << 24)
                                  | ((long)streamInfo[15] << 16)
                                  | ((long)streamInfo[16] << 8)
                                  | streamInfo[17];
                if (sampleRate > 0 && totalSamples > 0)
                {
                    return totalSamples / (double)sampleRate;
                }
                return 0;
            }
            if (isLast || fs.Position + blkLen > fs.Length) break;
            fs.Position += blkLen;
        }
        return 0;
    }

    private static double TryReadMp4DurationSeconds(FileStream fs)
    {
        return ScanMp4BoxesForMvhd(fs, fs.Length);
    }

    private static double ScanMp4BoxesForMvhd(FileStream fs, long endPos)
    {
        Span<byte> hdr = stackalloc byte[8];
        Span<byte> extSize = stackalloc byte[8];
        Span<byte> mvhd = stackalloc byte[32];

        while (fs.Position + 8 <= endPos)
        {
            long boxStart = fs.Position;
            if (fs.Read(hdr) != 8) break;
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            int headerLen = 8;
            long boxSize = size32;
            if (size32 == 1)
            {
                if (fs.Read(extSize) != 8) break;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(extSize);
                headerLen = 16;
            }
            else if (size32 == 0)
            {
                boxSize = endPos - boxStart;
            }

            if (boxSize < headerLen) break;
            long boxEnd = Math.Min(endPos, boxStart + boxSize);

            if (hdr[4] == 'm' && hdr[5] == 'o' && hdr[6] == 'o' && hdr[7] == 'v')
            {
                double inner = ScanMp4BoxesForMvhd(fs, boxEnd);
                if (inner > 0) return inner;
            }
            else if (hdr[4] == 'm' && hdr[5] == 'v' && hdr[6] == 'h' && hdr[7] == 'd')
            {
                int toRead = (int)Math.Min(32, boxEnd - fs.Position);
                if (toRead >= 20 && fs.Read(mvhd[..toRead]) == toRead)
                {
                    byte ver = mvhd[0];
                    if (ver == 0 && toRead >= 20)
                    {
                        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd[12..16]);
                        uint duration = BinaryPrimitives.ReadUInt32BigEndian(mvhd[16..20]);
                        if (timescale > 0 && duration > 0) return duration / (double)timescale;
                    }
                    else if (ver == 1 && toRead >= 32)
                    {
                        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd[20..24]);
                        ulong duration = BinaryPrimitives.ReadUInt64BigEndian(mvhd[24..32]);
                        if (timescale > 0 && duration > 0) return duration / (double)timescale;
                    }
                }
            }

            fs.Position = boxEnd;
        }
        return 0;
    }

    private static double TryReadMp3DurationSeconds(FileStream fs)
    {
        if (fs.Length < 16) return 0;
        long offset = 0;
        Span<byte> id3 = stackalloc byte[10];
        if (fs.Read(id3) == 10 && id3[0] == 'I' && id3[1] == 'D' && id3[2] == '3')
        {
            int tagSize = ((id3[6] & 0x7F) << 21)
                        | ((id3[7] & 0x7F) << 14)
                        | ((id3[8] & 0x7F) << 7)
                        | (id3[9] & 0x7F);
            int footer = (id3[5] & 0x10) != 0 ? 10 : 0;
            offset = Math.Min(fs.Length, 10L + tagSize + footer);
        }

        fs.Position = offset;
        int scanLen = (int)Math.Min(131072, fs.Length - offset);
        if (scanLen < 4) return 0;

        byte[] buf = ArrayPool<byte>.Shared.Rent(scanLen);
        try
        {
            int read = fs.Read(buf, 0, scanLen);
            for (int i = 0; i <= read - 4; i++)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0) continue;
                if (!TryParseMpegFrameHeader(buf, i, out int samplesPerFrame, out int sampleRate, out int bitrateBps, out int frameLen, out int sideInfoSize))
                {
                    continue;
                }

                // Check Xing / Info VBR header inside first frame
                int xingPos = i + 4 + sideInfoSize;
                if (xingPos + 12 <= read)
                {
                    bool isXing = (buf[xingPos] == 'X' && buf[xingPos + 1] == 'i' && buf[xingPos + 2] == 'n' && buf[xingPos + 3] == 'g')
                               || (buf[xingPos] == 'I' && buf[xingPos + 1] == 'n' && buf[xingPos + 2] == 'f' && buf[xingPos + 3] == 'o');
                    if (isXing)
                    {
                        uint flags = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(xingPos + 4, 4));
                        if ((flags & 0x01) != 0)
                        {
                            uint frames = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(xingPos + 8, 4));
                            if (frames > 0) return frames * (double)samplesPerFrame / sampleRate;
                        }
                    }
                }

                // Check VBRI header
                int vbriPos = i + 4 + 32;
                if (vbriPos + 18 <= read &&
                    buf[vbriPos] == 'V' && buf[vbriPos + 1] == 'B' && buf[vbriPos + 2] == 'R' && buf[vbriPos + 3] == 'I')
                {
                    uint frames = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(vbriPos + 14, 4));
                    if (frames > 0) return frames * (double)samplesPerFrame / sampleRate;
                }

                // Verify second consecutive MPEG frame sync to avoid false sync matches
                int nextFrameIdx = i + frameLen;
                if (nextFrameIdx + 4 <= read &&
                    buf[nextFrameIdx] == 0xFF &&
                    (buf[nextFrameIdx + 1] & 0xE0) == 0xE0 &&
                    TryParseMpegFrameHeader(buf, nextFrameIdx, out _, out _, out _, out _, out _))
                {
                    long audioBytes = Math.Max(0, fs.Length - (offset + i));
                    return audioBytes * 8.0 / bitrateBps;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return 0;
    }

    private static bool TryParseMpegFrameHeader(
        byte[] buf,
        int idx,
        out int samplesPerFrame,
        out int sampleRate,
        out int bitrateBps,
        out int frameLength,
        out int sideInfoSize)
    {
        samplesPerFrame = 0;
        sampleRate = 0;
        bitrateBps = 0;
        frameLength = 0;
        sideInfoSize = 0;

        byte b1 = buf[idx + 1];
        byte b2 = buf[idx + 2];
        byte b3 = buf[idx + 3];

        int versionBits = (b1 >> 3) & 0x03; // 0=2.5, 1=reserved, 2=2, 3=1
        int layerBits = (b1 >> 1) & 0x03;   // 0=reserved, 1=L3, 2=L2, 3=L1
        int bitrateIdx = (b2 >> 4) & 0x0F;
        int srIdx = (b2 >> 2) & 0x03;
        int padding = (b2 >> 1) & 0x01;
        int channelMode = (b3 >> 6) & 0x03;

        if (versionBits == 1 || layerBits == 0 || bitrateIdx == 0 || bitrateIdx == 15 || srIdx == 3)
        {
            return false;
        }

        int[] srBase = [44100, 48000, 32000];
        sampleRate = versionBits switch
        {
            3 => srBase[srIdx],
            2 => srBase[srIdx] / 2,
            _ => srBase[srIdx] / 4
        };

        int kbps;
        if (versionBits == 3) // MPEG 1
        {
            kbps = layerBits switch
            {
                3 => new[] { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 }[bitrateIdx],
                2 => new[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 }[bitrateIdx],
                _ => new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }[bitrateIdx]
            };
            samplesPerFrame = layerBits == 3 ? 384 : 1152;
            sideInfoSize = channelMode == 3 ? 17 : 32;
        }
        else // MPEG 2 / 2.5
        {
            kbps = layerBits == 3
                ? new[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 }[bitrateIdx]
                : new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 }[bitrateIdx];
            samplesPerFrame = layerBits == 3 ? 384 : (layerBits == 2 ? 1152 : 576);
            sideInfoSize = channelMode == 3 ? 9 : 17;
        }

        if (kbps <= 0 || sampleRate <= 0) return false;
        bitrateBps = kbps * 1000;

        frameLength = layerBits == 3
            ? (12 * bitrateBps / sampleRate + padding) * 4
            : (samplesPerFrame / 8) * bitrateBps / sampleRate + padding;

        return frameLength >= 24;
    }

    private static double TryReadShellComDurationSeconds(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }
        double result = 0;
        var thread = new Thread(() =>
        {
            object? shell = null;
            object? folder = null;
            object? item = null;
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null) return;
                shell = Activator.CreateInstance(shellType);
                if (shell is null) return;

                string? dir = Path.GetDirectoryName(filePath);
                string fileName = Path.GetFileName(filePath);
                if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(fileName)) return;

                dynamic dynShell = shell;
                folder = dynShell.NameSpace(dir);
                if (folder is null) return;

                dynamic dynFolder = folder;
                item = dynFolder.ParseName(fileName);
                if (item is null) return;

                string? rawDuration = dynFolder.GetDetailsOf(item, 27) as string;
                if (string.IsNullOrWhiteSpace(rawDuration)) return;

                string trimmed = rawDuration.Trim();
                if (TimeSpan.TryParseExact(
                        trimmed,
                        [@"hh\:mm\:ss", @"h\:mm\:ss", @"mm\:ss", @"m\:ss"],
                        CultureInfo.InvariantCulture,
                        out TimeSpan ts) ||
                    TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out ts))
                {
                    result = ts.TotalSeconds;
                }
            }
            catch
            {
                result = 0;
            }
            finally
            {
                if (item is not null && Marshal.IsComObject(item)) try { Marshal.FinalReleaseComObject(item); } catch { }
                if (folder is not null && Marshal.IsComObject(folder)) try { Marshal.FinalReleaseComObject(folder); } catch { }
                if (shell is not null && Marshal.IsComObject(shell)) try { Marshal.FinalReleaseComObject(shell); } catch { }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(2500);
        return result;
    }

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

                // Migrate legacy hardcoded "3:30" durations or raw yt-dlp filenames
                if (string.IsNullOrWhiteSpace(item.duration) || item.duration == "3:30" || item.duration == "0:00")
                {
                    string realDur = ReadLocalAudioDurationFormatted(item.path);
                    if (realDur != item.duration)
                    {
                        item.duration = realDur;
                        changed = true;
                    }
                }

                bool looksRaw = item.title.Contains('_') ||
                                item.artist.Contains('_') ||
                                item.title.Contains("HIGH QUALITY AUDIO", StringComparison.OrdinalIgnoreCase) ||
                                Regex.IsMatch(item.title, @"\([_-]{0,4}[A-Za-z0-9_-]{10,14}[_-]{0,4}\)\s*$", RegexOptions.CultureInvariant);
                if (looksRaw)
                {
                    (string cleanTitle, string cleanArtist) = CleanLocalAudioMetadata(Path.GetFileNameWithoutExtension(item.path));
                    if (item.title != cleanTitle || item.artist != cleanArtist)
                    {
                        item.title = cleanTitle;
                        item.artist = cleanArtist;
                        changed = true;
                    }
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
                (string title, string artist) = CleanLocalAudioMetadata(Path.GetFileNameWithoutExtension(fullPath));
                string duration = ReadLocalAudioDurationFormatted(fullPath);
                long size = 0;
                try { size = new FileInfo(fullPath).Length; } catch { /* ignore */ }

                string id = "local_win_" + Guid.NewGuid().ToString("N")[..12];
                var rec = new StoredLocalTrack
                {
                    id = id,
                    path = fullPath,
                    title = title,
                    artist = artist,
                    duration = duration,
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
