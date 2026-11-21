using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;

namespace KurfanovaMuzik;

/// <summary>
/// Modular SABR (Server-Abrasive Streaming) fallback client and UMP (Unified Media Protocol) binary parser.
/// Only invoked as a last-resort fallback after direct URL clients fail or return 403.
/// </summary>
public sealed class SabrFallbackClient
{
    private const int UmpPartMediaHeader = 20;
    private const int UmpPartMedia = 21;
    private const int UmpPartMediaEnd = 22;
    private const int UmpPartSabrRedirect = 58;
    private const int UmpPartSabrError = 59;

    public sealed record SabrBootstrapInfo(
        string VideoId,
        string ServerAbrStreamingUrl,
        string? VideoPlaybackUstreamerConfig,
        string? PoToken,
        string? VisitorData,
        string Title,
        string Artist,
        long DurationSec,
        int Itag,
        string MimeType,
        long ContentLength,
        long LastModified,
        long ApproxDurationMs,
        int Bitrate
    );

    public sealed record SabrAudioPayload(
        byte[] AudioBytes,
        string MimeType,
        int Itag,
        long TotalLength,
        string Title,
        string Artist,
        long DurationSec
    );

    public sealed class UmpMediaHeader
    {
        public uint HeaderId { get; set; }
        public string VideoId { get; set; } = string.Empty;
        public int Itag { get; set; }
        public long LastModified { get; set; }
        public string MimeType { get; set; } = string.Empty;
        public bool IsInitSegment { get; set; }
        public long SequenceNumber { get; set; }
        public long StartRange { get; set; }
        public long ContentLength { get; set; }
        public long DurationMs { get; set; }
    }

    public sealed class UmpParseResult
    {
        public Dictionary<uint, UmpMediaHeader> Headers { get; } = new();
        public Dictionary<uint, MemoryStream> Segments { get; } = new();
        public HashSet<uint> CompletedHeaders { get; } = new();
        public string? RedirectUrl { get; set; }
        public bool HasSabrError { get; set; }
        public int SabrErrorCode { get; set; }
    }

    public async Task<SabrAudioPayload?> TryResolveSabrFallbackAsync(
        SabrBootstrapInfo bootstrap,
        HttpClient httpClient,
        string userAgent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bootstrap.ServerAbrStreamingUrl))
        {
            return null;
        }

        string currentUrl = AppendSabrQueryParams(bootstrap.ServerAbrStreamingUrl, bootstrap.PoToken, requestNumber: 0);
        byte[] requestBody = BuildSabrAbrRequest(bootstrap, playerTimeMs: 0);

        for (int hop = 0; hop < 3; hop++)
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(TimeSpan.FromSeconds(8));

            using var req = new HttpRequestMessage(HttpMethod.Post, currentUrl)
            {
                Content = new ByteArrayContent(requestBody)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            req.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://music.youtube.com/");

            using HttpResponseMessage resp = await httpClient
                .SendAsync(req, HttpCompletionOption.ResponseContentRead, reqCts.Token)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            byte[] rawBytes = await resp.Content.ReadAsByteArrayAsync(reqCts.Token).ConfigureAwait(false);
            if (rawBytes.Length == 0)
            {
                return null;
            }

            UmpParseResult parsed = ParseUmpResponse(rawBytes);

            if (!string.IsNullOrWhiteSpace(parsed.RedirectUrl))
            {
                currentUrl = parsed.RedirectUrl!;
                continue;
            }

            if (parsed.HasSabrError && parsed.Segments.Count == 0)
            {
                return null;
            }

            byte[]? assembled = ExtractBestAudioBytes(parsed, bootstrap, out string resolvedMime, out int resolvedItag, out long resolvedTotal);
            if (assembled is { Length: >= 256 })
            {
                return new SabrAudioPayload(
                    assembled,
                    resolvedMime,
                    resolvedItag,
                    resolvedTotal,
                    bootstrap.Title,
                    bootstrap.Artist,
                    bootstrap.DurationSec);
            }

            return null;
        }

        return null;
    }

    public static UmpParseResult ParseUmpResponse(ReadOnlySpan<byte> data)
    {
        var result = new UmpParseResult();
        int offset = 0;

        while (offset < data.Length)
        {
            if (!TryReadUmpVarInt(data, ref offset, out uint partType)) break;
            if (!TryReadUmpVarInt(data, ref offset, out uint partSize)) break;
            if (partSize > (uint)(data.Length - offset)) break;

            ReadOnlySpan<byte> payload = data.Slice(offset, (int)partSize);
            offset += (int)partSize;

            switch ((int)partType)
            {
                case UmpPartMediaHeader:
                {
                    UmpMediaHeader hdr = ParseMediaHeaderProtobuf(payload);
                    result.Headers[hdr.HeaderId] = hdr;
                    break;
                }
                case UmpPartMedia:
                {
                    int segOff = 0;
                    if (TryReadUmpVarInt(payload, ref segOff, out uint headerId) && segOff < payload.Length)
                    {
                        if (!result.Segments.TryGetValue(headerId, out MemoryStream? ms))
                        {
                            ms = new MemoryStream();
                            result.Segments[headerId] = ms;
                        }
                        ms.Write(payload[segOff..]);
                    }
                    break;
                }
                case UmpPartMediaEnd:
                {
                    int endOff = 0;
                    if (TryReadUmpVarInt(payload, ref endOff, out uint headerId))
                    {
                        result.CompletedHeaders.Add(headerId);
                    }
                    break;
                }
                case UmpPartSabrRedirect:
                {
                    string? url = ParseFirstStringFieldProtobuf(payload, fieldNumber: 1);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        result.RedirectUrl = url;
                    }
                    break;
                }
                case UmpPartSabrError:
                {
                    result.HasSabrError = true;
                    result.SabrErrorCode = (int)ParseFirstVarIntFieldProtobuf(payload, fieldNumber: 2);
                    break;
                }
            }
        }

        return result;
    }

    public static bool TryReadUmpVarInt(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        if (offset >= data.Length) return false;

        byte b0 = data[offset];
        if (b0 < 0x80)
        {
            offset += 1;
            value = b0;
            return true;
        }
        if (b0 < 0xC0)
        {
            if (offset + 2 > data.Length) return false;
            value = (uint)((b0 & 0x3F) + (data[offset + 1] << 6));
            offset += 2;
            return true;
        }
        if (b0 < 0xE0)
        {
            if (offset + 3 > data.Length) return false;
            value = (uint)((b0 & 0x1F) + (data[offset + 1] << 5) + (data[offset + 2] << 13));
            offset += 3;
            return true;
        }
        if (b0 < 0xF0)
        {
            if (offset + 4 > data.Length) return false;
            value = (uint)((b0 & 0x0F) + (data[offset + 1] << 4) + (data[offset + 2] << 12) + (data[offset + 3] << 20));
            offset += 4;
            return true;
        }

        if (offset + 5 > data.Length) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 1, 4));
        offset += 5;
        return true;
    }

    private static byte[]? ExtractBestAudioBytes(
        UmpParseResult parsed,
        SabrBootstrapInfo bootstrap,
        out string mimeType,
        out int itag,
        out long totalLength)
    {
        mimeType = string.IsNullOrWhiteSpace(bootstrap.MimeType) ? "audio/mp4" : bootstrap.MimeType;
        itag = bootstrap.Itag;
        totalLength = bootstrap.ContentLength;

        if (parsed.Segments.Count == 0)
        {
            return null;
        }

        using var combined = new MemoryStream();
        var ordered = parsed.Headers.Values
            .OrderBy(h => h.StartRange)
            .ThenBy(h => h.SequenceNumber)
            .ToList();

        foreach (UmpMediaHeader hdr in ordered)
        {
            if (!string.IsNullOrWhiteSpace(hdr.MimeType) &&
                !hdr.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (parsed.Segments.TryGetValue(hdr.HeaderId, out MemoryStream? seg) && seg.Length > 0)
            {
                byte[] chunk = seg.ToArray();
                combined.Write(chunk, 0, chunk.Length);
                if (!string.IsNullOrWhiteSpace(hdr.MimeType))
                {
                    int semi = hdr.MimeType.IndexOf(';');
                    mimeType = semi > 0 ? hdr.MimeType[..semi].Trim() : hdr.MimeType.Trim();
                }
                if (hdr.Itag > 0) itag = hdr.Itag;
                if (hdr.ContentLength > 0) totalLength = hdr.ContentLength;
            }
        }

        if (combined.Length == 0)
        {
            foreach (MemoryStream seg in parsed.Segments.Values)
            {
                if (seg.Length > 0)
                {
                    byte[] chunk = seg.ToArray();
                    combined.Write(chunk, 0, chunk.Length);
                }
            }
        }

        if (combined.Length == 0) return null;
        byte[] output = combined.ToArray();
        if (totalLength <= 0) totalLength = output.Length;
        return output;
    }

    private static string AppendSabrQueryParams(string baseUrl, string? poToken, int requestNumber)
    {
        var sb = new StringBuilder(baseUrl);
        char sep = baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        sb.Append(sep).Append("rn=").Append(requestNumber);
        if (!baseUrl.Contains("c=", StringComparison.Ordinal))
        {
            sb.Append("&c=WEB_REMIX");
        }
        if (!string.IsNullOrWhiteSpace(poToken) && !baseUrl.Contains("pot=", StringComparison.Ordinal))
        {
            sb.Append("&pot=").Append(Uri.EscapeDataString(poToken));
        }
        return sb.ToString();
    }

    private static byte[] BuildSabrAbrRequest(SabrBootstrapInfo bootstrap, long playerTimeMs)
    {
        using var root = new MemoryStream();

        // Field 1: clientAbrState (length-delimited submessage)
        using (var abrState = new MemoryStream())
        {
            WriteProtoVarIntField(abrState, 1, (ulong)Math.Max(0, playerTimeMs));
            WriteProtoVarIntField(abrState, 25, 1); // enabledTrackTypesBitfield = 1 (audio)
            WriteProtoBytesField(root, 1, abrState.ToArray());
        }

        // Field 2: selectedFormatIds (repeated length-delimited)
        if (bootstrap.Itag > 0)
        {
            using var fmt = new MemoryStream();
            WriteProtoVarIntField(fmt, 1, (ulong)bootstrap.Itag);
            if (bootstrap.LastModified > 0)
            {
                WriteProtoVarIntField(fmt, 2, (ulong)bootstrap.LastModified);
            }
            WriteProtoBytesField(root, 2, fmt.ToArray());
        }

        // Field 5: videoPlaybackUstreamerConfig (bytes)
        if (!string.IsNullOrWhiteSpace(bootstrap.VideoPlaybackUstreamerConfig))
        {
            byte[]? cfgBytes = TryDecodeBase64Flexible(bootstrap.VideoPlaybackUstreamerConfig);
            if (cfgBytes is { Length: > 0 })
            {
                WriteProtoBytesField(root, 5, cfgBytes);
            }
        }

        // Field 16: poToken (bytes)
        if (!string.IsNullOrWhiteSpace(bootstrap.PoToken))
        {
            byte[]? potBytes = TryDecodeBase64Flexible(bootstrap.PoToken);
            if (potBytes is { Length: > 0 })
            {
                WriteProtoBytesField(root, 16, potBytes);
            }
        }

        return root.ToArray();
    }

    private static byte[]? TryDecodeBase64Flexible(string raw)
    {
        try
        {
            string norm = raw.Trim().Replace('-', '+').Replace('_', '/');
            int mod = norm.Length % 4;
            if (mod > 0)
            {
                norm = norm.PadRight(norm.Length + (4 - mod), '=');
            }
            return Convert.FromBase64String(norm);
        }
        catch
        {
            return null;
        }
    }

    private static UmpMediaHeader ParseMediaHeaderProtobuf(ReadOnlySpan<byte> payload)
    {
        var hdr = new UmpMediaHeader();
        int off = 0;
        while (off < payload.Length)
        {
            if (!TryReadProtoVarInt(payload, ref off, out ulong tag)) break;
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 0x07);

            switch (wire)
            {
                case 0:
                {
                    if (!TryReadProtoVarInt(payload, ref off, out ulong v)) return hdr;
                    switch (field)
                    {
                        case 1: hdr.HeaderId = (uint)v; break;
                        case 3: hdr.Itag = (int)v; break;
                        case 4: hdr.LastModified = (long)v; break;
                        case 6: hdr.IsInitSegment = v != 0; break;
                        case 7: hdr.SequenceNumber = (long)v; break;
                        case 9: hdr.StartRange = (long)v; break;
                        case 11: hdr.ContentLength = (long)v; break;
                        case 12: hdr.DurationMs = (long)v; break;
                    }
                    break;
                }
                case 2:
                {
                    if (!TryReadProtoVarInt(payload, ref off, out ulong len)) return hdr;
                    if (len > (ulong)(payload.Length - off)) return hdr;
                    ReadOnlySpan<byte> slice = payload.Slice(off, (int)len);
                    off += (int)len;
                    if (field == 2) hdr.VideoId = Encoding.UTF8.GetString(slice);
                    else if (field == 5) hdr.MimeType = Encoding.UTF8.GetString(slice);
                    break;
                }
                case 1:
                    if (off + 8 > payload.Length) return hdr;
                    off += 8;
                    break;
                case 5:
                    if (off + 4 > payload.Length) return hdr;
                    off += 4;
                    break;
                default:
                    return hdr;
            }
        }
        return hdr;
    }

    private static string? ParseFirstStringFieldProtobuf(ReadOnlySpan<byte> payload, int fieldNumber)
    {
        int off = 0;
        while (off < payload.Length)
        {
            if (!TryReadProtoVarInt(payload, ref off, out ulong tag)) break;
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 0x07);
            if (wire == 0)
            {
                if (!TryReadProtoVarInt(payload, ref off, out _)) break;
            }
            else if (wire == 2)
            {
                if (!TryReadProtoVarInt(payload, ref off, out ulong len) || len > (ulong)(payload.Length - off)) break;
                if (field == fieldNumber)
                {
                    return Encoding.UTF8.GetString(payload.Slice(off, (int)len));
                }
                off += (int)len;
            }
            else if (wire == 1) { if (off + 8 > payload.Length) break; off += 8; }
            else if (wire == 5) { if (off + 4 > payload.Length) break; off += 4; }
            else break;
        }
        return null;
    }

    private static ulong ParseFirstVarIntFieldProtobuf(ReadOnlySpan<byte> payload, int fieldNumber)
    {
        int off = 0;
        while (off < payload.Length)
        {
            if (!TryReadProtoVarInt(payload, ref off, out ulong tag)) break;
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 0x07);
            if (wire == 0)
            {
                if (!TryReadProtoVarInt(payload, ref off, out ulong v)) break;
                if (field == fieldNumber) return v;
            }
            else if (wire == 2)
            {
                if (!TryReadProtoVarInt(payload, ref off, out ulong len) || len > (ulong)(payload.Length - off)) break;
                off += (int)len;
            }
            else if (wire == 1) { if (off + 8 > payload.Length) break; off += 8; }
            else if (wire == 5) { if (off + 4 > payload.Length) break; off += 4; }
            else break;
        }
        return 0;
    }

    private static bool TryReadProtoVarInt(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (offset < data.Length && shift < 64)
        {
            byte b = data[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
            shift += 7;
        }
        return false;
    }

    private static void WriteProtoVarIntField(Stream s, int fieldNumber, ulong value)
    {
        WriteProtoVarInt(s, (ulong)((fieldNumber << 3) | 0));
        WriteProtoVarInt(s, value);
    }

    private static void WriteProtoBytesField(Stream s, int fieldNumber, ReadOnlySpan<byte> bytes)
    {
        WriteProtoVarInt(s, (ulong)((fieldNumber << 3) | 2));
        WriteProtoVarInt(s, (ulong)bytes.Length);
        s.Write(bytes);
    }

    private static void WriteProtoVarInt(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }
}
