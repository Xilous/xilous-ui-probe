using XilousProbe.Uia;

namespace XilousProbe.Overlay;

/// <summary>A measured distance: a line between two points, labelled with its length in pixels.</summary>
public readonly record struct Measure(int X1, int Y1, int X2, int Y2)
{
    public int Length => Math.Abs(X2 - X1) + Math.Abs(Y2 - Y1);
    public bool IsHorizontal => Y1 == Y2;
}

/// <summary>
/// Distances between the selection and another element, the way design tools show
/// them: from inner edges to outer edges when one contains the other, and across
/// every gap between them otherwise.
/// </summary>
public static class Redline
{
    public static IReadOnlyList<Measure> Between(PxRect a, PxRect b)
    {
        var result = new List<Measure>();
        if (a.IsEmpty || b.IsEmpty) return result;

        if (Contains(b, a)) return Inset(inner: a, outer: b);
        if (Contains(a, b)) return Inset(inner: b, outer: a);

        // Horizontal gap, drawn at the middle of where the two overlap vertically
        // (or at the selection's middle when they don't overlap at all).
        int midY = MidOverlap(a.Y, a.Bottom, b.Y, b.Bottom);
        if (b.X >= a.Right) result.Add(new Measure(a.Right, midY, b.X, midY));
        else if (a.X >= b.Right) result.Add(new Measure(b.Right, midY, a.X, midY));

        int midX = MidOverlap(a.X, a.Right, b.X, b.Right);
        if (b.Y >= a.Bottom) result.Add(new Measure(midX, a.Bottom, midX, b.Y));
        else if (a.Y >= b.Bottom) result.Add(new Measure(midX, b.Bottom, midX, a.Y));

        return result.Where(m => m.Length > 0).ToList();
    }

    private static bool Contains(PxRect outer, PxRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static List<Measure> Inset(PxRect inner, PxRect outer)
    {
        int cx = inner.X + inner.Width / 2, cy = inner.Y + inner.Height / 2;
        return new List<Measure>
        {
            new(outer.X, cy, inner.X, cy),
            new(inner.Right, cy, outer.Right, cy),
            new(cx, outer.Y, cx, inner.Y),
            new(cx, inner.Bottom, cx, outer.Bottom),
        }.Where(m => m.Length > 0).ToList();
    }

    private static int MidOverlap(int a0, int a1, int b0, int b1)
    {
        int lo = Math.Max(a0, b0), hi = Math.Min(a1, b1);
        return lo < hi ? (lo + hi) / 2 : (a0 + a1) / 2;
    }
}
