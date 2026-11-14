using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KurfanovaMuzik;

public sealed class SetupForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private readonly ExistingInstallInfo _existing;
    private readonly TextBox _txtPath;
    private readonly ModernPillButton _btnBrowse;
    private readonly ModernPillButton _btnDefault;
    private readonly ModernToggleRow _rowDesktopShortcut;
    private readonly ModernToggleRow _rowStartMenuShortcut;
    private readonly ModernToggleRow _rowCleanupOld;
    private readonly ModernToggleRow _rowRunOnStartup;
    private readonly ModernActionButton _btnInstall;
    private readonly ModernActionButton _btnCancel;
    private readonly Icon? _appIcon;
    private readonly Bitmap? _iconBitmap;

    private string _statusText = "Kuruluma hazır — tüm veriler bu klasörde tutulur.";
    private int _progressPercent = 0;
    private bool _isInstalling = false;

    public SetupForm(ExistingInstallInfo existing)
    {
        _existing = existing;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        DoubleBuffered = true;

        Text = "Kurfamüzik — Resmi Masaüstü Kurulum Sihirbazı";
        ClientSize = new Size(640, 568);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 11, 18);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);

        _appIcon = LoadEmbeddedIcon();
        if (_appIcon is not null)
        {
            Icon = _appIcon;
            try
            {
                using var ico48 = new Icon(_appIcon, new Size(48, 48));
                _iconBitmap = ico48.ToBitmap();
            }
            catch
            {
                _iconBitmap = _appIcon.ToBitmap();
            }
        }

        string initialInstallDir = !string.IsNullOrWhiteSpace(_existing.PreviousInstallDir)
            ? _existing.PreviousInstallDir!
            : AppPaths.DefaultInstallDir;

        var existingCfg = AppPaths.LoadOrDefaultConfig(new AppPaths(initialInstallDir), AppPaths.GetNormalizedCurrentVersion());

        _txtPath = new TextBox
        {
            Text = initialInstallDir,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(14, 16, 27),
            ForeColor = Color.FromArgb(236, 240, 255),
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Location = new Point(42, 206),
            Size = new Size(364, 20)
        };
        Controls.Add(_txtPath);

        _btnBrowse = new ModernPillButton("Gözat...", isAccent: false)
        {
            Location = new Point(424, 199),
            Size = new Size(86, 32)
        };
        _btnBrowse.Click += OnBrowseClick;
        Controls.Add(_btnBrowse);

        _btnDefault = new ModernPillButton("Varsayılan", isAccent: false)
        {
            Location = new Point(516, 199),
            Size = new Size(90, 32)
        };
        _btnDefault.Click += (_, _) =>
        {
            _txtPath.Text = AppPaths.DefaultInstallDir;
            Invalidate();
        };
        Controls.Add(_btnDefault);

        _rowDesktopShortcut = new ModernToggleRow(
            "Masaüstü Kısayolu Oluştur",
            "Masaüstüne Kurfamüzik hızlı başlatma simgesi ekler",
            initialChecked: true)
        {
            Location = new Point(22, 260),
            Size = new Size(596, 50)
        };
        Controls.Add(_rowDesktopShortcut);

        _rowStartMenuShortcut = new ModernToggleRow(
            "Başlat Menüsü ve Arama Kaydı",
            "Windows Başlat menüsüne ve sistem uygulama listesine ekler",
            initialChecked: true)
        {
            Location = new Point(22, 316),
            Size = new Size(596, 50)
        };
        Controls.Add(_rowStartMenuShortcut);

        _rowCleanupOld = new ModernToggleRow(
            "Eski Kopyaları Temizle ve Yenile",
            "Bilgisayardaki eski Kurfamüzik sürümlerini kaldırıp verileri koruyarak yeniler",
            initialChecked: true)
        {
            Location = new Point(22, 372),
            Size = new Size(596, 50)
        };
        Controls.Add(_rowCleanupOld);

        _rowRunOnStartup = new ModernToggleRow(
            "Windows Açılışında Otomatik Başlat",
            "Bilgisayar açıldığında Kurfamüzik'i hazır başlatır",
            initialChecked: existingCfg.RunOnStartup)
        {
            Location = new Point(22, 428),
            Size = new Size(596, 50)
        };
        Controls.Add(_rowRunOnStartup);

        _btnCancel = new ModernActionButton("İptal", isPrimary: false)
        {
            Location = new Point(326, 504),
            Size = new Size(106, 44)
        };
        _btnCancel.Click += (_, _) =>
        {
            if (_isInstalling) return;
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(_btnCancel);

        _btnInstall = new ModernActionButton(
            _existing.Found ? "Güncelle ve Başlat" : "Kur ve Başlat",
            isPrimary: true)
        {
            Location = new Point(442, 504),
            Size = new Size(176, 44)
        };
        _btnInstall.Click += OnInstallClick;
        Controls.Add(_btnInstall);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int dark = 1;
            _ = DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int corner = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // 1. Top Hero Header (0..96)
        Rectangle heroRect = new(0, 0, Width, 96);
        using (var heroGrad = new LinearGradientBrush(
                   heroRect,
                   Color.FromArgb(24, 20, 38),
                   Color.FromArgb(12, 13, 22),
                   LinearGradientMode.Horizontal))
        {
            g.FillRectangle(heroGrad, heroRect);
        }

        // Crimson top accent bar
        using (var topAccent = new LinearGradientBrush(
                   new Rectangle(0, 0, Width, 3),
                   Color.FromArgb(255, 46, 91),
                   Color.FromArgb(124, 58, 237),
                   LinearGradientMode.Horizontal))
        {
            g.FillRectangle(topAccent, 0, 0, Width, 3);
        }

        // Squircle Icon Frame
        Rectangle iconBox = new(22, 20, 56, 56);
        using (GraphicsPath iconPath = CreateRoundRect(iconBox, 14))
        using (var iconBg = new SolidBrush(Color.FromArgb(24, 27, 44)))
        using (var iconBorder = new Pen(Color.FromArgb(65, 45, 75), 1.2f))
        {
            g.FillPath(iconBg, iconPath);
            g.DrawPath(iconBorder, iconPath);
        }
        if (_iconBitmap is not null)
        {
            g.DrawImage(_iconBitmap, new Rectangle(30, 28, 40, 40));
        }

        // Title & Version Pill
        using (var titleFont = new Font("Segoe UI", 15F, FontStyle.Bold))
        {
            TextRenderer.DrawText(
                g,
                "Kurfamüzik Masaüstü",
                titleFont,
                new Point(88, 17),
                Color.White);
        }

        string verStr = AppPaths.GetNormalizedCurrentVersion().ToString(3);
        string badgeText = $"v{verStr} • %0 REKLAM MOTORU";
        using (var badgeFont = new Font("Segoe UI", 8F, FontStyle.Bold))
        {
            Size textSize = TextRenderer.MeasureText(g, badgeText, badgeFont);
            Rectangle badgeRect = new(314, 21, textSize.Width + 16, 23);
            using GraphicsPath badgePath = CreateRoundRect(badgeRect, 11);
            using var badgeBg = new SolidBrush(Color.FromArgb(48, 255, 46, 91));
            using var badgeBorder = new Pen(Color.FromArgb(115, 255, 46, 91), 1f);
            g.FillPath(badgeBg, badgePath);
            g.DrawPath(badgeBorder, badgePath);
            TextRenderer.DrawText(
                g,
                badgeText,
                badgeFont,
                badgeRect,
                Color.FromArgb(255, 118, 150),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        using (var subFont = new Font("Segoe UI", 9F, FontStyle.Regular))
        {
            TextRenderer.DrawText(
                g,
                "Sahiplenilmiş tek klasör mimarisi, kesintisiz ses motoru ve sessiz otomatik güncelleme",
                subFont,
                new Point(89, 50),
                Color.FromArgb(164, 172, 202));
        }

        using (var sepPen = new Pen(Color.FromArgb(28, 32, 52), 1f))
        {
            g.DrawLine(sepPen, 0, 96, Width, 96);
        }

        // 2. Smart Detection Banner (Y=110..154)
        Rectangle detectRect = new(22, 110, 596, 44);
        Color detectBgColor = _existing.Found
            ? Color.FromArgb(16, 33, 46)
            : Color.FromArgb(18, 21, 35);
        Color detectBorderColor = _existing.Found
            ? Color.FromArgb(45, 135, 185)
            : Color.FromArgb(34, 40, 66);
        Color detectDotColor = _existing.Found
            ? Color.FromArgb(56, 189, 248)
            : Color.FromArgb(16, 185, 129);

        using (GraphicsPath detectPath = CreateRoundRect(detectRect, 12))
        using (var detectBg = new SolidBrush(detectBgColor))
        using (var detectPen = new Pen(detectBorderColor, 1f))
        {
            g.FillPath(detectBg, detectPath);
            g.DrawPath(detectPen, detectPath);
        }

        using (var dotBrush = new SolidBrush(detectDotColor))
        {
            g.FillEllipse(dotBrush, new Rectangle(36, 127, 10, 10));
        }

        string detectMsg = _existing.Found
            ? $"Mevcut kurulum algılandı{(!string.IsNullOrWhiteSpace(_existing.ExistingVersion) ? $" (v{_existing.ExistingVersion})" : "")} — Eski sürüm temizlenip verileriniz korunarak yenilenecek."
            : "Resmi masaüstü kurulumu hazır — uygulama tüm önbellek ve güncellemelerini seçilen klasörde saklar.";
        using (var detectFont = new Font("Segoe UI", 9F, FontStyle.Regular))
        {
            TextRenderer.DrawText(
                g,
                detectMsg,
                detectFont,
                new Rectangle(54, 110, 554, 44),
                _existing.Found ? Color.FromArgb(186, 230, 253) : Color.FromArgb(205, 212, 235),
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        // 3. Owned Folder Card (Y=166..246)
        Rectangle folderCard = new(22, 166, 596, 80);
        using (GraphicsPath folderPath = CreateRoundRect(folderCard, 14))
        using (var folderBg = new SolidBrush(Color.FromArgb(17, 20, 33)))
        using (var folderBorder = new Pen(Color.FromArgb(34, 40, 64), 1f))
        {
            g.FillPath(folderBg, folderPath);
            g.DrawPath(folderBorder, folderPath);
        }

        using (var capFont = new Font("Segoe UI", 8F, FontStyle.Bold))
        {
            TextRenderer.DrawText(
                g,
                "SAHİPLENİLEN UYGULAMA KLASÖRÜ (TÜM VERİLER VE GÜNCELLEMELER)",
                capFont,
                new Point(32, 175),
                Color.FromArgb(136, 146, 182),
                TextFormatFlags.NoPrefix);
        }

        Rectangle inputBoxRect = new(32, 199, 384, 32);
        using (GraphicsPath inputPath = CreateRoundRect(inputBoxRect, 8))
        using (var inputFill = new SolidBrush(Color.FromArgb(14, 16, 27)))
        using (var inputPen = new Pen(Color.FromArgb(40, 46, 74), 1f))
        {
            g.FillPath(inputFill, inputPath);
            g.DrawPath(inputPen, inputPath);
        }

        // 4. Bottom Status & Progress Area (Y=504..548, X=22..312)
        using (var statusFont = new Font("Segoe UI", 8.5F, FontStyle.Regular))
        {
            TextRenderer.DrawText(
                g,
                _statusText,
                statusFont,
                new Rectangle(22, 504, 294, 22),
                _isInstalling ? Color.FromArgb(255, 110, 145) : Color.FromArgb(156, 164, 196),
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        Rectangle progTrack = new(24, 533, 288, 8);
        using (GraphicsPath trackPath = CreateRoundRect(progTrack, 4))
        using (var trackBrush = new SolidBrush(Color.FromArgb(22, 26, 42)))
        {
            g.FillPath(trackBrush, trackPath);
        }

        int fillPct = _isInstalling ? Math.Clamp(_progressPercent, 8, 100) : 100;
        int fillWidth = Math.Max(12, (int)(progTrack.Width * (fillPct / 100.0)));
        Rectangle progFill = new(progTrack.X, progTrack.Y, fillWidth, progTrack.Height);
        using (GraphicsPath fillPath = CreateRoundRect(progFill, 4))
        using (var fillGrad = new LinearGradientBrush(
                   progTrack,
                   _isInstalling ? Color.FromArgb(255, 46, 91) : Color.FromArgb(48, 56, 88),
                   _isInstalling ? Color.FromArgb(236, 72, 153) : Color.FromArgb(72, 82, 124),
                   LinearGradientMode.Horizontal))
        {
            g.FillPath(fillGrad, fillPath);
        }
    }

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        if (_isInstalling) return;
        using FolderBrowserDialog fbd = new()
        {
            Description = "Kurfamüzik Sahiplenilen Kurulum Klasörünü Seçin",
            UseDescriptionForTitle = true,
            InitialDirectory = _txtPath.Text.Trim()
        };

        if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
        {
            _txtPath.Text = fbd.SelectedPath;
            Invalidate();
        }
    }

    private async void OnInstallClick(object? sender, EventArgs e)
    {
        if (_isInstalling) return;

        string targetDir = _txtPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            targetDir = AppPaths.DefaultInstallDir;
            _txtPath.Text = targetDir;
        }

        _isInstalling = true;
        _progressPercent = 18;
        _statusText = "Kurulum başlatılıyor...";
        SetControlsInteractive(false);
        Invalidate();

        var opts = new InstallOptions
        {
            TargetInstallDir = targetDir,
            PreviousInstallDir = _existing.PreviousInstallDir,
            DesktopShortcut = _rowDesktopShortcut.Checked,
            StartMenuShortcut = _rowStartMenuShortcut.Checked,
            CleanupOldCopies = _rowCleanupOld.Checked,
            RunOnStartup = _rowRunOnStartup.Checked
        };

        try
        {
            int step = 18;
            await Task.Run(() =>
            {
                InstallerService.InstallOrUpgrade(opts, msg =>
                {
                    step = Math.Min(95, step + 18);
                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                        {
                            BeginInvoke(() =>
                            {
                                _statusText = msg;
                                _progressPercent = step;
                                Invalidate(new Rectangle(20, 500, 305, 55));
                            });
                        }
                    }
                    catch { }
                });
            });

            _progressPercent = 100;
            Invalidate();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _isInstalling = false;
            _progressPercent = 0;
            _statusText = "Kurulum sırasında hata oluştu.";
            SetControlsInteractive(true);
            Invalidate();

            MessageBox.Show(
                this,
                $"Kurulum sırasında hata oluştu:\n{ex.Message}",
                "Kurfamüzik Kurulum Hatası",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void SetControlsInteractive(bool enabled)
    {
        _txtPath.Enabled = enabled;
        _btnBrowse.Enabled = enabled;
        _btnDefault.Enabled = enabled;
        _rowDesktopShortcut.Enabled = enabled;
        _rowStartMenuShortcut.Enabled = enabled;
        _rowCleanupOld.Enabled = enabled;
        _rowRunOnStartup.Enabled = enabled;
        _btnInstall.Enabled = enabled;
        _btnCancel.Enabled = enabled;
    }

    public void SavePreviewPng(string outputPath)
    {
        CreateControl();
        PerformLayout();
        using var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            var pe = new PaintEventArgs(g, new Rectangle(Point.Empty, ClientSize));
            OnPaint(pe);

            // Render path text inside the input box
            TextRenderer.DrawText(
                g,
                _txtPath.Text,
                _txtPath.Font,
                new Rectangle(_txtPath.Left, _txtPath.Top, _txtPath.Width, _txtPath.Height),
                _txtPath.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);

            foreach (Control ctrl in Controls)
            {
                if (ctrl is IDrawablePreview drawable)
                {
                    using var childBmp = new Bitmap(ctrl.Width, ctrl.Height);
                    using (Graphics cg = Graphics.FromImage(childBmp))
                    {
                        drawable.RenderControl(cg);
                    }
                    g.DrawImage(childBmp, ctrl.Location);
                }
            }
        }
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        bmp.Save(outputPath, ImageFormat.Png);
    }

    private interface IDrawablePreview
    {
        void RenderControl(Graphics g);
    }

    internal static GraphicsPath CreateRoundRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
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

    private sealed class ModernToggleRow : Control, IDrawablePreview
    {
        private readonly string _title;
        private readonly string _subtitle;
        private bool _hovered;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Checked { get; set; }

        public ModernToggleRow(string title, string subtitle, bool initialChecked)
        {
            _title = title;
            _subtitle = subtitle;
            Checked = initialChecked;

            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            BackColor = Color.FromArgb(10, 11, 18);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Enabled) return;
            Checked = !Checked;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderControl(e.Graphics);
        }

        public void RenderControl(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? Color.FromArgb(10, 11, 18));

            Rectangle card = new(0, 0, Width - 1, Height - 1);
            Color fill = _hovered
                ? Color.FromArgb(24, 28, 46)
                : Color.FromArgb(17, 20, 33);
            Color border = Checked
                ? Color.FromArgb(88, 255, 46, 91)
                : (_hovered ? Color.FromArgb(52, 60, 94) : Color.FromArgb(32, 38, 62));

            using (GraphicsPath path = CreateRoundRect(card, 12))
            using (var bgBrush = new SolidBrush(fill))
            using (var borderPen = new Pen(border, 1f))
            {
                g.FillPath(bgBrush, path);
                g.DrawPath(borderPen, path);
            }

            using (var titleFont = new Font("Segoe UI", 9.5F, FontStyle.Bold))
            {
                TextRenderer.DrawText(
                    g,
                    _title,
                    titleFont,
                    new Point(14, 6),
                    Color.White);
            }

            using (var subFont = new Font("Segoe UI", 8.3F, FontStyle.Regular))
            {
                TextRenderer.DrawText(
                    g,
                    _subtitle,
                    subFont,
                    new Point(14, 26),
                    Color.FromArgb(148, 158, 192));
            }

            // Modern Pill Switch (44x24) on right
            Rectangle swRect = new(Width - 60, (Height - 24) / 2, 44, 24);
            using (GraphicsPath swPath = CreateRoundRect(swRect, 12))
            using (var swBrush = new SolidBrush(Checked ? Color.FromArgb(255, 46, 91) : Color.FromArgb(36, 42, 66)))
            {
                g.FillPath(swBrush, swPath);
            }

            int knobX = Checked ? (swRect.Right - 21) : (swRect.X + 3);
            Rectangle knobRect = new(knobX, swRect.Y + 3, 18, 18);
            using (var knobBrush = new SolidBrush(Checked ? Color.White : Color.FromArgb(148, 156, 188)))
            {
                g.FillEllipse(knobBrush, knobRect);
            }
        }
    }

    private sealed class ModernPillButton : Control, IDrawablePreview
    {
        private readonly string _label;
        private readonly bool _isAccent;
        private bool _hovered;

        public ModernPillButton(string label, bool isAccent)
        {
            _label = label;
            _isAccent = isAccent;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            BackColor = Color.FromArgb(17, 20, 33);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderControl(e.Graphics);
        }

        public void RenderControl(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(17, 20, 33));

            Rectangle r = new(0, 0, Width - 1, Height - 1);
            Color bg = _isAccent
                ? (_hovered ? Color.FromArgb(255, 68, 108) : Color.FromArgb(255, 46, 91))
                : (_hovered ? Color.FromArgb(38, 44, 72) : Color.FromArgb(27, 32, 52));
            Color border = _hovered ? Color.FromArgb(72, 82, 126) : Color.FromArgb(46, 54, 84);

            using (GraphicsPath path = CreateRoundRect(r, 8))
            using (var b = new SolidBrush(bg))
            using (var p = new Pen(border, 1f))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }

            using var f = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            TextRenderer.DrawText(
                g,
                _label,
                f,
                new Rectangle(0, 0, Width, Height),
                Color.FromArgb(230, 235, 252),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    private sealed class ModernActionButton : Control, IDrawablePreview
    {
        private readonly string _label;
        private readonly bool _isPrimary;
        private bool _hovered;

        public ModernActionButton(string label, bool isPrimary)
        {
            _label = label;
            _isPrimary = isPrimary;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            BackColor = Color.FromArgb(10, 11, 18);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderControl(e.Graphics);
        }

        public void RenderControl(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? Color.FromArgb(10, 11, 18));

            Rectangle r = new(0, 0, Width - 1, Height - 1);
            using GraphicsPath path = CreateRoundRect(r, 12);

            if (_isPrimary)
            {
                Color c1 = !Enabled
                    ? Color.FromArgb(90, 35, 55)
                    : (_hovered ? Color.FromArgb(255, 70, 112) : Color.FromArgb(255, 46, 91));
                Color c2 = !Enabled
                    ? Color.FromArgb(75, 28, 48)
                    : (_hovered ? Color.FromArgb(244, 38, 86) : Color.FromArgb(219, 24, 72));
                using var grad = new LinearGradientBrush(r, c1, c2, LinearGradientMode.Horizontal);
                g.FillPath(grad, path);
            }
            else
            {
                Color bg = _hovered ? Color.FromArgb(30, 35, 56) : Color.FromArgb(20, 24, 39);
                using var b = new SolidBrush(bg);
                using var p = new Pen(Color.FromArgb(42, 49, 78), 1f);
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }

            using var f = new Font("Segoe UI", _isPrimary ? 10F : 9.2F, _isPrimary ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(
                g,
                _label,
                f,
                new Rectangle(0, 0, Width, Height),
                _isPrimary ? Color.White : Color.FromArgb(205, 212, 236),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
