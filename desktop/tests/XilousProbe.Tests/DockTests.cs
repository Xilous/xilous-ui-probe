using XilousProbe.Widget;

namespace XilousProbe.Tests;

public class DockTests
{
    private static Display Screen(double l, double t, double r, double b) => new(new DRect(l, t, r, b), new DRect(l, t, r, b));

    private static readonly Display One = Screen(0, 0, 1920, 1080);
    // Two monitors side by side, the right one shorter and lower.
    private static readonly Display Left = Screen(0, 0, 1920, 1080);
    private static readonly Display Right = Screen(1920, 100, 3200, 900);

    [Fact]
    public void A_single_monitor_has_its_four_edges()
    {
        var segs = Dock.EdgeSegments([One]);
        Assert.Equal(4, segs.Count);
        Assert.Contains(segs, s => s.Side == Side.Right && s.Fixed == 1920 && s.From == 0 && s.To == 1080);
    }

    [Fact]
    public void A_shared_edge_is_not_an_outer_edge_but_its_exposed_parts_are()
    {
        var segs = Dock.EdgeSegments([Left, Right]);
        // The left monitor's right edge is covered by the right monitor from 100 to 900...
        var exposed = segs.Where(s => s.Monitor == 0 && s.Side == Side.Right).Select(s => (s.From, s.To)).ToList();
        Assert.Equal([(0.0, 100.0), (900.0, 1080.0)], exposed);
        // ...and the right monitor's left edge is covered entirely.
        Assert.DoesNotContain(segs, s => s.Monitor == 1 && s.Side == Side.Left);
    }

    [Fact]
    public void Edges_stop_at_the_taskbar()
    {
        var withTaskbar = new Display(new DRect(0, 0, 1920, 1080), new DRect(0, 0, 1920, 1032));
        var segs = Dock.EdgeSegments([withTaskbar]);
        Assert.Contains(segs, s => s.Side == Side.Bottom && s.Fixed == 1032);
        Assert.Contains(segs, s => s.Side == Side.Right && s.To == 1032);
    }

    [Fact]
    public void The_nearest_spot_sits_flush_on_the_closest_edge()
    {
        var segs = Dock.EdgeSegments([One]);
        var spot = Dock.NearestSpot(1800, 400, segs, 52);
        Assert.NotNull(spot);
        Assert.Equal(Side.Right, spot.Value.Side);
        Assert.Equal(1920 - 26, spot.Value.X);
        Assert.Equal(400, spot.Value.Y);
        Assert.Equal(1920 - 52 - (1800 - 26), spot.Value.Gap);
    }

    [Fact]
    public void A_spot_slides_along_the_edge_but_never_off_its_end()
    {
        var segs = Dock.EdgeSegments([One]);
        var spot = Dock.NearestSpot(1900, -500, segs, 52)!.Value;
        Assert.Equal(26, spot.Y); // clamped to the top end of the right edge... or the top edge's corner
    }

    [Fact]
    public void A_throw_carries_along_its_velocity()
    {
        var samples = new List<(double, double, double)> { (0.00, 100, 100), (0.05, 150, 100), (0.08, 180, 100) };
        var v = Dock.ThrowVelocity(samples, 0.09, 6000);
        Assert.True(v.X > 900, $"{v.X}");
        Assert.Equal(0, v.Y);
        Assert.Equal((0.0, 0.0), Dock.ThrowVelocity(samples, 0.5, 6000)); // stopped before release
    }

    private static DockFrame Run(DockMotion m, IReadOnlyList<Display> displays, double seconds, double start = 0)
    {
        DockFrame f = default;
        for (double t = start; t < start + seconds; t += 0.01)
            f = m.Tick(t, 0.01, displays, 1, 340, 200)!.Value;
        return f;
    }

    [Fact]
    public void Starting_from_an_anchor_it_rests_docked_on_that_edge()
    {
        var m = new DockMotion((1920, 360));
        var f = Run(m, [One], 1);
        Assert.True(f.Docked);
        Assert.Equal(Side.Right, f.Side);
        Assert.Equal(1920 - 52, f.Left, 1);
        Assert.False(f.Moving);
    }

    [Fact]
    public void Hovering_expands_it_away_from_the_right_edge_then_it_collapses_after_the_linger()
    {
        var m = new DockMotion((1920, 360));
        Run(m, [One], 1);
        m.SetHovered(true, 1);
        var open = Run(m, [One], 1, 1);
        Assert.Equal(340, open.Width, 0);
        Assert.Equal(1920 - 340, open.Left, 0); // grew leftward, still flush with the edge
        m.SetHovered(false, 2);
        var closed = Run(m, [One], 1.5, 2);
        Assert.Equal(52, closed.Width, 0);
    }

    [Fact]
    public void Dragged_near_another_edge_it_snaps_on_and_stays_after_release()
    {
        var m = new DockMotion((1920, 360));
        Run(m, [One], 1);
        m.PressAt(1894, 360, 1, 52);
        // Drag across the screen to just inside the top edge.
        double t = 1;
        for (int i = 1; i <= 40; i++)
        {
            t += 0.01;
            m.DragTo(1894 - i * 20, 360 - i * 8.5, t, 1);
            m.Tick(t, 0.01, [One], 1, 340, 200);
        }
        Assert.True(m.Dragging);
        var during = m.Tick(t, 0.01, [One], 1, 340, 200)!.Value;
        Assert.True(during.Docked, "within the catch distance of the top edge it is caught");
        Assert.Equal(Side.Top, during.Side);
        Assert.True(m.Release(t + 0.01, 1));
        var rest = Run(m, [One], 2, t + 0.02);
        Assert.Equal(Side.Top, rest.Side);
        Assert.Equal(0, rest.Top, 0);
        Assert.False(rest.Moving);
    }

    [Fact]
    public void A_click_without_movement_is_not_a_drag()
    {
        var m = new DockMotion((1920, 360));
        Run(m, [One], 1);
        m.PressAt(1894, 360, 1, 52);
        m.DragTo(1896, 361, 1.01, 1);
        Assert.False(m.Release(1.02, 1));
    }

    [Fact]
    public void Held_open_by_a_focused_text_box_it_stays_expanded()
    {
        var m = new DockMotion((1920, 360));
        Run(m, [One], 1);
        m.HoldOpen = true;
        var f = Run(m, [One], 2, 1);
        Assert.Equal(340, f.Width, 0);
    }

    [Fact]
    public void On_the_bottom_edge_it_expands_upward()
    {
        var m = new DockMotion((900, 1080));
        Run(m, [One], 1);
        m.SetHovered(true, 1);
        var f = Run(m, [One], 1, 1);
        Assert.Equal(Side.Bottom, f.Side);
        Assert.Equal(1080 - 200, f.Top, 0);
    }
}
