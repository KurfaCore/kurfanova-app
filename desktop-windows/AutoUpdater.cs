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

    private static readonly TimeSpan AutoCheckCooldown = TimeSpan.FromHours(2);
    private static readonly TimeSpan ManualCheckCooldown = TimeSpan.FromSeconds(2);
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly AppPaths _paths;
    private readonly string _updatesDir;
    private readonly string _stagedMetaPath;
    private readonly Version _currentVersion;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private DateTimeOffset _lastInMemoryCheckAt = DateTimeOffset.MinValue;
    private PendingRemoteManifest? _pendingRemote;
    private string? _stagedSha256;
    private long _stagedExpectedSize;
    private bool _stagedIsInstaller;
    private int _applyTriggered;

    private sealed record PendingRemoteManifest(
        Version Version,
        string DownloadUrl,
        string? Sha256,
        long ExpectedSize,
        string? Notes,
        bool IsInstaller);

    public string? StagedExePath { get; private set; }
    public Version? StagedVersion { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public bool HasStagedUpdate => !string.IsNullOrWhiteSpace(StagedExePath) && File.Exists(StagedExePath);

    public sealed record UpdateCheckResult(
        string Status, // "up-to-date" | "available" | "staged" | "error" | "skipped"
        Version CurrentVersion,
        Version? RemoteVersion,
        long SizeBytes,
        string? Notes,
        string? ErrorMessage = null);

    public sealed record UpdateProgressInfo(
        string Phase, // "available" | "downloading" | "verifying" | "ready" | "error" | "up-to-date" | "hide"
        Version Version,
        string? Notes,
        long BytesDownloaded,
        long TotalBytes,
        int Percent,
        string? ErrorMessage = null);

    public event Action<Version, string?>? UpdateReady;
    public event Action<UpdateProgressInfo>? UpdateProgressChanged;

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
            Timeout = TimeSpan.FromMinutes(4)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KurfanovaMuzik-Updater/2.0");
        return client;
    }

    /// <summary>
    /// Fast manifest check (< 2s) without silently downloading ~70 MB behind the user's back.
    /// Reports whether an update is available and waits for user confirmation before downloading.
    /// </summary>
    public async Task<UpdateCheckResult> CheckManifestAsync(CancellationToken ct, bool force = false, bool emitProgressEvents = true)
    {
        if (!await _checkGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new UpdateCheckResult("skipped", _currentVersion, StagedVersion, 0, ReleaseNotes);
        }

        try
        {
            CleanupOldTempFiles();

            // 1. If a verified update is already staged on disk and newer than running exe, surface it right away
            if (TryLoadExistingStaged(out Version? existingVer, out string? existingPath, out string? existingSha, out long existingSize, out bool existingIsInstaller, out string? existingNotes) &&
                existingVer is not null && existingPath is not null &&
                existingVer > _currentVersion)
            {
                long stagedLen = 0;
                try { stagedLen = new FileInfo(existingPath).Length; } catch { }
                StagedVersion = existingVer;
                StagedExePath = existingPath;
                _stagedSha256 = existingSha;
                _stagedExpectedSize = existingSize;
                _stagedIsInstaller = existingIsInstaller;
                ReleaseNotes = existingNotes;
                if (emitProgressEvents)
                {
                    UpdateProgressChanged?.Invoke(new UpdateProgressInfo("ready", existingVer, existingNotes, stagedLen, stagedLen, 100));
                    UpdateReady?.Invoke(existingVer, existingNotes);
                }
                return new UpdateCheckResult("staged", _currentVersion, existingVer, stagedLen, existingNotes);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (force)
            {
                if ((now - _lastInMemoryCheckAt) < ManualCheckCooldown && _pendingRemote is not null)
                {
                    return new UpdateCheckResult("available", _currentVersion, _pendingRemote.Version, _pendingRemote.ExpectedSize, _pendingRemote.Notes);
                }
            }
            else
            {
                if ((now - _lastInMemoryCheckAt) < AutoCheckCooldown)
                {
                    return new UpdateCheckResult("skipped", _currentVersion, null, 0, null);
                }
                DesktopAppConfig cfg = AppPaths.LoadOrDefaultConfig(_paths, _currentVersion);
                if (cfg.LastUpdateCheckUtc.HasValue && (now - cfg.LastUpdateCheckUtc.Value) < AutoCheckCooldown)
                {
                    _lastInMemoryCheckAt = cfg.LastUpdateCheckUtc.Value;
                    return new UpdateCheckResult("skipped", _currentVersion, null, 0, null);
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
            manifestCts.CancelAfter(TimeSpan.FromSeconds(15));
            string manifestUrl = $"{ManifestUrl}?t={now.ToUnixTimeSeconds()}";
            string manifestJson = await Http.GetStringAsync(manifestUrl, manifestCts.Token).ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(manifestJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new UpdateCheckResult("error", _currentVersion, null, 0, null, "Geçersiz güncelleme bildirimi.");
            }

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
            string? installerUrl = root.TryGetProperty("installerUrl", out JsonElement iuEl) && iuEl.ValueKind == JsonValueKind.String
                ? iuEl.GetString()
                : null;
            string? installerSha256 = root.TryGetProperty("installerSha256", out JsonElement isEl) && isEl.ValueKind == JsonValueKind.String
                ? isEl.GetString()?.Trim().ToLowerInvariant()
                : null;
            long installerSize = root.TryGetProperty("installerSize", out JsonElement iszEl) && iszEl.ValueKind == JsonValueKind.Number
                ? iszEl.GetInt64()
                : 0;
            string? notes = root.TryGetProperty("notes", out JsonElement nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(rawVersion) ||
                !TryParseNormalizedVersion(rawVersion, out Version? remoteVersion) ||
                remoteVersion is null)
            {
                return new UpdateCheckResult("error", _currentVersion, null, 0, null, "Sürüm verisi eksik veya bozuk.");
            }

            bool isInnoInstalled = AppPaths.IsInnoSetupInstalled;
            bool useInstaller = isInnoInstalled && !string.IsNullOrWhiteSpace(installerUrl);
            string? effectiveUrl = useInstaller ? installerUrl : downloadUrl;
            string? effectiveSha256 = useInstaller ? installerSha256 : expectedSha256;
            long effectiveSize = useInstaller ? installerSize : expectedSize;

            if (string.IsNullOrWhiteSpace(effectiveUrl) ||
                (!useInstaller && (string.IsNullOrWhiteSpace(effectiveSha256) || effectiveSha256.Length != 64)) ||
                (useInstaller && !string.IsNullOrWhiteSpace(effectiveSha256) && effectiveSha256.Length != 64))
            {
                return new UpdateCheckResult("error", _currentVersion, null, 0, null, "Sürüm verisi eksik veya bozuk.");
            }

            if (remoteVersion <= _currentVersion)
            {
                _pendingRemote = null;
                if (emitProgressEvents && force)
                {
                    UpdateProgressChanged?.Invoke(new UpdateProgressInfo("up-to-date", remoteVersion, null, 0, 0, 100));
                }
                return new UpdateCheckResult("up-to-date", _currentVersion, remoteVersion, 0, notes);
            }

            if (!IsTrustedReleaseUri(effectiveUrl))
            {
                return new UpdateCheckResult("error", _currentVersion, null, 0, null, "Güvenilmeyen güncelleme kaynağı.");
            }

            string targetExePath = useInstaller
                ? Path.Combine(_updatesDir, $"Kurfamuzik-Setup-{remoteVersion}.exe")
                : Path.Combine(_updatesDir, $"Kurfanova-Muzik-Windows-{remoteVersion}.exe");
            if (File.Exists(targetExePath) && VerifyFileIntegrity(targetExePath, effectiveSize, effectiveSha256))
            {
                long existingFileLen = 0;
                try { existingFileLen = new FileInfo(targetExePath).Length; } catch { }
                SaveStagedMeta(remoteVersion.ToString(), targetExePath, effectiveSha256, effectiveSize, useInstaller, notes);
                StagedVersion = remoteVersion;
                StagedExePath = targetExePath;
                _stagedSha256 = effectiveSha256;
                _stagedExpectedSize = effectiveSize;
                _stagedIsInstaller = useInstaller;
                ReleaseNotes = notes;
                if (emitProgressEvents)
                {
                    UpdateProgressChanged?.Invoke(new UpdateProgressInfo("ready", remoteVersion, notes, existingFileLen, existingFileLen, 100));
                    UpdateReady?.Invoke(remoteVersion, notes);
                }
                return new UpdateCheckResult("staged", _currentVersion, remoteVersion, existingFileLen, notes);
            }

            _pendingRemote = new PendingRemoteManifest(remoteVersion, effectiveUrl, effectiveSha256, effectiveSize, notes, useInstaller);
            if (emitProgressEvents)
            {
                UpdateProgressChanged?.Invoke(new UpdateProgressInfo("available", remoteVersion, notes, 0, effectiveSize, 0));
            }
            return new UpdateCheckResult("available", _currentVersion, remoteVersion, effectiveSize, notes);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult("error", _currentVersion, null, 0, null, "Bağlantı zaman aşımına uğradı.");
        }
        catch (Exception ex)
        {
            LogUpdater($"Manifest check error: {ex.Message}");
            if (emitProgressEvents && force)
            {
                UpdateProgressChanged?.Invoke(new UpdateProgressInfo("error", _currentVersion, null, 0, 0, 0, "Sunucuya erişilemedi."));
            }
            return new UpdateCheckResult("error", _currentVersion, null, 0, null, ex.Message);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>
    /// Downloads and SHA-256 verifies the approved pending update, reporting live progress to WebView UI.
    /// </summary>
    public async Task<bool> DownloadAndStageAsync(CancellationToken ct)
    {
        if (HasStagedUpdate && StagedVersion is not null && StagedVersion > _currentVersion)
        {
            long len = 0;
            try { len = new FileInfo(StagedExePath!).Length; } catch { }
            UpdateProgressChanged?.Invoke(new UpdateProgressInfo("ready", StagedVersion, ReleaseNotes, len, len, 100));
            UpdateReady?.Invoke(StagedVersion, ReleaseNotes);
            return true;
        }

        if (_pendingRemote is null)
        {
            UpdateCheckResult check = await CheckManifestAsync(ct, force: true, emitProgressEvents: false).ConfigureAwait(false);
            if (check.Status == "staged")
            {
                if (StagedVersion is not null)
                {
                    UpdateProgressChanged?.Invoke(new UpdateProgressInfo("ready", StagedVersion, ReleaseNotes, check.SizeBytes, check.SizeBytes, 100));
                    UpdateReady?.Invoke(StagedVersion, ReleaseNotes);
                }
                return true;
            }
            if (check.Status != "available" || _pendingRemote is null)
            {
                if (check.Status == "error")
                {
                    UpdateProgressChanged?.Invoke(new UpdateProgressInfo("error", _currentVersion, null, 0, 0, 0, check.ErrorMessage ?? "Güncelleme indirilemedi."));
                }
                return false;
            }
        }

        if (!await _checkGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return false;
        }

        PendingRemoteManifest manifest = _pendingRemote!;
        string targetExePath = manifest.IsInstaller
            ? Path.Combine(_updatesDir, $"Kurfamuzik-Setup-{manifest.Version}.exe")
            : Path.Combine(_updatesDir, $"Kurfanova-Muzik-Windows-{manifest.Version}.exe");
        string partPath = targetExePath + ".part";

        try
        {
            Directory.CreateDirectory(_updatesDir);
            UpdateProgressChanged?.Invoke(new UpdateProgressInfo("downloading", manifest.Version, manifest.Notes, 0, manifest.ExpectedSize, 0));

            long downloadedBytes = 0;
            long totalBytes = manifest.ExpectedSize;

            using (var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                dlCts.CancelAfter(TimeSpan.FromMinutes(4));
                using HttpResponseMessage response = await Http.GetAsync(
                    manifest.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    dlCts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    UpdateProgressChanged?.Invoke(new UpdateProgressInfo(
                        "error", manifest.Version, manifest.Notes, 0, totalBytes, 0,
                        $"İndirme başarısız (HTTP {(int)response.StatusCode})"));
                    return false;
                }

                if (response.Content.Headers.ContentLength is long cl && cl > 0)
                {
                    totalBytes = cl;
                }

                await using Stream netStream = await response.Content.ReadAsStreamAsync(dlCts.Token).ConfigureAwait(false);
                await using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    byte[] buffer = new byte[81920];
                    long lastEmitMs = 0;
                    int read;
                    while ((read = await netStream.ReadAsync(buffer.AsMemory(0, buffer.Length), dlCts.Token).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), dlCts.Token).ConfigureAwait(false);
                        downloadedBytes += read;

                        long nowMs = Environment.TickCount64;
                        if (nowMs - lastEmitMs >= 100)
                        {
                            lastEmitMs = nowMs;
                            int pct = totalBytes > 0 ? Math.Clamp((int)((downloadedBytes * 100L) / totalBytes), 0, 99) : 0;
                            UpdateProgressChanged?.Invoke(new UpdateProgressInfo("downloading", manifest.Version, manifest.Notes, downloadedBytes, totalBytes, pct));
                        }
                    }
                }
            }

            long finalSize = totalBytes > 0 ? totalBytes : downloadedBytes;
            UpdateProgressChanged?.Invoke(new UpdateProgressInfo("verifying", manifest.Version, manifest.Notes, downloadedBytes, finalSize, 100));

            if (!VerifyFileIntegrity(partPath, manifest.ExpectedSize, manifest.Sha256))
            {
                TryDeleteFile(partPath);
                LogUpdater($"SHA-256 verification failed for v{manifest.Version}");
                UpdateProgressChanged?.Invoke(new UpdateProgressInfo(
                    "error", manifest.Version, manifest.Notes, 0, finalSize, 0,
                    "İndirilen dosya doğrulamadan geçemedi (SHA-256 uyuşmazlığı)."));
                return false;
            }

            File.Move(partPath, targetExePath, overwrite: true);
            SaveStagedMeta(manifest.Version.ToString(), targetExePath, manifest.Sha256, finalSize, manifest.IsInstaller, manifest.Notes);
            LogUpdater($"Update staged & verified: v{manifest.Version} at {targetExePath} (installer={manifest.IsInstaller})");

            StagedVersion = manifest.Version;
            StagedExePath = targetExePath;
            _stagedSha256 = manifest.Sha256;
            _stagedExpectedSize = finalSize;
            _stagedIsInstaller = manifest.IsInstaller;
            ReleaseNotes = manifest.Notes;
            _pendingRemote = null;

            UpdateProgressChanged?.Invoke(new UpdateProgressInfo("ready", manifest.Version, manifest.Notes, finalSize, finalSize, 100));
            UpdateReady?.Invoke(manifest.Version, manifest.Notes);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partPath);
            UpdateProgressChanged?.Invoke(new UpdateProgressInfo("error", manifest.Version, manifest.Notes, 0, 0, 0, "İndirme iptal edildi veya zaman aşımına uğradı."));
            return false;
        }
        catch (Exception ex)
        {
            TryDeleteFile(partPath);
            LogUpdater($"DownloadAndStage error: {ex.Message}");
            UpdateProgressChanged?.Invoke(new UpdateProgressInfo("error", manifest.Version, manifest.Notes, 0, 0, 0, $"İndirme hatası: {ex.Message}"));
            return false;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    public async Task CheckAndStageAsync(CancellationToken ct, bool force = false)
    {
        UpdateCheckResult check = await CheckManifestAsync(ct, force, emitProgressEvents: true).ConfigureAwait(false);
        if (check.Status == "available")
        {
            // Keep UI on approval phase; user confirmation calls DownloadAndStageAsync
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
    /// Applies the verified staged update cleanly after MainForm and SingleInstanceMutex are released.
    /// Uses retry-guarded NTFS rename + cross-volume File.Copy fallback and passes --post-update-pid to the new instance.
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
            if (!VerifyFileIntegrity(StagedExePath, _stagedExpectedSize, _stagedSha256))
            {
                TryDeleteFile(_stagedMetaPath);
                Interlocked.Exchange(ref _applyTriggered, 0);
                LogUpdater("TryApplyPendingUpdate aborted: staged file integrity check failed.");
                return false;
            }

            if (_stagedIsInstaller ||
                Path.GetFileName(StagedExePath).StartsWith("Kurfamuzik-Setup", StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(_stagedMetaPath);
                string silentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS";
                if (relaunch)
                {
                    silentArgs += " /RELAUNCH=1";
                }
                LogUpdater($"Launching Inno Setup silent updater: {StagedExePath} {silentArgs}");
                Process.Start(new ProcessStartInfo(StagedExePath)
                {
                    Arguments = silentArgs,
                    WorkingDirectory = Path.GetDirectoryName(StagedExePath) ?? _updatesDir,
                    UseShellExecute = true
                });
                return true;
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
                    LogUpdater($"TryApplyPendingUpdate aborted: non-updatable exe path '{currentExe}'.");
                    return false;
                }
            }

            string oldExePath = currentExe! + ".old";
            TryDeleteFile(oldExePath);

            // Retry move briefly to tolerate Defender scan or slow WebView2 handle teardown
            bool movedOld = false;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    File.Move(currentExe!, oldExePath, overwrite: true);
                    movedOld = true;
                    break;
                }
                catch when (attempt < 5)
                {
                    Thread.Sleep(150);
                }
            }

            if (!movedOld)
            {
                Interlocked.Exchange(ref _applyTriggered, 0);
                LogUpdater($"Failed to rename running exe '{currentExe}' to .old");
                return false;
            }

            try
            {
                bool movedNew = false;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        File.Move(StagedExePath, currentExe!, overwrite: true);
                        movedNew = true;
                        break;
                    }
                    catch (IOException) when (attempt >= 2)
                    {
                        // Cross-volume or transient lock fallback: copy then delete staged
                        File.Copy(StagedExePath, currentExe!, overwrite: true);
                        TryDeleteFile(StagedExePath);
                        movedNew = true;
                        break;
                    }
                    catch when (attempt < 4)
                    {
                        Thread.Sleep(150);
                    }
                }
                if (!movedNew) throw new IOException("Could not place staged exe.");
            }
            catch (Exception ex)
            {
                LogUpdater($"Failed placing staged exe: {ex.Message}. Rolling back.");
                if (!File.Exists(currentExe!) && File.Exists(oldExePath))
                {
                    try { File.Move(oldExePath, currentExe!, overwrite: true); } catch { }
                }
                Interlocked.Exchange(ref _applyTriggered, 0);
                return false;
            }

            TryDeleteFile(_stagedMetaPath);
            LogUpdater($"Update applied successfully to {currentExe} (v{StagedVersion})");

            if (relaunch && File.Exists(currentExe!))
            {
                int currentPid = Environment.ProcessId;
                Process.Start(new ProcessStartInfo(currentExe!)
                {
                    Arguments = $"--post-update-pid {currentPid}",
                    WorkingDirectory = Path.GetDirectoryName(currentExe!) ?? _paths.InstallDir,
                    UseShellExecute = true
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            LogUpdater($"TryApplyPendingUpdate fatal error: {ex.Message}");
            Interlocked.Exchange(ref _applyTriggered, 0);
            return false;
        }
    }

    private bool TryLoadExistingStaged(out Version? version, out string? exePath, out string? sha256, out long expectedSize, out bool isInstaller, out string? notes)
    {
        version = null;
        exePath = null;
        sha256 = null;
        expectedSize = 0;
        isInstaller = false;
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
            expectedSize = root.TryGetProperty("expectedSize", out JsonElement szEl) && szEl.ValueKind == JsonValueKind.Number
                ? szEl.GetInt64()
                : 0;
            isInstaller = root.TryGetProperty("isInstaller", out JsonElement insEl) &&
                          (insEl.ValueKind == JsonValueKind.True || insEl.ValueKind == JsonValueKind.False) &&
                          insEl.GetBoolean();
            notes = root.TryGetProperty("notes", out JsonElement nEl) ? nEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(vStr) ||
                string.IsNullOrWhiteSpace(path) ||
                (!isInstaller && string.IsNullOrWhiteSpace(sha)) ||
                !TryParseNormalizedVersion(vStr, out version) ||
                !File.Exists(path) ||
                !VerifyFileIntegrity(path, expectedSize, sha))
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

    private void SaveStagedMeta(string version, string filePath, string? sha256, long expectedSize, bool isInstaller, string? notes)
    {
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                version,
                filePath,
                sha256,
                expectedSize,
                isInstaller,
                notes
            });
            File.WriteAllText(_stagedMetaPath, json, Encoding.UTF8);
        }
        catch { }
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
        catch { }
    }

    private static bool VerifyFileIntegrity(string path, long expectedSize, string? expectedSha256)
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

            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                return true;
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
        catch { }
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
