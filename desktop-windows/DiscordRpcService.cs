using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KurfanovaMuzik;

/// <summary>
/// Zero-dependency Discord Rich Presence client using Windows Named Pipes (discord-ipc-0..9).
/// Silently stays passive (zero exceptions, zero CPU) when Discord is not running.
/// </summary>
public sealed class DiscordRpcService : IDisposable
{
    private const string DefaultClientId = "1340774651936407653";
    private const int MaxPipeIndex = 9;
    private const int PipeConnectTimeoutMs = 40;
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _clientId;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private NamedPipeClientStream? _pipe;
    private bool _handshakeDone;
    private DateTimeOffset _nextProbeAllowedUtc = DateTimeOffset.MinValue;

    private string? _lastTitle;
    private string? _lastArtist;
    private bool _lastIsPlaying;
    private long _lastSeekBucket;
    private int _disposed;

    public bool IsConnected => _pipe is { IsConnected: true } && _handshakeDone;

    public DiscordRpcService(string? clientId = null)
    {
        _clientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId.Trim();
    }

    public void UpdatePresence(
        string? title,
        string? artist,
        bool isPlaying,
        double currentSec = 0,
        double durationSec = 0)
    {
        if (_disposed != 0) return;

        string cleanTitle = string.IsNullOrWhiteSpace(title) ? "Kurfamüzik" : title.Trim();
        string cleanArtist = string.IsNullOrWhiteSpace(artist) ? "Kurfamüzik" : artist.Trim();

        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long seekBucket = isPlaying && durationSec > 0
            ? (nowUnix - (long)Math.Max(0, currentSec)) / 4L
            : 0L;

        if (string.Equals(_lastTitle, cleanTitle, StringComparison.Ordinal) &&
            string.Equals(_lastArtist, cleanArtist, StringComparison.Ordinal) &&
            _lastIsPlaying == isPlaying &&
            (!isPlaying || Math.Abs(_lastSeekBucket - seekBucket) <= 1))
        {
            return;
        }

        _lastTitle = cleanTitle;
        _lastArtist = cleanArtist;
        _lastIsPlaying = isPlaying;
        _lastSeekBucket = seekBucket;

        _ = Task.Run(() => SendPresenceInternalAsync(cleanTitle, cleanArtist, isPlaying, currentSec, durationSec));
    }

    public void ClearPresence()
    {
        if (_disposed != 0) return;
        _lastTitle = null;
        _lastArtist = null;
        _lastIsPlaying = false;
        _lastSeekBucket = 0;

        if (_pipe is null || !_pipe.IsConnected)
        {
            return;
        }

        _ = Task.Run(ClearPresenceInternalAsync);
    }

    public async Task<bool> TrySendActivityAsync(
        string? title,
        string? artist,
        bool isPlaying,
        double currentSec = 0,
        double durationSec = 0,
        CancellationToken ct = default)
    {
        if (_disposed != 0) return false;
        string cleanTitle = string.IsNullOrWhiteSpace(title) ? "Kurfamüzik" : title.Trim();
        string cleanArtist = string.IsNullOrWhiteSpace(artist) ? "Kurfamüzik" : artist.Trim();
        return await SendPresenceInternalAsync(cleanTitle, cleanArtist, isPlaying, currentSec, durationSec, ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> SendPresenceInternalAsync(
        string title,
        string artist,
        bool isPlaying,
        double currentSec,
        double durationSec,
        CancellationToken externalCt = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, externalCt);
        CancellationToken ct = linkedCts.Token;

        try
        {
            await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        try
        {
            if (!await EnsureConnectedUnlockedAsync(ct).ConfigureAwait(false))
            {
                return false;
            }

            string displayDetails = string.Equals(artist, title, StringComparison.OrdinalIgnoreCase)
                ? $"Kurfamüzik — {title}"
                : $"Kurfamüzik — {artist} - {title}";
            if (displayDetails.Length > 128)
            {
                displayDetails = displayDetails[..125] + "...";
            }
            else if (displayDetails.Length < 2)
            {
                displayDetails = "Kurfamüzik";
            }

            string stateText = isPlaying ? "Dinliyor" : "Duraklatıldı";
            long? startTs = null;
            long? endTs = null;
            if (isPlaying && durationSec > 1)
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long posMs = (long)Math.Clamp(currentSec * 1000.0, 0, durationSec * 1000.0);
                long durMs = (long)(durationSec * 1000.0);
                startTs = nowMs - posMs;
                endTs = startTs + durMs;
            }

            object activity = new
            {
                type = 2,
                details = displayDetails,
                state = stateText,
                timestamps = (startTs.HasValue && endTs.HasValue)
                    ? new { start = startTs.Value, end = endTs.Value }
                    : null,
                assets = new
                {
                    large_text = displayDetails
                },
                instance = false
            };

            var frame = new
            {
                cmd = "SET_ACTIVITY",
                args = new
                {
                    pid = Environment.ProcessId,
                    activity
                },
                nonce = Guid.NewGuid().ToString("N")
            };

            string json = JsonSerializer.Serialize(frame, JsonOptions);
            await WriteIpcPacketUnlockedAsync(1, json, ct).ConfigureAwait(false);
            _ = await TryReadIpcPacketUnlockedAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            DisconnectUnlocked();
            return false;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task ClearPresenceInternalAsync()
    {
        try
        {
            await _ioLock.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        try
        {
            if (_pipe is not { IsConnected: true } || !_handshakeDone)
            {
                return;
            }

            var frame = new
            {
                cmd = "SET_ACTIVITY",
                args = new
                {
                    pid = Environment.ProcessId
                },
                nonce = Guid.NewGuid().ToString("N")
            };

            string json = JsonSerializer.Serialize(frame, JsonOptions);
            await WriteIpcPacketUnlockedAsync(1, json, _disposeCts.Token).ConfigureAwait(false);
            _ = await TryReadIpcPacketUnlockedAsync(_disposeCts.Token).ConfigureAwait(false);
        }
        catch
        {
            DisconnectUnlocked();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<bool> EnsureConnectedUnlockedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true } && _handshakeDone)
        {
            return true;
        }

        DisconnectUnlocked();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < _nextProbeAllowedUtc)
        {
            return false;
        }

        for (int i = 0; i <= MaxPipeIndex; i++)
        {
            ct.ThrowIfCancellationRequested();
            string pipeName = $"discord-ipc-{i}";
            NamedPipeClientStream? candidate = null;
            try
            {
                candidate = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await candidate.ConnectAsync(PipeConnectTimeoutMs, ct).ConfigureAwait(false);
                if (!candidate.IsConnected)
                {
                    candidate.Dispose();
                    continue;
                }

                _pipe = candidate;
                string handshakeJson = JsonSerializer.Serialize(new { v = 1, client_id = _clientId });
                await WriteIpcPacketUnlockedAsync(0, handshakeJson, ct).ConfigureAwait(false);
                string? reply = await TryReadIpcPacketUnlockedAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(reply))
                {
                    _handshakeDone = true;
                    _nextProbeAllowedUtc = DateTimeOffset.MinValue;
                    return true;
                }

                DisconnectUnlocked();
            }
            catch
            {
                if (ReferenceEquals(_pipe, candidate))
                {
                    DisconnectUnlocked();
                }
                else
                {
                    try { candidate?.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        _nextProbeAllowedUtc = DateTimeOffset.UtcNow + ReconnectBackoff;
        return false;
    }

    private async Task WriteIpcPacketUnlockedAsync(int opcode, string json, CancellationToken ct)
    {
        if (_pipe is null || !_pipe.IsConnected)
        {
            throw new IOException("Discord IPC pipe is not connected.");
        }

        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] packet = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), payload.Length);
        Buffer.BlockCopy(payload, 0, packet, 8, payload.Length);

        await _pipe.WriteAsync(packet, ct).ConfigureAwait(false);
        await _pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> TryReadIpcPacketUnlockedAsync(CancellationToken ct)
    {
        if (_pipe is null || !_pipe.IsConnected)
        {
            return null;
        }

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(600);

        byte[] header = new byte[8];
        if (!await ReadExactAsync(_pipe, header, 8, readCts.Token).ConfigureAwait(false))
        {
            return null;
        }

        int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (len <= 0 || len > 1_048_576)
        {
            return null;
        }

        byte[] body = new byte[len];
        if (!await ReadExactAsync(_pipe, body, len, readCts.Token).ConfigureAwait(false))
        {
            return null;
        }

        return Encoding.UTF8.GetString(body);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private void DisconnectUnlocked()
    {
        _handshakeDone = false;
        if (_pipe is not null)
        {
            try { _pipe.Dispose(); } catch { /* ignore */ }
            _pipe = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _disposeCts.Cancel(); } catch { /* ignore */ }
        try
        {
            if (_ioLock.Wait(150))
            {
                try { DisconnectUnlocked(); }
                finally { _ioLock.Release(); }
            }
        }
        catch { /* ignore */ }

        _ioLock.Dispose();
        _disposeCts.Dispose();
    }
}
