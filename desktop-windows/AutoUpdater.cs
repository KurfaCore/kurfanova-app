using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KurfanovaMuzik;

public sealed class AutoUpdater : IDisposable
{
    private const string ManifestUrl =
        "https://github.com/KurfaCore/kurfanova-app/releases/latest/download/win-update.json";

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _updatesDir;
    private readonly string _stagedMetaPath;
    private readonly Version _currentVersion;
    private int _applyTriggered;

    public string? StagedExePath { get; private set; }
    public Version? StagedVersion { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public bool HasStagedUpdate => !string.IsNullOrWhiteSpace(StagedExePath) && File.Exists(StagedExePath);

    public event Action<Version, string?>? UpdateReady;

    public AutoUpdater()
    {
        _updatesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KurfanovaMuzik",
            "updates");
        _stagedMetaPath = Path.Combine(_updatesDir, "staged-update.json");
        _currentVersion = NormalizeVersion(
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0));
    }

    public Version CurrentVersion => _currentVersion;

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KurfanovaMuzik-Updater/1.1");
        return client;
    }

    public async Task CheckAndStageAsync(CancellationToken ct)
    {
        try
        {
            CleanupOldTempFiles();

            // Check if a previously staged binary already exists and is still newer than us
            if (TryLoadExistingStaged(out Version? existingVer, out string? existingPath, out string? existingNotes) &&
                existingVer is not null && existingPath is not null &&
                existingVer > _currentVersion)
            {
                StagedVersion = existingVer;
                StagedExePath = existingPath;
                ReleaseNotes = existingNotes;
                UpdateReady?.Invoke(existingVer, existingNotes);
                return;
            }

            Directory.CreateDirectory(_updatesDir);

            using var manifestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            manifestCts.CancelAfter(TimeSpan.FromSeconds(20));
            string manifestUrl = $"{ManifestUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            string manifestJson = await Http.GetStringAsync(manifestUrl, manifestCts.Token).ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(manifestJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            string? rawVersion = root.TryGetProperty("version", out JsonElement vEl) && vEl.ValueKind == JsonValueKind.String
                ? vEl.GetString()
                : null;
            string? downloadUrl = root.TryGetProperty("url", out JsonElement uEl) && uEl.ValueKind == JsonValueKind.String
                ? uEl.GetString()
                : null;
            string? expectedSha256 = root.TryGetProperty("sha256", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.String
                ? sEl.GetString()?.Trim().ToLowerInvariant()
                : null;
            long expectedSize = root.TryGetProperty("size", out JsonElement szEl) && szEl.ValueKind == JsonValueKind.Number
                ? szEl.GetInt64()
                : 0;
            string? notes = root.TryGetProperty("notes", out JsonElement nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(rawVersion) ||
                string.IsNullOrWhiteSpace(downloadUrl) ||
                string.IsNullOrWhiteSpace(expectedSha256) ||
                expectedSha256.Length != 64 ||
                !TryParseNormalizedVersion(rawVersion, out Version? remoteVersion) ||
                remoteVersion is null ||
                remoteVersion <= _currentVersion)
            {
                return;
            }

            if (!IsTrustedReleaseUri(downloadUrl))
            {
                return;
            }

            string targetExePath = Path.Combine(_updatesDir, $"Kurfanova-Muzik-Windows-{remoteVersion}.exe");
            string partPath = targetExePath + ".part";

            if (File.Exists(targetExePath) && VerifyFileIntegrity(targetExePath, expectedSize, expectedSha256))
            {
                SaveStagedMeta(remoteVersion.ToString(), targetExePath, expectedSha256, notes);
                StagedVersion = remoteVersion;
                StagedExePath = targetExePath;
                ReleaseNotes = notes;
                UpdateReady?.Invoke(remoteVersion, notes);
                return;
            }

            using (var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                dlCts.CancelAfter(TimeSpan.FromMinutes(3));
                using HttpResponseMessage response = await Http.GetAsync(
                    downloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    dlCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return;

                await using Stream netStream = await response.Content.ReadAsStreamAsync(dlCts.Token).ConfigureAwait(false);
                await using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await netStream.CopyToAsync(fs, dlCts.Token).ConfigureAwait(false);
                }
            }

            if (!VerifyFileIntegrity(partPath, expectedSize, expectedSha256))
            {
                TryDeleteFile(partPath);
                return;
            }

            File.Move(partPath, targetExePath, overwrite: true);
            SaveStagedMeta(remoteVersion.ToString(), targetExePath, expectedSha256, notes);

            StagedVersion = remoteVersion;
            StagedExePath = targetExePath;
            ReleaseNotes = notes;
            UpdateReady?.Invoke(remoteVersion, notes);
        }
        catch
        {
            // Never interrupt playback or startup if offline/unreachable
        }
    }

    public bool TryApplyPendingUpdate(bool relaunch)
    {
        if (!HasStagedUpdate || string.IsNullOrWhiteSpace(StagedExePath))
        {
            return false;
        }
        if (Interlocked.CompareExchange(ref _applyTriggered, 1, 0) != 0)
        {
            return true;
        }

        try
        {
            string? currentExe = Environment.ProcessPath;
            if (!IsUpdatableProcessPath(currentExe))
            {
                Interlocked.Exchange(ref _applyTriggered, 0);
                return false;
            }

            Directory.CreateDirectory(_updatesDir);
            string scriptPath = Path.Combine(_updatesDir, "apply_update.ps1");
            string scriptBody = """
                $ErrorActionPreference = 'SilentlyContinue'
                $targetPid = [int]$env:KURFA_PID
                $cur = $env:KURFA_CUR
                $newExe = $env:KURFA_NEW
                $meta = $env:KURFA_META
                $relaunch = $env:KURFA_RELAUNCH -eq '1'
                $bak = "$cur.bak"

                for ($i = 0; $i -lt 30; $i++) {
                  $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
                  if (-not $proc) { break }
                  Start-Sleep -Milliseconds 500
                }

                if (-not (Test-Path -LiteralPath $newExe)) { exit 1 }

                $swapped = $false
                for ($attempt = 0; $attempt -lt 8; $attempt++) {
                  try {
                    if (Test-Path -LiteralPath $cur) {
                      Move-Item -LiteralPath $cur -Destination $bak -Force -ErrorAction Stop
                    }
                    Move-Item -LiteralPath $newExe -Destination $cur -Force -ErrorAction Stop
                    $swapped = $true
                    break
                  } catch {
                    if ((-not (Test-Path -LiteralPath $cur)) -and (Test-Path -LiteralPath $bak)) {
                      Move-Item -LiteralPath $bak -Destination $cur -Force -ErrorAction SilentlyContinue
                    }
                    Start-Sleep -Milliseconds 600
                  }
                }

                if ($swapped) {
                  Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue
                  Remove-Item -LiteralPath $meta -Force -ErrorAction SilentlyContinue
                  if ($relaunch -and (Test-Path -LiteralPath $cur)) {
                    Start-Process -FilePath $cur
                  }
                }

                Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
                """;
            File.WriteAllText(scriptPath, scriptBody, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            bool dirWritable = CanWriteToDirectory(Path.GetDirectoryName(currentExe!));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = !dirWritable,
                CreateNoWindow = dirWritable,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (!dirWritable)
            {
                psi.Verb = "runas";
                // Pass via Environment fails when UseShellExecute = true; only fall back to args when UAC is needed
                psi.Arguments =
                    $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"" +
                    $"$env:KURFA_PID='{Environment.ProcessId}';" +
                    $"$env:KURFA_CUR='{EscapePsSingleQuote(currentExe!)}';" +
                    $"$env:KURFA_NEW='{EscapePsSingleQuote(StagedExePath)}';" +
                    $"$env:KURFA_META='{EscapePsSingleQuote(_stagedMetaPath)}';" +
                    $"$env:KURFA_RELAUNCH='{(relaunch ? "1" : "0")}';" +
                    $"& '{EscapePsSingleQuote(scriptPath)}'\"";
            }
            else
            {
                psi.Environment["KURFA_PID"] = Environment.ProcessId.ToString();
                psi.Environment["KURFA_CUR"] = currentExe!;
                psi.Environment["KURFA_NEW"] = StagedExePath;
                psi.Environment["KURFA_META"] = _stagedMetaPath;
                psi.Environment["KURFA_RELAUNCH"] = relaunch ? "1" : "0";
            }

            Process? started = Process.Start(psi);
            if (started is null)
            {
                Interlocked.Exchange(ref _applyTriggered, 0);
                return false;
            }
            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _applyTriggered, 0);
            return false;
        }
    }

    private bool TryLoadExistingStaged(out Version? version, out string? exePath, out string? notes)
    {
        version = null;
        exePath = null;
        notes = null;
        try
        {
            if (!File.Exists(_stagedMetaPath)) return false;
            string json = File.ReadAllText(_stagedMetaPath, Encoding.UTF8);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string? vStr = root.TryGetProperty("version", out JsonElement vEl) ? vEl.GetString() : null;
            string? path = root.TryGetProperty("filePath", out JsonElement pEl) ? pEl.GetString() : null;
            string? sha = root.TryGetProperty("sha256", out JsonElement sEl) ? sEl.GetString() : null;
            notes = root.TryGetProperty("notes", out JsonElement nEl) ? nEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(vStr) ||
                string.IsNullOrWhiteSpace(path) ||
                string.IsNullOrWhiteSpace(sha) ||
                !TryParseNormalizedVersion(vStr, out version) ||
                !File.Exists(path) ||
                !VerifyFileIntegrity(path, 0, sha))
            {
                TryDeleteFile(_stagedMetaPath);
                return false;
            }

            exePath = path;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveStagedMeta(string version, string filePath, string sha256, string? notes)
    {
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                version,
                filePath,
                sha256,
                notes
            });
            File.WriteAllText(_stagedMetaPath, json, Encoding.UTF8);
        }
        catch
        {
            // Ignore metadata write failure
        }
    }

    private void CleanupOldTempFiles()
    {
        try
        {
            if (!Directory.Exists(_updatesDir)) return;
            foreach (string part in Directory.EnumerateFiles(_updatesDir, "*.part"))
            {
                TryDeleteFile(part);
            }
            string? currentExe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(currentExe))
            {
                TryDeleteFile(currentExe + ".bak");
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static bool VerifyFileIntegrity(string path, long expectedSize, string expectedSha256)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 1024 * 1024) return false;
            if (expectedSize > 0 && fi.Length != expectedSize) return false;

            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] header = new byte[2];
            if (fs.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                return false;
            }

            fs.Position = 0;
            byte[] hash = SHA256.HashData(fs);
            string actual = Convert.ToHexString(hash).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expectedSha256.Trim().ToLowerInvariant()));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTrustedReleaseUri(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri)) return false;
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return false;
        string host = uri.Host.ToLowerInvariant();
        return host == "github.com" ||
               host.EndsWith(".github.com", StringComparison.Ordinal) ||
               host == "githubusercontent.com" ||
               host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    private static bool IsUpdatableProcessPath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        string name = Path.GetFileName(exePath);
        if (name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return false;
        return File.Exists(exePath);
    }

    private static bool CanWriteToDirectory(string? dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath)) return false;
        try
        {
            string probe = Path.Combine(dirPath, $".kurfa_write_test_{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapePsSingleQuote(string value) => value.Replace("'", "''");

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static bool TryParseNormalizedVersion(string raw, out Version? normalized)
    {
        normalized = null;
        string cleaned = raw.Trim().TrimStart('v', 'V');
        int dash = cleaned.IndexOf('-');
        if (dash >= 0) cleaned = cleaned[..dash];
        if (!Version.TryParse(cleaned, out Version? parsed) || parsed is null) return false;
        normalized = NormalizeVersion(parsed);
        return true;
    }

    private static Version NormalizeVersion(Version v) =>
        new(
            Math.Max(0, v.Major),
            Math.Max(0, v.Minor),
            Math.Max(0, v.Build),
            Math.Max(0, v.Revision));

    public void Dispose()
    {
        // Shared HttpClient stays static for reuse; nothing unmanaged held here
    }
}
