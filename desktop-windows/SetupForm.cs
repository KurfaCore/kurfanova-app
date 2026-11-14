using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace KurfanovaMuzik;

public sealed class SetupForm : Form
{
    private readonly ExistingInstallInfo _existing;
    private readonly TextBox _txtPath;
    private readonly CheckBox _chkDesktopShortcut;
    private readonly CheckBox _chkStartMenuShortcut;
    private readonly CheckBox _chkRunOnStartup;
    private readonly CheckBox _chkCleanupOld;
    private readonly CheckBox _chkDeleteSourceExe;
    private readonly Button _btnInstall;
    private readonly Button _btnCancel;
    private readonly Button _btnBrowse;
    private readonly Button _btnDefault;
    private readonly Label _lblStatus;

    public SetupForm(ExistingInstallInfo existing)
    {
        _existing = existing;

        Color darkBg = Color.FromArgb(11, 11, 20); // #0B0B14
        Color panelBg = Color.FromArgb(20, 22, 37); // #141625
        Color inputBg = Color.FromArgb(15, 17, 28);
        Color borderColor = Color.FromArgb(35, 38, 60);
        Color accentPink = Color.FromArgb(255, 46, 91); // #FF2E5B
        Color textMuted = Color.FromArgb(160, 165, 185);

        Text = "Kurfamüzik Kurulum Sihirbazı";
        ClientSize = new Size(620, 520);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = darkBg;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular);

        Icon? appIcon = LoadEmbeddedIcon();
        if (appIcon is not null)
        {
            Icon = appIcon;
        }

        // Header Panel
        Panel headerPanel = new()
        {
            Location = new Point(0, 0),
            Size = new Size(620, 74),
            BackColor = panelBg
        };
        headerPanel.Paint += (_, pe) =>
        {
            using var pen = new Pen(borderColor);
            pe.Graphics.DrawLine(pen, 0, 73, 620, 73);
        };

        PictureBox picIcon = new()
        {
            Location = new Point(20, 16),
            Size = new Size(42, 42),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        if (appIcon is not null)
        {
            picIcon.Image = appIcon.ToBitmap();
        }

        Label lblTitle = new()
        {
            Text = "Kurfamüzik",
            Font = new Font("Segoe UI", 13.5F, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(72, 14),
            AutoSize = true
        };

        Label lblSubtitle = new()
        {
            Text = "Windows Masaüstü Kurulum ve Güncelleme Sihirbazı",
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = textMuted,
            Location = new Point(73, 41),
            AutoSize = true
        };

        headerPanel.Controls.Add(picIcon);
        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(lblSubtitle);
        Controls.Add(headerPanel);

        // Detection Card / Banner
        Panel detectionCard = new()
        {
            Location = new Point(20, 88),
            Size = new Size(580, 50),
            BackColor = _existing.Found ? Color.FromArgb(24, 30, 48) : panelBg
        };
        detectionCard.Paint += (_, pe) =>
        {
            using var pen = new Pen(_existing.Found ? Color.FromArgb(59, 130, 246) : borderColor);
            pe.Graphics.DrawRectangle(pen, 0, 0, detectionCard.Width - 1, detectionCard.Height - 1);
        };

        Label lblDetection = new()
        {
            Location = new Point(12, 8),
            Size = new Size(556, 34),
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = _existing.Found ? Color.FromArgb(147, 197, 253) : textMuted,
            Text = _existing.Found
                ? $"Mevcut sürüm tespit edildi{(!string.IsNullOrWhiteSpace(_existing.ExistingVersion) ? $" (v{_existing.ExistingVersion})" : "")} — Eski sürüm temizlenip verileriniz korunarak yenilenecek."
                : "Kurfamüzik bilgisayarınıza kurulacak ve masaüstü kısayolları hazırlanacaktır."
        };
        detectionCard.Controls.Add(lblDetection);
        Controls.Add(detectionCard);

        // Folder selection
        Label lblFolder = new()
        {
            Text = "Kurulum Klasörü:",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(20, 150),
            AutoSize = true
        };
        Controls.Add(lblFolder);

        string initialInstallDir = !string.IsNullOrWhiteSpace(_existing.PreviousInstallDir)
            ? _existing.PreviousInstallDir
            : AppPaths.DefaultInstallDir;

        _txtPath = new TextBox
        {
            Location = new Point(20, 174),
            Size = new Size(385, 26),
            BackColor = inputBg,
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Text = initialInstallDir
        };
        Controls.Add(_txtPath);

        _btnBrowse = new Button
        {
            Text = "Klasör Seç...",
            Location = new Point(415, 173),
            Size = new Size(95, 28),
            BackColor = Color.FromArgb(28, 32, 52),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnBrowse.FlatAppearance.BorderColor = borderColor;
        _btnBrowse.Click += OnBrowseClick;
        Controls.Add(_btnBrowse);

        _btnDefault = new Button
        {
            Text = "Varsayılan",
            Location = new Point(515, 173),
            Size = new Size(85, 28),
            BackColor = Color.FromArgb(28, 32, 52),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnDefault.FlatAppearance.BorderColor = borderColor;
        _btnDefault.Click += (_, _) => _txtPath.Text = AppPaths.DefaultInstallDir;
        Controls.Add(_btnDefault);

        // Checkboxes Panel
        Panel optionsPanel = new()
        {
            Location = new Point(20, 215),
            Size = new Size(580, 205),
            BackColor = panelBg
        };
        optionsPanel.Paint += (_, pe) =>
        {
            using var pen = new Pen(borderColor);
            pe.Graphics.DrawRectangle(pen, 0, 0, optionsPanel.Width - 1, optionsPanel.Height - 1);
        };

        // Try load existing config if available
        DesktopAppConfig existingCfg = AppPaths.LoadOrDefaultConfig(
            new AppPaths(initialInstallDir),
            AppPaths.GetNormalizedCurrentVersion());

        _chkDesktopShortcut = new CheckBox
        {
            Text = "Masaüstüne kısayol oluştur (Kurfamüzik)",
            Location = new Point(16, 14),
            Size = new Size(540, 24),
            Checked = true,
            ForeColor = Color.White
        };

        _chkStartMenuShortcut = new CheckBox
        {
            Text = "Başlat Menüsüne kısayol ekle",
            Location = new Point(16, 46),
            Size = new Size(540, 24),
            Checked = true,
            ForeColor = Color.White
        };

        _chkRunOnStartup = new CheckBox
        {
            Text = "Windows açılışında otomatik başlat",
            Location = new Point(16, 78),
            Size = new Size(540, 24),
            Checked = existingCfg.RunOnStartup,
            ForeColor = Color.White
        };

        _chkCleanupOld = new CheckBox
        {
            Text = "Eski Kurfamüzik kurulum ve kopyalarını temizleyip yenile",
            Location = new Point(16, 110),
            Size = new Size(540, 24),
            Checked = true,
            ForeColor = Color.White
        };

        bool isFromTempOrDownloads = IsInDownloadsOrTemp(Environment.ProcessPath);
        _chkDeleteSourceExe = new CheckBox
        {
            Text = "Kurulum tamamlandıktan sonra bu indirilen kurulum dosyasını sil",
            Location = new Point(16, 142),
            Size = new Size(540, 48),
            Checked = isFromTempOrDownloads,
            ForeColor = Color.White
        };

        optionsPanel.Controls.Add(_chkDesktopShortcut);
        optionsPanel.Controls.Add(_chkStartMenuShortcut);
        optionsPanel.Controls.Add(_chkRunOnStartup);
        optionsPanel.Controls.Add(_chkCleanupOld);
        optionsPanel.Controls.Add(_chkDeleteSourceExe);
        Controls.Add(optionsPanel);

        // Status Label
        _lblStatus = new Label
        {
            Location = new Point(20, 435),
            Size = new Size(330, 42),
            ForeColor = Color.FromArgb(244, 114, 182),
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Text = string.Empty
        };
        Controls.Add(_lblStatus);

        // Action Buttons
        _btnCancel = new Button
        {
            Text = "İptal",
            Location = new Point(360, 436),
            Size = new Size(95, 38),
            BackColor = Color.FromArgb(28, 32, 52),
            ForeColor = Color.FromArgb(200, 205, 225),
            FlatStyle = FlatStyle.Flat
        };
        _btnCancel.FlatAppearance.BorderColor = borderColor;
        _btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(_btnCancel);

        _btnInstall = new Button
        {
            Text = _existing.Found ? "Güncelle ve Başlat" : "Kur ve Başlat",
            Location = new Point(465, 436),
            Size = new Size(135, 38),
            BackColor = accentPink,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnInstall.FlatAppearance.BorderSize = 0;
        _btnInstall.Click += OnInstallClick;
        Controls.Add(_btnInstall);
    }

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using FolderBrowserDialog fbd = new()
        {
            Description = "Kurfamüzik Kurulum Klasörünü Seçin",
            UseDescriptionForTitle = true,
            InitialDirectory = _txtPath.Text.Trim()
        };

        if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
        {
            _txtPath.Text = fbd.SelectedPath;
        }
    }

    private async void OnInstallClick(object? sender, EventArgs e)
    {
        string targetDir = _txtPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            targetDir = AppPaths.DefaultInstallDir;
            _txtPath.Text = targetDir;
        }

        // Disable controls
        _btnInstall.Enabled = false;
        _btnCancel.Enabled = false;
        _btnBrowse.Enabled = false;
        _btnDefault.Enabled = false;
        _chkDesktopShortcut.Enabled = false;
        _chkStartMenuShortcut.Enabled = false;
        _chkRunOnStartup.Enabled = false;
        _chkCleanupOld.Enabled = false;
        _chkDeleteSourceExe.Enabled = false;
        _txtPath.Enabled = false;

        _lblStatus.Text = "Kurulum başlatılıyor...";

        var opts = new InstallOptions
        {
            TargetInstallDir = targetDir,
            PreviousInstallDir = _existing.PreviousInstallDir,
            DesktopShortcut = _chkDesktopShortcut.Checked,
            StartMenuShortcut = _chkStartMenuShortcut.Checked,
            RunOnStartup = _chkRunOnStartup.Checked,
            CleanupOldCopies = _chkCleanupOld.Checked,
            DeleteSourceInstallerAfterExit = _chkDeleteSourceExe.Checked
        };

        try
        {
            await Task.Run(() =>
            {
                InstallerService.InstallOrUpgrade(opts, msg =>
                {
                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                        {
                            BeginInvoke(() => _lblStatus.Text = msg);
                        }
                    }
                    catch { }
                });
            });

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Hata oluştu!";
            MessageBox.Show(
                this,
                $"Kurulum sırasında hata oluştu:\n{ex.Message}",
                "Kurulum Hatası",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            _btnInstall.Enabled = true;
            _btnCancel.Enabled = true;
            _btnBrowse.Enabled = true;
            _btnDefault.Enabled = true;
            _chkDesktopShortcut.Enabled = true;
            _chkStartMenuShortcut.Enabled = true;
            _chkRunOnStartup.Enabled = true;
            _chkCleanupOld.Enabled = true;
            _chkDeleteSourceExe.Enabled = true;
            _txtPath.Enabled = true;
        }
    }

    private static bool IsInDownloadsOrTemp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string fullPath = Path.GetFullPath(path);
            string tempDir = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase)) return true;

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloadsDir = Path.Combine(userProfile, "Downloads");
            if (fullPath.StartsWith(downloadsDir, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KurfanovaMuzik.app.ico");
            return stream is not null ? new Icon(stream) : null;
        }
        catch
        {
            return null;
        }
    }
}
