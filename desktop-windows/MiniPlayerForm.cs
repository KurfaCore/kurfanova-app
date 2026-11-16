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
    private readonly Action _onRestoreMain;

    private string _title = "Kurfamüzik";
    private string _artist = "Bir parça seçin";
    private bool _isPlaying;
    private double _curSec;
    private double _durSec;
    private string? _lastThumbUrl;
    private Bitmap? _coverBitmap;

    private readonly Rectangle _coverRect = new(14, 14, 68, 68);
    private readonly Rectangle _closeRect = new(296, 10, 22, 22);
    private readonly Rectangle _prevRect = new(102, 54, 28, 28);
    private readonly Rectangle _playRect = new(138, 52, 32, 32);
    private readonly Rectangle _nextRect = new(178, 54, 28, 28);

    private Point _dragStart;
    private bool _dragging;
    private int _hoverBtn; // 1=prev, 2=play, 3=next, 4=close

    public MiniPlayerForm(Action onPrev, Action onTogglePlay, Action onNext, Action onRestoreMain)
    {
        _onPrev = onPrev;
        _onTogglePlay = onTogglePlay;
        _onNext = onNext;
        _onRestoreMain = onRestoreMain;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Size = new Size(332, 102);
        BackColor = Color.FromArgb(20, 23, 32);

        Rectangle wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - 20, wa.Bottom - Height - 20);
        ApplyRoundedRegion();

        MouseDown += OnCardMouseDown;
        MouseMove += OnCardMouseMove;
        MouseUp += (_, _) => _dragging = false;
        MouseLeave += (_, _) => { if (_hoverBtn != 0) { _hoverBtn = 0; Cursor = Cursors.Default; Invalidate(); } };
        DoubleClick += (_, e) =>
        {
            if (_coverRect.Contains(((MouseEventArgs)e).Location)) _onRestoreMain();
        };
    }

    private void OnCardMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_closeRect.Contains(e.Location)) { Hide(); return; }
        if (_prevRect.Contains(e.Location)) { _onPrev(); return; }
        if (_playRect.Contains(e.Location)) { _onTogglePlay(); return; }
        if (_nextRect.Contains(e.Location)) { _onNext(); return; }

        _dragging = true;
        _dragStart = e.Location;
    }

    private void OnCardMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            Point screenPos = PointToScreen(e.Location);
            Location = new Point(screenPos.X - _dragStart.X, screenPos.Y - _dragStart.Y);
            return;
        }

        int nextHover = _prevRect.Contains(e.Location) ? 1
            : _playRect.Contains(e.Location) ? 2
            : _nextRect.Contains(e.Location) ? 3
            : _closeRect.Contains(e.Location) ? 4
            : 0;
        if (nextHover != _hoverBtn)
        {
            _hoverBtn = nextHover;
            Cursor = nextHover != 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    private void ApplyRoundedRegion()
    {
        using var path = CreateRoundedPath(new Rectangle(0, 0, Width, Height), 16);
        Region = new Region(path);
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
        using (var borderPath = CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 16))
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

        // Title & artist
        using (var titleFont = new Font("Segoe UI", 9.75f, FontStyle.Bold))
        using (var artistFont = new Font("Segoe UI", 8.25f, FontStyle.Regular))
        using (var timeFont = new Font("Consolas", 7.75f, FontStyle.Regular))
        using (var whiteBrush = new SolidBrush(Color.White))
        using (var mutedBrush = new SolidBrush(Color.FromArgb(164, 174, 194)))
        {
            var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(_title, titleFont, whiteBrush, new RectangleF(94, 14, 194, 20), fmt);
            g.DrawString(_artist, artistFont, mutedBrush, new RectangleF(94, 34, 194, 16), fmt);

            if (_durSec > 0)
            {
                string tStr = $"{FormatSec(_curSec)} / {FormatSec(_durSec)}";
                g.DrawString(tStr, timeFont, mutedBrush, new RectangleF(216, 61, 102, 16), new StringFormat { Alignment = StringAlignment.Far });
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

        // Vector Prev button
        DrawSkipButton(g, _prevRect, isNext: false, hovered: _hoverBtn == 1);

        // Circular Pink Play/Pause button
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

        // Vector Next button
        DrawSkipButton(g, _nextRect, isNext: true, hovered: _hoverBtn == 3);

        // Bottom rounded progress track + fill
        var trackRect = new Rectangle(14, Height - 9, Width - 28, 3);
        using (var trackPath = CreateRoundedPath(trackRect, 1))
        using (var trackBrush = new SolidBrush(Color.FromArgb(46, 52, 68)))
        {
            g.FillPath(trackBrush, trackPath);
        }

        double pct = _durSec > 0 ? Math.Clamp(_curSec / _durSec, 0.0, 1.0) : 0.0;
        int fillW = (int)Math.Round(trackRect.Width * pct);
        if (fillW >= 3)
        {
            var fillRect = new Rectangle(trackRect.X, trackRect.Y, fillW, trackRect.Height);
            using var fillPath = CreateRoundedPath(fillRect, 1);
            using var fillBrush = new SolidBrush(Color.FromArgb(255, 46, 99));
            g.FillPath(fillBrush, fillPath);
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

    public void UpdateState(string? title, string? artist, string? thumbUrl, bool isPlaying, double curSec, double durSec)
    {
        if (IsDisposed) return;

        if (!string.IsNullOrWhiteSpace(title)) _title = title;
        if (!string.IsNullOrWhiteSpace(artist)) _artist = artist;
        _isPlaying = isPlaying;
        _curSec = curSec;
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
