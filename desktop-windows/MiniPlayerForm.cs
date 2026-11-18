using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Net.Http;
using System.Windows.Forms;

namespace KurfanovaMuzik;

public sealed class MiniPlayerForm : Form
{
    private static readonly HttpClient ThumbClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly Action _onPrev;
    private readonly Action _onTogglePlay;
    private readonly Action _onNext;
    private readonly Action _onToggleMute;
    private readonly Action<double> _onSeekFraction;
    private readonly Action _onRestoreMain;

    private string _title = "Kurfamüzik";
    private string _artist = "Bir parça seçin";
    private bool _isPlaying;
    private bool _isMuted;
    private double _curSec;
    private double _durSec;
    private string? _lastThumbUrl;
    private Bitmap? _coverBitmap;
    private float _scale = 1f;

    private readonly Rectangle _coverRect = new(14, 14, 68, 68);
    private readonly Rectangle _closeRect = new(298, 10, 22, 22);
    private readonly Rectangle _prevRect = new(98, 53, 28, 28);
    private readonly Rectangle _playRect = new(132, 51, 32, 32);
    private readonly Rectangle _nextRect = new(170, 53, 28, 28);
    private readonly Rectangle _muteRect = new(204, 53, 28, 28);
    private readonly Rectangle _trackHitRect = new(14, 86, 304, 16);

    private Point _dragStart;
    private bool _draggingWindow;
    private bool _scrubbingSeek;
    private int _hoverBtn; // 1=prev, 2=play, 3=next, 4=close, 5=mute, 6=seek

    public MiniPlayerForm(
        Action onPrev,
        Action onTogglePlay,
        Action onNext,
        Action onToggleMute,
        Action<double> onSeekFraction,
        Action onRestoreMain)
    {
        _onPrev = onPrev;
        _onTogglePlay = onTogglePlay;
        _onNext = onNext;
        _onToggleMute = onToggleMute;
        _onSeekFraction = onSeekFraction;
        _onRestoreMain = onRestoreMain;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(20, 23, 32);

        UpdateScaleForDpi(DeviceDpi);
        Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Right - Width - (int)Math.Round(20 * _scale), wa.Bottom - Height - (int)Math.Round(20 * _scale));
        ApplyRoundedCorners();

        MouseDown += OnCardMouseDown;
        MouseMove += OnCardMouseMove;
        MouseUp += OnCardMouseUp;
        MouseLeave += (_, _) =>
        {
            if (!_scrubbingSeek && _hoverBtn != 0)
            {
                _hoverBtn = 0;
                Cursor = Cursors.Default;
                Invalidate();
            }
        };
        DoubleClick += (_, e) =>
        {
            if (_coverRect.Contains(ToLogical(((MouseEventArgs)e).Location))) _onRestoreMain();
        };
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateScaleForDpi(e.DeviceDpiNew);
        ApplyRoundedCorners();
        Invalidate();
    }

    private void UpdateScaleForDpi(int dpi)
    {
        _scale = Math.Clamp((dpi > 0 ? dpi : 96) / 96f, 1f, 2.5f);
        Size = new Size((int)Math.Round(332 * _scale), (int)Math.Round(104 * _scale));
    }

    private Point ToLogical(Point physical) =>
        new((int)Math.Round(physical.X / _scale), (int)Math.Round(physical.Y / _scale));

    private void OnCardMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Point lp = ToLogical(e.Location);
        if (_trackHitRect.Contains(lp) && _durSec > 0)
        {
            _scrubbingSeek = true;
            double frac = Math.Clamp((lp.X - _trackHitRect.X) / (double)_trackHitRect.Width, 0.0, 1.0);
            _curSec = frac * _durSec;
            Invalidate();
            return;
        }
        if (_closeRect.Contains(lp)) { Hide(); return; }
        if (_prevRect.Contains(lp)) { _onPrev(); return; }
        if (_playRect.Contains(lp)) { _onTogglePlay(); return; }
        if (_nextRect.Contains(lp)) { _onNext(); return; }
        if (_muteRect.Contains(lp)) { _onToggleMute(); return; }

        _draggingWindow = true;
        _dragStart = e.Location;
    }

    private void OnCardMouseMove(object? sender, MouseEventArgs e)
    {
        Point lp = ToLogical(e.Location);
        if (_scrubbingSeek && _durSec > 0)
        {
            double frac = Math.Clamp((lp.X - _trackHitRect.X) / (double)_trackHitRect.Width, 0.0, 1.0);
            _curSec = frac * _durSec;
            Invalidate();
            return;
        }

        if (_draggingWindow)
        {
            Point screenPos = PointToScreen(e.Location);
            Location = new Point(screenPos.X - _dragStart.X, screenPos.Y - _dragStart.Y);
            return;
        }

        int nextHover = _prevRect.Contains(lp) ? 1
            : _playRect.Contains(lp) ? 2
            : _nextRect.Contains(lp) ? 3
            : _closeRect.Contains(lp) ? 4
            : _muteRect.Contains(lp) ? 5
            : _trackHitRect.Contains(lp) ? 6
            : 0;
        if (nextHover != _hoverBtn)
        {
            _hoverBtn = nextHover;
            Cursor = nextHover != 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    private void OnCardMouseUp(object? sender, MouseEventArgs e)
    {
        if (_scrubbingSeek)
        {
            _scrubbingSeek = false;
            Point lp = ToLogical(e.Location);
            double frac = Math.Clamp((lp.X - _trackHitRect.X) / (double)_trackHitRect.Width, 0.0, 1.0);
            _onSeekFraction(frac);
            Invalidate();
        }
        _draggingWindow = false;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void ApplyRoundedCorners()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && IsHandleCreated)
        {
            int round = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            Region = null;
            return;
        }
        int r = (int)Math.Round(16 * _scale);
        using var path = CreateRoundedPath(new Rectangle(0, 0, Width, Height), r);
        Region = new Region(path);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedCorners();
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // Card background + subtle specular border
        using (var bgBrush = new SolidBrush(Color.FromArgb(20, 23, 32)))
        {
            g.FillRectangle(bgBrush, ClientRectangle);
        }
        if (_scale != 1f)
        {
            g.ScaleTransform(_scale, _scale);
        }
        using (var borderPath = CreateRoundedPath(new Rectangle(0, 0, 331, 103), 16))
        using (var borderPen = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
        {
            g.DrawPath(borderPen, borderPath);
        }

        // Rounded center-cropped cover art
        using (var coverPath = CreateRoundedPath(_coverRect, 11))
        {
            GraphicsState saved = g.Save();
            g.SetClip(coverPath);
            if (_coverBitmap is not null)
            {
                Rectangle src = GetCenterSquareCrop(_coverBitmap.Width, _coverBitmap.Height);
                g.DrawImage(_coverBitmap, _coverRect, src, GraphicsUnit.Pixel);
            }
            else
            {
                using var ph = new SolidBrush(Color.FromArgb(34, 38, 52));
                g.FillRectangle(ph, _coverRect);
            }
            g.Restore(saved);
            using var cBorder = new Pen(Color.FromArgb(38, 255, 255, 255), 1f);
            g.DrawPath(cBorder, coverPath);
        }

        // Title, artist, and time readout
        using (var titleFont = new Font("Segoe UI", 9.75f, FontStyle.Bold))
        using (var artistFont = new Font("Segoe UI", 8.25f, FontStyle.Regular))
        using (var timeFont = new Font("Consolas", 7.75f, FontStyle.Regular))
        using (var whiteBrush = new SolidBrush(Color.White))
        using (var mutedBrush = new SolidBrush(Color.FromArgb(164, 174, 194)))
        {
            var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(_title, titleFont, whiteBrush, new RectangleF(94, 14, 196, 20), fmt);
            g.DrawString(_artist, artistFont, mutedBrush, new RectangleF(94, 34, 196, 16), fmt);

            if (_durSec > 0)
            {
                string tStr = $"{FormatSec(_curSec)} / {FormatSec(_durSec)}";
                g.DrawString(tStr, timeFont, mutedBrush, new RectangleF(232, 60, 86, 16), new StringFormat { Alignment = StringAlignment.Far });
            }
        }

        // Vector Close (X) button
        if (_hoverBtn == 4)
        {
            using var hb = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
            g.FillEllipse(hb, _closeRect);
        }
        using (var xPen = new Pen(_hoverBtn == 4 ? Color.White : Color.FromArgb(164, 174, 194), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(xPen, _closeRect.X + 7, _closeRect.Y + 7, _closeRect.Right - 7, _closeRect.Bottom - 7);
            g.DrawLine(xPen, _closeRect.Right - 7, _closeRect.Y + 7, _closeRect.X + 7, _closeRect.Bottom - 7);
        }

        // Vector Prev / Play / Next / Mute buttons
        DrawSkipButton(g, _prevRect, isNext: false, hovered: _hoverBtn == 1);

        Color playFill = _hoverBtn == 2 ? Color.FromArgb(255, 72, 118) : Color.FromArgb(255, 46, 99);
        using (var pb = new SolidBrush(playFill))
        {
            g.FillEllipse(pb, _playRect);
        }
        using (var iconBrush = new SolidBrush(Color.White))
        {
            if (_isPlaying)
            {
                g.FillRectangle(iconBrush, _playRect.X + 11, _playRect.Y + 10, 3.5f, 12);
                g.FillRectangle(iconBrush, _playRect.X + 17.5f, _playRect.Y + 10, 3.5f, 12);
            }
            else
            {
                PointF[] tri =
                [
                    new(_playRect.X + 12.5f, _playRect.Y + 9.5f),
                    new(_playRect.X + 22.5f, _playRect.Y + 16f),
                    new(_playRect.X + 12.5f, _playRect.Y + 22.5f)
                ];
                g.FillPolygon(iconBrush, tri);
            }
        }

        DrawSkipButton(g, _nextRect, isNext: true, hovered: _hoverBtn == 3);
        DrawMuteButton(g, _muteRect, _isMuted, hovered: _hoverBtn == 5);

        // Bottom interactive rounded progress bar (grows slightly on hover/scrub)
        int barH = (_hoverBtn == 6 || _scrubbingSeek) ? 5 : 3;
        int barY = 104 - 9 - (barH - 3) / 2;
        var trackRect = new Rectangle(14, barY, 332 - 28, barH);
        using (var trackPath = CreateRoundedPath(trackRect, barH / 2))
        using (var trackBrush = new SolidBrush(Color.FromArgb(46, 52, 68)))
        {
            g.FillPath(trackBrush, trackPath);
        }

        double pct = _durSec > 0 ? Math.Clamp(_curSec / _durSec, 0.0, 1.0) : 0.0;
        int fillW = (int)Math.Round(trackRect.Width * pct);
        if (fillW >= 3)
        {
            var fillRect = new Rectangle(trackRect.X, trackRect.Y, fillW, trackRect.Height);
            using var fillPath = CreateRoundedPath(fillRect, barH / 2);
            using var fillBrush = new SolidBrush(Color.FromArgb(255, 46, 99));
            g.FillPath(fillBrush, fillPath);
        }
        if ((_hoverBtn == 6 || _scrubbingSeek) && _durSec > 0)
        {
            int knobX = trackRect.X + fillW;
            int knobY = trackRect.Y + barH / 2;
            using var kb = new SolidBrush(Color.White);
            g.FillEllipse(kb, knobX - 5, knobY - 5, 10, 10);
        }
    }

    private static void DrawSkipButton(Graphics g, Rectangle r, bool isNext, bool hovered)
    {
        if (hovered)
        {
            using var hb = new SolidBrush(Color.FromArgb(35, 255, 255, 255));
            g.FillEllipse(hb, r);
        }
        using var b = new SolidBrush(hovered ? Color.White : Color.FromArgb(215, 222, 235));
        int cx = r.X + r.Width / 2;
        int cy = r.Y + r.Height / 2;
        if (isNext)
        {
            PointF[] tri = [new(cx - 5, cy - 5), new(cx + 3, cy), new(cx - 5, cy + 5)];
            g.FillPolygon(b, tri);
            g.FillRectangle(b, cx + 4, cy - 5, 2, 10);
        }
        else
        {
            g.FillRectangle(b, cx - 6, cy - 5, 2, 10);
            PointF[] tri = [new(cx + 5, cy - 5), new(cx - 3, cy), new(cx + 5, cy + 5)];
            g.FillPolygon(b, tri);
        }
    }

    private static void DrawMuteButton(Graphics g, Rectangle r, bool isMuted, bool hovered)
    {
        if (hovered)
        {
            using var hb = new SolidBrush(Color.FromArgb(35, 255, 255, 255));
            g.FillEllipse(hb, r);
        }
        Color col = isMuted ? Color.FromArgb(255, 100, 130) : (hovered ? Color.White : Color.FromArgb(200, 210, 225));
        int cx = r.X + r.Width / 2;
        int cy = r.Y + r.Height / 2;
        using var b = new SolidBrush(col);
        PointF[] speaker =
        [
            new(cx - 6, cy - 3),
            new(cx - 3, cy - 3),
            new(cx + 1, cy - 6),
            new(cx + 1, cy + 6),
            new(cx - 3, cy + 3),
            new(cx - 6, cy + 3)
        ];
        g.FillPolygon(b, speaker);

        if (isMuted)
        {
            using var slashPen = new Pen(col, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(slashPen, cx + 3, cy - 3, cx + 7, cy + 3);
            g.DrawLine(slashPen, cx + 7, cy - 3, cx + 3, cy + 3);
        }
        else
        {
            using var wavePen = new Pen(col, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(wavePen, cx, cy - 4, 6, 8, -50, 100);
        }
    }

    private static Rectangle GetCenterSquareCrop(int w, int h)
    {
        int side = Math.Min(w, h);
        return new Rectangle((w - side) / 2, (h - side) / 2, side, side);
    }

    private static string FormatSec(double sec)
    {
        int s = Math.Max(0, (int)Math.Floor(sec));
        return $"{s / 60}:{s % 60:D2}";
    }

    public void UpdateState(string? title, string? artist, string? thumbUrl, bool isPlaying, bool isMuted, double curSec, double durSec)
    {
        if (IsDisposed) return;

        if (!string.IsNullOrWhiteSpace(title)) _title = title;
        if (!string.IsNullOrWhiteSpace(artist)) _artist = artist;
        _isPlaying = isPlaying;
        _isMuted = isMuted;
        if (!_scrubbingSeek) _curSec = curSec;
        _durSec = durSec;

        if (!string.IsNullOrWhiteSpace(thumbUrl) &&
            !string.Equals(thumbUrl, _lastThumbUrl, StringComparison.Ordinal))
        {
            _lastThumbUrl = thumbUrl;
            _ = LoadThumbnailAsync(thumbUrl);
        }
        Invalidate();
    }

    private async Task LoadThumbnailAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return;
            }
            byte[] bytes = await ThumbClient.GetByteArrayAsync(uri).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes);
            using var loaded = Image.FromStream(ms);
            var copy = new Bitmap(loaded);
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed) { copy.Dispose(); return; }
                    _coverBitmap?.Dispose();
                    _coverBitmap = copy;
                    Invalidate();
                });
            }
            else
            {
                copy.Dispose();
            }
        }
        catch
        {
            // Keep existing cover thumbnail if network fails
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coverBitmap?.Dispose();
        }
        base.Dispose(disposing);
    }
}
