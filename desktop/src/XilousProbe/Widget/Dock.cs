namespace XilousProbe.Widget;

// Edge docking for the floating widget: a port of the Xilous Usage Monitor's
// src/dock.rs, so the two widgets move the same way. The same tuning, springs and
// rules: dragged, it follows the cursor; near an outer edge a magnet leans it in,
// inside the catch distance it snaps on and slides along that edge; let go, it
// carries the throw onto the nearest outer edge. Hovered, it expands away from
// its edge.
//
// Pure geometry and springs, no WPF or Win32: WidgetWindow feeds it the displays,
// the cursor and the clock, and draws whatever frame it returns. Coordinates are
// physical screen pixels; distances are tuned in 96-DPI pixels and scaled by `unit`.

/// <summary>A rectangle in screen pixels, half-open like a Win32 RECT.</summary>
public readonly record struct DRect(double L, double T, double R, double B)
{
    public double DistanceTo(double x, double y)
    {
        double dx = x - Math.Clamp(x, L, R), dy = y - Math.Clamp(y, T, B);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>A monitor: its full bounds, and its work area (the bounds less the taskbar).</summary>
public readonly record struct Display(DRect Bounds, DRect Work);

public enum Side { Left, Right, Top, Bottom }

/// <summary>A stretch of outer edge the widget may sit on: Fixed is the edge's coordinate across it, From..To its extent along it.</summary>
public readonly record struct Segment(Side Side, double Fixed, double From, double To, int Monitor);

/// <summary>Where a collapsed widget would sit on an edge, and how far the widget being placed is from it.</summary>
public readonly record struct Spot(double X, double Y, Side Side, int Monitor, double Gap);

public static class Dock
{
    // Magnet and throw tuning, in base pixels (96 DPI) and seconds: dock.rs's numbers.
    public const double MagnetRange = 160, CatchDistance = 32, ReleaseHysteresis = 1.5;
    public const double ThrowReach = 0.22, MaxThrowSpeed = 6000, DragThreshold = 4;
    public const double HoverLinger = 0.3, LiftGrowth = 0.06, ThrowWindow = 0.09, ThrowStale = 0.06;
    /// <summary>The collapsed widget's side, in base pixels.</summary>
    public const double CollapsedSize = 52;

    public static bool Vertical(Side s) => s is Side.Left or Side.Right;

    public static (double X, double Y) Normal(Side s) => s switch
    {
        Side.Left => (-1, 0),
        Side.Right => (1, 0),
        Side.Top => (0, -1),
        _ => (0, 1),
    };

    /// <summary>
    /// The outer edges of the desktop: every monitor edge minus wherever it touches another
    /// monitor, so the widget never docks in the middle of a multi-monitor desktop; each then
    /// pulled in to the monitor's work area, so it never sits under the taskbar.
    /// </summary>
    public static List<Segment> EdgeSegments(IReadOnlyList<Display> displays)
    {
        static List<(double, double)> Subtract(List<(double From, double To)> ranges, double a, double b)
        {
            var output = new List<(double, double)>(ranges.Count + 1);
            foreach (var (from, to) in ranges)
            {
                if (b <= from || a >= to) { output.Add((from, to)); continue; }
                if (a > from) output.Add((from, a));
                if (b < to) output.Add((b, to));
            }
            return output;
        }

        var segments = new List<Segment>();
        for (int i = 0; i < displays.Count; i++)
        {
            var m = displays[i].Bounds;
            var w = displays[i].Work;
            var sides = new (Side Side, double Fixed, double From, double To)[]
            {
                (Side.Left, m.L, m.T, m.B), (Side.Right, m.R, m.T, m.B),
                (Side.Top, m.T, m.L, m.R), (Side.Bottom, m.B, m.L, m.R),
            };
            foreach (var (side, fixedAt, from, to) in sides)
            {
                var ranges = new List<(double, double)> { (from, to) };
                for (int j = 0; j < displays.Count; j++)
                {
                    if (j == i) continue;
                    var n = displays[j].Bounds;
                    var (touches, a, b) = side switch
                    {
                        Side.Left => (n.R == m.L, n.T, n.B),
                        Side.Right => (n.L == m.R, n.T, n.B),
                        Side.Top => (n.B == m.T, n.L, n.R),
                        _ => (n.T == m.B, n.L, n.R),
                    };
                    if (touches) ranges = Subtract(ranges, a, b);
                }
                // Into the work area: the edge moves in past a taskbar on that side, and the
                // extent along it stops where a taskbar on a neighbouring side begins.
                var (workFixed, lo, hi) = side switch
                {
                    Side.Left => (w.L, w.T, w.B),
                    Side.Right => (w.R, w.T, w.B),
                    Side.Top => (w.T, w.L, w.R),
                    _ => (w.B, w.L, w.R),
                };
                foreach (var (a, b) in ranges)
                {
                    double f = Math.Max(a, lo), t = Math.Min(b, hi);
                    if (t > f) segments.Add(new Segment(side, workFixed, f, t, i));
                }
            }
        }
        return segments;
    }

    /// <summary>The docked spot nearest to a collapsed widget of the given size centred at (x, y).</summary>
    public static Spot? NearestSpot(double x, double y, IReadOnlyList<Segment> segments, double size)
    {
        Spot? best = null;
        double bestD = double.PositiveInfinity;
        double w = size, h = size;
        foreach (var s in segments)
        {
            double along = Vertical(s.Side) ? h : w;
            if (s.To - s.From < along) continue;
            double left, top, gap;
            switch (s.Side)
            {
                case Side.Left:
                    left = s.Fixed; top = Math.Clamp(y - h / 2, s.From, s.To - h); gap = (x - w / 2) - left;
                    break;
                case Side.Right:
                    left = s.Fixed - w; top = Math.Clamp(y - h / 2, s.From, s.To - h); gap = left - (x - w / 2);
                    break;
                case Side.Top:
                    left = Math.Clamp(x - w / 2, s.From, s.To - w); top = s.Fixed; gap = (y - h / 2) - top;
                    break;
                default:
                    left = Math.Clamp(x - w / 2, s.From, s.To - w); top = s.Fixed - h; gap = top - (y - h / 2);
                    break;
            }
            double cx = left + w / 2, cy = top + h / 2;
            double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            if (d < bestD)
            {
                bestD = d;
                best = new Spot(cx, cy, s.Side, s.Monitor, Math.Max(0, gap));
            }
        }
        return best;
    }

    public static int NearestMonitor(double x, double y, IReadOnlyList<Display> displays)
    {
        int best = 0;
        double bestD = double.PositiveInfinity;
        for (int i = 0; i < displays.Count; i++)
        {
            double d = displays[i].Work.DistanceTo(x, y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>A free widget's centre, kept where the whole collapsed widget stays on the nearest monitor.</summary>
    public static (double X, double Y) KeepOnMonitor(double x, double y, double size, IReadOnlyList<Display> displays)
    {
        var m = displays[NearestMonitor(x, y, displays)].Work;
        return (Fit(x, m.L + size / 2, m.R - size / 2), Fit(y, m.T + size / 2, m.B - size / 2));
    }

    /// <summary>Clamp that prefers the low bound when the range is empty.</summary>
    public static double Fit(double v, double lo, double hi) => hi < lo ? lo : Math.Clamp(v, lo, hi);

    /// <summary>Cursor velocity over the last stretch of a drag, in pixels per second; zero when it had stopped before the release.</summary>
    public static (double X, double Y) ThrowVelocity(IReadOnlyList<(double T, double X, double Y)> samples, double now, double max)
    {
        if (samples.Count == 0) return (0, 0);
        var last = samples[^1];
        if (now - last.T > ThrowStale) return (0, 0);
        var first = last;
        foreach (var s in samples)
            if (last.T - s.T <= ThrowWindow) { first = s; break; }
        double dt = Math.Max(last.T - first.T, 0.008);
        return (Math.Clamp((last.X - first.X) / dt, -max, max), Math.Clamp((last.Y - first.Y) / dt, -max, max));
    }
}

/// <summary>Spring constants from a perceived duration and a bounce (Motion's visualDuration/bounce mapping).</summary>
public readonly record struct SpringCfg(double Stiffness, double Damping)
{
    public static SpringCfg Of(double duration, double bounce)
    {
        double root = 2 * Math.PI / (duration * 1.2);
        double b = Math.Clamp(bounce, 0, 0.95);
        return new SpringCfg(root * root, 2 * (1 - b) * root);
    }

    public static readonly SpringCfg Follow = Of(0.1, 0);
    public static readonly SpringCfg Slide = Of(0.12, 0);
    public static readonly SpringCfg Snap = Of(0.45, 0.3);
    public static readonly SpringCfg Lift = Of(0.25, 0.35);
    public static readonly SpringCfg HoverIn = Of(0.34, 0.15);
    public static readonly SpringCfg HoverOut = Of(0.28, 0);
}

public sealed class Spring(double x = 0)
{
    public double X = x, V;

    /// <summary>Advance by dt seconds toward target, in small fixed substeps so a long frame cannot destabilise a stiff spring.</summary>
    public void Step(double target, SpringCfg cfg, double dt)
    {
        double left = dt;
        while (left > 0)
        {
            double h = Math.Min(left, 0.004);
            double accel = -cfg.Stiffness * (X - target) - cfg.Damping * V;
            V += accel * h;
            X += V * h;
            left -= h;
        }
    }

    public bool Settled(double target, double eps) => Math.Abs(X - target) < eps && Math.Abs(V) < eps * 10;
}

/// <summary>One frame of the widget: where its panel goes and how far each motion has gone.</summary>
/// <remarks>CenterX/CenterY is the collapsed widget's centre: it moves only when the widget does, never while it expands.</remarks>
public readonly record struct DockFrame(
    double Left, double Top, double Width, double Height,
    double Collapsed, Side Side, bool Docked, double Expanded, double Lift, bool Moving,
    double CenterX, double CenterY);

public sealed class DockMotion
{
    private sealed class Press
    {
        public (double X, double Y) Start, Grab, At;
        public bool Active, Caught;
        public (double X, double Y)? Free;
        public readonly List<(double T, double X, double Y)> Samples = [];
    }

    private Spring _x, _y;
    private readonly Spring _expand = new(), _lift = new();
    private bool _placed;
    private Press? _press;
    private bool _hovered;
    private double _leftAt = double.NegativeInfinity;

    /// <summary>The point the widget docks nearest to; what is saved across restarts.</summary>
    public (double X, double Y) Anchor { get; private set; }
    public Side Side { get; private set; } = Side.Right;
    public int Monitor { get; private set; }
    /// <summary>Something outside the motion wants the widget open (a focused text box, an open menu).</summary>
    public bool HoldOpen { get; set; }

    public DockMotion((double X, double Y) anchor)
    {
        Anchor = anchor;
        _x = new Spring(anchor.X);
        _y = new Spring(anchor.Y);
    }

    public bool Dragging => _press is { Active: true };
    public bool Pressed => _press is not null;

    /// <summary>Glide to the edge nearest a new anchor.</summary>
    public void SetAnchor((double X, double Y) anchor)
    {
        Anchor = anchor;
        _press = null;
    }

    /// <summary>The button went down on the widget. size is the collapsed size in screen pixels.</summary>
    public void PressAt(double x, double y, double now, double size)
    {
        // A grab on the expanded panel is pulled in over the collapsed widget, which is what the drag carries.
        var grab = (Math.Clamp(x - _x.X, -size / 2, size / 2), Math.Clamp(y - _y.X, -size / 2, size / 2));
        _press = new Press { Start = (x, y), Grab = grab, At = (x - grab.Item1, y - grab.Item2) };
        _press.Samples.Add((now, x, y));
    }

    /// <summary>The cursor moved with the button down. unit is screen pixels per 96-DPI pixel.</summary>
    public void DragTo(double x, double y, double now, double unit)
    {
        if (_press is not { } p) return;
        p.At = (x - p.Grab.X, y - p.Grab.Y);
        double moved = Math.Sqrt((x - p.Start.X) * (x - p.Start.X) + (y - p.Start.Y) * (y - p.Start.Y));
        if (!p.Active && moved > Dock.DragThreshold * unit) p.Active = true;
        p.Samples.Add((now, x, y));
        if (p.Samples.Count > 16) p.Samples.RemoveAt(0);
    }

    /// <summary>The button came up. True when it ended a drag, in which case the widget is thrown onto the nearest edge.</summary>
    public bool Release(double now, double unit)
    {
        var p = _press;
        _press = null;
        if (p is null || !p.Active) return false;
        var v = Dock.ThrowVelocity(p.Samples, now, Dock.MaxThrowSpeed * unit);
        var from = p.Caught ? (_x.X, _y.X) : p.Free ?? p.At;
        Anchor = (from.Item1 + v.X * Dock.ThrowReach, from.Item2 + v.Y * Dock.ThrowReach);
        // The springs set off at the throw's speed, so the glide continues the drag.
        _x.V = v.X;
        _y.V = v.Y;
        return true;
    }

    public void CancelPress() => _press = null;

    public void SetHovered(bool hovered, double now)
    {
        if (_hovered && !hovered) _leftAt = now;
        _hovered = hovered;
    }

    /// <summary>After a drop the cursor is usually still over the widget; it settles collapsed until hovered afresh.</summary>
    public void ForgetHover()
    {
        _hovered = false;
        _leftAt = double.NegativeInfinity;
    }

    /// <summary>
    /// Advance by dt seconds and lay out the frame. unit is screen pixels per 96-DPI pixel;
    /// expandedW/H the expanded panel's size in screen pixels. Null without displays.
    /// </summary>
    public DockFrame? Tick(double now, double dt, IReadOnlyList<Display> displays, double unit, double expandedW, double expandedH)
    {
        if (displays.Count == 0) return null;
        var segments = Dock.EdgeSegments(displays);
        if (segments.Count == 0) return null;
        dt = Math.Clamp(dt, 0, 0.064);
        double size = Dock.CollapsedSize * unit;

        (double X, double Y) target;
        SpringCfg cfgX, cfgY;
        bool docked;
        bool dragging = Dragging;
        if (_press is { Active: true } p)
        {
            var free = Dock.KeepOnMonitor(p.At.X, p.At.Y, size, displays);
            var spot = Dock.NearestSpot(free.X, free.Y, segments, size);
            double catchAt = Dock.CatchDistance * unit * (p.Caught ? Dock.ReleaseHysteresis : 1);
            if (spot is { } s && s.Gap <= catchAt)
            {
                p.Caught = true;
                target = (s.X, s.Y);
                // Snapped on across the edge, sliding freely along it.
                (cfgX, cfgY) = Dock.Vertical(s.Side) ? (SpringCfg.Snap, SpringCfg.Slide) : (SpringCfg.Slide, SpringCfg.Snap);
                Side = s.Side;
                Monitor = s.Monitor;
                docked = true;
            }
            else
            {
                p.Caught = false;
                var to = free;
                // Inside the range the magnet leans it toward the edge, more the closer it gets,
                // reaching the edge exactly at the catch distance so the catch never jumps.
                if (spot is { } near)
                {
                    double range = Dock.MagnetRange * unit, catchD = Dock.CatchDistance * unit;
                    if (near.Gap < range)
                    {
                        double t = Math.Clamp(1 - (near.Gap - catchD) / Math.Max(range - catchD, 1), 0, 1);
                        double pull = t * t * (3 - 2 * t) * near.Gap;
                        var n = Dock.Normal(near.Side);
                        to = (to.X + n.X * pull, to.Y + n.Y * pull);
                    }
                }
                p.Free = to;
                target = to;
                cfgX = cfgY = SpringCfg.Follow;
                Monitor = Dock.NearestMonitor(to.X, to.Y, displays);
                docked = false;
            }
        }
        else
        {
            var spot = Dock.NearestSpot(Anchor.X, Anchor.Y, segments, size);
            if (spot is not { } s) return null;
            target = (s.X, s.Y);
            cfgX = cfgY = SpringCfg.Snap;
            Side = s.Side;
            Monitor = s.Monitor;
            Anchor = target;
            docked = true;
        }

        if (!_placed)
        {
            _x = new Spring(target.X);
            _y = new Spring(target.Y);
            _placed = true;
        }
        _x.Step(target.X, cfgX, dt);
        _y.Step(target.Y, cfgY, dt);
        double liftTo = dragging ? 1 : 0;
        _lift.Step(liftTo, SpringCfg.Lift, dt);

        // It opens at once and closes a beat after the cursor leaves; never during a drag.
        bool wantOpen = !dragging && (_hovered || HoldOpen || now - _leftAt < Dock.HoverLinger);
        double openTo = wantOpen ? 1 : 0;
        _expand.Step(openTo, wantOpen ? SpringCfg.HoverIn : SpringCfg.HoverOut, dt);

        double e = Math.Clamp(_expand.X, 0, 1.04);
        double c = size * (1 + Dock.LiftGrowth * _lift.X);
        double w = c + (expandedW - c) * e, h = c + (expandedH - c) * e;

        // The expansion grows away from the edge the widget sits on.
        (double, double) Grow(double cx, double cy)
        {
            double left = cx - c / 2, top = cy - c / 2;
            if (docked && Side == Side.Right) left += c - w;
            if (docked && Side == Side.Bottom) top += c - h;
            return (left, top);
        }
        var (fl, ft) = Grow(_x.X, _y.X);
        if (!dragging)
        {
            // Keep the expansion on its monitor, worked out at the resting spot so a spring's
            // overshoot is never clipped flat against the screen edge.
            var (rl, rt) = Grow(target.X, target.Y);
            var m = displays[Math.Min(Monitor, displays.Count - 1)].Work;
            fl += Dock.Fit(rl, m.L, m.R - w) - rl;
            ft += Dock.Fit(rt, m.T, m.B - h) - rt;
        }

        bool settled = _x.Settled(target.X, 0.3) && _y.Settled(target.Y, 0.3)
                       && _lift.Settled(liftTo, 0.002) && _expand.Settled(openTo, 0.002);
        bool lingering = !_hovered && now - _leftAt < Dock.HoverLinger;
        return new DockFrame(fl, ft, w, h, c, Side, docked, e, _lift.X, dragging || !settled || lingering, _x.X, _y.X);
    }
}
