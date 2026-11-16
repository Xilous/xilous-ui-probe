using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XilousProbe.Uia;

namespace XilousProbe.Overlay;

/// <summary>
/// The label beside the element: what it is while hovering, and the toolbar
/// (Copy, Screenshot, Parent) once it is selected.
/// </summary>
public sealed class InfoWindow : OverlayWindow
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private readonly Border _card;
    private readonly List<Button> _buttons = new();
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
    private readonly TextBlock _detail = new() { FontFamily = Mono, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _source = new() { FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _hint = new() { FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Text = "Alt  measure   ·   Esc  back" };
    private readonly TextBlock _toast = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _parentButton;
    private CancellationTokenSource? _toastCts;

    public event Action? CopyClicked;
    public event Action? ScreenshotClicked;
    public event Action? ParentClicked;

    public InfoWindow() : base(clickThrough: true)
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _toolbar.Children.Add(MakeButton("Copy", "Copy a pointer for your coding agent", () => CopyClicked?.Invoke()));
        _toolbar.Children.Add(MakeButton("Screenshot", "Copy a PNG of the element", () => ScreenshotClicked?.Invoke()));
        _parentButton = MakeButton("Parent", "Select the element that contains this one", () => ParentClicked?.Invoke());
        _toolbar.Children.Add(_parentButton);

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_detail);
        stack.Children.Add(_source);
        stack.Children.Add(_toolbar);
        stack.Children.Add(_hint);
        stack.Children.Add(_toast);

        Content = _card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 10),
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.35 },
            Margin = new Thickness(8),
        };
        SetSelected(false, false);
        ApplyTheme();
        Theme.XilousTheme.Changed += ApplyTheme;
        Closed += (_, _) => Theme.XilousTheme.Changed -= ApplyTheme;
    }

    /// <summary>Repaints the card in the current Xilous theme.</summary>
    private void ApplyTheme()
    {
        var p = Theme.XilousTheme.Current;
        _card.Background = Theme.Palette.Brush(p.Bg);
        _card.BorderBrush = Theme.Palette.Brush(p.Line);
        _title.Foreground = Theme.Palette.Brush(p.Ink);
        foreach (var t in new[] { _detail, _source, _hint }) t.Foreground = Theme.Palette.Brush(p.Muted);
        _toast.Foreground = Theme.Palette.Brush(p.Accent);
        foreach (var b in _buttons)
        {
            b.Foreground = Theme.Palette.Brush(p.Ink);
            b.Background = Theme.Palette.Brush(p.Field);
            b.BorderBrush = Theme.Palette.Brush(p.Line);
        }
    }

    private Button MakeButton(string text, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            ToolTip = tip,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 4, 10, 4),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
        };
        b.Click += (_, _) => onClick();
        _buttons.Add(b);
        return b;
    }

    public void SetInfo(ElementInfo info)
    {
        var (title, detail, source) = PointerFormatter.Label(info);
        _title.Text = title;
        _detail.Text = detail;
        _detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        _source.Text = source;
        _parentButton.IsEnabled = !info.IsTopLevel;
    }

    public void SetSelected(bool selected, bool measuring)
    {
        _toolbar.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        _hint.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = measuring ? "Measuring: release Alt to stop" : "Alt  measure   ·   Esc  back";
        SetClickThrough(!selected);
    }

    public async void Toast(string text, bool error = false)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        _toast.Text = text;
        var p = Theme.XilousTheme.Current;
        _toast.Foreground = Theme.Palette.Brush(error ? p.Error : p.Accent);
        _toast.Visibility = Visibility.Visible;
        try { await Task.Delay(1800, cts.Token); }
        catch (TaskCanceledException) { return; }
        _toast.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Places the card beside an element: under it when there is room on that monitor,
    /// above it otherwise, and inside its top edge for an element that fills the screen.
    /// </summary>
    public void PlaceBeside(PxRect target)
    {
        EnsureShown();
        UpdateLayout();
        double s = Scale;
        int w = (int)Math.Ceiling(ActualWidth * s), h = (int)Math.Ceiling(ActualHeight * s);
        var work = Native.WorkAreaAt(target.X + target.Width / 2, target.Y + target.Height / 2);

        int x = Math.Clamp(target.X - (int)(8 * s), work.Left, Math.Max(work.Left, work.Right - w));
        int y;
        if (target.Bottom + h <= work.Bottom) y = target.Bottom - (int)(4 * s);
        else if (target.Y - h >= work.Top) y = target.Y - h + (int)(4 * s);
        else y = Math.Clamp(target.Y, work.Top, Math.Max(work.Top, work.Bottom - h));
        MovePx(x, y);
    }
}
