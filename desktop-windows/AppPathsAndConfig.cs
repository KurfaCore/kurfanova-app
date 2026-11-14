using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Win32;

namespace KurfanovaMuzik;

public sealed class DesktopAppConfig
{
    public string InstallDir { get; set; } = string.Empty;
    public bool DesktopShortcut { get; set; } = true;
    public bool StartMenuShortcut { get; set; } = true;
    public bool RunOnStartup { get; set; } = false;
    public string InstalledVersion { get; set; } = "1.1.0";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AppPaths
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public string InstallDir { get; }
    public string InstalledExePath => Path.Combine(InstallDir, "KurfanovaMuzik.exe");
    public string IconPath => Path.Combine(InstallDir, "kurfanova.ico");
    public string ConfigPath => Path.Combine(InstallDir, "config.json");
    public string DataDir => Path.Combine(InstallDir, "Data");
    public string WebView2DataDir => Path.Combine(DataDir, "WebView2");
    public string UpdatesDir => Path.Combine(InstallDir, "Updates");
    public string LogsDir => Path.Combine(InstallDir, "Logs");

    public AppPaths(string? installDir = null)
    {
        string dir = string.IsNullOrWhiteSpace(installDir) ? DefaultInstallDir : installDir.Trim();
        InstallDir = Path.GetFullPath(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KurfanovaMuzik");

    public static string ResolveActiveInstallDir()
    {
        try
        {
            using RegistryKey? brandKey = Registry.CurrentUser.OpenSubKey(@"Software\KurfaCore\KurfanovaMuzik", writable: false);
            if (brandKey?.GetValue("InstallLocation") is string brandLoc && !string.IsNullOrWhiteSpace(brandLoc))
            {
                return Path.GetFullPath(brandLoc.Trim());
            }

            using RegistryKey? uninstallKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\KurfanovaMuzik", writable: false);
            if (uninstallKey?.GetValue("InstallLocation") is string uninstLoc && !string.IsNullOrWhiteSpace(uninstLoc))
            {
                return Path.GetFullPath(uninstLoc.Trim());
            }
        }
        catch
        {
            // Fallback on registry read failure
        }

        return DefaultInstallDir;
    }

    public static DesktopAppConfig LoadOrDefaultConfig(AppPaths paths, Version currentVersion)
    {
        try
        {
            if (File.Exists(paths.ConfigPath))
            {
                string json = File.ReadAllText(paths.ConfigPath);
                DesktopAppConfig? cfg = JsonSerializer.Deserialize<DesktopAppConfig>(json);
                if (cfg is not null)
                {
                    if (string.IsNullOrWhiteSpace(cfg.InstallDir))
                    {
                        cfg.InstallDir = paths.InstallDir;
                    }
                    return cfg;
                }
            }
        }
        catch
        {
            // Ignore corrupted config read and create default
        }

        return new DesktopAppConfig
        {
            InstallDir = paths.InstallDir,
            DesktopShortcut = true,
            StartMenuShortcut = true,
            RunOnStartup = false,
            InstalledVersion = currentVersion.ToString(3),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public static void SaveConfig(AppPaths paths, DesktopAppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(paths.InstallDir);
            cfg.InstallDir = paths.InstallDir;
            cfg.UpdatedAt = DateTimeOffset.UtcNow;
            string json = JsonSerializer.Serialize(cfg, JsonOptions);
            File.WriteAllText(paths.ConfigPath, json);
        }
        catch
        {
            // Ignore config write errors
        }
    }

    public static bool IsProcessRunningFromInstallDir(string? processPath, AppPaths paths)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return false;
        try
        {
            string current = Path.GetFullPath(processPath);
            string installed = Path.GetFullPath(paths.InstalledExePath);
            return string.Equals(current, installed, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static Version GetNormalizedCurrentVersion()
    {
        Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 1, 0, 0);
        return new Version(
            Math.Max(0, v.Major),
            Math.Max(0, v.Minor),
            Math.Max(0, v.Build),
            Math.Max(0, v.Revision));
    }
}
