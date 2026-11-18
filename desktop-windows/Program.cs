using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace KurfanovaMuzik;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\KurfamuzikDesktopSingleInstanceMutex";
    private const string ActivatePipeName = "KurfamuzikDesktopActivatePipe";

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    [STAThread]
    private static void Main(string[] args)
    {
        // Register AppUserModelID before creating any Win32 window (required for Windows 10 taskbar/tray/toast identity)
        try { _ = SetCurrentProcessExplicitAppUserModelID("KurfaCore.Kurfamuzik"); } catch { }

        // 0. Preview screenshot helpers for visual verification
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
                ExistingExePath: Path.Combine(AppPaths.DefaultInstallDir, "KurfanovaMuzik.exe"),
                ExistingVersion: "1.1.16",
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

        if (isPortable || (isFromInstallDir && !forceSetup))
        {
            using var singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                SignalExistingInstance();
                return;
            }

            var config = AppPaths.LoadOrDefaultConfig(activePaths, AppPaths.GetNormalizedCurrentVersion());
            if (!isPortable)
            {
                InstallerService.SyncMetadataOnLaunch(activePaths, config, AppPaths.GetNormalizedCurrentVersion());
            }

            var mainForm = new MainForm(activePaths, config);
            StartActivationPipeListener(mainForm);
            Application.Run(mainForm);
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

    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ActivatePipeName, PipeDirection.Out);
            client.Connect(1200);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("ACTIVATE");
        }
        catch
        {
            // Existing instance might be closing or elevating
        }
    }

    private static void StartActivationPipeListener(MainForm form)
    {
        var thread = new Thread(() =>
        {
            while (!form.IsDisposed)
            {
                try
                {
                    using var server = new NamedPipeServerStream(ActivatePipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    string? msg = reader.ReadLine();
                    if (string.Equals(msg, "ACTIVATE", StringComparison.OrdinalIgnoreCase) && !form.IsDisposed && form.IsHandleCreated)
                    {
                        form.BeginInvoke(form.BringExistingWindowToFront);
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
