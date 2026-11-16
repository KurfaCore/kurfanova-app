using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Windows.Forms;

namespace KurfanovaMuzik;

public sealed class MiniPlayerForm : Form
{
    private static readonly HttpClient ThumbClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly PictureBox _coverBox;
    private readonly Label _titleLabel;
    private readonly Label _artistLabel;
    private readonly Button _prevBtn;
    private readonly Button _playBtn;
    private readonly Button _nextBtn;
    private readonly Button _closeBtn;
    private readonly Panel _progressTrack;
    private readonly Panel _progressFill;

    private readonly Action _onPrev;
    private readonly Action _onTogglePlay;
    private readonly Action _onNext;
    private readonly Action _onRestoreMain;

    private string? _lastThumbUrl;
    private Point _dragStart;
    private bool _dragging;

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
        Size = new Size(328, 102);
        BackColor = Color.FromArgb(20, 23, 32);

        Rectangle wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - 20, wa.Bottom - Height - 20);

        _coverBox = new PictureBox
        {
            Size = new Size(64, 64),
            Location = new Point(14, 15),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(34, 38, 52)
        };

        _titleLabel = new Label
        {
            Location = new Point(88, 16),
            Size = new Size(198, 22),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoEllipsis = true,
            Text = "Kurfa Müzik"
        };

        _artistLabel = new Label
        {
            Location = new Point(88, 38),
            Size = new Size(198, 18),
            ForeColor = Color.FromArgb(165, 174, 192),
            Font = new Font("Segoe UI", 8.25f, FontStyle.Regular),
            AutoEllipsis = true,
            Text = "Bir parça seçin"
        };

        _closeBtn = CreateIconButton("✕", new Point(294, 10), new Size(24, 24), 9f);
        _closeBtn.ForeColor = Color.FromArgb(165, 174, 192);
        _closeBtn.Click += (_, _) => Hide();

        _prevBtn = CreateIconButton("⏮", new Point(116, 60), new Size(32, 28), 10f);
        _prevBtn.Click += (_, _) => _onPrev();

        _playBtn = CreateIconButton("▶", new Point(156, 58), new Size(36, 32), 11f);
        _playBtn.BackColor = Color.FromArgb(255, 46, 99);
        _playBtn.ForeColor = Color.White;
        _playBtn.Click += (_, _) => _onTogglePlay();

        _nextBtn = CreateIconButton("⏭", new Point(200, 60), new Size(32, 28), 10f);
        _nextBtn.Click += (_, _) => _onNext();

        _progressTrack = new Panel
        {
            Location = new Point(0, Height - 4),
            Size = new Size(Width, 4),
            BackColor = Color.FromArgb(42, 46, 62)
        };

        _progressFill = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(0, 4),
            BackColor = Color.FromArgb(255, 46, 99)
        };
        _progressTrack.Controls.Add(_progressFill);

        Controls.Add(_coverBox);
        Controls.Add(_titleLabel);
        Controls.Add(_artistLabel);
        Controls.Add(_closeBtn);
        Controls.Add(_prevBtn);
        Controls.Add(_playBtn);
        Controls.Add(_nextBtn);
        Controls.Add(_progressTrack);

        AttachDragHandlers(this);
        AttachDragHandlers(_coverBox);
        AttachDragHandlers(_titleLabel);
        AttachDragHandlers(_artistLabel);

        _coverBox.DoubleClick += (_, _) => _onRestoreMain();
        ApplyRoundedRegion();
    }

    private static Button CreateIconButton(string glyph, Point location, Size size, float fontSize)
    {
        var btn = new Button
        {
            Text = glyph,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Symbol", fontSize, FontStyle.Bold),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(46, 51, 68);
        return btn;
    }

    private void AttachDragHandlers(Control ctl)
    {
        ctl.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragStart = e.Location;
        };
        ctl.MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            Point screenPos = ctl.PointToScreen(e.Location);
            Location = new Point(screenPos.X - _dragStart.X, screenPos.Y - _dragStart.Y);
        };
        ctl.MouseUp += (_, _) => _dragging = false;
    }

    private void ApplyRoundedRegion()
    {
        using var path = new GraphicsPath();
        const int r = 16;
        path.AddArc(0, 0, r, r, 180, 90);
        path.AddArc(Width - r, 0, r, r, 270, 90);
        path.AddArc(Width - r, Height - r, r, r, 0, 90);
        path.AddArc(0, Height - r, r, r, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    public void UpdateState(string? title, string? artist, string? thumbUrl, bool isPlaying, double curSec, double durSec)
    {
        if (IsDisposed) return;

        if (!string.IsNullOrWhiteSpace(title))
        {
            _titleLabel.Text = title;
        }
        if (!string.IsNullOrWhiteSpace(artist))
        {
            _artistLabel.Text = artist;
        }
        _playBtn.Text = isPlaying ? "⏸" : "▶";

        double pct = durSec > 0 ? Math.Clamp(curSec / durSec, 0.0, 1.0) : 0.0;
        _progressFill.Width = (int)Math.Round(Width * pct);

        if (!string.IsNullOrWhiteSpace(thumbUrl) &&
            !string.Equals(thumbUrl, _lastThumbUrl, StringComparison.Ordinal))
        {
            _lastThumbUrl = thumbUrl;
            _ = LoadThumbnailAsync(thumbUrl);
        }
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
                    Image? old = _coverBox.Image;
                    _coverBox.Image = copy;
                    old?.Dispose();
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
}
