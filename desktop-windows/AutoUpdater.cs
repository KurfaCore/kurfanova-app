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

    private static readonly TimeSpan AutoCheckCooldown = TimeSpan.FromHours(4);
    private static readonly TimeSpan ManualCheckCooldown = TimeSpan.FromSeconds(25);
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly AppPaths _paths;
    private readonly string _updatesDir;
    private readonly string _stagedMetaPath;
    private readonly Version _currentVersion;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private DateTimeOffset _lastInMemoryCheckAt = DateTimeOffset.MinValue;
    private string? _stagedSha256;
    private int _applyTriggered;

    public string? StagedExePath { get; private set; }
    public Version? StagedVersion { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public bool HasStagedUpdate => !string.IsNullOrWhiteSpace(StagedExePath) && File.Exists(StagedExePath);

    public event Action<Version, string?>? UpdateReady;

    public AutoUpdater() : this(new AppPaths(AppPaths.ResolveActiveInstallDir()))
    {
    }

    public AutoUpdater(AppPaths? paths)
    {
        _paths = paths ?? new AppPaths(AppPaths.ResolveActiveInstallDir());
        _updatesDir = _paths.UpdatesDir;
        _stagedMetaPath = Path.Combine(_updatesDir, "staged-update.json");
        _currentVersion = NormalizeVersion(
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 1, 0, 0));
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KurfanovaMuzik-Updater/1.2");
        return client;
    }

    public async Task CheckAndStageAsync(CancellationToken ct, bool force = false)
    {
        if (!await _checkGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            CleanupOldTempFiles();

            if (TryLoadExistingStaged(out Version? existingVer, out string? existingPath, out string? existingSha, out string? existingNotes) &&
                existingVer is not null && existingPath is not null &&
                existingVer > _currentVersion)
            {
                StagedVersion = existingVer;
                StagedExePath = existingPath;
                _stagedSha256 = existingSha;
                ReleaseNotes = existingNotes;
                UpdateReady?.Invoke(existingVer, existingNotes);
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (force)
            {
                if ((now - _lastInMemoryCheckAt) < ManualCheckCooldown)
                {
                    return;
                }
            }
            else
            {
                if ((now - _lastInMemoryCheckAt) < AutoCheckCooldown)
                {
                    return;
                }
                DesktopAppConfig cfg = AppPaths.LoadOrDefaultConfig(_paths, _currentVersion);
                if (cfg.LastUpdateCheckUtc.HasValue && (now - cfg.LastUpdateCheckUtc.Value) < AutoCheckCooldown)
                {
                    _lastInMemoryCheckAt = cfg.LastUpdateCheckUtc.Value;
                    return;
                }
            }

            _lastInMemoryCheckAt = now;
            try
            {
                DesktopAppConfig cfg = AppPaths.LoadOrDefaultConfig(_paths, _currentVersion);
                cfg.LastUpdateCheckUtc = now;
                AppPaths.SaveConfig(_paths, cfg);
            }
            catch { }

            Directory.CreateDirectory(_updatesDir);

            using var manifestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            manifestCts.CancelAfter(TimeSpan.FromSeconds(18));
            string manifestUrl = $"{ManifestUrl}?t={now.ToUnixTimeSeconds()}";
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
                _stagedSha256 = expectedSha256;
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
            LogUpdater($"Update staged: v{remoteVersion} at {targetExePath}");

            StagedVersion = remoteVersion;
            StagedExePath = targetExePath;
            _stagedSha256 = expectedSha256;
            ReleaseNotes = notes;
            UpdateReady?.Invoke(remoteVersion, notes);
        }
        catch
        {
            // Never interrupt playback or startup if offline/unreachable
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private void LogUpdater(string message)
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDir);
            string logFile = Path.Combine(_paths.LogsDir, "updater.log");
            File.AppendAllText(logFile, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>
    /// Applies the verified staged update in-place using Windows NTFS native live executable rename
    /// (100% in-process file operations, zero external scripts, zero AV behavioral false positives).
    /// </summary>
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
            if (!string.IsNullOrWhiteSpace(_stagedSha256) &&
                !VerifyFileIntegrity(StagedExePath, 0, _stagedSha256))
            {
                TryDeleteFile(_stagedMetaPath);
                Interlocked.Exchange(ref _applyTriggered, 0);
                return false;
            }

            string? currentExe = Environment.ProcessPath;
            if (!IsUpdatableProcessPath(currentExe))
            {
                if (IsUpdatableProcessPath(_paths.InstalledExePath))
                {
                    currentExe = _paths.InstalledExePath;
                }
                else
                {
                    Interlocked.Exchange(ref _applyTriggered, 0);
                    return false;
                }
            }

            string oldExePath = currentExe! + ".old";
            TryDeleteFile(oldExePath);

            // NTFS allows renaming a running executable within the same directory
            File.Move(currentExe!, oldExePath, overwrite: true);
            try
            {
                File.Move(StagedExePath, currentExe!, overwrite: true);
            }
            catch
            {
                if (!File.Exists(currentExe!) && File.Exists(oldExePath))
                {
                    try { File.Move(oldExePath, currentExe!, overwrite: true); } catch { }
                }
                Interlocked.Exchange(ref _applyTriggered, 0);
                return false;
            }

            TryDeleteFile(_stagedMetaPath);
            LogUpdater($"Update applied natively in-place to {currentExe} (v{StagedVersion})");

            if (relaunch && File.Exists(currentExe!))
            {
                Process.Start(new ProcessStartInfo(currentExe!)
                {
                    WorkingDirectory = Path.GetDirectoryName(currentExe!) ?? _paths.InstallDir,
                    UseShellExecute = true
                });
            }

            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _applyTriggered, 0);
            return false;
        }
    }

    private bool TryLoadExistingStaged(out Version? version, out string? exePath, out string? sha256, out string? notes)
    {
        version = null;
        exePath = null;
        sha256 = null;
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
            sha256 = sha;
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
            if (Directory.Exists(_updatesDir))
            {
                foreach (string part in Directory.EnumerateFiles(_updatesDir, "*.part"))
                {
                    TryDeleteFile(part);
                }
            }
            string? currentExe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(currentExe))
            {
                TryDeleteFile(currentExe + ".bak");
                TryDeleteFile(currentExe + ".old");
            }
            if (Directory.Exists(_paths.InstallDir))
            {
                foreach (string oldFile in Directory.EnumerateFiles(_paths.InstallDir, "*.old"))
                {
                    TryDeleteFile(oldFile);
                }
                foreach (string bakFile in Directory.EnumerateFiles(_paths.InstallDir, "*.bak"))
                {
                    TryDeleteFile(bakFile);
                }
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
        _checkGate.Dispose();
    }
}
