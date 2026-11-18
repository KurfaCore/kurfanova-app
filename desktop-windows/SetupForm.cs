using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;

namespace KurfanovaMuzik;

internal sealed class SetupForm : Form
{
    private readonly ExistingInstallInfo _existing;
    private readonly TextBox _txtPath;
    private readonly LightButton _btnBrowse;
    private readonly LightButton _btnDefault;
    private readonly LightToggleRow _rowDesktopShortcut;
    private readonly LightToggleRow _rowStartMenuShortcut;
    private readonly LightToggleRow _rowCleanupOld;
    private readonly LightToggleRow _rowRunOnStartup;
    private readonly LightButton _btnCancel;
    private readonly LightButton _btnInstall;
    private readonly Icon? _appIcon;
    private readonly Bitmap? _iconBitmap;
    private readonly float _scale;

    private string _statusText;
    private int _progressPercent;
    private bool _isInstalling;

    private static readonly Color BgWindow = Color.FromArgb(245, 246, 250);      // #F5F6FA
    private static readonly Color BgCard = Color.FromArgb(255, 255, 255);        // #FFFFFF
    private static readonly Color BgSubtle = Color.FromArgb(248, 250, 252);      // #F8FAFC
    private static readonly Color BorderCard = Color.FromArgb(226, 232, 240);    // #E2E8F0
    private static readonly Color BorderInput = Color.FromArgb(203, 213, 225);   // #CBD5E1
    private static readonly Color TextPrimary = Color.FromArgb(15, 23, 42);      // #0F172A
    private static readonly Color TextSecondary = Color.FromArgb(100, 116, 139); // #64748B
    private static readonly Color AccentBrand = Color.FromArgb(255, 32, 86);     // #FF2056
    private static readonly Color AccentHover = Color.FromArgb(225, 29, 72);     // #E11D48

    private const int MarginX = 24;
    private const int HeaderY = 20;
    private const int HeaderH = 84;
    private const int LocationY = 118;
    private const int LocationH = 104;
    private const int OptionsY = 236;
    private const int OptionsH = 196;
    private const int FooterY = 446;

    public SetupForm(ExistingInstallInfo existing)
    {
        _existing = existing;

        Text = "Kurfamüzik Kurulumu";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoScaleMode = AutoScaleMode.None;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        _scale = Math.Clamp((DeviceDpi > 0 ? DeviceDpi : 96) / 96f, 1f, 2f);
        ClientSize = new Size(580, 520);
        BackColor = BgWindow;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);
        DoubleBuffered = true;

        _appIcon = LoadEmbeddedIcon();
        if (_appIcon is not null)
        {
            Icon = _appIcon;
        }
        _iconBitmap = LoadEmbeddedLogoBitmap();

        string initialTarget = !string.IsNullOrWhiteSpace(_existing.PreviousInstallDir)
            ? _existing.PreviousInstallDir!
            : AppPaths.DefaultInstallDir;

        _statusText = _existing.Found
            ? $"Güncellemeye hazır (v{_existing.ExistingVersion ?? "eski"})."
            : "Kuruluma hazır.";

        int contentW = ClientSize.Width - MarginX * 2; // 532

        // Path input inside Location card (input + 2 action pills)
        _txtPath = new TextBox
        {
            Location = new Point(MarginX + 22, LocationY + 42),
            Size = new Size(310, 22),
            Text = initialTarget,
            BackColor = BgSubtle,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
        };
        Controls.Add(_txtPath);

        _btnBrowse = new LightButton("Gözat", isPrimary: false)
        {
            Location = new Point(MarginX + 350, LocationY + 35),
            Size = new Size(76, 34)
        };
        _btnBrowse.Click += (_, _) => BrowseFolder();
        Controls.Add(_btnBrowse);

        _btnDefault = new LightButton("Varsayılan", isPrimary: false)
        {
            Location = new Point(MarginX + 432, LocationY + 35),
            Size = new Size(86, 34)
        };
        _btnDefault.Click += (_, _) => _txtPath.Text = AppPaths.DefaultInstallDir;
        Controls.Add(_btnDefault);

        // Clean single-line toggle rows inside Options card
        int rowX = MarginX + 16;
        int rowW = contentW - 32;
        int rowH = 44;

        _rowDesktopShortcut = new LightToggleRow("Masaüstü kısayolu oluştur", isChecked: true, showDivider: true)
        {
            Location = new Point(rowX, OptionsY + 10),
            Size = new Size(rowW, rowH)
        };
        Controls.Add(_rowDesktopShortcut);

        _rowStartMenuShortcut = new LightToggleRow("Başlat menüsüne ekle", isChecked: true, showDivider: true)
        {
            Location = new Point(rowX, OptionsY + 10 + rowH),
            Size = new Size(rowW, rowH)
        };
        Controls.Add(_rowStartMenuShortcut);

        _rowCleanupOld = new LightToggleRow("Eski kurulum kalıntılarını temizle", isChecked: true, showDivider: true)
        {
            Location = new Point(rowX, OptionsY + 10 + rowH * 2),
            Size = new Size(rowW, rowH)
        };
        Controls.Add(_rowCleanupOld);

        _rowRunOnStartup = new LightToggleRow("Windows açılışında başlat", isChecked: false, showDivider: false)
        {
            Location = new Point(rowX, OptionsY + 10 + rowH * 3),
            Size = new Size(rowW, rowH)
        };
        Controls.Add(_rowRunOnStartup);

        // Footer buttons
        _btnCancel = new LightButton("İptal", isPrimary: false)
        {
            Location = new Point(ClientSize.Width - MarginX - 96 - 12 - 168, FooterY + 18),
            Size = new Size(96, 40)
        };
        _btnCancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(_btnCancel);

        string installLabel = _existing.Found ? "Güncelle ve Aç" : "Kurulumu Başlat";
        _btnInstall = new LightButton(installLabel, isPrimary: true)
        {
            Location = new Point(ClientSize.Width - MarginX - 168, FooterY + 18),
            Size = new Size(168, 40)
        };
        _btnInstall.Click += async (_, _) => await StartInstallAsync();
        Controls.Add(_btnInstall);

        AcceptButton = _btnInstall;
        CancelButton = _btnCancel;

        if (_scale != 1f)
        {
            Scale(new SizeF(_scale, _scale));
            ClientSize = new Size((int)Math.Round(580 * _scale), (int)Math.Round(520 * _scale));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (_scale != 1f)
        {
            g.ScaleTransform(_scale, _scale);
        }

        int contentW = 580 - MarginX * 2;
        string appVersion = AppPaths.GetNormalizedCurrentVersion().ToString(3);

        // 1. Header Card
        var headerRect = new Rectangle(MarginX, HeaderY, contentW, HeaderH);
        DrawCard(g, headerRect, 14);

        var iconFrame = new Rectangle(MarginX + 18, HeaderY + 18, 48, 48);
        using (GraphicsPath iconPath = CreateRoundedRect(iconFrame, 12))
        using (var iconBg = new SolidBrush(Color.FromArgb(255, 241, 244)))
        using (var iconBorder = new Pen(Color.FromArgb(254, 205, 211), 1f))
        {
            g.FillPath(iconBg, iconPath);
            g.DrawPath(iconBorder, iconPath);
        }
        if (_iconBitmap is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_iconBitmap, new Rectangle(iconFrame.X + 8, iconFrame.Y + 8, 32, 32));
        }

        using (var titleFont = new Font("Segoe UI", 13.5F, FontStyle.Bold))
        using (var subFont = new Font("Segoe UI", 9.25F, FontStyle.Regular))
        using (var titleBrush = new SolidBrush(TextPrimary))
        using (var subBrush = new SolidBrush(TextSecondary))
        {
            g.DrawString("Kurfamüzik Kurulumu", titleFont, titleBrush, MarginX + 80, HeaderY + 19);
            g.DrawString("Masaüstü uygulaması • Reklamsız sürüm", subFont, subBrush, MarginX + 81, HeaderY + 46);
        }

        // Clean right version pill
        string verText = $"v{appVersion}";
        using (var pillFont = new Font("Segoe UI", 8.5F, FontStyle.Bold))
        {
            SizeF textSize = g.MeasureString(verText, pillFont);
            int pillW = (int)Math.Ceiling(textSize.Width) + 18;
            int pillH = 26;
            var pillRect = new Rectangle(headerRect.Right - pillW - 18, HeaderY + 29, pillW, pillH);
            using GraphicsPath pillPath = CreateRoundedRect(pillRect, 13);
            using var pillBg = new SolidBrush(BgSubtle);
            using var pillBorder = new Pen(BorderCard, 1f);
            using var pillTextBrush = new SolidBrush(TextPrimary);
            g.FillPath(pillBg, pillPath);
            g.DrawPath(pillBorder, pillPath);

            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(verText, pillFont, pillTextBrush, pillRect, sf);
        }

        // 2. Install Location Card
        var locRect = new Rectangle(MarginX, LocationY, contentW, LocationH);
        DrawCard(g, locRect, 14);

        using (var labelFont = new Font("Segoe UI", 8.5F, FontStyle.Bold))
        using (var labelBrush = new SolidBrush(TextSecondary))
        {
            g.DrawString("KURULUM KLASÖRÜ", labelFont, labelBrush, MarginX + 16, LocationY + 12);
        }

        var inputBoxRect = new Rectangle(MarginX + 16, LocationY + 35, 326, 34);
        using (GraphicsPath inputPath = CreateRoundedRect(inputBoxRect, 8))
        using (var inputBg = new SolidBrush(BgSubtle))
        using (var inputPen = new Pen(BorderInput, 1f))
        {
            g.FillPath(inputBg, inputPath);
            g.DrawPath(inputPen, inputPath);
        }

        using (var hintFont = new Font("Segoe UI", 8.25F, FontStyle.Regular))
        using (var hintBrush = new SolidBrush(_existing.Found ? Color.FromArgb(5, 150, 105) : TextSecondary))
        {
            string hint = _existing.Found
                ? $"Mevcut kurulum (v{_existing.ExistingVersion ?? "eski"}) korunarak güncellenir."
                : "Standart kullanıcı dizinine yönetici izni olmadan kurulur.";
            g.DrawString(hint, hintFont, hintBrush, MarginX + 16, LocationY + 78);
        }

        // 3. Options Card
        var optsRect = new Rectangle(MarginX, OptionsY, contentW, OptionsH);
        DrawCard(g, optsRect, 14);

        // 4. Footer Status & Progress Strip
        using (var statusFont = new Font("Segoe UI", 8.5F, FontStyle.Regular))
        using (var statusBrush = new SolidBrush(TextSecondary))
        {
            var statusRect = new Rectangle(MarginX, FooterY + 12, 240, 20);
            using var sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(_statusText, statusFont, statusBrush, statusRect, sfLeft);
        }

        var trackRect = new Rectangle(MarginX, FooterY + 38, 232, 6);
        using (GraphicsPath trackPath = CreateRoundedRect(trackRect, 3))
        using (var trackBrush = new SolidBrush(BorderCard))
        {
            g.FillPath(trackBrush, trackPath);
        }

        if (_progressPercent > 0)
        {
            int fillW = Math.Max(8, (int)(trackRect.Width * (Math.Clamp(_progressPercent, 0, 100) / 100.0)));
            var fillRect = new Rectangle(trackRect.X, trackRect.Y, fillW, trackRect.Height);
            using GraphicsPath fillPath = CreateRoundedRect(fillRect, 3);
            using var fillBrush = new SolidBrush(AccentBrand);
            g.FillPath(fillBrush, fillPath);
        }
    }

    private static void DrawCard(Graphics g, Rectangle rect, int radius)
    {
        using GraphicsPath path = CreateRoundedRect(rect, radius);
        using var bg = new SolidBrush(BgCard);
        using var border = new Pen(BorderCard, 1f);
        g.FillPath(bg, path);
        g.DrawPath(border, path);
    }

    private void BrowseFolder()
    {
        using var fbd = new FolderBrowserDialog
        {
            Description = "Kurfamüzik kurulum klasörünü seçin",
            ShowNewFolderButton = true
        };
        if (Directory.Exists(_txtPath.Text))
        {
            fbd.SelectedPath = _txtPath.Text;
        }
        if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
        {
            _txtPath.Text = fbd.SelectedPath;
        }
    }

    private async Task StartInstallAsync()
    {
        if (_isInstalling) return;

        string targetDir = _txtPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            targetDir = AppPaths.DefaultInstallDir;
            _txtPath.Text = targetDir;
        }

        _isInstalling = true;
        _txtPath.Enabled = false;
        _btnBrowse.Enabled = false;
        _btnDefault.Enabled = false;
        _btnCancel.Enabled = false;
        _btnInstall.Enabled = false;
        _rowDesktopShortcut.Enabled = false;
        _rowStartMenuShortcut.Enabled = false;
        _rowCleanupOld.Enabled = false;
        _rowRunOnStartup.Enabled = false;

        _progressPercent = 18;
        _statusText = "Kurulum hazırlanıyor...";
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
            await Task.Run(() =>
            {
                InstallerService.InstallOrUpgrade(opts, msg =>
                {
                    if (!IsDisposed)
                    {
                        BeginInvoke(() =>
                        {
                            _statusText = msg;
                            if (_progressPercent < 88) _progressPercent += 14;
                            Invalidate();
                        });
                    }
                });
            });

            _progressPercent = 100;
            _statusText = "Kurulum tamamlandı.";
            Invalidate();
            await Task.Delay(260);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _isInstalling = false;
            _txtPath.Enabled = true;
            _btnBrowse.Enabled = true;
            _btnDefault.Enabled = true;
            _btnCancel.Enabled = true;
            _btnInstall.Enabled = true;
            _rowDesktopShortcut.Enabled = true;
            _rowStartMenuShortcut.Enabled = true;
            _rowCleanupOld.Enabled = true;
            _rowRunOnStartup.Enabled = true;
            _progressPercent = 0;
            _statusText = "Kurulum hatası oluştu.";
            Invalidate();

            MessageBox.Show(
                this,
                $"Kurulum sırasında bir hata oluştu:\n\n{ex.Message}",
                "Kurfamüzik Kurulum Hatası",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    public void SavePreviewPng(string outputPath)
    {
        CreateControl();
        PerformLayout();

        using var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(BackColor);
            using var pe = new PaintEventArgs(g, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            OnPaint(pe);

            foreach (Control ctrl in Controls)
            {
                GraphicsState state = g.Save();
                g.TranslateTransform(ctrl.Left, ctrl.Top);
                if (ctrl is IDrawablePreview preview)
                {
                    preview.PaintTo(g, ctrl.ClientSize);
                }
                else if (ctrl is TextBox tb)
                {
                    using var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap };
                    using var tbBrush = new SolidBrush(TextPrimary);
                    g.DrawString(tb.Text, tb.Font, tbBrush, new RectangleF(0, 0, tb.Width, tb.Height), sf);
                }
                g.Restore(state);
            }
        }

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        bmp.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _iconBitmap?.Dispose();
            _appIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
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

    private static Bitmap? LoadEmbeddedLogoBitmap()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KurfanovaMuzik.tray-logo.png");
            if (stream is not null)
            {
                using var tmp = new Bitmap(stream);
                return new Bitmap(tmp);
            }
        }
        catch { }
        return null;
    }

    private interface IDrawablePreview
    {
        void PaintTo(Graphics g, Size size);
    }

    private sealed class LightToggleRow : Control, IDrawablePreview
    {
        private readonly string _title;
        private readonly bool _showDivider;
        private bool _checked;
        private bool _hovered;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked != value)
                {
                    _checked = value;
                    Invalidate();
                }
            }
        }

        public LightToggleRow(string title, bool isChecked, bool showDivider)
        {
            _title = title;
            _checked = isChecked;
            _showDivider = showDivider;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled)
            {
                Checked = !Checked;
            }
            base.OnClick(e);
        }

        public void PaintTo(Graphics g, Size size)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            if (_hovered && Enabled)
            {
                using GraphicsPath hoverPath = CreateRoundedRect(new Rectangle(0, 2, size.Width, size.Height - 4), 8);
                using var hoverBg = new SolidBrush(Color.FromArgb(248, 250, 252));
                g.FillPath(hoverBg, hoverPath);
            }

            using (var textBrush = new SolidBrush(Enabled ? TextPrimary : TextSecondary))
            using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(_title, Font, textBrush, new RectangleF(10, 0, size.Width - 70, size.Height), sf);
            }

            // Clean capsule switch
            const int swW = 38;
            const int swH = 22;
            var swRect = new Rectangle(size.Width - swW - 10, (size.Height - swH) / 2, swW, swH);
            using (GraphicsPath swPath = CreateRoundedRect(swRect, 11))
            using (var swBrush = new SolidBrush(_checked ? AccentBrand : Color.FromArgb(203, 213, 225)))
            {
                g.FillPath(swBrush, swPath);
            }

            int knobSize = 16;
            int knobX = _checked ? swRect.Right - knobSize - 3 : swRect.X + 3;
            int knobY = swRect.Y + 3;
            using (var knobBrush = new SolidBrush(Color.White))
            {
                g.FillEllipse(knobBrush, knobX, knobY, knobSize, knobSize);
            }

            if (_showDivider)
            {
                using var divPen = new Pen(Color.FromArgb(241, 245, 249), 1f);
                g.DrawLine(divPen, 10, size.Height - 1, size.Width - 10, size.Height - 1);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            PaintTo(e.Graphics, ClientSize);
        }
    }

    private sealed class LightButton : Control, IDrawablePreview, IButtonControl
    {
        private readonly bool _isPrimary;
        private bool _hovered;
        private bool _pressed;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value)
        {
        }

        public void PerformClick()
        {
            if (Enabled) OnClick(EventArgs.Empty);
        }

        public LightButton(string text, bool isPrimary)
        {
            Text = text;
            _isPrimary = isPrimary;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 9.25F, isPrimary ? FontStyle.Bold : FontStyle.Regular);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        public void PaintTo(Graphics g, Size size)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var rect = new Rectangle(0, 0, size.Width - 1, size.Height - 1);
            using GraphicsPath path = CreateRoundedRect(rect, 9);

            Color fillColor;
            Color borderColor;
            Color textColor;

            if (_isPrimary)
            {
                fillColor = !Enabled ? Color.FromArgb(203, 213, 225) : (_pressed ? Color.FromArgb(190, 18, 60) : (_hovered ? AccentHover : AccentBrand));
                borderColor = fillColor;
                textColor = Color.White;
            }
            else
            {
                fillColor = !Enabled ? BgSubtle : (_pressed ? Color.FromArgb(226, 232, 240) : (_hovered ? Color.FromArgb(241, 245, 249) : BgCard));
                borderColor = BorderInput;
                textColor = Enabled ? TextPrimary : TextSecondary;
            }

            using (var fill = new SolidBrush(fillColor))
            {
                g.FillPath(fill, path);
            }
            using (var border = new Pen(borderColor, 1f))
            {
                g.DrawPath(border, path);
            }

            using var textBrush = new SolidBrush(textColor);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Text, Font, textBrush, rect, sf);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            PaintTo(e.Graphics, ClientSize);
        }
    }
}
