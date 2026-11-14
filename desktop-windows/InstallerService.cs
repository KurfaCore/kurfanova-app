using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace KurfanovaMuzik;

public sealed record ExistingInstallInfo(
    bool Found,
    string? PreviousInstallDir,
    string? ExistingExePath,
    string? ExistingVersion,
    string[] ExtraOldCopyPaths,
    int RunningOtherProcesses);

public sealed class InstallOptions
{
    public string TargetInstallDir { get; set; } = AppPaths.DefaultInstallDir;
    public string? PreviousInstallDir { get; set; }
    public bool DesktopShortcut { get; set; } = true;
    public bool StartMenuShortcut { get; set; } = true;
    public bool RunOnStartup { get; set; } = false;
    public bool CleanupOldCopies { get; set; } = true;
    public bool DeleteSourceInstallerAfterExit { get; set; } = false;
}

public static class InstallerService
{
    public const string RegistryBrandKey = @"Software\KurfaCore\KurfanovaMuzik";
    public const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KurfanovaMuzik";
    public const string RunRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupRegValueName = "Kurfamüzik";
    public const string AppDisplayName = "Kurfamüzik";
    public const string DesktopShortcutFileName = "Kurfamüzik.lnk";

    public static ExistingInstallInfo DetectExistingInstallation()
    {
        string? previousInstallDir = null;
        string? existingExePath = null;
        string? existingVersion = null;
        var extraOldCopies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Check Brand Key in Registry
        try
        {
            using RegistryKey? brandKey = Registry.CurrentUser.OpenSubKey(RegistryBrandKey, writable: false);
            if (brandKey?.GetValue("InstallLocation") is string brandLoc && !string.IsNullOrWhiteSpace(brandLoc))
            {
                previousInstallDir = Path.GetFullPath(brandLoc.Trim());
            }
            if (brandKey?.GetValue("Version") is string brandVer && !string.IsNullOrWhiteSpace(brandVer))
            {
                existingVersion = brandVer.Trim();
            }
        }
        catch { }

        // 2. Check Uninstall Key in Registry
        try
        {
            using RegistryKey? uninstKey = Registry.CurrentUser.OpenSubKey(UninstallRegKey, writable: false);
            if (string.IsNullOrWhiteSpace(previousInstallDir) &&
                uninstKey?.GetValue("InstallLocation") is string uninstLoc &&
                !string.IsNullOrWhiteSpace(uninstLoc))
            {
                previousInstallDir = Path.GetFullPath(uninstLoc.Trim());
            }
            if (string.IsNullOrWhiteSpace(existingVersion) &&
                uninstKey?.GetValue("DisplayVersion") is string uninstVer &&
                !string.IsNullOrWhiteSpace(uninstVer))
            {
                existingVersion = uninstVer.Trim();
            }
        }
        catch { }

        // 3. Check known directories: %LocalAppData%\KurfanovaMuzik and %LocalAppData%\Programs\KurfanovaMuzik
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string defaultDir = Path.Combine(localAppData, "KurfanovaMuzik");
        string programsDir = Path.Combine(localAppData, "Programs", "KurfanovaMuzik");

        string[] candidatePaths =
        [
            Path.Combine(defaultDir, "KurfanovaMuzik.exe"),
            Path.Combine(defaultDir, "Kurfanova-Muzik-Windows.exe")
        ];

        foreach (string candidate in candidatePaths)
        {
            if (File.Exists(candidate))
            {
                if (existingExePath is null)
                {
                    existingExePath = candidate;
                    if (string.IsNullOrWhiteSpace(previousInstallDir))
                    {
                        previousInstallDir = Path.GetDirectoryName(candidate);
                    }
                }
                else
                {
                    extraOldCopies.Add(candidate);
                }
            }
        }

        if (Directory.Exists(programsDir))
        {
            try
            {
                foreach (string exe in Directory.EnumerateFiles(programsDir, "*.exe"))
                {
                    if (existingExePath is null)
                    {
                        existingExePath = exe;
                        if (string.IsNullOrWhiteSpace(previousInstallDir))
                        {
                            previousInstallDir = programsDir;
                        }
                    }
                    else
                    {
                        extraOldCopies.Add(exe);
                    }
                }
            }
            catch { }
        }

        // 4. Check Desktop for Kurfanova*.exe (excluding current process)
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string? currentProcess = Environment.ProcessPath;
            if (Directory.Exists(desktop))
            {
                foreach (string exe in Directory.EnumerateFiles(desktop, "Kurfanova*.exe"))
                {
                    if (!string.Equals(exe, currentProcess, StringComparison.OrdinalIgnoreCase))
                    {
                        extraOldCopies.Add(exe);
                    }
                }
            }
        }
        catch { }

        // If version not in registry, inspect file version info
        if (existingVersion is null && existingExePath is not null && File.Exists(existingExePath))
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(existingExePath);
                if (!string.IsNullOrWhiteSpace(fvi.FileVersion))
                {
                    existingVersion = fvi.FileVersion;
                }
                else if (!string.IsNullOrWhiteSpace(fvi.ProductVersion))
                {
                    existingVersion = fvi.ProductVersion;
                }
            }
            catch { }
        }

        // Count running processes
        int runningOtherProcesses = 0;
        try
        {
            int myPid = Environment.ProcessId;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != myPid &&
                        (p.ProcessName.Contains("Kurfanova", StringComparison.OrdinalIgnoreCase) ||
                         p.ProcessName.Contains("Kurfamuzik", StringComparison.OrdinalIgnoreCase) ||
                         p.ProcessName.Contains("Kurfamüzik", StringComparison.OrdinalIgnoreCase)))
                    {
                        runningOtherProcesses++;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        bool found = !string.IsNullOrWhiteSpace(previousInstallDir) ||
                     !string.IsNullOrWhiteSpace(existingExePath) ||
                     extraOldCopies.Count > 0;

        return new ExistingInstallInfo(
            found,
            previousInstallDir,
            existingExePath,
            existingVersion,
            extraOldCopies.ToArray(),
            runningOtherProcesses);
    }

    public static void StopOtherRunningInstances()
    {
        int myPid = Environment.ProcessId;
        var toKill = new List<Process>();

        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == myPid)
                    {
                        p.Dispose();
                        continue;
                    }

                    bool match = false;
                    if (p.ProcessName.Contains("KurfanovaMuzik", StringComparison.OrdinalIgnoreCase) ||
                        p.ProcessName.Contains("Kurfanova-Muzik-Windows", StringComparison.OrdinalIgnoreCase) ||
                        p.ProcessName.Contains("Kurfamüzik", StringComparison.OrdinalIgnoreCase) ||
                        p.ProcessName.Contains("Kurfamuzik", StringComparison.OrdinalIgnoreCase))
                    {
                        match = true;
                    }
                    else
                    {
                        try
                        {
                            string? module = p.MainModule?.FileName;
                            if (!string.IsNullOrWhiteSpace(module) &&
                                (module.Contains("KurfanovaMuzik", StringComparison.OrdinalIgnoreCase) ||
                                 module.Contains("Kurfanova-Muzik-Windows", StringComparison.OrdinalIgnoreCase)))
                            {
                                match = true;
                            }
                        }
                        catch { }
                    }

                    if (match)
                    {
                        toKill.Add(p);
                    }
                    else
                    {
                        p.Dispose();
                    }
                }
                catch
                {
                    p.Dispose();
                }
            }

            foreach (var p in toKill)
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }
    }

    public static void InstallOrUpgrade(InstallOptions opts, Action<string>? onProgress = null)
    {
        // 1. Stop other running instances
        onProgress?.Invoke("Çalışan diğer Kurfamüzik işlemleri durduruluyor...");
        StopOtherRunningInstances();

        // 2. Prepare target directories
        onProgress?.Invoke("Kurulum klasörleri hazırlanıyor...");
        var targetPaths = new AppPaths(opts.TargetInstallDir);
        Directory.CreateDirectory(targetPaths.InstallDir);
        Directory.CreateDirectory(targetPaths.DataDir);
        Directory.CreateDirectory(targetPaths.WebView2DataDir);
        Directory.CreateDirectory(targetPaths.UpdatesDir);
        Directory.CreateDirectory(targetPaths.LogsDir);

        // 3. Preserve & migrate user data from old flat paths if applicable
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string legacyAppData = Path.Combine(localAppData, "KurfanovaMuzik");

        string oldFlatWebView2 = Path.Combine(legacyAppData, "WebView2");
        if (!string.Equals(Path.GetFullPath(oldFlatWebView2), Path.GetFullPath(targetPaths.WebView2DataDir), StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(oldFlatWebView2))
        {
            try
            {
                if (!Directory.Exists(targetPaths.WebView2DataDir) || Directory.GetFileSystemEntries(targetPaths.WebView2DataDir).Length == 0)
                {
                    CopyDirectory(oldFlatWebView2, targetPaths.WebView2DataDir);
                    try { Directory.Delete(oldFlatWebView2, recursive: true); } catch { }
                }
            }
            catch { }
        }

        string oldFlatUpdates = Path.Combine(legacyAppData, "updates");
        if (!string.Equals(Path.GetFullPath(oldFlatUpdates), Path.GetFullPath(targetPaths.UpdatesDir), StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(oldFlatUpdates))
        {
            try
            {
                if (!Directory.Exists(targetPaths.UpdatesDir) || Directory.GetFileSystemEntries(targetPaths.UpdatesDir).Length == 0)
                {
                    CopyDirectory(oldFlatUpdates, targetPaths.UpdatesDir);
                    try { Directory.Delete(oldFlatUpdates, recursive: true); } catch { }
                }
            }
            catch { }
        }

        // 4. Clean old binaries inside targetPaths.InstallDir
        onProgress?.Invoke("Eski ikili dosyalar temizleniyor...");
        try
        {
            string targetExeName = Path.GetFileName(targetPaths.InstalledExePath);
            foreach (string file in Directory.EnumerateFiles(targetPaths.InstallDir))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("config.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("kurfanova.ico", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // If already running from installed exe, avoid deleting self
                if (name.Equals(targetExeName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Environment.ProcessPath, targetPaths.InstalledExePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (name.StartsWith("Kurfanova", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals(targetExeName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
                else if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }

        // Clean previous install location if different and cleanup requested
        if (opts.CleanupOldCopies &&
            !string.IsNullOrWhiteSpace(opts.PreviousInstallDir) &&
            !string.Equals(Path.GetFullPath(opts.PreviousInstallDir), Path.GetFullPath(targetPaths.InstallDir), StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(opts.PreviousInstallDir))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(opts.PreviousInstallDir, "Kurfanova*.exe"))
                {
                    if (!string.Equals(file, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                string oldIco = Path.Combine(opts.PreviousInstallDir, "kurfanova.ico");
                if (File.Exists(oldIco)) try { File.Delete(oldIco); } catch { }
            }
            catch { }
        }

        // 5. Clean old Desktop copies and old shortcuts if requested
        if (opts.CleanupOldCopies)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (Directory.Exists(desktop))
                {
                    foreach (string exe in Directory.EnumerateFiles(desktop, "Kurfanova*.exe"))
                    {
                        if (!string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(exe); } catch { }
                        }
                    }
                    foreach (string lnk in Directory.EnumerateFiles(desktop, "Kurfanova*.lnk"))
                    {
                        if (!Path.GetFileName(lnk).Equals(DesktopShortcutFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(lnk); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        // 6. Copy Environment.ProcessPath cleanly to targetPaths.InstalledExePath
        onProgress?.Invoke("Kurfamüzik dosyaları kopyalanıyor...");
        string? currentExe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentExe) &&
            !string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetPaths.InstalledExePath), StringComparison.OrdinalIgnoreCase))
        {
            string tempNewExe = targetPaths.InstalledExePath + ".new";
            bool copied = false;
            Exception? lastEx = null;

            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    File.Copy(currentExe, tempNewExe, overwrite: true);
                    File.Move(tempNewExe, targetPaths.InstalledExePath, overwrite: true);
                    copied = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    Thread.Sleep(400);
                }
            }

            if (!copied && lastEx is not null)
            {
                throw new IOException($"Uygulama dosyası kopyalanamadı: {lastEx.Message}", lastEx);
            }
        }

        // Extract embedded icon to targetPaths.IconPath
        try
        {
            using Stream? iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KurfanovaMuzik.app.ico");
            if (iconStream is not null)
            {
                using var fs = new FileStream(targetPaths.IconPath, FileMode.Create, FileAccess.Write, FileShare.None);
                iconStream.CopyTo(fs);
            }
        }
        catch { }

        // 7. Create DesktopAppConfig and save config.json
        onProgress?.Invoke("Yapılandırma ayarları kaydediliyor...");
        var cfg = new DesktopAppConfig
        {
            InstallDir = targetPaths.InstallDir,
            DesktopShortcut = opts.DesktopShortcut,
            StartMenuShortcut = opts.StartMenuShortcut,
            RunOnStartup = opts.RunOnStartup,
            InstalledVersion = AppPaths.GetNormalizedCurrentVersion().ToString(3),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        AppPaths.SaveConfig(targetPaths, cfg);

        // 8. Apply shortcuts and registry integration
        onProgress?.Invoke("Kısayollar ve sistem kaydı oluşturuluyor...");
        ApplyShortcutsAndRegistry(targetPaths, cfg, AppPaths.GetNormalizedCurrentVersion());

        // 9. Write install log line
        try
        {
            string logFile = Path.Combine(targetPaths.LogsDir, "app.log");
            string logLine = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Kurfamüzik v{cfg.InstalledVersion} installed to {targetPaths.InstallDir}{Environment.NewLine}";
            File.AppendAllText(logFile, logLine, Encoding.UTF8);
        }
        catch { }

        // 10. Launch installed EXE
        onProgress?.Invoke("Kurfamüzik başlatılıyor...");
        Process.Start(new ProcessStartInfo(targetPaths.InstalledExePath)
        {
            WorkingDirectory = targetPaths.InstallDir,
            UseShellExecute = true
        });

        // 11. Schedule deletion of source installer if requested
        if (opts.DeleteSourceInstallerAfterExit &&
            !string.IsNullOrWhiteSpace(currentExe) &&
            !string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetPaths.InstalledExePath), StringComparison.OrdinalIgnoreCase))
        {
            ScheduleDeleteSourceInstaller(currentExe);
        }
    }

    public static void ApplyShortcutsAndRegistry(AppPaths paths, DesktopAppConfig cfg, Version currentVer)
    {
        string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string desktopLnk = Path.Combine(desktopDir, DesktopShortcutFileName);

        string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Kurfamüzik");
        string startMenuMainLnk = Path.Combine(startMenuDir, "Kurfamüzik.lnk");
        string startMenuUninstallLnk = Path.Combine(startMenuDir, "Kurfamüzik Kaldır.lnk");

        string psScript = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $wsh = New-Object -ComObject WScript.Shell

            # Desktop Shortcut
            $desktopLnk = '{{EscapePsSingleQuote(desktopLnk)}}'
            if ('{{(cfg.DesktopShortcut ? "1" : "0")}}' -eq '1') {
                $sc = $wsh.CreateShortcut($desktopLnk)
                $sc.TargetPath = '{{EscapePsSingleQuote(paths.InstalledExePath)}}'
                $sc.WorkingDirectory = '{{EscapePsSingleQuote(paths.InstallDir)}}'
                $sc.IconLocation = '{{EscapePsSingleQuote(paths.IconPath)}},0'
                $sc.Description = 'Kurfamüzik Masaüstü Uygulaması'
                $sc.Save()
            } else {
                if (Test-Path -LiteralPath $desktopLnk) {
                    Remove-Item -LiteralPath $desktopLnk -Force -ErrorAction SilentlyContinue
                }
            }

            # Start Menu Shortcuts
            $startMenuDir = '{{EscapePsSingleQuote(startMenuDir)}}'
            $startMenuMainLnk = '{{EscapePsSingleQuote(startMenuMainLnk)}}'
            $startMenuUninstallLnk = '{{EscapePsSingleQuote(startMenuUninstallLnk)}}'
            if ('{{(cfg.StartMenuShortcut ? "1" : "0")}}' -eq '1') {
                if (-not (Test-Path -LiteralPath $startMenuDir)) {
                    New-Item -ItemType Directory -LiteralPath $startMenuDir -Force | Out-Null
                }
                $scMain = $wsh.CreateShortcut($startMenuMainLnk)
                $scMain.TargetPath = '{{EscapePsSingleQuote(paths.InstalledExePath)}}'
                $scMain.WorkingDirectory = '{{EscapePsSingleQuote(paths.InstallDir)}}'
                $scMain.IconLocation = '{{EscapePsSingleQuote(paths.IconPath)}},0'
                $scMain.Description = 'Kurfamüzik Masaüstü Uygulaması'
                $scMain.Save()

                $scUninst = $wsh.CreateShortcut($startMenuUninstallLnk)
                $scUninst.TargetPath = '{{EscapePsSingleQuote(paths.InstalledExePath)}}'
                $scUninst.Arguments = '--uninstall'
                $scUninst.WorkingDirectory = '{{EscapePsSingleQuote(paths.InstallDir)}}'
                $scUninst.IconLocation = '{{EscapePsSingleQuote(paths.IconPath)}},0'
                $scUninst.Description = 'Kurfamüzik Masaüstü Uygulamasını Kaldır'
                $scUninst.Save()
            } else {
                if (Test-Path -LiteralPath $startMenuDir) {
                    Remove-Item -LiteralPath $startMenuDir -Recurse -Force -ErrorAction SilentlyContinue
                }
            }

            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            """;

        ExecutePsScript(psScript);

        // Registry: Run on Windows startup
        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, writable: true);
            if (runKey is not null)
            {
                if (cfg.RunOnStartup)
                {
                    runKey.SetValue(StartupRegValueName, $"\"{paths.InstalledExePath}\"");
                }
                else
                {
                    runKey.DeleteValue(StartupRegValueName, throwOnMissingValue: false);
                }
            }
        }
        catch { }

        // Registry: KurfaCore Brand Key
        try
        {
            using RegistryKey brandKey = Registry.CurrentUser.CreateSubKey(RegistryBrandKey, writable: true);
            brandKey.SetValue("InstallLocation", paths.InstallDir);
            brandKey.SetValue("Version", currentVer.ToString(3));
            brandKey.SetValue("DataDir", paths.DataDir);
            brandKey.SetValue("UpdatedAt", DateTimeOffset.UtcNow.ToString("O"));
        }
        catch { }

        // Registry: Windows Installed Apps (Uninstall key)
        try
        {
            using RegistryKey uninstKey = Registry.CurrentUser.CreateSubKey(UninstallRegKey, writable: true);
            uninstKey.SetValue("DisplayName", AppDisplayName);
            uninstKey.SetValue("DisplayVersion", currentVer.ToString(3));
            uninstKey.SetValue("Publisher", "KurfaCore");
            uninstKey.SetValue("DisplayIcon", paths.IconPath);
            uninstKey.SetValue("InstallLocation", paths.InstallDir);
            uninstKey.SetValue("UninstallString", $"\"{paths.InstalledExePath}\" --uninstall");
            uninstKey.SetValue("QuietUninstallString", $"\"{paths.InstalledExePath}\" --uninstall --silent");
            uninstKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
            uninstKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            long sizeInKb = 0;
            if (File.Exists(paths.InstalledExePath))
            {
                try
                {
                    sizeInKb = new FileInfo(paths.InstalledExePath).Length / 1024;
                }
                catch { }
            }
            uninstKey.SetValue("EstimatedSize", (int)Math.Max(1024, sizeInKb), RegistryValueKind.DWord);
        }
        catch { }
    }

    public static void SyncMetadataOnLaunch(AppPaths paths, DesktopAppConfig cfg, Version currentVer)
    {
        try
        {
            string verStr = currentVer.ToString(3);
            bool versionChanged = !string.Equals(cfg.InstalledVersion, verStr, StringComparison.OrdinalIgnoreCase);

            if (versionChanged)
            {
                cfg.InstalledVersion = verStr;
                cfg.UpdatedAt = DateTimeOffset.UtcNow;
                AppPaths.SaveConfig(paths, cfg);
            }

            using (RegistryKey brandKey = Registry.CurrentUser.CreateSubKey(RegistryBrandKey, writable: true))
            {
                brandKey.SetValue("InstallLocation", paths.InstallDir);
                brandKey.SetValue("Version", verStr);
                brandKey.SetValue("DataDir", paths.DataDir);
                brandKey.SetValue("UpdatedAt", DateTimeOffset.UtcNow.ToString("O"));
            }

            using (RegistryKey uninstKey = Registry.CurrentUser.CreateSubKey(UninstallRegKey, writable: true))
            {
                uninstKey.SetValue("DisplayName", AppDisplayName);
                uninstKey.SetValue("DisplayVersion", verStr);
                uninstKey.SetValue("Publisher", "KurfaCore");
                uninstKey.SetValue("DisplayIcon", paths.IconPath);
                uninstKey.SetValue("InstallLocation", paths.InstallDir);
                uninstKey.SetValue("UninstallString", $"\"{paths.InstalledExePath}\" --uninstall");
                uninstKey.SetValue("QuietUninstallString", $"\"{paths.InstalledExePath}\" --uninstall --silent");
                uninstKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
                uninstKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);

                if (File.Exists(paths.InstalledExePath))
                {
                    try
                    {
                        long sizeInKb = new FileInfo(paths.InstalledExePath).Length / 1024;
                        uninstKey.SetValue("EstimatedSize", (int)Math.Max(1024, sizeInKb), RegistryValueKind.DWord);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    public static void RunUninstall(bool silent)
    {
        bool purgeData = false;
        if (!silent)
        {
            DialogResult confirm = MessageBox.Show(
                "Kurfamüzik masaüstü uygulaması bilgisayardan kaldırılsın mı?",
                "Kurfamüzik Kaldır",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            DialogResult purgeChoice = MessageBox.Show(
                "Favori ve ayar önbellek verileri de silinsin mi?\n\n('Hayır' seçerseniz gelecekte yeniden kurduğunuzda verileriniz korunur.)",
                "Veri Temizleme",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            purgeData = (purgeChoice == DialogResult.Yes);
        }

        StopOtherRunningInstances();

        string activeInstallDir = AppPaths.ResolveActiveInstallDir();
        var paths = new AppPaths(activeInstallDir);

        // Remove shortcuts
        try
        {
            string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), DesktopShortcutFileName);
            if (File.Exists(desktopLnk)) File.Delete(desktopLnk);
        }
        catch { }

        try
        {
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Kurfamüzik");
            if (Directory.Exists(startMenuDir)) Directory.Delete(startMenuDir, recursive: true);
        }
        catch { }

        // Remove registry values
        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, writable: true);
            runKey?.DeleteValue(StartupRegValueName, throwOnMissingValue: false);
        }
        catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, throwOnMissingSubKey: false);
        }
        catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryBrandKey, throwOnMissingSubKey: false);
        }
        catch { }

        // Schedule background cleanup via PowerShell after PID exits
        try
        {
            string tempScript = Path.Combine(Path.GetTempPath(), $"kurfa_uninstall_{Guid.NewGuid():N}.ps1");
            string scriptBody = """
                $ErrorActionPreference = 'SilentlyContinue'
                $targetPid = [int]$env:KURFA_UNINST_PID
                $dir = $env:KURFA_UNINST_DIR
                $purge = $env:KURFA_UNINST_PURGE -eq '1'

                for ($i = 0; $i -lt 30; $i++) {
                    $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
                    if (-not $proc) { break }
                    Start-Sleep -Milliseconds 500
                }

                if (Test-Path -LiteralPath $dir) {
                    Remove-Item -LiteralPath "$dir\KurfanovaMuzik.exe" -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath "$dir\Kurfanova-Muzik-Windows.exe" -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath "$dir\kurfanova.ico" -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath "$dir\config.json" -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath "$dir\Updates" -Recurse -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath "$dir\Logs" -Recurse -Force -ErrorAction SilentlyContinue

                    if ($purge) {
                        Remove-Item -LiteralPath "$dir\Data" -Recurse -Force -ErrorAction SilentlyContinue
                        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
                    } else {
                        $items = Get-ChildItem -LiteralPath $dir -ErrorAction SilentlyContinue
                        if (-not $items) {
                            Remove-Item -LiteralPath $dir -Force -ErrorAction SilentlyContinue
                        }
                    }
                }

                Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
                """;

            File.WriteAllText(tempScript, scriptBody, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.Environment["KURFA_UNINST_PID"] = Environment.ProcessId.ToString();
            psi.Environment["KURFA_UNINST_DIR"] = paths.InstallDir;
            psi.Environment["KURFA_UNINST_PURGE"] = purgeData ? "1" : "0";

            Process.Start(psi);
        }
        catch { }

        if (!silent)
        {
            MessageBox.Show(
                "Kurfamüzik başarıyla kaldırıldı.",
                "Kurfamüzik Kaldırıldı",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        Environment.Exit(0);
    }

    private static void ScheduleDeleteSourceInstaller(string sourceExePath)
    {
        try
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"kurfa_del_src_{Guid.NewGuid():N}.ps1");
            string scriptBody = """
                $ErrorActionPreference = 'SilentlyContinue'
                $targetPid = [int]$env:KURFA_SRC_PID
                $exe = $env:KURFA_SRC_EXE

                for ($i = 0; $i -lt 30; $i++) {
                    $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
                    if (-not $proc) { break }
                    Start-Sleep -Milliseconds 500
                }

                for ($attempt = 0; $attempt -lt 10; $attempt++) {
                    try {
                        if (Test-Path -LiteralPath $exe) {
                            Remove-Item -LiteralPath $exe -Force -ErrorAction Stop
                        }
                        break
                    } catch {
                        Start-Sleep -Milliseconds 500
                    }
                }

                Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
                """;

            File.WriteAllText(scriptPath, scriptBody, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.Environment["KURFA_SRC_PID"] = Environment.ProcessId.ToString();
            psi.Environment["KURFA_SRC_EXE"] = sourceExePath;

            Process.Start(psi);
        }
        catch { }
    }

    private static void ExecutePsScript(string scriptBody)
    {
        try
        {
            string tempScript = Path.Combine(Path.GetTempPath(), $"kurfa_ps_{Guid.NewGuid():N}.ps1");
            File.WriteAllText(tempScript, scriptBody, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using Process? proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            if (File.Exists(tempScript))
            {
                try { File.Delete(tempScript); } catch { }
            }
        }
        catch { }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string targetFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: true);
        }
        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            string targetSubDir = Path.Combine(destinationDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, targetSubDir);
        }
    }

    private static string EscapePsSingleQuote(string value) => value.Replace("'", "''");
}
