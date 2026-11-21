using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace KurfanovaMuzik;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\KurfamuzikDesktopSingleInstanceMutex";
    private const string ActivatePipeName = "KurfamuzikDesktopActivatePipe";
    private const string TestSingleInstanceMutexName = "KurfaTest_SingleInstance_Mutex";
    private const string TestActivatePipeName = "KurfaTestDesktopActivatePipe";

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    [STAThread]
    private static void Main(string[] args)
    {
        bool isTestMode = args.Any(a => string.Equals(a, "--test", StringComparison.OrdinalIgnoreCase));
        string? customTargetUrl = ParseUrlArg(args);

        // Register AppUserModelID before creating any Win32 window (required for Windows 10 taskbar/tray/toast identity)
        try { _ = SetCurrentProcessExplicitAppUserModelID(isTestMode ? "KurfaCore.KurfaTest" : "KurfaCore.Kurfamuzik"); } catch { }

        // 0. Preview screenshot helpers for visual verification
        int previewMiniIdx = Array.FindIndex(args, a => string.Equals(a, "--preview-miniplayer", StringComparison.OrdinalIgnoreCase));
        if (previewMiniIdx >= 0 && previewMiniIdx + 1 < args.Length)
        {
            ApplicationConfiguration.Initialize();
            using var previewMini = new MiniPlayerForm(
                () => { }, () => { }, () => { }, () => { }, _ => { }, () => { });
            previewMini.SavePreviewPng(args[previewMiniIdx + 1]);
            return;
        }

        int previewTrayIdx = Array.FindIndex(args, a => string.Equals(a, "--preview-tray", StringComparison.OrdinalIgnoreCase));
        if (previewTrayIdx >= 0 && previewTrayIdx + 1 < args.Length)
        {
            ApplicationConfiguration.Initialize();
            using var previewTray = new TrayMenuForm(
                $"v{AppPaths.GetNormalizedCurrentVersion().ToString(3)}",
                () => { }, () => { }, () => { });
            previewTray.SavePreviewPng(args[previewTrayIdx + 1]);
            return;
        }

        int previewIdx = Array.FindIndex(args, a => string.Equals(a, "--preview-setup", StringComparison.OrdinalIgnoreCase));
        if (previewIdx >= 0 && previewIdx + 1 < args.Length)
        {
            ApplicationConfiguration.Initialize();
            var sampleExisting = new ExistingInstallInfo(
                Found: true,
                PreviousInstallDir: AppPaths.DefaultInstallDir,
                ExistingExePath: Path.Combine(AppPaths.DefaultInstallDir, "Kurfanova-Muzik-Windows.exe"),
                ExistingVersion: "1.1.16",
                ExtraOldCopyPaths: [],
                RunningOtherProcesses: 0);
            using var previewForm = new SetupForm(sampleExisting);
            previewForm.SavePreviewPng(args[previewIdx + 1]);
            return;
        }

        // 1. Handle temp-helper uninstallation cleanup (pure C#, no cmd/powershell)
        int cleanupIdx = Array.FindIndex(args, a => string.Equals(a, "--uninstall-cleanup", StringComparison.OrdinalIgnoreCase));
        if (cleanupIdx >= 0 && cleanupIdx + 1 < args.Length)
        {
            string targetDir = args[cleanupIdx + 1];
            int waitPid = 0;
            int pidIdx = Array.FindIndex(args, a => string.Equals(a, "--pid", StringComparison.OrdinalIgnoreCase));
            if (pidIdx >= 0 && pidIdx + 1 < args.Length)
            {
                int.TryParse(args[pidIdx + 1], out waitPid);
            }
            bool purge = args.Any(a => string.Equals(a, "--purge", StringComparison.OrdinalIgnoreCase));
            bool silentCleanup = args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
            InstallerService.RunUninstallCleanupHelper(targetDir, waitPid, purge, silentCleanup);
            return;
        }

        // 2. Handle uninstallation
        if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            bool silent = args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
            InstallerService.RunUninstall(silent);
            return;
        }

        ApplicationConfiguration.Initialize();

        var activePaths = new AppPaths(isTestMode ? AppPaths.DefaultTestInstallDir : AppPaths.ResolveActiveInstallDir());
        bool isPortable = args.Any(a => string.Equals(a, "--portable", StringComparison.OrdinalIgnoreCase));
        bool forceSetup = !isTestMode && args.Any(a => string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(a, "--reinstall", StringComparison.OrdinalIgnoreCase));
        bool isInnoInstalled = !isTestMode && AppPaths.IsInnoSetupInstalled;
        bool isFromInstallDir = !isTestMode && AppPaths.IsProcessRunningFromInstallDir(Environment.ProcessPath, activePaths);
        bool effectivePortable = isTestMode || isPortable || (!isInnoInstalled && !isFromInstallDir);

        string mutexName = isTestMode ? TestSingleInstanceMutexName : SingleInstanceMutexName;
        string pipeName = isTestMode ? TestActivatePipeName : ActivatePipeName;

        if (!forceSetup)
        {
            // Wait briefly for previous process to exit cleanly during post-update relaunch
            int postPidIdx = Array.FindIndex(args, a => string.Equals(a, "--post-update-pid", StringComparison.OrdinalIgnoreCase));
            if (postPidIdx >= 0 && postPidIdx + 1 < args.Length && int.TryParse(args[postPidIdx + 1], out int oldPid) && oldPid > 0)
            {
                try
                {
                    using var oldProc = System.Diagnostics.Process.GetProcessById(oldPid);
                    oldProc.WaitForExit(3500);
                }
                catch { /* already exited */ }
            }

            var singleInstanceMutex = new Mutex(true, mutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                singleInstanceMutex.Dispose();
                SignalExistingInstance(pipeName);
                return;
            }

            AutoUpdater? pendingUpdater = null;
            bool relaunchAfterUpdate = false;

            try
            {
                var config = AppPaths.LoadOrDefaultConfig(activePaths, AppPaths.GetNormalizedCurrentVersion());
                if (!effectivePortable)
                {
                    InstallerService.SyncMetadataOnLaunch(activePaths, config, AppPaths.GetNormalizedCurrentVersion());
                }

                var mainForm = new MainForm(activePaths, config, isTestMode, customTargetUrl);
                StartActivationPipeListener(mainForm, pipeName);
                Application.Run(mainForm);
                pendingUpdater = mainForm.DetachPendingUpdaterOnExit(out relaunchAfterUpdate);
            }
            finally
            {
                // Always release single-instance Mutex BEFORE applying pending update & relaunching new EXE!
                try { singleInstanceMutex.ReleaseMutex(); } catch { }
                singleInstanceMutex.Dispose();
            }

            if (pendingUpdater is not null)
            {
                try
                {
                    pendingUpdater.TryApplyPendingUpdate(relaunch: relaunchAfterUpdate);
                }
                finally
                {
                    pendingUpdater.Dispose();
                }
            }
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

    private static string? ParseUrlArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.IsNullOrWhiteSpace(arg)) continue;

            string? candidate = null;
            if (arg.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
            {
                candidate = arg["--url=".Length..].Trim().Trim('"');
            }
            else if (string.Equals(arg, "--url", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                candidate = args[i + 1].Trim().Trim('"');
            }

            if (!string.IsNullOrWhiteSpace(candidate) &&
                Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed) &&
                (parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                 parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
            {
                return parsed.AbsoluteUri;
            }
        }
        return null;
    }

    private static void SignalExistingInstance(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(1200);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("ACTIVATE");
        }
        catch
        {
            // Existing instance might be closing or elevating
        }
    }

    private static void StartActivationPipeListener(MainForm form, string pipeName)
    {
        var thread = new Thread(() =>
        {
            while (!form.IsDisposed)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    string? msg = reader.ReadLine();
                    if (string.Equals(msg, "ACTIVATE", StringComparison.OrdinalIgnoreCase) && !form.IsDisposed && form.IsHandleCreated)
                    {
                        form.BeginInvoke(form.BringExistingWindowToFront);
                    }
                    else if (string.Equals(msg, "QUIT", StringComparison.OrdinalIgnoreCase) && !form.IsDisposed && form.IsHandleCreated)
                    {
                        form.BeginInvoke(form.ForceExitForInstaller);
                    }
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
        })
        {
            IsBackground = true,
            Name = "KurfamuzikActivatePipe"
        };
        thread.Start();
    }
}
