namespace KurfanovaMuzik;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 0. Preview screenshot helper for visual verification
        int previewIdx = Array.FindIndex(args, a => string.Equals(a, "--preview-setup", StringComparison.OrdinalIgnoreCase));
        if (previewIdx >= 0 && previewIdx + 1 < args.Length)
        {
            ApplicationConfiguration.Initialize();
            var sampleExisting = new ExistingInstallInfo(
                Found: true,
                PreviousInstallDir: AppPaths.DefaultInstallDir,
                ExistingExePath: Path.Combine(AppPaths.DefaultInstallDir, "KurfanovaMuzik.exe"),
                ExistingVersion: "1.1.2",
                ExtraOldCopyPaths: [],
                RunningOtherProcesses: 0);
            using var previewForm = new SetupForm(sampleExisting);
            previewForm.SavePreviewPng(args[previewIdx + 1]);
            return;
        }

        // 1. Handle uninstallation
        if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            bool silent = args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
            InstallerService.RunUninstall(silent);
            return;
        }

        ApplicationConfiguration.Initialize();

        var activePaths = new AppPaths(AppPaths.ResolveActiveInstallDir());
        bool isPortable = args.Any(a => string.Equals(a, "--portable", StringComparison.OrdinalIgnoreCase));
        bool forceSetup = args.Any(a => string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(a, "--reinstall", StringComparison.OrdinalIgnoreCase));
        bool isFromInstallDir = AppPaths.IsProcessRunningFromInstallDir(Environment.ProcessPath, activePaths);

        if (isPortable)
        {
            var config = AppPaths.LoadOrDefaultConfig(activePaths, AppPaths.GetNormalizedCurrentVersion());
            Application.Run(new MainForm(activePaths, config));
        }
        else if (isFromInstallDir && !forceSetup)
        {
            var config = AppPaths.LoadOrDefaultConfig(activePaths, AppPaths.GetNormalizedCurrentVersion());
            InstallerService.SyncMetadataOnLaunch(activePaths, config, AppPaths.GetNormalizedCurrentVersion());
            Application.Run(new MainForm(activePaths, config));
        }
        else
        {
            ExistingInstallInfo existing = InstallerService.DetectExistingInstallation();
            using var setup = new SetupForm(existing);
            if (setup.ShowDialog() == DialogResult.OK)
            {
                // Installation completed and target EXE was launched
            }
        }
    }
}
