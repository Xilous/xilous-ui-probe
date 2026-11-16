using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using XilousProbe.Uia;

namespace XilousProbe.Overlay;

/// <summary>Draws the measured distances between the selection and the element under the cursor.</summary>
public sealed class RedlineWindow : OverlayWindow
{
    private const int Pad = 40;
    private readonly Canvas _canvas = new();

    public RedlineWindow() : base(clickThrough: true)
    {
        Content = _canvas;
    }

    public void Show(PxRect a, PxRect b)
    {
        var measures = Redline.Between(a, b);
        var area = PxRect.Union(a, b);
        area = new PxRect(area.X - Pad, area.Y - Pad, area.Width + Pad * 2, area.Height + Pad * 2);
        PlacePx(area);
        double s = Scale;
        _canvas.Children.Clear();
        var palette = Theme.XilousTheme.Current;
        var LineBrush = Theme.Palette.Brush(palette.Measure);
        var labelInk = Theme.Palette.Brush(palette.OnMeasure);

        foreach (var m in measures)
        {
            double x1 = (m.X1 - area.X) / s, y1 = (m.Y1 - area.Y) / s, x2 = (m.X2 - area.X) / s, y2 = (m.Y2 - area.Y) / s;
            _canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = LineBrush, StrokeThickness = 1 });
            // End ticks
            foreach (var (px, py) in new[] { (x1, y1), (x2, y2) })
            {
                _canvas.Children.Add(m.IsHorizontal
                    ? new Line { X1 = px, X2 = px, Y1 = py - 4, Y2 = py + 4, Stroke = LineBrush, StrokeThickness = 1 }
                    : new Line { Y1 = py, Y2 = py, X1 = px - 4, X2 = px + 4, Stroke = LineBrush, StrokeThickness = 1 });
            }
            var pill = new Border
            {
                Background = LineBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Child = new TextBlock { Text = m.Length.ToString(), Foreground = labelInk, FontSize = 11, FontWeight = FontWeights.SemiBold },
            };
            pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double cx = (x1 + x2) / 2, cy = (y1 + y2) / 2;
            Canvas.SetLeft(pill, m.IsHorizontal ? cx - pill.DesiredSize.Width / 2 : cx + 6);
            Canvas.SetTop(pill, m.IsHorizontal ? cy - pill.DesiredSize.Height - 4 : cy - pill.DesiredSize.Height / 2);
            _canvas.Children.Add(pill);
        }
    }
}
