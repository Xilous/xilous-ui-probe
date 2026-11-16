using System.Windows.Media;
using System.Windows.Shapes;
using XilousProbe.Uia;

namespace XilousProbe.Overlay;

public enum HighlightStyle { Hover, Selected, MeasureTarget }

/// <summary>The outline drawn over an element: dashed while hovering, solid once selected.</summary>
public sealed class HighlightWindow : OverlayWindow
{
    /// <summary>The selection colour: the Xilous theme's accent.</summary>
    public static Color Accent => Theme.XilousTheme.Current.Accent;
    /// <summary>The measuring colour, kept apart from the selection's.</summary>
    public static Color MeasureColor => Theme.XilousTheme.Current.Measure;

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
