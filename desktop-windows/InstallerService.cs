using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
}

public static class InstallerService
{
    public const string RegistryBrandKey = @"Software\KurfaCore\KurfanovaMuzik";
    public const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KurfanovaMuzik";
    public const string RunRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupRegValueName = "Kurfamüzik";
    public const string AppDisplayName = "Kurfamüzik";
    public const string DesktopShortcutFileName = "Kurfamüzik.lnk";

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    private static readonly string[] KnownProcessNames =
    [
        "KurfanovaMuzik",
        "Kurfanova-Muzik-Windows"
    ];

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([In, MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([In, MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [In, MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([In, MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    public static ExistingInstallInfo DetectExistingInstallation()
    {
        string? previousInstallDir = null;
        string? existingExePath = null;
        string? existingVersion = null;
        var extraOldCopies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        int runningOtherProcesses = CountOtherRunningInstances();

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

    private static int CountOtherRunningInstances()
    {
        int count = 0;
        int myPid = Environment.ProcessId;
        foreach (string procName in KnownProcessNames)
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName(procName))
                {
                    try
                    {
                        if (p.Id != myPid) count++;
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
        return count;
    }

    public static void StopOtherRunningInstances()
    {
        int myPid = Environment.ProcessId;
        foreach (string procName in KnownProcessNames)
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName(procName))
                {
                    try
                    {
                        if (p.Id == myPid) continue;

                        try
                        {
                            if (p.CloseMainWindow())
                            {
                                p.WaitForExit(1500);
                            }
                        }
                        catch { }

                        if (!p.HasExited)
                        {
                            p.Kill();
                            p.WaitForExit(3000);
                        }
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
    }

    public static void InstallOrUpgrade(InstallOptions opts, Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Çalışan eski Kurfamüzik işlemleri kapatılıyor...");
        StopOtherRunningInstances();

        onProgress?.Invoke("Sahiplenilen uygulama klasörü hazırlanıyor...");
        var targetPaths = new AppPaths(opts.TargetInstallDir);
        Directory.CreateDirectory(targetPaths.InstallDir);
        Directory.CreateDirectory(targetPaths.DataDir);
        Directory.CreateDirectory(targetPaths.WebView2DataDir);
        Directory.CreateDirectory(targetPaths.UpdatesDir);
        Directory.CreateDirectory(targetPaths.LogsDir);

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

        onProgress?.Invoke("Eski ikili dosyalar ve kalıntılar temizleniyor...");
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
                         name.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }

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

        onProgress?.Invoke("Resmi uygulama paketi kurulum klasörüne yerleştiriliyor...");
        string? currentExe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentExe) &&
            !string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetPaths.InstalledExePath), StringComparison.OrdinalIgnoreCase))
        {
            string tempNewExe = targetPaths.InstalledExePath + ".new";
            string oldRenameExe = targetPaths.InstalledExePath + ".old";
            bool copied = false;
            Exception? lastEx = null;

            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    File.Copy(currentExe, tempNewExe, overwrite: true);
                    if (File.Exists(targetPaths.InstalledExePath))
                    {
                        try
                        {
                            File.Delete(targetPaths.InstalledExePath);
                        }
                        catch
                        {
                            try { if (File.Exists(oldRenameExe)) File.Delete(oldRenameExe); } catch { }
                            File.Move(targetPaths.InstalledExePath, oldRenameExe, overwrite: true);
                        }
                    }
                    File.Move(tempNewExe, targetPaths.InstalledExePath, overwrite: true);
                    try { if (File.Exists(oldRenameExe)) File.Delete(oldRenameExe); } catch { }
                    copied = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    Thread.Sleep(350);
                }
            }

            if (!copied && lastEx is not null)
            {
                throw new IOException($"Uygulama dosyası kopyalanamadı: {lastEx.Message}", lastEx);
            }
        }

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

        onProgress?.Invoke("Yapılandırma ve kısayollar kaydediliyor...");
        var cfg = new DesktopAppConfig
        {
            InstallDir = targetPaths.InstallDir,
            DesktopShortcut = opts.DesktopShortcut,
            StartMenuShortcut = opts.StartMenuShortcut,
            RunOnStartup = opts.RunOnStartup,
            InstalledVersion = AppPaths.GetNormalizedCurrentVersion().ToString(3),
            UpdatedAt = DateTimeOffset.UtcNow,
            LastUpdateCheckUtc = DateTimeOffset.UtcNow
        };
        AppPaths.SaveConfig(targetPaths, cfg);

        ApplyShortcutsAndRegistry(targetPaths, cfg, AppPaths.GetNormalizedCurrentVersion());

        try
        {
            string logFile = Path.Combine(targetPaths.LogsDir, "app.log");
            string logLine = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Kurfamüzik v{cfg.InstalledVersion} installed to {targetPaths.InstallDir}{Environment.NewLine}";
            File.AppendAllText(logFile, logLine, Encoding.UTF8);
        }
        catch { }

        onProgress?.Invoke("Kurfamüzik başlatılıyor...");
        Process.Start(new ProcessStartInfo(targetPaths.InstalledExePath)
        {
            WorkingDirectory = targetPaths.InstallDir,
            UseShellExecute = true
        });
    }

    private static void CreateShortcutNative(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string iconPath,
        string description,
        string? arguments = null)
    {
        object? linkObj = null;
        try
        {
            string? parentDir = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            linkObj = new ShellLink();
            var link = (IShellLinkW)linkObj;
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                link.SetArguments(arguments);
            }
            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            {
                link.SetIconLocation(iconPath, 0);
            }

            var persistFile = (IPersistFile)linkObj;
            persistFile.Save(shortcutPath, true);
        }
        catch
        {
            // Ignore shortcut creation errors on restricted shell environments
        }
        finally
        {
            if (linkObj is not null && Marshal.IsComObject(linkObj))
            {
                try { Marshal.FinalReleaseComObject(linkObj); } catch { }
            }
        }
    }

    public static void ApplyShortcutsAndRegistry(AppPaths paths, DesktopAppConfig cfg, Version currentVer)
    {
        string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string desktopLnk = Path.Combine(desktopDir, DesktopShortcutFileName);

        if (cfg.DesktopShortcut)
        {
            CreateShortcutNative(
                desktopLnk,
                paths.InstalledExePath,
                paths.InstallDir,
                paths.IconPath,
                "Kurfamüzik — Reklamsız Masaüstü Uygulaması");
        }
        else
        {
            try
            {
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);
            }
            catch { }
        }

        string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Kurfamüzik");
        string startMenuMainLnk = Path.Combine(startMenuDir, "Kurfamüzik.lnk");
        string startMenuUninstallLnk = Path.Combine(startMenuDir, "Kurfamüzik Kaldır.lnk");

        if (cfg.StartMenuShortcut)
        {
            CreateShortcutNative(
                startMenuMainLnk,
                paths.InstalledExePath,
                paths.InstallDir,
                paths.IconPath,
                "Kurfamüzik — Reklamsız Masaüstü Uygulaması");

            CreateShortcutNative(
                startMenuUninstallLnk,
                paths.InstalledExePath,
                paths.InstallDir,
                paths.IconPath,
                "Kurfamüzik Masaüstü Uygulamasını Kaldır",
                "--uninstall");
        }
        else
        {
            try
            {
                if (Directory.Exists(startMenuDir)) Directory.Delete(startMenuDir, recursive: true);
            }
            catch { }
        }

        // Only open HKCU\...\Run for writing if RunOnStartup is enabled OR a previous entry exists to clean up
        try
        {
            if (cfg.RunOnStartup)
            {
                using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, writable: true);
                runKey?.SetValue(StartupRegValueName, $"\"{paths.InstalledExePath}\"");
            }
            else
            {
                bool hasExistingStartupVal = false;
                using (RegistryKey? readRunKey = Registry.CurrentUser.OpenSubKey(RunRegKey, writable: false))
                {
                    hasExistingStartupVal = readRunKey?.GetValue(StartupRegValueName) is not null;
                }
                if (hasExistingStartupVal)
                {
                    using RegistryKey? writeRunKey = Registry.CurrentUser.OpenSubKey(RunRegKey, writable: true);
                    writeRunKey?.DeleteValue(StartupRegValueName, throwOnMissingValue: false);
                }
            }
        }
        catch { }

        try
        {
            using RegistryKey brandKey = Registry.CurrentUser.CreateSubKey(RegistryBrandKey, writable: true);
            brandKey.SetValue("InstallLocation", paths.InstallDir);
            brandKey.SetValue("Version", currentVer.ToString(3));
            brandKey.SetValue("DataDir", paths.DataDir);
            brandKey.SetValue("UpdatedAt", DateTimeOffset.UtcNow.ToString("O"));
        }
        catch { }

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

        try
        {
            using RegistryKey? readRunKey = Registry.CurrentUser.OpenSubKey(RunRegKey, writable: false);
            if (readRunKey?.GetValue(StartupRegValueName) is not null)
            {
                using RegistryKey? writeRunKey = Registry.CurrentUser.OpenSubKey(RunRegKey, writable: true);
                writeRunKey?.DeleteValue(StartupRegValueName, throwOnMissingValue: false);
            }
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

        try
        {
            if (Directory.Exists(paths.InstallDir))
            {
                try { if (File.Exists(paths.IconPath)) File.Delete(paths.IconPath); } catch { }
                try { if (File.Exists(paths.ConfigPath)) File.Delete(paths.ConfigPath); } catch { }
                try { if (Directory.Exists(paths.UpdatesDir)) Directory.Delete(paths.UpdatesDir, recursive: true); } catch { }
                try { if (Directory.Exists(paths.LogsDir)) Directory.Delete(paths.LogsDir, recursive: true); } catch { }

                if (purgeData && Directory.Exists(paths.DataDir))
                {
                    try { Directory.Delete(paths.DataDir, recursive: true); } catch { }
                }

                if (File.Exists(paths.InstalledExePath))
                {
                    try
                    {
                        File.Delete(paths.InstalledExePath);
                    }
                    catch
                    {
                        // Running executable can be renamed into Temp and marked for OS cleanup without external scripts
                        try
                        {
                            string tempOld = Path.Combine(Path.GetTempPath(), $"kurfa_uninst_{Guid.NewGuid():N}.old");
                            File.Move(paths.InstalledExePath, tempOld, overwrite: true);
                            MoveFileEx(tempOld, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                        }
                        catch { }
                    }
                }

                try
                {
                    if (Directory.GetFileSystemEntries(paths.InstallDir).Length == 0)
                    {
                        Directory.Delete(paths.InstallDir, recursive: false);
                    }
                }
                catch { }
            }
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
}
