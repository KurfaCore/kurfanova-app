using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace KurfanovaMuzik;

/// <summary>
/// Clean, anti-aliased rounded tray popup menu compatible with both Windows 10 and Windows 11.
/// Uses 32-bit premultiplied ARGB UpdateLayeredWindow for smooth corners and soft drop shadow on every Windows build.
/// </summary>
internal sealed class TrayMenuForm : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopMost = 0x00000008;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private const uint UlwAlpha = 0x00000002;

    private const int LogicalCardW = 228;
    private const int LogicalShadow = 10;
    private const int LogicalCorner = 12;

    private readonly string _versionLabel;
    private readonly Action _onToggleWindow;
    private readonly Action _onCheckUpdates;
    private readonly Action _onExit;
    private readonly Bitmap? _logoBitmap;

    private bool _windowVisible;
    private string _lang = "tr";
    private int _hoverIndex = -1; // 0: toggle, 1: update, 2: exit
    private float _scale = 1f;
    private Rectangle[] _itemRects = new Rectangle[3];
    private int _cardW;
    private int _cardH;
    private int _shadowPx;

    public TrayMenuForm(
        string versionLabel,
        Action onToggleWindow,
        Action onCheckUpdates,
        Action onExit)
    {
        _versionLabel = versionLabel;
        _onToggleWindow = onToggleWindow;
        _onCheckUpdates = onCheckUpdates;
        _onExit = onExit;
        _logoBitmap = LoadTrayLogoBitmap();

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        UpdateMetricsForDpi(96);

        MouseMove += OnMenuMouseMove;
        MouseLeave += (_, _) =>
        {
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Cursor = Cursors.Default;
                RenderLayered();
            }
        };
        MouseDown += OnMenuMouseDown;
        KeyDown += OnMenuKeyDown;
        Deactivate += (_, _) => Hide();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExToolWindow | WsExTopMost;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => false;

    public void ShowAtCursor(bool isWindowCurrentlyVisible)
    {
        _windowVisible = isWindowCurrentlyVisible;
        _hoverIndex = -1;

        Point cursor = Cursor.Position;
        Location = new Point(cursor.X, cursor.Y);
        if (!IsHandleCreated)
        {
            CreateHandle();
        }
        Screen screen = Screen.FromPoint(cursor);
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        UpdateMetricsForDpi(dpi);

        Rectangle wa = screen.WorkingArea;
        int totalW = _cardW + _shadowPx * 2;
        int totalH = _cardH + _shadowPx * 2;

        // Place neatly above/left of cursor, clamped inside active monitor working area
        int x = cursor.X - totalW / 2;
        int y = cursor.Y - _cardH - _shadowPx - (int)Math.Round(8 * _scale);

        if (y < wa.Top - _shadowPx + 4)
            y = cursor.Y - _shadowPx + (int)Math.Round(8 * _scale);
        if (x + _cardW + _shadowPx > wa.Right - 6)
            x = wa.Right - _cardW - _shadowPx - 6;
        if (x + _shadowPx < wa.Left + 6)
            x = wa.Left - _shadowPx + 6;
        if (y + _cardH + _shadowPx > wa.Bottom - 6)
            y = wa.Bottom - _cardH - _shadowPx - 6;

        Size = new Size(totalW, totalH);
        Location = new Point(x, y);

        RenderLayered();
        if (!Visible)
        {
            Show();
        }
        Activate();
    }

    public void SetLanguage(string? lang)
    {
        string normalized = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";
        if (_lang == normalized) return;
        _lang = normalized;
        if (Visible && IsHandleCreated && !IsDisposed)
        {
            RenderLayered();
        }
    }

    public void SavePreviewPng(string outputPath)
    {
        _windowVisible = false;
        _hoverIndex = 0; // show subtle hover highlight on primary action for visual QA
        UpdateMetricsForDpi(96);
        using var bmp = DrawMenuBitmap();
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        bmp.Save(outputPath, ImageFormat.Png);
    }

    private void UpdateMetricsForDpi(int dpi)
    {
        _scale = Math.Clamp(dpi / 96f, 1f, 2.5f);
        _shadowPx = S(LogicalShadow);
        _cardW = S(LogicalCardW);

        int padX = S(8);
        int headerH = S(40);
        int rowH = S(34);
        int gap = S(3);
        int sepSpace = S(9);

        int y = _shadowPx + headerH + sepSpace;
        _itemRects[0] = new Rectangle(_shadowPx + padX, y, _cardW - padX * 2, rowH);
        y += rowH + gap;
        _itemRects[1] = new Rectangle(_shadowPx + padX, y, _cardW - padX * 2, rowH);
        y += rowH + sepSpace;
        _itemRects[2] = new Rectangle(_shadowPx + padX, y, _cardW - padX * 2, rowH);
        y += rowH + S(8);

        _cardH = y - _shadowPx;
    }

    private int S(float logicalPx) => (int)Math.Round(logicalPx * _scale);

    private static Bitmap? LoadTrayLogoBitmap()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using Stream? s = asm.GetManifestResourceStream("KurfanovaMuzik.tray-logo.png");
            if (s is not null)
            {
                using var temp = new Bitmap(s);
                return new Bitmap(temp);
            }
        }
        catch { }
        return null;
    }

    private void OnMenuMouseMove(object? sender, MouseEventArgs e)
    {
        int next = HitTestItem(e.Location);
        if (next != _hoverIndex)
        {
            _hoverIndex = next;
            Cursor = next >= 0 ? Cursors.Hand : Cursors.Default;
            RenderLayered();
        }
    }

    private void OnMenuMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int idx = HitTestItem(e.Location);
        if (idx >= 0) ExecuteItem(idx);
    }

    private void OnMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Down)
        {
            _hoverIndex = (_hoverIndex + 1) % 3;
            RenderLayered();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Up)
        {
            _hoverIndex = _hoverIndex <= 0 ? 2 : _hoverIndex - 1;
            RenderLayered();
            e.Handled = true;
            return;
        }
        if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && _hoverIndex >= 0)
        {
            ExecuteItem(_hoverIndex);
            e.Handled = true;
        }
    }

    private int HitTestItem(Point p)
    {
        for (int i = 0; i < _itemRects.Length; i++)
        {
            if (_itemRects[i].Contains(p)) return i;
        }
        return -1;
    }

    private void ExecuteItem(int index)
    {
        Hide();
        switch (index)
        {
            case 0:
                _onToggleWindow();
                break;
            case 1:
                _onCheckUpdates();
                break;
            case 2:
                _onExit();
                break;
        }
    }

    private Bitmap DrawMenuBitmap()
    {
        int totalW = _cardW + _shadowPx * 2;
        int totalH = _cardH + _shadowPx * 2;
        var bmp = new Bitmap(totalW, totalH, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        int radius = S(LogicalCorner);
        var cardRect = new Rectangle(_shadowPx, _shadowPx, _cardW, _cardH);

        // Soft ambient drop shadow compatible with both Win10 and Win11
        for (int i = 6; i >= 1; i--)
        {
            int inflate = S(i * 1.15f);
            int yShift = S(i * 0.45f);
            var sr = new Rectangle(cardRect.X - inflate, cardRect.Y - inflate + yShift, cardRect.Width + inflate * 2, cardRect.Height + inflate * 2);
            using GraphicsPath sp = CreateRoundedPath(sr, radius + inflate);
            using var sb = new SolidBrush(Color.FromArgb(Math.Max(4, 24 - i * 3), 0, 0, 0));
            g.FillPath(sb, sp);
        }

        // Card surface (#13151F dark glass)
        using (GraphicsPath cardPath = CreateRoundedPath(cardRect, radius))
        {
            using var bgBrush = new SolidBrush(Color.FromArgb(248, 19, 21, 31));
            g.FillPath(bgBrush, cardPath);

            // Top specular highlight inside clip
            g.SetClip(cardPath);
            using (var headerGrad = new LinearGradientBrush(
                new Rectangle(cardRect.X, cardRect.Y, cardRect.Width, S(48)),
                Color.FromArgb(14, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(headerGrad, cardRect.X, cardRect.Y, cardRect.Width, S(48));
            }
            g.ResetClip();

            using var rimPen = new Pen(Color.FromArgb(30, 255, 255, 255), 1f);
            var borderRect = new Rectangle(cardRect.X, cardRect.Y, cardRect.Width - 1, cardRect.Height - 1);
            using GraphicsPath borderPath = CreateRoundedPath(borderRect, radius);
            g.DrawPath(rimPen, borderPath);
        }

        // Header row: Logo (20x20) + "Kurfamüzik" + Version Pill
        int headerY = cardRect.Y + S(11);
        int logoSize = S(20);
        int logoX = cardRect.X + S(14);
        if (_logoBitmap is not null)
        {
            g.DrawImage(_logoBitmap, new Rectangle(logoX, headerY, logoSize, logoSize));
        }

        int titleX = logoX + logoSize + S(9);
        using var fontBrand = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        using var brushLight = new SolidBrush(Color.FromArgb(244, 244, 245));
        g.DrawString("Kurfamüzik", fontBrand, brushLight, new PointF(titleX, headerY + S(1)));

        using var fontVer = new Font("Segoe UI", 7.5F, FontStyle.Bold);
        SizeF verSize = g.MeasureString(_versionLabel, fontVer);
        int pillW = (int)Math.Ceiling(verSize.Width) + S(12);
        int pillH = S(18);
        var pillRect = new Rectangle(cardRect.Right - S(12) - pillW, headerY + S(1), pillW, pillH);
        using (GraphicsPath pillPath = CreateRoundedPath(pillRect, pillH / 2))
        using (var pillBg = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
        using (var pillBorder = new Pen(Color.FromArgb(24, 255, 255, 255), 1f))
        {
            g.FillPath(pillBg, pillPath);
            g.DrawPath(pillBorder, pillPath);
        }
        using var brushMuted = new SolidBrush(Color.FromArgb(161, 161, 178));
        using var sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(_versionLabel, fontVer, brushMuted, new RectangleF(pillRect.X, pillRect.Y + 0.5f, pillRect.Width, pillRect.Height), sfCenter);

        // First separator
        int sep1Y = cardRect.Y + S(42);
        using (var sepPen = new Pen(Color.FromArgb(20, 255, 255, 255), 1f))
        {
            g.DrawLine(sepPen, cardRect.X + S(10), sep1Y, cardRect.Right - S(10), sep1Y);
        }

        bool isEn = string.Equals(_lang, "en", StringComparison.OrdinalIgnoreCase);

        // Item 0: Pencereyi Gizle / Pencereyi Aç (Hide Window / Open Window)
        string toggleLabel = isEn
            ? (_windowVisible ? "Hide Window" : "Open Window")
            : (_windowVisible ? "Pencereyi Gizle" : "Pencereyi Aç");
        DrawMenuItem(g, 0, _itemRects[0], toggleLabel, false, IconKind.Window);

        // Item 1: Güncelleme Denetle / Check Updates
        DrawMenuItem(g, 1, _itemRects[1], isEn ? "Check Updates" : "Güncelleme Denetle", false, IconKind.Update);

        // Second separator
        int sep2Y = _itemRects[1].Bottom + S(4);
        using (var sepPen2 = new Pen(Color.FromArgb(20, 255, 255, 255), 1f))
        {
            g.DrawLine(sepPen2, cardRect.X + S(10), sep2Y, cardRect.Right - S(10), sep2Y);
        }

        // Item 2: Çıkış / Exit
        DrawMenuItem(g, 2, _itemRects[2], isEn ? "Exit" : "Çıkış", true, IconKind.Exit);

        return bmp;
    }

    private enum IconKind { Window, Update, Exit }

    private void DrawMenuItem(Graphics g, int idx, Rectangle rect, string label, bool danger, IconKind icon)
    {
        bool hovered = _hoverIndex == idx;
        if (hovered)
        {
            using GraphicsPath hp = CreateRoundedPath(rect, S(8));
            Color hoverBg = danger ? Color.FromArgb(30, 251, 113, 133) : Color.FromArgb(24, 255, 255, 255);
            using var hb = new SolidBrush(hoverBg);
            g.FillPath(hb, hp);
        }

        Color fg = danger
            ? Color.FromArgb(251, 113, 133)
            : (hovered ? Color.FromArgb(255, 255, 255) : Color.FromArgb(228, 228, 235));
        Color iconColor = danger
            ? Color.FromArgb(251, 113, 133)
            : (hovered ? Color.FromArgb(255, 59, 92) : Color.FromArgb(168, 172, 188));

        int iconSize = S(14);
        int ix = rect.X + S(10);
        int iy = rect.Y + (rect.Height - iconSize) / 2;
        DrawVectorIcon(g, icon, new RectangleF(ix, iy, iconSize, iconSize), iconColor);

        using var fontItem = new Font("Segoe UI", 9.25F, danger ? FontStyle.Bold : FontStyle.Regular);
        using var textBrush = new SolidBrush(fg);
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        var textRect = new RectangleF(ix + iconSize + S(10), rect.Y, rect.Width - (iconSize + S(24)), rect.Height);
        g.DrawString(label, fontItem, textBrush, textRect, sf);
    }

    private void DrawVectorIcon(Graphics g, IconKind kind, RectangleF r, Color color)
    {
        float stroke = Math.Max(1.3f, 1.4f * _scale);
        using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        switch (kind)
        {
            case IconKind.Window:
            {
                var box = new RectangleF(r.X + r.Width * 0.12f, r.Y + r.Height * 0.14f, r.Width * 0.76f, r.Height * 0.72f);
                using GraphicsPath gp = CreateRoundedPathF(box, r.Width * 0.16f);
                g.DrawPath(pen, gp);
                float barY = box.Y + box.Height * 0.30f;
                g.DrawLine(pen, box.X + 0.5f, barY, box.Right - 0.5f, barY);
                break;
            }
            case IconKind.Update:
            {
                var arc = new RectangleF(r.X + r.Width * 0.14f, r.Y + r.Height * 0.14f, r.Width * 0.72f, r.Height * 0.72f);
                g.DrawArc(pen, arc, -60, 275);
                // Arrowhead at top-right
                float ax = arc.Right - arc.Width * 0.12f;
                float ay = arc.Y + arc.Height * 0.18f;
                float aw = r.Width * 0.22f;
                g.DrawLine(pen, ax, ay, ax - aw, ay - aw * 0.15f);
                g.DrawLine(pen, ax, ay, ax + aw * 0.15f, ay - aw);
                break;
            }
            case IconKind.Exit:
            {
                // Clean power glyph
                var arc = new RectangleF(r.X + r.Width * 0.16f, r.Y + r.Height * 0.20f, r.Width * 0.68f, r.Height * 0.68f);
                g.DrawArc(pen, arc, -52, 284);
                float cx = r.X + r.Width * 0.5f;
                g.DrawLine(pen, cx, r.Y + r.Height * 0.10f, cx, r.Y + r.Height * 0.46f);
                break;
            }
        }
    }

    private void RenderLayered()
    {
        if (IsDisposed || !IsHandleCreated) return;
        using Bitmap bmp = DrawMenuBitmap();
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBmp = SelectObject(memDc, hBmp);
        try
        {
            var size = new NativeSize(bmp.Width, bmp.Height);
            var srcPt = new NativePoint(0, 0);
            var topPos = new NativePoint(Left, Top);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };
            UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref srcPt, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(memDc, oldBmp);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath CreateRoundedPathF(RectangleF bounds, float radius)
    {
        float d = Math.Max(1f, Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logoBitmap?.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int cx, int cy)
    {
        public int CX = cx;
        public int CY = cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize,
        IntPtr hdcSrc, ref NativePoint pptSrc, int crKey, ref BlendFunction pblend, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);
}
