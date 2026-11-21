using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KurfanovaMuzik;

public sealed class MiniPlayerForm : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopMost = 0x00000008;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private const uint UlwAlpha = 0x00000002;

    private const int LogicalCardW = 352;
    private const int LogicalCardH = 114;
    private const int LogicalShadow = 10;
    private const int CornerRadius = 18;

    private static readonly HttpClient ThumbClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly Action _onPrev;
    private readonly Action _onTogglePlay;
    private readonly Action _onNext;
    private readonly Action _onToggleMute;
    private readonly Action<double> _onSeekFraction;
    private readonly Action _onRestoreMain;
    private readonly System.Windows.Forms.Timer _progressTimer;
    private readonly ToolTip _toolTip;
    private readonly Bitmap? _logoBitmap;

    private string _lang = "tr";
    private string _title = "Kurfamüzik";
    private string _artist = "Bir parça seçin";
    private bool _isPlaying;
    private bool _isMuted;
    private double _curSec;
    private double _durSec;
    private string? _lastThumbUrl;
    private Bitmap? _coverBitmap;
    private Color _ambientTint = Color.FromArgb(148, 163, 210);
    private Color _accentTint = Color.FromArgb(236, 240, 248);
    private float _scale = 1f;
    private bool _positionedOnce;

    // Logical coordinates inside the layered window (card origin is (LogicalShadow, LogicalShadow))
    private readonly Rectangle _cardRect = new(LogicalShadow, LogicalShadow, LogicalCardW, LogicalCardH);
    private readonly Rectangle _coverRect = new(LogicalShadow + 14, LogicalShadow + 13, 70, 70);
    private readonly Rectangle _restoreRect = new(LogicalShadow + 292, LogicalShadow + 11, 22, 22);
    private readonly Rectangle _closeRect = new(LogicalShadow + 318, LogicalShadow + 11, 22, 22);
    private readonly Rectangle _prevRect = new(LogicalShadow + 96, LogicalShadow + 53, 28, 28);
    private readonly Rectangle _playRect = new(LogicalShadow + 128, LogicalShadow + 51, 32, 32);
    private readonly Rectangle _nextRect = new(LogicalShadow + 164, LogicalShadow + 53, 28, 28);
    private readonly Rectangle _muteRect = new(LogicalShadow + 196, LogicalShadow + 53, 28, 28);
    private readonly Rectangle _trackHitRect = new(LogicalShadow + 14, LogicalShadow + LogicalCardH - 18, LogicalCardW - 28, 18);

    private Point _dragStart;
    private bool _draggingWindow;
    private bool _scrubbingSeek;
    private int _hoverBtn; // 1=prev, 2=play, 3=next, 4=close, 5=mute, 6=seek, 7=restore

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
        _logoBitmap = LoadLogoBitmap();

        _toolTip = new ToolTip
        {
            InitialDelay = 350,
            ReshowDelay = 150,
            AutoPopDelay = 3000,
            ShowAlways = true
        };

        _progressTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _progressTimer.Tick += (_, _) =>
        {
            if (Visible && _isPlaying && !_scrubbingSeek && _durSec > 0)
            {
                _curSec = Math.Min(_durSec, _curSec + 0.25);
                RenderLayered();
            }
        };

        UpdateScaleForDpi(96);
        PositionBottomRight();

        MouseDown += OnCardMouseDown;
        MouseMove += OnCardMouseMove;
        MouseUp += OnCardMouseUp;
        MouseLeave += (_, _) =>
        {
            if (!_scrubbingSeek && _hoverBtn != 0)
            {
                _hoverBtn = 0;
                Cursor = Cursors.Default;
                _toolTip.SetToolTip(this, string.Empty);
                RenderLayered();
            }
        };
        DoubleClick += (_, e) =>
        {
            if (_coverRect.Contains(ToLogical(((MouseEventArgs)e).Location)))
            {
                _onRestoreMain();
            }
        };
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

    protected override bool ShowWithoutActivation => true;

    private bool IsEnglish => string.Equals(_lang, "en", StringComparison.OrdinalIgnoreCase);

    public void SetLanguage(string? lang)
    {
        string normalized = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";
        if (_lang == normalized) return;
        _lang = normalized;

        if (_artist is "Bir parça seçin" or "Select a track" or "")
        {
            _artist = IsEnglish ? "Select a track" : "Bir parça seçin";
        }
        if (string.IsNullOrWhiteSpace(_title))
        {
            _title = "Kurfamüzik";
        }

        if (_hoverBtn != 0)
        {
            _toolTip.SetToolTip(this, GetTooltipForButton(_hoverBtn));
        }
        if (Visible && IsHandleCreated && !IsDisposed)
        {
            RenderLayered();
        }
    }

    private void PositionBottomRight()
    {
        Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int margin = (int)Math.Round(20 * _scale);
        int shadowPx = (int)Math.Round(LogicalShadow * _scale);
        int cardW = (int)Math.Round(LogicalCardW * _scale);
        int cardH = (int)Math.Round(LogicalCardH * _scale);
        Location = new Point(
            wa.Right - cardW - shadowPx - margin,
            wa.Bottom - cardH - shadowPx - margin);
        _positionedOnce = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateScaleForDpi(DeviceDpi > 0 ? DeviceDpi : 96);
        if (!_positionedOnce)
        {
            PositionBottomRight();
        }
        RenderLayered();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateProgressTimerState();
        if (Visible && IsHandleCreated && !IsDisposed)
        {
            UpdateScaleForDpi(DeviceDpi > 0 ? DeviceDpi : 96);
            RenderLayered();
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateScaleForDpi(e.DeviceDpiNew);
        RenderLayered();
    }

    private void UpdateScaleForDpi(int dpi)
    {
        _scale = Math.Clamp((dpi > 0 ? dpi : 96) / 96f, 1f, 2.5f);
        int totalW = (int)Math.Round((LogicalCardW + LogicalShadow * 2) * _scale);
        int totalH = (int)Math.Round((LogicalCardH + LogicalShadow * 2) * _scale);
        if (Size.Width != totalW || Size.Height != totalH)
        {
            Size = new Size(totalW, totalH);
        }
    }

    private Point ToLogical(Point physical) =>
        new((int)Math.Round(physical.X / _scale), (int)Math.Round(physical.Y / _scale));

    private void UpdateProgressTimerState()
    {
        if (IsDisposed) return;
        _progressTimer.Enabled = Visible && _isPlaying && _durSec > 0;
    }

    private string GetTooltipForButton(int btn)
    {
        return btn switch
        {
            1 => IsEnglish ? "Previous" : "Önceki",
            2 => _isPlaying ? (IsEnglish ? "Pause" : "Duraklat") : (IsEnglish ? "Play" : "Oynat"),
            3 => IsEnglish ? "Next" : "Sonraki",
            4 => IsEnglish ? "Close" : "Kapat",
            5 => _isMuted ? (IsEnglish ? "Unmute" : "Sesi Aç") : (IsEnglish ? "Mute" : "Sessize Al"),
            7 => IsEnglish ? "Open Main Window" : "Ana Pencereyi Aç",
            _ => string.Empty
        };
    }

    private void OnCardMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Point lp = ToLogical(e.Location);

        if (_trackHitRect.Contains(lp) && _durSec > 0)
        {
            _scrubbingSeek = true;
            double frac = Math.Clamp((lp.X - _trackHitRect.X) / (double)_trackHitRect.Width, 0.0, 1.0);
            _curSec = frac * _durSec;
            RenderLayered();
            return;
        }
        if (_closeRect.Contains(lp))
        {
            Hide();
            return;
        }
        if (_restoreRect.Contains(lp))
        {
            _onRestoreMain();
            return;
        }
        if (_prevRect.Contains(lp))
        {
            _onPrev();
            return;
        }
        if (_playRect.Contains(lp))
        {
            _isPlaying = !_isPlaying;
            UpdateProgressTimerState();
            if (_hoverBtn == 2)
            {
                _toolTip.SetToolTip(this, GetTooltipForButton(2));
            }
            RenderLayered();
            _onTogglePlay();
            return;
        }
        if (_nextRect.Contains(lp))
        {
            _onNext();
            return;
        }
        if (_muteRect.Contains(lp))
        {
            _isMuted = !_isMuted;
            if (_hoverBtn == 5)
            {
                _toolTip.SetToolTip(this, GetTooltipForButton(5));
            }
            RenderLayered();
            _onToggleMute();
            return;
        }

        if (_cardRect.Contains(lp))
        {
            _draggingWindow = true;
            _dragStart = e.Location;
        }
    }

    private void OnCardMouseMove(object? sender, MouseEventArgs e)
    {
        Point lp = ToLogical(e.Location);
        if (_scrubbingSeek && _durSec > 0)
        {
            double frac = Math.Clamp((lp.X - _trackHitRect.X) / (double)_trackHitRect.Width, 0.0, 1.0);
            _curSec = frac * _durSec;
            RenderLayered();
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
            : _restoreRect.Contains(lp) ? 7
            : 0;

        if (nextHover != _hoverBtn)
        {
            _hoverBtn = nextHover;
            Cursor = nextHover != 0 ? Cursors.Hand : Cursors.Default;
            _toolTip.SetToolTip(this, GetTooltipForButton(nextHover));
            RenderLayered();
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
            RenderLayered();
        }
        _draggingWindow = false;
    }

    public void SavePreviewPng(string outputPath)
    {
        string prevTitle = _title;
        string prevArtist = _artist;
        bool prevPlaying = _isPlaying;
        bool prevMuted = _isMuted;
        double prevCur = _curSec;
        double prevDur = _durSec;
        int prevHover = _hoverBtn;

        _title = "Gülpembe (Canlı Kayıt)";
        _artist = "Barış Manço";
        _isPlaying = true;
        _isMuted = false;
        _curSec = 84;
        _durSec = 248;
        _hoverBtn = 2;
        UpdateScaleForDpi(96);

        try
        {
            using Bitmap bmp = DrawCardBitmap();
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            bmp.Save(outputPath, ImageFormat.Png);
        }
        finally
        {
            _title = prevTitle;
            _artist = prevArtist;
            _isPlaying = prevPlaying;
            _isMuted = prevMuted;
            _curSec = prevCur;
            _durSec = prevDur;
            _hoverBtn = prevHover;
        }
    }

    private Bitmap DrawCardBitmap()
    {
        int totalW = Math.Max(1, (int)Math.Round((LogicalCardW + LogicalShadow * 2) * _scale));
        int totalH = Math.Max(1, (int)Math.Round((LogicalCardH + LogicalShadow * 2) * _scale));
        var bmp = new Bitmap(totalW, totalH, PixelFormat.Format32bppPArgb);

        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        if (_scale != 1f)
        {
            g.ScaleTransform(_scale, _scale);
        }

        // 1. Soft multi-layer drop shadow
        for (int i = 6; i >= 1; i--)
        {
            float inflate = i * 1.15f;
            float yShift = i * 0.50f;
            var sr = new RectangleF(
                _cardRect.X - inflate,
                _cardRect.Y - inflate + yShift,
                _cardRect.Width + inflate * 2f,
                _cardRect.Height + inflate * 2f);
            using GraphicsPath sp = CreateRoundedPathF(sr, CornerRadius + inflate);
            using var sb = new SolidBrush(Color.FromArgb(Math.Max(4, 24 - i * 3), 0, 0, 0));
            g.FillPath(sb, sp);
        }

        // 2. Subtle cover-tinted frosted obsidian glass + soft ambient wash + specular rim
        Color baseTop = BlendColor(Color.FromArgb(18, 21, 32), _ambientTint, 0.14f);
        Color baseBot = BlendColor(Color.FromArgb(24, 28, 42), _ambientTint, 0.09f);
        using (GraphicsPath cardPath = CreateRoundedPath(_cardRect, CornerRadius))
        {
            using (var bgGrad = new LinearGradientBrush(
                _cardRect,
                Color.FromArgb(242, baseTop),
                Color.FromArgb(246, baseBot),
                LinearGradientMode.ForwardDiagonal))
            {
                g.FillPath(bgGrad, cardPath);
            }

            GraphicsState saved = g.Save();
            g.SetClip(cardPath);

            // Subtle ambient cover glow behind left artwork side (soft & refined, never neon)
            var ambientRect = new RectangleF(_cardRect.X - 30f, _cardRect.Y - 24f, 188f, 152f);
            using (var ambientPath = new GraphicsPath())
            {
                ambientPath.AddEllipse(ambientRect);
                using var ambientBrush = new PathGradientBrush(ambientPath)
                {
                    CenterColor = Color.FromArgb(38, _ambientTint),
                    SurroundColors = [Color.FromArgb(0, _ambientTint)]
                };
                g.FillPath(ambientBrush, ambientPath);
            }

            // Top specular highlight
            using (var topSheen = new LinearGradientBrush(
                new Rectangle(_cardRect.X, _cardRect.Y, _cardRect.Width, 34),
                Color.FromArgb(18, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(topSheen, _cardRect.X, _cardRect.Y, _cardRect.Width, 34);
            }

            g.ResetClip();
            g.Restore(saved);

            var borderRect = new RectangleF(_cardRect.X + 0.5f, _cardRect.Y + 0.5f, _cardRect.Width - 1f, _cardRect.Height - 1f);
            using GraphicsPath borderPath = CreateRoundedPathF(borderRect, CornerRadius);
            using var rimPen = new Pen(Color.FromArgb(36, 255, 255, 255), 1f);
            g.DrawPath(rimPen, borderPath);
        }

        // 3. Left 70x70 rounded album art (corner 12) with subtle neutral rim
        using (GraphicsPath coverPath = CreateRoundedPath(_coverRect, 12))
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
                DrawLogoFallback(g, _coverRect);
            }
            g.Restore(saved);

            using var cBorder = new Pen(Color.FromArgb(28, 255, 255, 255), 1f);
            g.DrawPath(cBorder, coverPath);
        }

        // 4. Center metadata: Title, Artist, and Duration Timer
        int metaX = LogicalShadow + 96;
        int metaW = 190; // ends at +286, safely left of _restoreRect (+292)
        using (var titleFont = new Font("Segoe UI", 9.75f, FontStyle.Bold))
        using (var artistFont = new Font("Segoe UI", 8.25f, FontStyle.Regular))
        using (var timeFont = new Font("Segoe UI", 7.5f, FontStyle.Regular))
        using (var whiteBrush = new SolidBrush(Color.FromArgb(248, 250, 252)))
        using (var mutedBrush = new SolidBrush(Color.FromArgb(182, 192, 210)))
        {
            using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(_title, titleFont, whiteBrush, new RectangleF(metaX, LogicalShadow + 11, metaW, 20), fmt);
            g.DrawString(_artist, artistFont, mutedBrush, new RectangleF(metaX, LogicalShadow + 30, metaW, 17), fmt);

            // Duration timer cur / dur aligned on the transport baseline
            string tStr = _durSec > 0 ? $"{FormatSec(_curSec)} / {FormatSec(_durSec)}" : "--:-- / --:--";
            using var timeFmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            g.DrawString(tStr, timeFont, mutedBrush, new RectangleF(LogicalShadow + 228, LogicalShadow + 59, 110, 16), timeFmt);
        }

        // 5. Top-right utility buttons: Open Main Window (_restoreRect) + Close (_closeRect)
        DrawRestoreButton(g, _restoreRect, _hoverBtn == 7);
        DrawCloseButton(g, _closeRect, _hoverBtn == 4);

        // 6. Transport buttons: Prev, Apple-style Frosted Play/Pause, Next, Mute
        DrawSkipButton(g, _prevRect, isNext: false, hovered: _hoverBtn == 1);
        DrawPlayButton(g, _playRect, _isPlaying, hovered: _hoverBtn == 2, _ambientTint);
        DrawSkipButton(g, _nextRect, isNext: true, hovered: _hoverBtn == 3);
        DrawMuteButton(g, _muteRect, _isMuted, hovered: _hoverBtn == 5, _accentTint);

        // 7. Clean edge-docked thin rounded progress line at card bottom
        DrawSeekBar(g);

        return bmp;
    }

    private static Bitmap? LoadLogoBitmap()
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

    private void DrawLogoFallback(Graphics g, Rectangle rect)
    {
        using var bg = new SolidBrush(Color.FromArgb(27, 33, 50)); // #1B2132
        g.FillRectangle(bg, rect);

        if (_logoBitmap is not null)
        {
            const float emblemSize = 36f;
            float x = rect.X + (rect.Width - emblemSize) / 2f;
            float y = rect.Y + (rect.Height - emblemSize) / 2f;
            g.DrawImage(_logoBitmap, new RectangleF(x, y, emblemSize, emblemSize));
        }
    }

    private static void DrawRestoreButton(Graphics g, Rectangle r, bool hovered)
    {
        if (hovered)
        {
            using var hb = new SolidBrush(Color.FromArgb(36, 255, 255, 255));
            g.FillEllipse(hb, r);
        }
        Color fg = hovered ? Color.White : Color.FromArgb(164, 174, 194);
        using var pen = new Pen(fg, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        // Diagonal outward expand arrows (top-right & bottom-left)
        float x1 = r.X + 6f;
        float y1 = r.Y + 6f;
        float x2 = r.Right - 6f;
        float y2 = r.Bottom - 6f;

        // Top-right arrow
        g.DrawLine(pen, x2 - 4.2f, y1, x2, y1);
        g.DrawLine(pen, x2, y1, x2, y1 + 4.2f);
        g.DrawLine(pen, x2 - 4.5f, y1 + 4.5f, x2, y1);

        // Bottom-left arrow
        g.DrawLine(pen, x1, y2 - 4.2f, x1, y2);
        g.DrawLine(pen, x1, y2, x1 + 4.2f, y2);
        g.DrawLine(pen, x1 + 4.5f, y2 - 4.5f, x1, y2);
    }

    private static void DrawCloseButton(Graphics g, Rectangle r, bool hovered)
    {
        if (hovered)
        {
            using var hb = new SolidBrush(Color.FromArgb(46, 244, 63, 94));
            g.FillEllipse(hb, r);
        }
        Color fg = hovered ? Color.FromArgb(255, 190, 200) : Color.FromArgb(164, 174, 194);
        using var pen = new Pen(fg, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, r.X + 7, r.Y + 7, r.Right - 7, r.Bottom - 7);
        g.DrawLine(pen, r.Right - 7, r.Y + 7, r.X + 7, r.Bottom - 7);
    }

    private static void DrawPlayButton(Graphics g, Rectangle r, bool isPlaying, bool hovered, Color ambientTint)
    {
        if (hovered)
        {
            var glowRect = new RectangleF(r.X - 3f, r.Y - 3f, r.Width + 6f, r.Height + 6f);
            using var glowPath = new GraphicsPath();
            glowPath.AddEllipse(glowRect);
            using var pgb = new PathGradientBrush(glowPath)
            {
                CenterColor = Color.FromArgb(46, ambientTint),
                SurroundColors = [Color.FromArgb(0, ambientTint)]
            };
            g.FillPath(pgb, glowPath);
        }

        Color cTop = hovered ? Color.FromArgb(255, 255, 255) : Color.FromArgb(244, 247, 252);
        Color cBot = hovered ? Color.FromArgb(236, 241, 249) : Color.FromArgb(224, 230, 242);
        using (var pb = new LinearGradientBrush(r, cTop, cBot, LinearGradientMode.Vertical))
        {
            g.FillEllipse(pb, r);
        }

        using var rim = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
        g.DrawEllipse(rim, r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);

        using var iconBrush = new SolidBrush(Color.FromArgb(18, 22, 34));
        if (isPlaying)
        {
            g.FillRectangle(iconBrush, r.X + 11f, r.Y + 10f, 3.4f, 12f);
            g.FillRectangle(iconBrush, r.X + 17.6f, r.Y + 10f, 3.4f, 12f);
        }
        else
        {
            PointF[] tri =
            [
                new(r.X + 12.6f, r.Y + 9.6f),
                new(r.X + 22.2f, r.Y + 16f),
                new(r.X + 12.6f, r.Y + 22.4f)
            ];
            g.FillPolygon(iconBrush, tri);
        }
    }

    private void DrawSeekBar(Graphics g)
    {
        bool activeSeek = _hoverBtn == 6 || _scrubbingSeek;
        float barH = activeSeek ? 4.5f : 3f;
        float barY = LogicalShadow + LogicalCardH - 9f - (barH - 3f) * 0.5f;
        var trackRect = new RectangleF(LogicalShadow + 14f, barY, LogicalCardW - 28f, barH);

        using (var trackPath = CreateRoundedPathF(trackRect, barH * 0.5f))
        using (var trackBrush = new SolidBrush(Color.FromArgb(42, 255, 255, 255)))
        {
            g.FillPath(trackBrush, trackPath);
        }

        double pct = _durSec > 0 ? Math.Clamp(_curSec / _durSec, 0.0, 1.0) : 0.0;
        float fillW = (float)(trackRect.Width * pct);
        if (fillW >= 3f)
        {
            var fillRect = new RectangleF(trackRect.X, trackRect.Y, fillW, trackRect.Height);
            using var fillPath = CreateRoundedPathF(fillRect, barH * 0.5f);
            Color fillStart = BlendColor(_ambientTint, Color.White, 0.35f);
            Color fillEnd = BlendColor(_accentTint, Color.White, 0.50f);
            using var fillBrush = new LinearGradientBrush(
                fillRect,
                Color.FromArgb(235, fillStart),
                Color.FromArgb(245, fillEnd),
                LinearGradientMode.Horizontal);
            g.FillPath(fillBrush, fillPath);
        }

        if (activeSeek && _durSec > 0)
        {
            float knobX = Math.Clamp(trackRect.X + fillW, trackRect.X + 4f, trackRect.Right - 4f);
            float knobY = trackRect.Y + barH * 0.5f;
            using var halo = new SolidBrush(Color.FromArgb(56, _accentTint));
            g.FillEllipse(halo, knobX - 6.5f, knobY - 6.5f, 13f, 13f);
            using var kb = new SolidBrush(Color.White);
            g.FillEllipse(kb, knobX - 4f, knobY - 4f, 8f, 8f);
        }
    }

    private static void DrawSkipButton(Graphics g, Rectangle r, bool isNext, bool hovered)
    {
        if (hovered)
        {
            using var hb = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
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

    private static void DrawMuteButton(Graphics g, Rectangle r, bool isMuted, bool hovered, Color accentTint)
    {
        if (hovered)
        {
            using var hb = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
            g.FillEllipse(hb, r);
        }
        Color col = isMuted
            ? BlendColor(Color.FromArgb(244, 114, 140), accentTint, 0.25f)
            : (hovered ? Color.White : Color.FromArgb(200, 210, 225));
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

    private static (Color Ambient, Color Accent) ExtractCoverPalette(Bitmap bmp)
    {
        double rSum = 0, gSum = 0, bSum = 0, wSum = 0;
        int stepX = Math.Max(1, bmp.Width / 12);
        int stepY = Math.Max(1, bmp.Height / 12);
        for (int y = stepY / 2; y < bmp.Height; y += stepY)
        {
            for (int x = stepX / 2; x < bmp.Width; x += stepX)
            {
                Color p = bmp.GetPixel(x, y);
                if (p.A < 128) continue;
                int max = Math.Max(p.R, Math.Max(p.G, p.B));
                int min = Math.Min(p.R, Math.Min(p.G, p.B));
                if (max < 22 || min > 236) continue;
                double sat = max > 0 ? (max - min) / (double)max : 0;
                double lum = (0.299 * p.R + 0.587 * p.G + 0.114 * p.B) / 255.0;
                double midBias = 1.0 - Math.Abs(lum - 0.52);
                double w = 0.25 + sat * 1.45 + Math.Max(0.0, midBias) * 0.4;
                rSum += p.R * w;
                gSum += p.G * w;
                bSum += p.B * w;
                wSum += w;
            }
        }

        if (wSum <= 0.01)
        {
            return (Color.FromArgb(148, 163, 210), Color.FromArgb(236, 240, 248));
        }

        int r = Math.Clamp((int)Math.Round(rSum / wSum), 0, 255);
        int g = Math.Clamp((int)Math.Round(gSum / wSum), 0, 255);
        int b = Math.Clamp((int)Math.Round(bSum / wSum), 0, 255);

        // Soften toward refined frosted slate so cover tint is subtle, never garish neon
        Color raw = Color.FromArgb(r, g, b);
        Color ambient = BlendColor(raw, Color.FromArgb(140, 152, 188), 0.38f);
        Color accent = BlendColor(raw, Color.FromArgb(235, 240, 250), 0.45f);
        return (ambient, accent);
    }

    private static Color BlendColor(Color a, Color b, float t)
    {
        float k = Math.Clamp(t, 0f, 1f);
        int r = (int)Math.Round(a.R + (b.R - a.R) * k);
        int g = (int)Math.Round(a.G + (b.G - a.G) * k);
        int bl = (int)Math.Round(a.B + (b.B - a.B) * k);
        return Color.FromArgb(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(bl, 0, 255));
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

        if (!string.IsNullOrWhiteSpace(title))
        {
            _title = title;
        }
        if (!string.IsNullOrWhiteSpace(artist))
        {
            if (artist is "Bir parça seçin" or "Select a track")
            {
                _artist = IsEnglish ? "Select a track" : "Bir parça seçin";
            }
            else
            {
                _artist = artist;
            }
        }
        _isPlaying = isPlaying;
        _isMuted = isMuted;
        if (!_scrubbingSeek) _curSec = curSec;
        _durSec = durSec;
        UpdateProgressTimerState();

        if (!string.IsNullOrWhiteSpace(thumbUrl) &&
            !string.Equals(thumbUrl, _lastThumbUrl, StringComparison.Ordinal))
        {
            _lastThumbUrl = thumbUrl;
            _ = LoadThumbnailAsync(thumbUrl);
        }
        if (IsHandleCreated)
        {
            RenderLayered();
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
                (Color ambient, Color accent) = ExtractCoverPalette(copy);
                BeginInvoke(() =>
                {
                    if (IsDisposed) { copy.Dispose(); return; }
                    _coverBitmap?.Dispose();
                    _coverBitmap = copy;
                    _ambientTint = ambient;
                    _accentTint = accent;
                    RenderLayered();
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

    private void RenderLayered()
    {
        if (IsDisposed || !IsHandleCreated) return;
        using Bitmap bmp = DrawCardBitmap();
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
            _progressTimer.Stop();
            _progressTimer.Dispose();
            _toolTip.Dispose();
            _coverBitmap?.Dispose();
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
