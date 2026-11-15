using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// "Liquid" physics: the island's outline ripples, it squashes / leans while dragged and wobbles back,
/// clicks spread a ripple, music makes the bottom edge drip with the beat, content appears with an iOS-like
/// spring + blur.
/// </summary>
public partial class MainWindow
{
    // jelly: scale & lean, underdamped so they wobble a couple of times like a water drop
    private readonly Spring _sx = new(1) { Stiffness = 420, Damping = 12 };
    private readonly Spring _sy = new(1) { Stiffness = 420, Damping = 12 };
    private readonly Spring _skew = new(0) { Stiffness = 300, Damping = 10 };

    // floating window position while / after dragging
    private readonly Spring _px = new(0) { Stiffness = 560, Damping = 38 };
    private readonly Spring _py = new(0) { Stiffness = 560, Damping = 38 };
    private bool _posActive;

    private double _ripple;       // amplitude (px) of the wave running around the edge
    private double _ripplePhase;
    private double _audioLevel;
    private double _dripPhase;

    private bool Liquid => _settings.LiquidWaves && !Lite;
    /// <summary>The waves slider: 1 at the default (60), from about 0.2 to 1.7.</summary>
    private double LiquidK => Math.Clamp(_settings.LiquidStrength, 10, 100) / 60.0;

    /// <summary>
    /// Light graphics: on weak PCs every effect is a full redraw of a see-through window on the CPU.
    /// Default: on when the graphics card is not fully accelerated or there are 4 logical cores or fewer.
    /// </summary>
    private bool Lite => _settings.LiteGraphics ?? (AutoLite || _slowPcSeen || _batterySaver);

    // the island watches its own animations: if many frames come late, light graphics switch on by themselves
    private bool _slowPcSeen;
    private int _animFrames, _animSlow;

    private void CountAnimationFrame(double dt)
    {
        if (_settings.LiteGraphics != null || _slowPcSeen || _demoMedia != null) return;
        _animFrames++;
        if (dt > 0.028) _animSlow++;
        if (_animFrames < 240) return;
        if (_animSlow > _animFrames * 0.2)
        {
            _slowPcSeen = true;
            Log.Write($"slow animations ({_animSlow}/{_animFrames} late frames): light graphics on");
            ApplyGlass(); UpdateAuraVisibility();
            ShowToast("", L.T("Включена облегчённая графика"), L.T("Анимации шли рывками — эффекты выключены для плавности. Вернуть: Настройки → Поведение."));
        }
        _animFrames = _animSlow = 0;
    }
    private static readonly bool AutoLite = (System.Windows.Media.RenderCapability.Tier >> 16) < 2 || Environment.ProcessorCount <= 4;

    // ---- reactive pill: the lower edge reaches for the cursor and breathes with the music ----
    private readonly Spring _reach = new(0) { Stiffness = 210, Damping = 17 };
    private double _reachX;
    private Color? _albumColor;

    private readonly Spring _lean = new(0) { Stiffness = 190, Damping = 16 };
    private Point _reachCursor;      // where the bulge points, in island coordinates (eased every frame)
    private Point _reachGoal;        // where the cursor really is
    private Point _reachOrigin;      // the island's top-left corner on screen, in pixels
    private long _reachMovedTick;    // when the cursor last moved near the island

    /// <summary>The cursor is moving near the island (or the body is still settling): the reach wants smooth frames.</summary>
    private bool ReachMoving => !_reach.IsSettled || !_lean.IsSettled ||
                                (_reach.Target > 0 && Environment.TickCount64 - _reachMovedTick < 150);

    /// <summary>
    /// The pill feels the cursor: from below the lower edge swells towards it like a drop about to fall,
    /// from the side the whole island leans over a few pixels.
    /// </summary>
    private void UpdateReach(Native.POINT cursor)
    {
        double bump = 0, lean = 0;
        bool moved = false;
        if (_settings.Reactive && _mode == Mode.Compact && !_dragging && !_hidden && !_overFullscreen && Island.IsVisible)
        {
            Point tl;
            try { tl = Island.PointToScreen(new Point(0, 0)); }
            catch { return; }
            double x = (cursor.X - tl.X) / Dpi, y = (cursor.Y - tl.Y) / Dpi;
            double w = Island.ActualWidth, h = Island.ActualHeight;
            double dx = x < 0 ? x : x > w ? x - w : 0;      // horizontal distance outside the pill
            double dy = y > h ? y - h : y < 0 ? y : 0;       // vertical distance outside the pill
            double dist = Math.Sqrt(dx * dx + dy * dy);
            const double range = 170;
            if (dist > 0 && dist < range)
            {
                double k = 1 - dist / range;
                k = k * k * (3 - 2 * k);                     // smoothstep: soft start, firm end
                bump = k;                                    // 0…1: how hard the body flows to the cursor
                if (dx != 0) lean = Math.Sign(dx) * 4 * k;
                // where the cursor is, in island coordinates. The bulge itself is eased towards it every frame
                // (StepLiquid): easing it here, 30 times a second, made the wave move in visible steps.
                moved = Math.Abs(x - _reachGoal.X) > 0.5 || Math.Abs(y - _reachGoal.Y) > 0.5;
                if (moved) _reachMovedTick = Environment.TickCount64;
                _reachGoal = new Point(x, y);
                _reachOrigin = tl;
                // a new approach starts at the cursor, not where the last one ended
                if (_reach.Value < 0.02 && _reach.Target < 0.02) _reachCursor = _reachGoal;
            }
        }
        bool changed = Math.Abs(_reach.Target - bump) > 0.05 || Math.Abs(_lean.Target - lean) > 0.05;
        _reach.Target = bump;
        _lean.Target = lean;
        if (changed || moved) StartRendering();
    }

    /// <summary>For a split second the island splits into red / green / blue, like a misaligned print.</summary>
    private Views.ChromaticEffect? _chromatic;
    private bool _chromaticTried;

    private static bool PerfNoChromatic;

    private void PlayChromatic(double px = 7)
    {
        if (Lite || PerfNoChromatic) return;
        if (!_chromaticTried)
        {
            _chromaticTried = true;
            _chromatic = Views.ChromaticEffect.TryCreate();
        }
        if (_chromatic == null || IslandBody.ActualWidth < 10) return;

        double peak = px / Math.Max(200, IslandBody.ActualWidth);
        IslandBody.Effect = _chromatic;
        var anim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(-peak * 0.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new SineEase()));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        anim.Completed += (_, _) => { if (IslandBody.Effect == _chromatic) IslandBody.Effect = null; }; // back to crisp
        _chromatic.BeginAnimation(Views.ChromaticEffect.AmountProperty, anim);
    }

    /// <summary>A soft coloured flash around the island when something happens.</summary>
    private void PulseGlow(Color color)
    {
        if (!_settings.Reactive || IslandShadow is not DropShadowEffect fx) return;
        var dur = TimeSpan.FromMilliseconds(1300);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        fx.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(color, Colors.Black, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        fx.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(1, fx.Opacity, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        fx.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(52, 34, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        fx.BeginAnimation(DropShadowEffect.ShadowDepthProperty, new DoubleAnimation(0, 6, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>The vivid colour of an album cover (for the equaliser, glow and character accents).</summary>
    private static Color? DominantColor(System.Windows.Media.Imaging.BitmapSource art)
    {
        try
        {
            double k = 12.0 / Math.Max(art.PixelWidth, art.PixelHeight);
            var small = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                new System.Windows.Media.Imaging.TransformedBitmap(art, new ScaleTransform(k, k)), PixelFormats.Bgra32, null, 0);
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);
            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double bb = px[i], gg = px[i + 1], rr = px[i + 2];
                double max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
                double weight = 0.05 + (max - min) / 255.0 * (max / 255.0); // colourful, bright pixels count most
                r += rr * weight; g += gg * weight; b += bb * weight; total += weight;
            }
            if (total <= 0) return null;
            r /= total; g /= total; b /= total;
            double top = Math.Max(r, Math.Max(g, b));
            if (top < 1) return null;
            double boost = 235 / top; // bright enough to read on a black island
            return Color.FromRgb((byte)Math.Min(255, r * boost), (byte)Math.Min(255, g * boost), (byte)Math.Min(255, b * boost));
        }
        catch { return null; }
    }

    /// <summary>
    /// The island's outline. Floating: a rounded pill; docked: flat top with small "ears".
    /// <paramref name="bump"/> px of the lower edge bulge out around <paramref name="bumpX"/>.
    /// </summary>
    private static Geometry PillGeometry(double w, double h, double r, bool docked, double bumpX, double bump)
    {
        const double ear = 9;
        double half = Math.Min(40, Math.Max(6, (w - 2 * r) / 2));
        double bx = Math.Clamp(bumpX, r + half, Math.Max(r + half, w - r - half));
        bool hasBump = bump > 0.2 && w - 2 * r > 14;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (docked)
            {
                c.BeginFigure(new Point(-ear, 0), true, true);
                c.QuadraticBezierTo(new Point(0, 0), new Point(0, ear), true, true);
                c.LineTo(new Point(0, h - r), true, true);
            }
            else
            {
                c.BeginFigure(new Point(r, 0), true, true);
                c.ArcTo(new Point(0, r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
                c.LineTo(new Point(0, h - r), true, true);
            }
            c.ArcTo(new Point(r, h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            if (hasBump)
            {
                c.LineTo(new Point(bx - half, h), true, true);
                c.BezierTo(new Point(bx - half * 0.5, h), new Point(bx - half * 0.45, h + bump), new Point(bx, h + bump), true, true);
                c.BezierTo(new Point(bx + half * 0.45, h + bump), new Point(bx + half * 0.5, h), new Point(bx + half, h), true, true);
            }
            c.LineTo(new Point(w - r, h), true, true);
            c.ArcTo(new Point(w, h - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            if (docked)
            {
                c.LineTo(new Point(w, ear), true, true);
                c.QuadraticBezierTo(new Point(w, 0), new Point(w + ear, 0), true, true);
            }
            else
            {
                c.LineTo(new Point(w, r), true, true);
                c.ArcTo(new Point(w - r, 0), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            }
        }
        return g;
    }

    /// <summary>Send a wave around the island's edge.</summary>
    public void Kick(double px)
    {
        if (!Liquid) return;
        _ripple = Math.Min(7 * LiquidK, _ripple + px * LiquidK);
        StartRendering();
    }

    private void Squish(double scale)
    {
        if (!Liquid) return;
        _sx.Target = _sy.Target = scale;
        StartRendering();
    }

    private void Unsquish(double bounce)
    {
        _sx.Target = _sy.Target = 1;
        if (Liquid)
        {
            _sx.Velocity += bounce;
            _sy.Velocity += bounce;
        }
        StartRendering();
    }

    /// <summary>Called every frame. Returns true while anything still moves.</summary>
    private bool StepLiquid(double dt)
    {
        _sx.Step(dt);
        _sy.Step(dt);
        _skew.Step(dt);
        IslandScale.ScaleX = _sx.Value;
        IslandScale.ScaleY = _sy.Value;
        IslandSkew.AngleX = _skew.Value;

        if (_posActive)
        {
            if (_dragging && !DragOnSpring)
            {
                // while the hand holds it, the island sits exactly where the cursor is *now* (read this frame,
                // not at the last mouse event) — a spring here trails the cursor by tens of pixels
                var (tx, ty) = DragTarget(CursorDip());
                _px.Target = tx; _py.Target = ty;
                _px.Snap(tx); _py.Snap(ty);
            }
            else
            {
                _px.Step(dt);
                _py.Step(dt);
            }
            if (_hwnd != IntPtr.Zero)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                Native.MoveTo(_hwnd, (int)Math.Round(_px.Value * dpi.DpiScaleX), (int)Math.Round(_py.Value * dpi.DpiScaleY));
            }
            else { Left = _px.Value; Top = _py.Value; }
            if (!_dragging && _px.IsSettled && _py.IsSettled)
            {
                _posActive = false;
                ClampPosition();
                SavePosition();
            }
        }

        bool playing = _lastMedia?.IsPlaying == true && !_hidden && !_fullscreenHidden && !Quiet;
        double peak = playing ? _audio.Peak : 0;
        _audioLevel += (peak - _audioLevel) * (peak > _audioLevel ? 0.45 : 0.08);
        if (!playing && _audioLevel < 0.005) _audioLevel = 0;

        _ripple *= Math.Exp(-dt * 2.3);
        if (_ripple < 0.03) _ripple = 0;
        _ripplePhase += dt * 7.5;
        _dripPhase += dt * (2.2 + _audioLevel * 5);

        // the bulge follows the cursor every frame: read it now, ease towards it by the time that really passed
        if (_reach.Target > 0)
        {
            Native.GetCursorPos(out var cp);
            if (_fakeCursor is Native.POINT fake) cp = fake;
            double scale = Dpi;
            double gx = (cp.X - _reachOrigin.X) / scale, gy = (cp.Y - _reachOrigin.Y) / scale;
            if (Math.Abs(gx - _reachGoal.X) > 0.5 || Math.Abs(gy - _reachGoal.Y) > 0.5) _reachMovedTick = Environment.TickCount64;
            _reachGoal = new Point(gx, gy);
        }
        double ease = 1 - Math.Exp(-dt * 14);
        _reachCursor = new Point(_reachCursor.X + (_reachGoal.X - _reachCursor.X) * ease, _reachCursor.Y + (_reachGoal.Y - _reachCursor.Y) * ease);
        bool chasing = Math.Abs(_reachCursor.X - _reachGoal.X) > 0.3 || Math.Abs(_reachCursor.Y - _reachGoal.Y) > 0.3;
        _reach.Step(dt);
        _lean.Step(dt);
        // the glow along the lower edge is a strip of light, not a shape: where the body swells out it would
        // show as a bright blob, so it dims while the island reaches
        double dim = 1 - Math.Clamp(_reach.Value, 0, 1) * 0.9;
        if (Math.Abs(AuraDim.Opacity - dim) > 0.01) AuraDim.Opacity = dim;
        IslandShift.X = _lean.Value;
        bool shapeMoving = Liquid && (_ripple > 0 || playing || _audioLevel > 0);
        bool reactive = _settings.Reactive && !Lite && _mode == Mode.Compact &&
                        (playing || _audioLevel > 0 || !_reach.IsSettled || !_lean.IsSettled || (_reach.Value > 0.01 && chasing));
        UpdateShape(shapeMoving);

        return shapeMoving || reactive || _posActive || !(_sx.IsSettled && _sy.IsSettled && _skew.IsSettled);
    }

    private double Radius(double h) => _settings.Docked
        ? Math.Min(h / 2, h <= 60 ? 15 : 28)
        : Math.Min(h / 2, 30);

    /// <summary>
    /// Notch shape: flat top flush with the screen edge, small concave "ears" that melt into it,
    /// rounded bottom corners.
    /// </summary>
    private static Geometry DockedGeometry(double w, double h, double r)
    {
        const double ear = 9;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(-ear, 0), true, true);
            c.QuadraticBezierTo(new Point(0, 0), new Point(0, ear), true, true);
            c.LineTo(new Point(0, h - r), true, true);
            c.ArcTo(new Point(r, h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            c.LineTo(new Point(w - r, h), true, true);
            c.ArcTo(new Point(w, h - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            c.LineTo(new Point(w, ear), true, true);
            c.QuadraticBezierTo(new Point(w, 0), new Point(w + ear, 0), true, true);
        }
        return g;
    }

    private void UpdateShape(bool liquid)
    {
        double w = Island.Width, h = Island.Height;
        if (double.IsNaN(w) || double.IsNaN(h) || w < 2 || h < 2) return;
        double r = Radius(h);

        // reactive bulge of the lower edge: towards the cursor, or a gentle wandering swell with the music
        double bump = 0, bumpX = w / 2;
        if (_settings.Reactive && !Lite && _mode == Mode.Compact)
        {
            if (_reach.Value > 0.01) bump = -1; // handled by FlowGeometry below
            else if (_audioLevel > 0.02) { bump = _audioLevel * 5.5; bumpX = w / 2 + Math.Sin(_dripPhase * 0.6) * w * 0.22; }
        }

        Geometry g = bump < 0 ? FlowGeometry(w, h, r, _settings.Docked, _reachCursor, _reach.Value)
                   : liquid && !_settings.Docked ? LiquidGeometry(w, h, r)
                   : PillGeometry(w, h, r, _settings.Docked, bumpX, bump);
        g.Freeze();
        IslandShape.Data = g;
        IslandShine.Data = g;
        ShapeChanged(g, w);
    }

    /// <summary>
    /// The island reaches for the cursor like a drop about to fall: the stretch of the outline nearest to the
    /// cursor swells into a rounded lobe that points at it, with soft shoulders running into the body. The
    /// rest of the outline stays where it is, and so does the top edge.
    /// (Before, every point of the side facing the cursor moved by the same vector, which sheared the whole
    /// end of the pill and left a sharp corner.)
    /// </summary>
    private static Geometry FlowGeometry(double w, double h, double r, bool docked, Point cursor, double strength)
    {
        var pts = new List<Point>(280);
        var normals = new List<Vector>(280);
        var pinned = new List<bool>(280);   // points that keep their place whatever happens (the docked "ears")

        void Add(double x, double y, double nx, double ny) { pts.Add(new Point(x, y)); normals.Add(new Vector(nx, ny)); pinned.Add(false); }
        void Pin(double x, double y) { pts.Add(new Point(x, y)); normals.Add(default); pinned.Add(true); }
        void Arc(double cx, double cy, double fromDeg, double toDeg)
        {
            int n = Math.Max(6, (int)(r * Math.PI / 2 / 2.5));
            for (int i = 1; i <= n; i++)
            {
                double a = (fromDeg + (toDeg - fromDeg) * i / n) * Math.PI / 180;
                double nx = Math.Cos(a), ny = Math.Sin(a);
                Add(cx + r * nx, cy + r * ny, nx, ny);
            }
        }

        // the resting outline, as a ring of points a few pixels apart, each with its outward direction
        const double step = 3, ear = 9;
        if (docked)
        {
            Pin(-ear, 0);
            for (int i = 1; i <= 6; i++) { double t = i / 6.0; Pin(-ear + ear * (1 - (1 - t) * (1 - t)), ear * t * t); }
            for (double y = ear + step; y < h - r; y += step) Add(0, y, -1, 0);
        }
        else
        {
            Add(r, 0, 0, -1);
            Arc(r, r, 270, 180);
            for (double y = r + step; y < h - r; y += step) Add(0, y, -1, 0);
        }
        Arc(r, h - r, 180, 90);
        for (double x = r + step; x < w - r; x += step) Add(x, h, 0, 1);
        Arc(w - r, h - r, 90, 0);
        if (docked)
        {
            for (double y = h - r - step; y > ear; y -= step) Add(w, y, 1, 0);
            for (int i = 0; i <= 6; i++) { double t = i / 6.0; Pin(w + ear * t * t, ear * (1 - t) * (1 - t)); }
        }
        else
        {
            for (double y = h - r - step; y > r; y -= step) Add(w, y, 1, 0);
            Arc(w - r, r, 0, -90);
            for (double x = w - r - step; x > r; x -= step) Add(x, 0, 0, -1);
        }

        int count = pts.Count;
        if (strength > 0.004)
        {
            // how far along the outline each point is, and which point is nearest to the cursor
            var along = new double[count];
            int nearest = -1;
            double best = double.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (i > 0) along[i] = along[i - 1] + (pts[i] - pts[i - 1]).Length;
                if (pinned[i]) continue;
                double d = (cursor - pts[i]).LengthSquared;
                if (d < best) { best = d; nearest = i; }
            }
            double total = along[count - 1] + (pts[0] - pts[count - 1]).Length;

            if (nearest >= 0)
            {
                // the whole lobe moves one way: from the nearest point of the outline towards the cursor
                var aim = cursor - pts[nearest];
                double room = aim.Length;
                if (room > 0.5) aim /= room; else aim = normals[nearest];

                double tip = Math.Min(17 * strength, room * 0.8);   // how far the tip goes: never past the cursor
                double width = 16 + 10 * strength;                  // half-width of the lobe along the outline
                double shoulders = width * 2.2;                     // the low swell it grows out of
                double anchor = along[nearest];
                for (int i = 0; i < count; i++)
                {
                    if (pinned[i]) continue;
                    double ds = Math.Abs(along[i] - anchor);
                    ds = Math.Min(ds, total - ds);     // the outline is a ring
                    // a bell: round at the tip, running smoothly into the body on both sides
                    double lobe = Math.Exp(-(ds * ds) / (width * width));
                    double swell = Math.Exp(-(ds * ds) / (shoulders * shoulders));
                    double amount = tip * (0.84 * lobe + 0.16 * swell);
                    if (amount < 0.02) continue;

                    var p = pts[i];
                    // it hangs from the top: the top edge never moves up or down, everything below the upper third does
                    double hold = Math.Clamp(p.Y / (h * 0.3), 0, 1);
                    hold = hold * hold * (3 - 2 * hold);

                    // a touch of "straight out" keeps the flanks from folding where the outline turns a corner
                    var dir = aim * 0.85 + normals[i] * 0.15;
                    if (dir.LengthSquared < 0.0001) continue;
                    dir.Normalize();
                    var move = dir * amount;
                    // sideways the floating pill may stretch at any height (its end grows a round nose, the top
                    // edge just gets longer); the docked one is held whole, its ears are fixed to the screen edge
                    if (docked) move *= hold; else move.Y *= hold;
                    pts[i] = p + move;
                }
            }
        }

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (docked)
            {
                // the ears must meet the screen edge exactly: straight segments
                c.BeginFigure(pts[0], true, true);
                c.PolyLineTo(pts.Skip(1).ToList(), true, true);
            }
            else
            {
                // a smooth closed curve through the ring: each point is a control point, the curve passes
                // through the midpoints between neighbours (straight where the points are in line)
                static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                c.BeginFigure(Mid(pts[count - 1], pts[0]), true, true);
                for (int i = 0; i < count; i++)
                    c.QuadraticBezierTo(pts[i], Mid(pts[i], pts[(i + 1) % count]), true, true);
            }
        }
        return g;
    }

    private Geometry LiquidGeometry(double W, double H, double R)
    {
        var pts = new List<Point>(260);
        double L = 2 * (W - 2 * R) + 2 * (H - 2 * R) + 2 * Math.PI * R;
        int waves = Math.Max(3, (int)Math.Round(L / 150));
        bool playing = _lastMedia?.IsPlaying == true;
        double drip = (playing ? 1.2 + _audioLevel * 9 : _audioLevel * 9) * LiquidK;
        double s = 0;

        void Add(double x, double y, double nx, double ny, double ds, double bottomT = -1)
        {
            s += ds;
            double weight = 0.3 + 0.7 * (y / H); // it hangs from the top, the bottom moves most
            double d = _ripple * weight * Math.Sin(2 * Math.PI * waves * s / L - _ripplePhase);
            if (bottomT >= 0 && drip > 0)
            {
                double env = Math.Sin(Math.PI * bottomT);
                d += Math.Max(0, drip * env * (0.55 + 0.45 * Math.Sin(bottomT * 9 + _dripPhase) * Math.Cos(bottomT * 4 - _dripPhase * 0.7)));
            }
            pts.Add(new Point(x + nx * d, y + ny * d));
        }

        void Arc(double cx, double cy, double fromDeg, double toDeg)
        {
            int n = Math.Max(4, (int)(R * Math.PI / 2 / 4));
            double step = (toDeg - fromDeg) / n * Math.PI / 180;
            for (int i = 1; i <= n; i++)
            {
                double a = fromDeg * Math.PI / 180 + step * i;
                double nx = Math.Cos(a), ny = Math.Sin(a);
                Add(cx + R * nx, cy + R * ny, nx, ny, R * Math.Abs(step));
            }
        }

        const double seg = 5;
        pts.Add(new Point(R, 0));
        for (double x = R + seg; x < W - R; x += seg) Add(x, 0, 0, -1, seg);               // top
        Arc(W - R, R, -90, 0);                                                            // top-right
        for (double y = R + seg; y < H - R; y += seg) Add(W, y, 1, 0, seg);               // right
        Arc(W - R, H - R, 0, 90);                                                         // bottom-right
        double bw = Math.Max(1, W - 2 * R);
        for (double x = W - R - seg; x > R; x -= seg) Add(x, H, 0, 1, seg, (W - R - x) / bw); // bottom (drips)
        Arc(R, H - R, 90, 180);                                                           // bottom-left
        for (double y = H - R - seg; y > R; y -= seg) Add(0, y, -1, 0, seg);              // left
        Arc(R, R, 180, 270);                                                              // top-left

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true, true);
            ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        return g;
    }

    // ---------------------------------------------------------------- click ripple

    private void SpawnRipple(Point p)
    {
        if (!Liquid || RippleLayer.ActualWidth < 1) return;
        double max = Math.Max(Island.ActualWidth, Island.ActualHeight) * 0.85;
        var tint = _settings.DarkTheme ? Colors.White : Colors.Black;
        var fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, tint.R, tint.G, tint.B), 0.0),
                new GradientStop(Color.FromArgb(0x16, tint.R, tint.G, tint.B), 0.72),
                new GradientStop(Color.FromArgb(0x30, tint.R, tint.G, tint.B), 0.93),
                new GradientStop(Color.FromArgb(0x00, tint.R, tint.G, tint.B), 1.0),
            },
        };
        var scale = new ScaleTransform(0.04, 0.04);
        var e = new Ellipse
        {
            Width = max * 2, Height = max * 2, Fill = fill, IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale,
        };
        System.Windows.Controls.Canvas.SetLeft(e, p.X - max);
        System.Windows.Controls.Canvas.SetTop(e, p.Y - max);
        RippleLayer.Children.Add(e);

        var dur = TimeSpan.FromMilliseconds(700);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.04, 1, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.04, 1, dur) { EasingFunction = ease });
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(560)) { BeginTime = TimeSpan.FromMilliseconds(160) };
        fade.Completed += (_, _) => RippleLayer.Children.Remove(e);
        e.BeginAnimation(OpacityProperty, fade);
    }

    // ---------------------------------------------------------------- iOS-like transitions

    /// <summary>Appear: spring scale-up from <paramref name="fromScale"/>, optional slide, un-blur, fade in.</summary>
    private void Reveal(FrameworkElement el, double fromScale = 0.94, double slideX = 0, int delayMs = 0)
    {
        el.Visibility = Visibility.Visible;
        var st = new ScaleTransform(fromScale, fromScale);
        var tt = new TranslateTransform(slideX, 0);
        el.RenderTransformOrigin = new Point(0.5, 0);
        el.RenderTransform = new TransformGroup { Children = { st, tt } };

        var begin = TimeSpan.FromMilliseconds(delayMs);
        bool hop = _settings.Bouncy;
        var spring = new SpringEase { DampingRatio = Liquid ? 0.68 : hop ? 0.7 : 0.86 };
        var move = TimeSpan.FromMilliseconds((Liquid ? 520 : hop ? 500 : 420) * (_settings.FastAnimations ? 0.65 : 1));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, move) { BeginTime = begin, EasingFunction = spring });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, move) { BeginTime = begin, EasingFunction = spring });
        if (slideX != 0)
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(slideX, 0, move) { BeginTime = begin, EasingFunction = spring });
        if (hop)  // a small jump: drops in from a few pixels above and settles on a spring
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, 0, move) { BeginTime = begin, EasingFunction = spring });

        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
        {
            BeginTime = begin,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        // content comes into focus: soft first, sharp only at rest
        var blur = new BlurEffect { Radius = 18, RenderingBias = RenderingBias.Performance };
        el.Effect = blur;
        var unblur = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(380))
        {
            BeginTime = begin,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        unblur.Completed += (_, _) => { if (el.Effect == blur) el.Effect = null; };
        blur.BeginAnimation(BlurEffect.RadiusProperty, unblur);
    }

    /// <summary>Disappear: quick fade with a little blur and shrink.</summary>
    private void Conceal(FrameworkElement el, Action? done = null)
    {
        var dur = TimeSpan.FromMilliseconds(160);
        var fade = new DoubleAnimation(0, dur);
        if (done != null) fade.Completed += (_, _) => done();
        el.BeginAnimation(OpacityProperty, fade);

        if (el.RenderTransform is TransformGroup { Children.Count: > 0 } group && group.Children[0] is ScaleTransform st && !st.IsFrozen)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, dur));
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, dur));
        }
        var blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        el.Effect = blur;
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, 14, dur));
    }
}
