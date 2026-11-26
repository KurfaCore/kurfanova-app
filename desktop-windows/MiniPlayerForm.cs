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

    private const int ShadowPad = 12;
    private const int FocusCardW = 296;
    private const int FocusCardH = 296;
    private const int CompactCardW = 384;
    private const int CompactCardH = 116;

    private static readonly HttpClient ThumbClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly Action _onPrev;
    private readonly Action _onTogglePlay;
    private readonly Action _onNext;
    private readonly Action _onToggleMute;
    private readonly Action<double> _onSeekFraction;
    private readonly Action _onRestoreMain;
    private readonly Action<double>? _onSetVolume;
    private readonly System.Windows.Forms.Timer _progressTimer;
    private readonly System.Windows.Forms.Timer _animTimer;
    private readonly ToolTip _toolTip;
    private readonly Bitmap? _logoBitmap;

    private string _lang = "tr";
    private string _title = "Kurfamüzik";
    private string _artist = "Bir parça seçin";
    private bool _isPlaying;
    private bool _isMuted;
    private double _volume = 0.80;
    private double _curSec;
    private double _durSec;
    private string? _lastThumbUrl;
    private Bitmap? _coverBitmap;
    private Bitmap? _prevCoverBitmap;
    private Color _ambientTint = Color.FromArgb(138, 152, 224);
    private Color _accentTint = Color.FromArgb(244, 63, 94);
    private float _scale = 1f;
    private bool _positionedOnce;

    // Mode & 60fps animation states
    private bool _isFocusMode = true;
    private bool _closingAnim;
    private float _openAnim = 1f;
    private float _hoverOverlayAnim;
    private float _volExpandAnim;
    private float _modeAnim = 1f;
    private float _coverFadeAnim = 1f;
    private long _volWheelHoldUntil;

    // Hit-test rectangles updated dynamically per mode & _volExpandAnim
    private Rectangle _cardRect;
    private Rectangle _coverRect;
    private Rectangle _modeToggleRect;
    private Rectangle _restoreRect;
    private Rectangle _closeRect;
    private Rectangle _prevRect;
    private Rectangle _playRect;
    private Rectangle _nextRect;
    private Rectangle _muteRect;
    private Rectangle _volTrackHitRect;
    private Rectangle _trackHitRect;

    private Point _dragStart;
    private bool _draggingWindow;
    private bool _scrubbingSeek;
    private bool _scrubbingVolume;
    private bool _mouseInside;
    private int _hoverBtn; // 1=prev, 2=play, 3=next, 4=close, 5=mute, 6=seek, 7=restore, 8=modeToggle, 9=volSlider

    public MiniPlayerForm(
        Action onPrev,
        Action onTogglePlay,
        Action onNext,
        Action onToggleMute,
        Action<double> onSeekFraction,
        Action onRestoreMain,
        Action<double>? onSetVolume = null)
    {
        _onPrev = onPrev;
        _onTogglePlay = onTogglePlay;
        _onNext = onNext;
        _onToggleMute = onToggleMute;
        _onSeekFraction = onSeekFraction;
        _onRestoreMain = onRestoreMain;
        _onSetVolume = onSetVolume;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        _logoBitmap = LoadLogoBitmap();

        _toolTip = new ToolTip
        {
            InitialDelay = 300,
            ReshowDelay = 120,
            AutoPopDelay = 2800,
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

        _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animTimer.Tick += OnAnimationTick;

        RecalculateLayoutRects();
        UpdateScaleForDpi(96);
        PositionBottomRight();

        MouseEnter += (_, _) =>
        {
            _mouseInside = true;
            EnsureAnimRunning();
        };
        MouseDown += OnCardMouseDown;
        MouseMove += OnCardMouseMove;
        MouseUp += OnCardMouseUp;
        MouseWheel += OnCardMouseWheel;
        MouseLeave += (_, _) =>
        {
            _mouseInside = false;
            if (!_scrubbingSeek && !_scrubbingVolume)
            {
                _hoverBtn = 0;
                Cursor = Cursors.Default;
                _toolTip.SetToolTip(this, string.Empty);
            }
            EnsureAnimRunning();
        };
        DoubleClick += (_, e) =>
        {
            Point lp = ToLogical(((MouseEventArgs)e).Location);
            if (_coverRect.Contains(lp) && _hoverBtn == 0)
            {
                _onRestoreMain();
            }
        };
    }

    public bool IsClosingAnimation => _closingAnim;

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

    private int CurrentCardW => _isFocusMode ? FocusCardW : CompactCardW;
    private int CurrentCardH => _isFocusMode ? FocusCardH : CompactCardH;
    private int CurrentCornerRadius => _isFocusMode ? 20 : 18;

    public void ShowAnimated()
    {
        if (IsDisposed) return;
        _closingAnim = false;
        if (!Visible)
        {
            _openAnim = 0.04f;
            _hoverOverlayAnim = _isFocusMode ? 0f : 1f;
            _volExpandAnim = 0f;
            _modeAnim = 1f;
            Show();
        }
        EnsureAnimRunning();
    }

    public void CloseAnimated()
    {
        if (IsDisposed || !Visible) return;
        _closingAnim = true;
        EnsureAnimRunning();
    }

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

    private void ToggleMode()
    {
        // Keep the bottom-right corner anchored when morphing between Cover (296x296) and Compact (384x116)
        int anchorRight = Left + Width;
        int anchorBottom = Top + Height;

        _isFocusMode = !_isFocusMode;
        _modeAnim = 0f;
        _hoverBtn = 0;
        _scrubbingVolume = false;
        _scrubbingSeek = false;
        _volWheelHoldUntil = 0;

        RecalculateLayoutRects();
        UpdateScaleForDpi(DeviceDpi > 0 ? DeviceDpi : 96);

        Rectangle wa = Screen.FromPoint(new Point(anchorRight, anchorBottom)).WorkingArea;
        int nextX = Math.Clamp(anchorRight - Width, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - Width - 8));
        int nextY = Math.Clamp(anchorBottom - Height, wa.Top + 8, Math.Max(wa.Top + 8, wa.Bottom - Height - 8));
        Location = new Point(nextX, nextY);

        if (_isFocusMode)
        {
            _mouseInside = true;
            _hoverOverlayAnim = 1f;
        }
        EnsureAnimRunning();
        RenderLayered();
    }

    private void RecalculateLayoutRects()
    {
        int w = CurrentCardW;
        int h = CurrentCardH;
        _cardRect = new Rectangle(ShadowPad, ShadowPad, w, h);

        if (_isFocusMode)
        {
            _coverRect = _cardRect;
            _modeToggleRect = new Rectangle(ShadowPad + w - 96, ShadowPad + 12, 24, 24);
            _restoreRect = new Rectangle(ShadowPad + w - 66, ShadowPad + 12, 24, 24);
            _closeRect = new Rectangle(ShadowPad + w - 36, ShadowPad + 12, 24, 24);

            int centerX = ShadowPad + w / 2;
            int transY = ShadowPad + h / 2 + 22;
            _playRect = new Rectangle(centerX - 26, transY - 26, 52, 52);
            _prevRect = new Rectangle(centerX - 26 - 16 - 34, transY - 17, 34, 34);
            _nextRect = new Rectangle(centerX + 26 + 16, transY - 17, 34, 34);

            int hudY = ShadowPad + h - 48;
            _muteRect = new Rectangle(ShadowPad + 16, hudY, 26, 26);
            int volW = (int)Math.Round(84f * _volExpandAnim);
            _volTrackHitRect = volW > 6
                ? new Rectangle(_muteRect.Right + 4, hudY + 1, volW, 24)
                : Rectangle.Empty;

            _trackHitRect = new Rectangle(ShadowPad + 16, ShadowPad + h - 20, w - 32, 18);
        }
        else
        {
            _coverRect = new Rectangle(ShadowPad + 14, ShadowPad + 13, 74, 74);
            _modeToggleRect = new Rectangle(ShadowPad + w - 92, ShadowPad + 11, 24, 24);
            _restoreRect = new Rectangle(ShadowPad + w - 64, ShadowPad + 11, 24, 24);
            _closeRect = new Rectangle(ShadowPad + w - 36, ShadowPad + 11, 24, 24);

            int baseX = ShadowPad + 100;
            int transY = ShadowPad + 53;
            _prevRect = new Rectangle(baseX, transY, 28, 28);
            _playRect = new Rectangle(baseX + 32, transY - 2, 32, 32);
            _nextRect = new Rectangle(baseX + 68, transY, 28, 28);
            _muteRect = new Rectangle(baseX + 100, transY, 28, 28);

            int volW = (int)Math.Round(74f * _volExpandAnim);
            _volTrackHitRect = volW > 6
                ? new Rectangle(_muteRect.Right + 2, transY + 2, volW, 24)
                : Rectangle.Empty;

            _trackHitRect = new Rectangle(ShadowPad + 14, ShadowPad + h - 19, w - 28, 18);
        }
    }

    private void PositionBottomRight()
    {
        Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int margin = (int)Math.Round(20 * _scale);
        int shadowPx = (int)Math.Round(ShadowPad * _scale);
        int cardW = (int)Math.Round(CurrentCardW * _scale);
        int cardH = (int)Math.Round(CurrentCardH * _scale);
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
        int totalW = (int)Math.Round((CurrentCardW + ShadowPad * 2) * _scale);
        int totalH = (int)Math.Round((CurrentCardH + ShadowPad * 2) * _scale);
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

    private void EnsureAnimRunning()
    {
        if (!IsDisposed && !_animTimer.Enabled)
        {
            _animTimer.Start();
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (IsDisposed || !Visible)
        {
            _animTimer.Stop();
            return;
        }

        // Verify cursor position against card bounds in case MouseLeave was missed
        if (_mouseInside && !_draggingWindow && !_scrubbingSeek && !_scrubbingVolume)
        {
            Point cursorLocal = ToLogical(PointToClient(Cursor.Position));
            if (!_cardRect.Contains(cursorLocal))
            {
                _mouseInside = false;
                _hoverBtn = 0;
            }
        }

        float openTarget = _closingAnim ? 0f : 1f;
        float overlayTarget = _isFocusMode
            ? ((_mouseInside || _scrubbingSeek || _scrubbingVolume || Environment.TickCount64 < _volWheelHoldUntil) ? 1f : 0f)
            : 1f;
        bool wantVolExpand = _hoverBtn == 5 || _hoverBtn == 9 || _scrubbingVolume || Environment.TickCount64 < _volWheelHoldUntil;
        float volTarget = wantVolExpand ? 1f : 0f;

        bool changed = false;
        changed |= StepToward(ref _openAnim, openTarget, _closingAnim ? 0.22f : 0.20f);
        changed |= StepToward(ref _hoverOverlayAnim, overlayTarget, 0.24f);
        changed |= StepToward(ref _volExpandAnim, volTarget, 0.26f);
        changed |= StepToward(ref _modeAnim, 1f, 0.22f);
        changed |= StepToward(ref _coverFadeAnim, 1f, 0.14f);

        if (_coverFadeAnim >= 0.995f && _prevCoverBitmap is not null)
        {
            _prevCoverBitmap.Dispose();
            _prevCoverBitmap = null;
        }

        if (_closingAnim && _openAnim <= 0.015f)
        {
            _closingAnim = false;
            _openAnim = 0f;
            _animTimer.Stop();
            Hide();
            return;
        }

        RecalculateLayoutRects();
        RenderLayered();

        if (!changed && Environment.TickCount64 >= _volWheelHoldUntil)
        {
            _animTimer.Stop();
        }
    }

    private static bool StepToward(ref float current, float target, float factor)
    {
        float diff = target - current;
        if (Math.Abs(diff) <= 0.004f)
        {
            if (Math.Abs(diff) > 0f)
            {
                current = target;
                return true;
            }
            return false;
        }
        current += diff * factor;
        return true;
    }

    private string GetTooltipForButton(int btn)
    {
        return btn switch
        {
            1 => IsEnglish ? "Previous" : "Önceki",
            2 => _isPlaying ? (IsEnglish ? "Pause" : "Duraklat") : (IsEnglish ? "Play" : "Oynat"),
            3 => IsEnglish ? "Next" : "Sonraki",
            4 => IsEnglish ? "Close" : "Kapat",
            5 => _isMuted ? (IsEnglish ? "Unmute" : "Sesi Aç") : (IsEnglish ? "Mute (Middle-click or scroll wheel)" : "Sessize Al (Orta tuş / tekerlek)"),
            7 => IsEnglish ? "Open Main Window" : "Ana Pencereyi Aç",
            8 => _isFocusMode ? (IsEnglish ? "Compact Bar" : "Küçük Bar Modu") : (IsEnglish ? "Cover Focus Mode" : "Kapak Moduna Büyüt"),
            9 => $"{Math.Round((_isMuted ? 0 : _volume) * 100)}%",
            _ => string.Empty
        };
    }

    private void OnCardMouseWheel(object? sender, MouseEventArgs e)
    {
        if (e.Delta == 0) return;
        double step = e.Delta > 0 ? 0.05 : -0.05;
        double nextVol = Math.Clamp((_isMuted ? 0.0 : _volume) + step, 0.0, 1.0);
        _volume = nextVol;
        _isMuted = nextVol <= 0.001;
        _volWheelHoldUntil = Environment.TickCount64 + 1500;
        _mouseInside = true;
        _onSetVolume?.Invoke(_volume);
        EnsureAnimRunning();
        RenderLayered();
    }

    private void OnCardMouseDown(object? sender, MouseEventArgs e)
    {
        Point lp = ToLogical(e.Location);

        // Middle mouse click toggles Mute anywhere inside card
        if (e.Button == MouseButtons.Middle)
        {
            if (_cardRect.Contains(lp))
            {
                _isMuted = !_isMuted;
                _volWheelHoldUntil = Environment.TickCount64 + 1100;
                _onToggleMute();
                EnsureAnimRunning();
                RenderLayered();
            }
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        bool overlayVisible = !_isFocusMode || _hoverOverlayAnim > 0.15f;

        if (overlayVisible && _volExpandAnim > 0.2f && _volTrackHitRect.Contains(lp))
        {
            _scrubbingVolume = true;
            UpdateVolumeFromPoint(lp);
            EnsureAnimRunning();
            return;
        }

        if (overlayVisible && _trackHitRect.Contains(lp) && _durSec > 0)
        {
            _scrubbingSeek = true;
            double frac = Math.Clamp((lp.X - _trackHitRect.X) / (double)_trackHitRect.Width, 0.0, 1.0);
            _curSec = frac * _durSec;
            RenderLayered();
            return;
        }

        if (overlayVisible && _closeRect.Contains(lp))
        {
            CloseAnimated();
            return;
        }
        if (overlayVisible && _modeToggleRect.Contains(lp))
        {
            ToggleMode();
            return;
        }
        if (overlayVisible && _restoreRect.Contains(lp))
        {
            _onRestoreMain();
            return;
        }
        if (overlayVisible && _prevRect.Contains(lp))
        {
            _onPrev();
            return;
        }
        if (overlayVisible && _playRect.Contains(lp))
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
        if (overlayVisible && _nextRect.Contains(lp))
        {
            _onNext();
            return;
        }
        if (overlayVisible && _muteRect.Contains(lp))
        {
            _isMuted = !_isMuted;
            _volWheelHoldUntil = Environment.TickCount64 + 1200;
            if (_hoverBtn == 5)
            {
                _toolTip.SetToolTip(this, GetTooltipForButton(5));
            }
            EnsureAnimRunning();
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

    private void UpdateVolumeFromPoint(Point lp)
    {
        if (_volTrackHitRect.Width <= 4) return;
        float usableX = lp.X - (_volTrackHitRect.X + 4);
        float usableW = Math.Max(8, _volTrackHitRect.Width - 10);
        double vol = Math.Clamp(usableX / usableW, 0.0, 1.0);
        _volume = vol;
        _isMuted = vol <= 0.001;
        _onSetVolume?.Invoke(vol);
        RenderLayered();
    }

    private void OnCardMouseMove(object? sender, MouseEventArgs e)
    {
        _mouseInside = true;
        Point lp = ToLogical(e.Location);

        if (_scrubbingVolume)
        {
            UpdateVolumeFromPoint(lp);
            return;
        }

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
            : (_volExpandAnim > 0.25f && _volTrackHitRect.Contains(lp)) ? 9
            : _trackHitRect.Contains(lp) ? 6
            : _restoreRect.Contains(lp) ? 7
            : _modeToggleRect.Contains(lp) ? 8
            : 0;

        if (nextHover != _hoverBtn)
        {
            _hoverBtn = nextHover;
            Cursor = nextHover != 0 ? Cursors.Hand : Cursors.Default;
            _toolTip.SetToolTip(this, GetTooltipForButton(nextHover));
            EnsureAnimRunning();
            RenderLayered();
        }
        else if (_isFocusMode && _hoverOverlayAnim < 0.99f)
        {
            EnsureAnimRunning();
        }
    }

    private void OnCardMouseUp(object? sender, MouseEventArgs e)
    {
        if (_scrubbingVolume)
        {
            _scrubbingVolume = false;
            _volWheelHoldUntil = Environment.TickCount64 + 900;
            EnsureAnimRunning();
        }
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
        SavePreviewSnapshot(outputPath, focusMode: false, hoverAnim: 1f, volAnim: 0f);
    }

    public void SavePreviewModes(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        SavePreviewSnapshot(Path.Combine(outputDir, "exe_mini_focus_idle.png"), focusMode: true, hoverAnim: 0f, volAnim: 0f);
        SavePreviewSnapshot(Path.Combine(outputDir, "exe_mini_focus_hover.png"), focusMode: true, hoverAnim: 1f, volAnim: 1f);
        SavePreviewSnapshot(Path.Combine(outputDir, "exe_mini_compact_vol.png"), focusMode: false, hoverAnim: 1f, volAnim: 1f);
    }

    private void SavePreviewSnapshot(string outputPath, bool focusMode, float hoverAnim, float volAnim)
    {
        bool prevMode = _isFocusMode;
        string prevTitle = _title;
        string prevArtist = _artist;
        bool prevPlaying = _isPlaying;
        bool prevMuted = _isMuted;
        double prevVol = _volume;
        double prevCur = _curSec;
        double prevDur = _durSec;
        int prevHover = _hoverBtn;
        float prevOpen = _openAnim;
        float prevOv = _hoverOverlayAnim;
        float prevVolAnim = _volExpandAnim;
        float prevModeAnim = _modeAnim;
        Bitmap? prevCover = _coverBitmap;
        Color prevAmb = _ambientTint;
        Color prevAcc = _accentTint;

        Bitmap sampleCover = CreateSampleLetterboxedCover();
        (Color amb, Color acc) = ExtractCoverPalette(sampleCover);

        _isFocusMode = focusMode;
        _title = "Gülpembe (Canlı Kayıt)";
        _artist = "Barış Manço";
        _isPlaying = true;
        _isMuted = false;
        _volume = 0.72;
        _curSec = 101;
        _durSec = 271;
        _hoverBtn = hoverAnim > 0.5f ? 2 : 0;
        _openAnim = 1f;
        _hoverOverlayAnim = hoverAnim;
        _volExpandAnim = volAnim;
        _modeAnim = 1f;
        _coverFadeAnim = 1f;
        _coverBitmap = sampleCover;
        _ambientTint = amb;
        _accentTint = acc;

        RecalculateLayoutRects();
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
            sampleCover.Dispose();
            _isFocusMode = prevMode;
            _title = prevTitle;
            _artist = prevArtist;
            _isPlaying = prevPlaying;
            _isMuted = prevMuted;
            _volume = prevVol;
            _curSec = prevCur;
            _durSec = prevDur;
            _hoverBtn = prevHover;
            _openAnim = prevOpen;
            _hoverOverlayAnim = prevOv;
            _volExpandAnim = prevVolAnim;
            _modeAnim = prevModeAnim;
            _coverBitmap = prevCover;
            _ambientTint = prevAmb;
            _accentTint = prevAcc;
            RecalculateLayoutRects();
            UpdateScaleForDpi(96);
        }
    }

    private static Bitmap CreateSampleLetterboxedCover()
    {
        // Simulated 320x240 4:3 YouTube thumbnail with baked-in top/bottom black bars to verify smart crop
        var bmp = new Bitmap(320, 240, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        var artRect = new Rectangle(0, 30, 320, 180);
        using (var bg = new LinearGradientBrush(artRect, Color.FromArgb(140, 24, 62), Color.FromArgb(30, 36, 84), LinearGradientMode.ForwardDiagonal))
        {
            g.FillRectangle(bg, artRect);
        }
        using (var glow = new GraphicsPath())
        {
            glow.AddEllipse(new Rectangle(70, 42, 180, 155));
            using var pgb = new PathGradientBrush(glow)
            {
                CenterColor = Color.FromArgb(210, 244, 63, 94),
                SurroundColors = [Color.FromArgb(0, 244, 63, 94)]
            };
            g.FillPath(pgb, glow);
        }
        using var ringPen = new Pen(Color.FromArgb(140, 255, 228, 234), 2.5f);
        g.DrawEllipse(ringPen, 114, 74, 92, 92);
        return bmp;
    }

    private Bitmap DrawCardBitmap()
    {
        int totalW = Math.Max(1, (int)Math.Round((CurrentCardW + ShadowPad * 2) * _scale));
        int totalH = Math.Max(1, (int)Math.Round((CurrentCardH + ShadowPad * 2) * _scale));
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

        // Open/close/mode subtle spring scale & float transform
        float easedOpen = 1f - MathF.Pow(1f - Math.Clamp(_openAnim, 0f, 1f), 3f);
        float easedMode = 1f - MathF.Pow(1f - Math.Clamp(_modeAnim, 0f, 1f), 3f);
        float s = (0.89f + 0.11f * easedOpen) * (0.96f + 0.04f * easedMode);
        float yFloat = (1f - easedOpen) * 14f;
        if (Math.Abs(s - 1f) > 0.001f || Math.Abs(yFloat) > 0.05f)
        {
            float cx = ShadowPad + CurrentCardW / 2f;
            float cy = ShadowPad + CurrentCardH / 2f;
            g.TranslateTransform(cx, cy + yFloat);
            g.ScaleTransform(s, s);
            g.TranslateTransform(-cx, -cy);
        }

        // 1. Multi-layer soft ambient drop shadow
        for (int i = 6; i >= 1; i--)
        {
            float inflate = i * 1.18f;
            float yShift = i * 0.52f;
            var sr = new RectangleF(
                _cardRect.X - inflate,
                _cardRect.Y - inflate + yShift,
                _cardRect.Width + inflate * 2f,
                _cardRect.Height + inflate * 2f);
            using GraphicsPath sp = CreateRoundedPathF(sr, CurrentCornerRadius + inflate);
            using var sb = new SolidBrush(Color.FromArgb(Math.Max(4, 24 - i * 3), 0, 0, 0));
            g.FillPath(sb, sp);
        }

        if (_isFocusMode)
        {
            DrawFocusCoverMode(g);
        }
        else
        {
            DrawCompactBarMode(g);
        }

        return bmp;
    }

    private void DrawFocusCoverMode(Graphics g)
    {
        float ov = Math.Clamp(_hoverOverlayAnim, 0f, 1f);
        using GraphicsPath cardPath = CreateRoundedPath(_cardRect, CurrentCornerRadius);

        GraphicsState cardSaved = g.Save();
        g.SetClip(cardPath);

        // Full-bleed smart-cropped artwork
        DrawArtworkBitmap(g, _cardRect);

        // Subtle bottom gradient scrim so hairline progress bar stays readable on bright covers
        using (var bottomScrim = new LinearGradientBrush(
            new Rectangle(_cardRect.X, _cardRect.Bottom - 56, _cardRect.Width, 56),
            Color.FromArgb(0, 10, 12, 20),
            Color.FromArgb(140, 10, 12, 20),
            LinearGradientMode.Vertical))
        {
            g.FillRectangle(bottomScrim, _cardRect.X, _cardRect.Bottom - 56, _cardRect.Width, 56);
        }

        if (ov > 0.01f)
        {
            int tintAlpha = (int)Math.Round(176 * ov);
            Color baseDark = BlendColor(Color.FromArgb(12, 14, 24), _ambientTint, 0.24f);
            using (var ovBrush = new LinearGradientBrush(
                _cardRect,
                Color.FromArgb((int)Math.Round(152 * ov), baseDark),
                Color.FromArgb(tintAlpha, Color.FromArgb(10, 12, 22)),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(ovBrush, _cardRect);
            }
        }

        // Specular top sheen inside rounded boundary
        using (var topSheen = new LinearGradientBrush(
            new Rectangle(_cardRect.X, _cardRect.Y, _cardRect.Width, 34),
            Color.FromArgb(24, 255, 255, 255),
            Color.FromArgb(0, 255, 255, 255),
            LinearGradientMode.Vertical))
        {
            g.FillRectangle(topSheen, _cardRect.X, _cardRect.Y, _cardRect.Width, 34);
        }

        g.Restore(cardSaved);

        var borderRect = new RectangleF(_cardRect.X + 0.5f, _cardRect.Y + 0.5f, _cardRect.Width - 1f, _cardRect.Height - 1f);
        using GraphicsPath borderPath = CreateRoundedPathF(borderRect, CurrentCornerRadius);
        using var rimPen = new Pen(Color.FromArgb(46, 255, 255, 255), 1f);
        g.DrawPath(rimPen, borderPath);

        // Hover HUD Overlay
        if (ov > 0.03f)
        {
            // Top-right glass pills: Mode Switch (Compact), Open Main, Close
            DrawPillBadge(g, ov);
            DrawModeToggleButton(g, _modeToggleRect, toCompact: true, _hoverBtn == 8, ov);
            DrawRestoreButton(g, _restoreRect, _hoverBtn == 7, ov);
            DrawCloseButton(g, _closeRect, _hoverBtn == 4, ov);

            // Centered Metadata above transport
            int textAlpha = (int)Math.Round(250 * ov);
            int subAlpha = (int)Math.Round(196 * ov);
            using (var titleFont = new Font("Segoe UI", 11.25f, FontStyle.Bold))
            using (var artistFont = new Font("Segoe UI", 8.75f, FontStyle.Regular))
            using (var titleBrush = new SolidBrush(Color.FromArgb(textAlpha, 255, 255, 255)))
            using (var artistBrush = new SolidBrush(Color.FromArgb(subAlpha, 215, 224, 240)))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.DrawString(_title, titleFont, titleBrush, new RectangleF(ShadowPad + 18, ShadowPad + 76, CurrentCardW - 36, 24), fmt);
                g.DrawString(_artist, artistFont, artistBrush, new RectangleF(ShadowPad + 24, ShadowPad + 100, CurrentCardW - 48, 19), fmt);
            }

            // Center Expressive Transport Controls
            DrawSkipButton(g, _prevRect, isNext: false, hovered: _hoverBtn == 1, alphaScale: ov, pillBack: true);
            DrawPlayButton(g, _playRect, _isPlaying, hovered: _hoverBtn == 2, _ambientTint, alphaScale: ov);
            DrawSkipButton(g, _nextRect, isNext: true, hovered: _hoverBtn == 3, alphaScale: ov, pillBack: true);

            // Bottom HUD Row: Left Speaker + Expanding Horizontal Volume Slider; Right Timestamp
            DrawMuteButton(g, _muteRect, _isMuted, _hoverBtn == 5, _accentTint, alphaScale: ov);
            if (_volExpandAnim > 0.03f)
            {
                DrawExpandingVolumeSlider(g, _muteRect.Right + 4, _muteRect.Y + 1, 84f, ov);
            }

            string tStr = _durSec > 0 ? $"{FormatSec(_curSec)} / {FormatSec(_durSec)}" : "--:-- / --:--";
            using (var timeFont = new Font("Segoe UI", 7.75f, FontStyle.Bold))
            using (var timeBrush = new SolidBrush(Color.FromArgb((int)Math.Round(210 * ov), 226, 232, 240)))
            using (var timeFmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(tStr, timeFont, timeBrush, new RectangleF(ShadowPad + CurrentCardW - 118, _muteRect.Y + 4, 102, 18), timeFmt);
            }
        }

        // Bottom edge progress bar: hairline 2.5px at idle, expands smoothly on hover/scrub
        DrawSeekBar(g, isIdleHairline: ov < 0.05f, insetX: 16f, baseOffsetFromBottom: ov > 0.1f ? 12f : 9f);
    }

    private void DrawCompactBarMode(Graphics g)
    {
        Color baseTop = BlendColor(Color.FromArgb(18, 21, 32), _ambientTint, 0.16f);
        Color baseBot = BlendColor(Color.FromArgb(24, 28, 42), _ambientTint, 0.10f);
        using (GraphicsPath cardPath = CreateRoundedPath(_cardRect, CurrentCornerRadius))
        {
            using (var bgGrad = new LinearGradientBrush(
                _cardRect,
                Color.FromArgb(244, baseTop),
                Color.FromArgb(248, baseBot),
                LinearGradientMode.ForwardDiagonal))
            {
                g.FillPath(bgGrad, cardPath);
            }

            GraphicsState saved = g.Save();
            g.SetClip(cardPath);

            var ambientRect = new RectangleF(_cardRect.X - 30f, _cardRect.Y - 24f, 198f, 156f);
            using (var ambientPath = new GraphicsPath())
            {
                ambientPath.AddEllipse(ambientRect);
                using var ambientBrush = new PathGradientBrush(ambientPath)
                {
                    CenterColor = Color.FromArgb(46, _ambientTint),
                    SurroundColors = [Color.FromArgb(0, _ambientTint)]
                };
                g.FillPath(ambientBrush, ambientPath);
            }

            using (var topSheen = new LinearGradientBrush(
                new Rectangle(_cardRect.X, _cardRect.Y, _cardRect.Width, 34),
                Color.FromArgb(20, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(topSheen, _cardRect.X, _cardRect.Y, _cardRect.Width, 34);
            }

            g.Restore(saved);

            var borderRect = new RectangleF(_cardRect.X + 0.5f, _cardRect.Y + 0.5f, _cardRect.Width - 1f, _cardRect.Height - 1f);
            using GraphicsPath borderPath = CreateRoundedPathF(borderRect, CurrentCornerRadius);
            using var rimPen = new Pen(Color.FromArgb(38, 255, 255, 255), 1f);
            g.DrawPath(rimPen, borderPath);
        }

        // Left square smart-cropped cover art
        using (GraphicsPath coverPath = CreateRoundedPath(_coverRect, 12))
        {
            GraphicsState saved = g.Save();
            g.SetClip(coverPath);
            DrawArtworkBitmap(g, _coverRect);
            g.Restore(saved);

            using var cBorder = new Pen(Color.FromArgb(32, 255, 255, 255), 1f);
            g.DrawPath(cBorder, coverPath);
        }

        // Center metadata (leaves safe room for 3 top-right buttons)
        int metaX = ShadowPad + 100;
        int metaW = CurrentCardW - 100 - 98;
        using (var titleFont = new Font("Segoe UI", 9.75f, FontStyle.Bold))
        using (var artistFont = new Font("Segoe UI", 8.25f, FontStyle.Regular))
        using (var whiteBrush = new SolidBrush(Color.FromArgb(248, 250, 252)))
        using (var mutedBrush = new SolidBrush(Color.FromArgb(186, 196, 214)))
        {
            using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(_title, titleFont, whiteBrush, new RectangleF(metaX, ShadowPad + 11, metaW, 20), fmt);
            g.DrawString(_artist, artistFont, mutedBrush, new RectangleF(metaX, ShadowPad + 30, metaW, 17), fmt);
        }

        // Top-right controls: Cover Focus Toggle, Restore Main, Close
        DrawModeToggleButton(g, _modeToggleRect, toCompact: false, _hoverBtn == 8, 1f);
        DrawRestoreButton(g, _restoreRect, _hoverBtn == 7, 1f);
        DrawCloseButton(g, _closeRect, _hoverBtn == 4, 1f);

        // Transport row + expanding horizontal volume bar
        DrawSkipButton(g, _prevRect, isNext: false, hovered: _hoverBtn == 1, alphaScale: 1f, pillBack: false);
        DrawPlayButton(g, _playRect, _isPlaying, hovered: _hoverBtn == 2, _ambientTint, alphaScale: 1f);
        DrawSkipButton(g, _nextRect, isNext: true, hovered: _hoverBtn == 3, alphaScale: 1f, pillBack: false);
        DrawMuteButton(g, _muteRect, _isMuted, hovered: _hoverBtn == 5, _accentTint, alphaScale: 1f);

        if (_volExpandAnim > 0.03f)
        {
            DrawExpandingVolumeSlider(g, _muteRect.Right + 2, _muteRect.Y + 2, 74f, 1f);
        }

        // Smoothly crossfade duration timer out while volume bar expands into right space so there is zero overlap
        float timeAlphaScale = Math.Clamp(1f - _volExpandAnim * 1.15f, 0f, 1f);
        if (timeAlphaScale > 0.04f)
        {
            string tStr = _durSec > 0 ? $"{FormatSec(_curSec)} / {FormatSec(_durSec)}" : "--:-- / --:--";
            using var timeFont = new Font("Segoe UI", 7.5f, FontStyle.Regular);
            using var timeBrush = new SolidBrush(Color.FromArgb((int)Math.Round(182 * timeAlphaScale), 186, 196, 214));
            using var timeFmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            g.DrawString(tStr, timeFont, timeBrush, new RectangleF(ShadowPad + CurrentCardW - 116, ShadowPad + 59, 100, 16), timeFmt);
        }

        DrawSeekBar(g, isIdleHairline: false, insetX: 14f, baseOffsetFromBottom: 10f);
    }

    private void DrawArtworkBitmap(Graphics g, Rectangle dest)
    {
        if (_coverBitmap is not null)
        {
            if (_coverFadeAnim < 0.99f && _prevCoverBitmap is not null)
            {
                Rectangle prevSrc = GetSmartSquareCrop(_prevCoverBitmap);
                g.DrawImage(_prevCoverBitmap, dest, prevSrc, GraphicsUnit.Pixel);

                Rectangle curSrc = GetSmartSquareCrop(_coverBitmap);
                using var ia = new ImageAttributes();
                var cm = new ColorMatrix { Matrix33 = Math.Clamp(_coverFadeAnim, 0f, 1f) };
                ia.SetColorMatrix(cm);
                g.DrawImage(_coverBitmap, dest, curSrc.X, curSrc.Y, curSrc.Width, curSrc.Height, GraphicsUnit.Pixel, ia);
            }
            else
            {
                Rectangle src = GetSmartSquareCrop(_coverBitmap);
                g.DrawImage(_coverBitmap, dest, src, GraphicsUnit.Pixel);
            }
        }
        else
        {
            DrawLogoFallback(g, dest);
        }
    }

    private void DrawPillBadge(Graphics g, float ov)
    {
        var pillRect = new RectangleF(ShadowPad + 12, ShadowPad + 12, 82, 22);
        using GraphicsPath pp = CreateRoundedPathF(pillRect, 11);
        using var bg = new SolidBrush(Color.FromArgb((int)Math.Round(138 * ov), 14, 18, 28));
        g.FillPath(bg, pp);
        using var rim = new Pen(Color.FromArgb((int)Math.Round(42 * ov), 255, 255, 255), 1f);
        g.DrawPath(rim, pp);
        using var dot = new SolidBrush(Color.FromArgb((int)Math.Round(240 * ov), _accentTint));
        g.FillEllipse(dot, pillRect.X + 8, pillRect.Y + 8, 6, 6);
        using var f = new Font("Segoe UI", 6.75f, FontStyle.Bold);
        using var tb = new SolidBrush(Color.FromArgb((int)Math.Round(235 * ov), 248, 250, 252));
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        g.DrawString("KURFA", f, tb, new RectangleF(pillRect.X + 18, pillRect.Y, 60, pillRect.Height), sf);
    }

    private void DrawExpandingVolumeSlider(Graphics g, float x, float y, float maxWidth, float ov)
    {
        float w = Math.Max(10f, maxWidth * _volExpandAnim);
        var pillRect = new RectangleF(x, y, w, 24f);
        using GraphicsPath pp = CreateRoundedPathF(pillRect, 12f);
        using var bg = new SolidBrush(Color.FromArgb((int)Math.Round(168 * ov * _volExpandAnim), 15, 19, 30));
        g.FillPath(bg, pp);
        using var rim = new Pen(Color.FromArgb((int)Math.Round(42 * ov * _volExpandAnim), 255, 255, 255), 1f);
        g.DrawPath(rim, pp);

        if (w < 24f) return;

        float trackX = pillRect.X + 8f;
        float trackW = Math.Max(8f, w - (_isFocusMode ? 36f : 16f));
        float trackY = pillRect.Y + 10.25f;
        float trackH = 3.5f;

        var trRect = new RectangleF(trackX, trackY, trackW, trackH);
        using (GraphicsPath tp = CreateRoundedPathF(trRect, trackH * 0.5f))
        using (var tb = new SolidBrush(Color.FromArgb((int)Math.Round(65 * ov), 255, 255, 255)))
        {
            g.FillPath(tb, tp);
        }

        double effVol = _isMuted ? 0.0 : Math.Clamp(_volume, 0.0, 1.0);
        float fillW = (float)(trackW * effVol);
        if (fillW >= 2.5f)
        {
            var flRect = new RectangleF(trackX, trackY, fillW, trackH);
            using GraphicsPath fp = CreateRoundedPathF(flRect, trackH * 0.5f);
            Color cStart = BlendColor(_ambientTint, Color.White, 0.25f);
            Color cEnd = BlendColor(_accentTint, Color.White, 0.40f);
            using var fb = new LinearGradientBrush(flRect, Color.FromArgb((int)Math.Round(240 * ov), cStart), Color.FromArgb((int)Math.Round(250 * ov), cEnd), LinearGradientMode.Horizontal);
            g.FillPath(fb, fp);
        }

        float kx = Math.Clamp(trackX + fillW, trackX + 2f, trackX + trackW - 2f);
        float ky = trackY + trackH * 0.5f;
        using var halo = new SolidBrush(Color.FromArgb((int)Math.Round(66 * ov * _volExpandAnim), _accentTint));
        g.FillEllipse(halo, kx - 5.5f, ky - 5.5f, 11f, 11f);
        using var kb = new SolidBrush(Color.FromArgb((int)Math.Round(250 * ov * _volExpandAnim), Color.White));
        g.FillEllipse(kb, kx - 3.6f, ky - 3.6f, 7.2f, 7.2f);

        if (_isFocusMode && w >= 66f)
        {
            string pctStr = $"{Math.Round(effVol * 100)}%";
            using var pf = new Font("Segoe UI", 6.75f, FontStyle.Bold);
            using var pb = new SolidBrush(Color.FromArgb((int)Math.Round(225 * ov * _volExpandAnim), 240, 245, 255));
            using var pfmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            g.DrawString(pctStr, pf, pb, new RectangleF(trackX + trackW, pillRect.Y, 24f, 24f), pfmt);
        }
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
        using var bg = new SolidBrush(Color.FromArgb(24, 30, 48));
        g.FillRectangle(bg, rect);

        if (_logoBitmap is not null)
        {
            float emblemSize = rect.Width > 160 ? 78f : 38f;
            float x = rect.X + (rect.Width - emblemSize) / 2f;
            float y = rect.Y + (rect.Height - emblemSize) / 2f;
            g.DrawImage(_logoBitmap, new RectangleF(x, y, emblemSize, emblemSize));
        }
    }

    private static void DrawModeToggleButton(Graphics g, Rectangle r, bool toCompact, bool hovered, float alphaScale)
    {
        using (var bg = new SolidBrush(Color.FromArgb((int)Math.Round((hovered ? 170 : 108) * alphaScale), 15, 19, 30)))
        {
            g.FillEllipse(bg, r);
        }
        Color fg = Color.FromArgb((int)Math.Round((hovered ? 255 : 210) * alphaScale), Color.White);
        using var pen = new Pen(fg, 1.45f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        if (toCompact)
        {
            // Horizontal bar silhouette icon inside circle
            g.DrawRoundedRectangle(pen, r.X + 5.5f, r.Y + 8.5f, r.Width - 11f, 7f, 2.5f);
        }
        else
        {
            // Rounded square cover icon inside circle
            g.DrawRoundedRectangle(pen, r.X + 6.5f, r.Y + 6.5f, r.Width - 13f, r.Height - 13f, 2.5f);
        }
    }

    private static void DrawRestoreButton(Graphics g, Rectangle r, bool hovered, float alphaScale)
    {
        using (var hb = new SolidBrush(Color.FromArgb((int)Math.Round((hovered ? 170 : 108) * alphaScale), 15, 19, 30)))
        {
            g.FillEllipse(hb, r);
        }
        Color fg = Color.FromArgb((int)Math.Round((hovered ? 255 : 210) * alphaScale), Color.White);
        using var pen = new Pen(fg, 1.45f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        float x1 = r.X + 6.5f;
        float y1 = r.Y + 6.5f;
        float x2 = r.Right - 6.5f;
        float y2 = r.Bottom - 6.5f;

        g.DrawLine(pen, x2 - 4.0f, y1, x2, y1);
        g.DrawLine(pen, x2, y1, x2, y1 + 4.0f);
        g.DrawLine(pen, x2 - 4.2f, y1 + 4.2f, x2, y1);

        g.DrawLine(pen, x1, y2 - 4.0f, x1, y2);
        g.DrawLine(pen, x1, y2, x1 + 4.0f, y2);
        g.DrawLine(pen, x1 + 4.2f, y2 - 4.2f, x1, y2);
    }

    private static void DrawCloseButton(Graphics g, Rectangle r, bool hovered, float alphaScale)
    {
        using (var hb = new SolidBrush(Color.FromArgb((int)Math.Round((hovered ? 195 : 108) * alphaScale), hovered ? 225 : 15, hovered ? 45 : 19, hovered ? 78 : 30)))
        {
            g.FillEllipse(hb, r);
        }
        Color fg = Color.FromArgb((int)Math.Round((hovered ? 255 : 215) * alphaScale), Color.White);
        using var pen = new Pen(fg, 1.55f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, r.X + 7.5f, r.Y + 7.5f, r.Right - 7.5f, r.Bottom - 7.5f);
        g.DrawLine(pen, r.Right - 7.5f, r.Y + 7.5f, r.X + 7.5f, r.Bottom - 7.5f);
    }

    private static void DrawPlayButton(Graphics g, Rectangle r, bool isPlaying, bool hovered, Color ambientTint, float alphaScale)
    {
        if (hovered)
        {
            var glowRect = new RectangleF(r.X - 4f, r.Y - 4f, r.Width + 8f, r.Height + 8f);
            using var glowPath = new GraphicsPath();
            glowPath.AddEllipse(glowRect);
            using var pgb = new PathGradientBrush(glowPath)
            {
                CenterColor = Color.FromArgb((int)Math.Round(72 * alphaScale), ambientTint),
                SurroundColors = [Color.FromArgb(0, ambientTint)]
            };
            g.FillPath(pgb, glowPath);
        }

        Color cTop = Color.FromArgb((int)Math.Round(250 * alphaScale), hovered ? 255 : 246, hovered ? 255 : 249, 255);
        Color cBot = Color.FromArgb((int)Math.Round(244 * alphaScale), hovered ? 238 : 224, hovered ? 242 : 231, 245);
        using (var pb = new LinearGradientBrush(r, cTop, cBot, LinearGradientMode.Vertical))
        {
            g.FillEllipse(pb, r);
        }

        using var rim = new Pen(Color.FromArgb((int)Math.Round(100 * alphaScale), 255, 255, 255), 1f);
        g.DrawEllipse(rim, r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);

        float cx = r.X + r.Width / 2f;
        float cy = r.Y + r.Height / 2f;
        float barH = r.Height * 0.37f;
        float barW = r.Width * 0.11f;
        float gap = r.Width * 0.09f;

        using var iconBrush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * alphaScale), 18, 22, 34));
        if (isPlaying)
        {
            g.FillRectangle(iconBrush, cx - gap / 2f - barW, cy - barH / 2f, barW, barH);
            g.FillRectangle(iconBrush, cx + gap / 2f, cy - barH / 2f, barW, barH);
        }
        else
        {
            float halfH = barH * 0.56f;
            float leftX = cx - r.Width * 0.11f;
            float rightX = cx + r.Width * 0.19f;
            PointF[] tri = [new(leftX, cy - halfH), new(rightX, cy), new(leftX, cy + halfH)];
            g.FillPolygon(iconBrush, tri);
        }
    }

    private void DrawSeekBar(Graphics g, bool isIdleHairline, float insetX, float baseOffsetFromBottom)
    {
        bool activeSeek = _hoverBtn == 6 || _scrubbingSeek;
        float barH = isIdleHairline ? 2.6f : (activeSeek ? 4.6f : 3.2f);
        float barY = ShadowPad + CurrentCardH - baseOffsetFromBottom - (barH - 3f) * 0.5f;
        var trackRect = new RectangleF(ShadowPad + insetX, barY, CurrentCardW - insetX * 2f, barH);

        using (var trackPath = CreateRoundedPathF(trackRect, barH * 0.5f))
        using (var trackBrush = new SolidBrush(Color.FromArgb(isIdleHairline ? 56 : 48, 255, 255, 255)))
        {
            g.FillPath(trackBrush, trackPath);
        }

        double pct = _durSec > 0 ? Math.Clamp(_curSec / _durSec, 0.0, 1.0) : 0.0;
        float fillW = (float)(trackRect.Width * pct);
        if (fillW >= 3f)
        {
            var fillRect = new RectangleF(trackRect.X, trackRect.Y, fillW, trackRect.Height);
            using var fillPath = CreateRoundedPathF(fillRect, barH * 0.5f);
            Color fillStart = BlendColor(_ambientTint, Color.White, 0.25f);
            Color fillEnd = BlendColor(_accentTint, Color.White, 0.35f);
            using var fillBrush = new LinearGradientBrush(
                fillRect,
                Color.FromArgb(238, fillStart),
                Color.FromArgb(248, fillEnd),
                LinearGradientMode.Horizontal);
            g.FillPath(fillBrush, fillPath);
        }

        if (activeSeek && _durSec > 0 && !isIdleHairline)
        {
            float knobX = Math.Clamp(trackRect.X + fillW, trackRect.X + 4f, trackRect.Right - 4f);
            float knobY = trackRect.Y + barH * 0.5f;
            using var halo = new SolidBrush(Color.FromArgb(72, _accentTint));
            g.FillEllipse(halo, knobX - 6.5f, knobY - 6.5f, 13f, 13f);
            using var kb = new SolidBrush(Color.White);
            g.FillEllipse(kb, knobX - 4f, knobY - 4f, 8f, 8f);
        }
    }

    private static void DrawSkipButton(Graphics g, Rectangle r, bool isNext, bool hovered, float alphaScale, bool pillBack)
    {
        if (pillBack || hovered)
        {
            int bgAlpha = hovered ? 128 : 74;
            using var hb = new SolidBrush(Color.FromArgb((int)Math.Round(bgAlpha * alphaScale), pillBack ? 18 : 255, pillBack ? 22 : 255, pillBack ? 34 : 255));
            g.FillEllipse(hb, r);
        }
        using var b = new SolidBrush(Color.FromArgb((int)Math.Round((hovered ? 255 : 225) * alphaScale), Color.White));
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

    private static void DrawMuteButton(Graphics g, Rectangle r, bool isMuted, bool hovered, Color accentTint, float alphaScale)
    {
        if (hovered)
        {
            using var hb = new SolidBrush(Color.FromArgb((int)Math.Round(96 * alphaScale), 255, 255, 255));
            g.FillEllipse(hb, r);
        }
        Color col = isMuted
            ? Color.FromArgb((int)Math.Round(245 * alphaScale), 244, 114, 140)
            : Color.FromArgb((int)Math.Round((hovered ? 255 : 216) * alphaScale), Color.White);
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
                if (max < 24 || min > 236) continue;
                double sat = max > 0 ? (max - min) / (double)max : 0;
                double lum = (0.299 * p.R + 0.587 * p.G + 0.114 * p.B) / 255.0;
                double midBias = 1.0 - Math.Abs(lum - 0.52);
                double w = 0.25 + sat * 1.8 + Math.Max(0.0, midBias) * 0.4;
                rSum += p.R * w;
                gSum += p.G * w;
                bSum += p.B * w;
                wSum += w;
            }
        }

        if (wSum <= 0.01)
        {
            return (Color.FromArgb(138, 152, 224), Color.FromArgb(244, 63, 94));
        }

        int r = Math.Clamp((int)Math.Round(rSum / wSum), 0, 255);
        int g = Math.Clamp((int)Math.Round(gSum / wSum), 0, 255);
        int b = Math.Clamp((int)Math.Round(bSum / wSum), 0, 255);

        Color raw = Color.FromArgb(r, g, b);
        Color ambient = BlendColor(raw, Color.FromArgb(132, 146, 190), 0.32f);
        Color accent = BlendColor(raw, Color.FromArgb(255, 240, 244), 0.22f);
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

    private static Rectangle GetSmartSquareCrop(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        if (w <= 4 || h <= 4) return new Rectangle(0, 0, w, h);

        // Trim baked-in top & bottom black letterbox bars (common on 4:3 YouTube thumbnails)
        int topTrim = 0;
        int botTrim = h;
        int maxScanY = h / 4;
        int sampleX1 = Math.Max(0, w / 4);
        int sampleX2 = w / 2;
        int sampleX3 = Math.Min(w - 1, (w * 3) / 4);

        static bool IsDarkBarRow(Bitmap b, int y, int x1, int x2, int x3)
        {
            Color c1 = b.GetPixel(x1, y);
            Color c2 = b.GetPixel(x2, y);
            Color c3 = b.GetPixel(x3, y);
            int m1 = Math.Max(c1.R, Math.Max(c1.G, c1.B));
            int m2 = Math.Max(c2.R, Math.Max(c2.G, c2.B));
            int m3 = Math.Max(c3.R, Math.Max(c3.G, c3.B));
            return m1 < 18 && m2 < 18 && m3 < 18;
        }

        while (topTrim < maxScanY && IsDarkBarRow(bmp, topTrim, sampleX1, sampleX2, sampleX3))
        {
            topTrim++;
        }
        while (botTrim > h - maxScanY && IsDarkBarRow(bmp, botTrim - 1, sampleX1, sampleX2, sampleX3))
        {
            botTrim--;
        }

        int usableH = Math.Max(4, botTrim - topTrim);
        int side = Math.Min(w, usableH);
        int srcX = (w - side) / 2;
        int srcY = topTrim + (usableH - side) / 2;
        return new Rectangle(srcX, srcY, side, side);
    }

    private static string FormatSec(double sec)
    {
        int s = Math.Max(0, (int)Math.Floor(sec));
        return $"{s / 60}:{s % 60:D2}";
    }

    public void UpdateState(string? title, string? artist, string? thumbUrl, bool isPlaying, bool isMuted, double curSec, double durSec, double? volume = null)
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
        if (volume.HasValue && !_scrubbingVolume && Environment.TickCount64 >= _volWheelHoldUntil)
        {
            _volume = Math.Clamp(volume.Value, 0.0, 1.0);
        }
        if (!_scrubbingSeek) _curSec = curSec;
        _durSec = durSec;
        UpdateProgressTimerState();

        if (!string.IsNullOrWhiteSpace(thumbUrl) &&
            !string.Equals(thumbUrl, _lastThumbUrl, StringComparison.Ordinal))
        {
            _lastThumbUrl = thumbUrl;
            _ = LoadThumbnailAsync(thumbUrl);
        }
        if (IsHandleCreated && Visible)
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
                    _prevCoverBitmap?.Dispose();
                    _prevCoverBitmap = _coverBitmap;
                    _coverBitmap = copy;
                    _coverFadeAnim = _prevCoverBitmap is not null ? 0f : 1f;
                    _ambientTint = ambient;
                    _accentTint = accent;
                    EnsureAnimRunning();
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
            byte masterAlpha = (byte)Math.Clamp((int)Math.Round(255f * _openAnim), 0, 255);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = masterAlpha,
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
            _animTimer.Stop();
            _animTimer.Dispose();
            _toolTip.Dispose();
            _coverBitmap?.Dispose();
            _prevCoverBitmap?.Dispose();
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

internal static class GraphicsExt
{
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, float x, float y, float w, float h, float r)
    {
        float d = Math.Max(1f, Math.Min(r * 2f, Math.Min(w, h)));
        using var gp = new GraphicsPath();
        gp.AddArc(x, y, d, d, 180, 90);
        gp.AddArc(x + w - d, y, d, d, 270, 90);
        gp.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        gp.AddArc(x, y + h - d, d, d, 90, 90);
        gp.CloseFigure();
        g.DrawPath(pen, gp);
    }
}
