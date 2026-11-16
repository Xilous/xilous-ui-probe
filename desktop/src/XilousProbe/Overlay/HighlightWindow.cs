using System.Windows.Media;
using System.Windows.Shapes;
using XilousProbe.Uia;

namespace XilousProbe.Overlay;

public enum HighlightStyle { Hover, Selected, MeasureTarget }

/// <summary>The outline drawn over an element: dashed while hovering, solid once selected.</summary>
public sealed class HighlightWindow : OverlayWindow
{
    public static readonly Color Accent = Color.FromRgb(0xD9, 0x77, 0x57);
    public static readonly Color MeasureColor = Color.FromRgb(0x3B, 0x82, 0xF6);

    private readonly Rectangle _rect = new() { StrokeThickness = 1.5, SnapsToDevicePixels = true };

    public HighlightWindow() : base(clickThrough: true)
    {
        Content = _rect;
        SetStyle(HighlightStyle.Hover);
    }

    public void SetStyle(HighlightStyle style)
    {
        var color = style == HighlightStyle.MeasureTarget ? MeasureColor : Accent;
        _rect.Stroke = new SolidColorBrush(color);
        _rect.StrokeThickness = style == HighlightStyle.Selected ? 2 : 1.5;
        _rect.StrokeDashArray = style == HighlightStyle.Selected ? null : new DoubleCollection { 4, 3 };
        _rect.Fill = new SolidColorBrush(Color.FromArgb(style == HighlightStyle.Selected ? (byte)0x22 : (byte)0x14, color.R, color.G, color.B));
    }

    /// <summary>Outlines a rectangle, grown by a pixel so the stroke sits just outside the element.</summary>
    public void Show(PxRect r, HighlightStyle style)
    {
        SetStyle(style);
        PlacePx(new PxRect(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2));
    }
}
