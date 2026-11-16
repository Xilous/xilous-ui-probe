using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using XilousProbe.Launcher;

namespace XilousProbe.Widget;

/// <summary>
/// Xilous Probe's interface: a small widget docked on an outer edge of the desktop,
/// floating above other windows, collapsed to its round button. Hovered, it expands
/// away from its edge; dragged, it moves anywhere and snaps onto the nearest outer
/// edge (DockMotion, ported from the Xilous Usage Monitor). Clicking the round button
/// toggles Point Mode; right-clicking opens the same menu as the tray icon.
///
/// The window is a fixed-size transparent canvas that the panel is drawn inside, so it
/// only moves while the panel animates; its fully transparent pixels let clicks through.
/// Everything is placed in physical pixels through SetWindowPos, like the overlays.
/// </summary>
public partial class WidgetWindow : Window
{
    /// <summary>Room around the panel for its shadow, in DIPs.</summary>
    private const double ShadowMargin = 24;
    private const double ExpandedWidth = 340;
    private const double Radius = 14;

    private readonly App _app;
    private readonly DockMotion _motion;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly EventHandler _displaysChanged;
    private List<Display> _displays;
    private double _lastTick;
    private nint _hwnd;
    private bool _pressOnDisc;
    private bool _saveWhenSettled;
    private bool _menuOpen;
    private bool _typing;

    public WidgetWindow(App app)
    {
        _app = app;
        InitializeComponent();
        VersionLabel.Text = $"v{Update.Updater.CurrentVersion}";
        HotkeyLabel.Text = app.HotkeyRegistered ? $"{app.HotkeyDisplay} · Esc steps back" : $"{app.HotkeyDisplay} is taken by another app";

        _displays = ReadDisplays();
        var saved = app.Settings.Data;
        var anchor = saved.WidgetX is double x && saved.WidgetY is double y ? (x, y) : DefaultAnchor(_displays);
        _motion = new DockMotion(anchor);

        // 10 ms while anything moves, as the usage monitor does; stopped when it settles.
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
        _timer.Tick += (_, _) => OnFrame();

        Card.PreviewMouseLeftButtonDown += OnPress;
        Card.PreviewMouseMove += OnDrag;
        Card.PreviewMouseLeftButtonUp += OnRelease;
        Card.LostMouseCapture += (_, _) => { if (_motion.Pressed) { _motion.CancelPress(); Kick(); } };
        Card.MouseEnter += (_, _) => { _motion.SetHovered(true, Now); Kick(); };
        Card.MouseLeave += (_, _) => { _motion.SetHovered(false, Now); Kick(); };
        Card.MouseRightButtonUp += OnMenu;

        UrlBox.PreviewMouseLeftButtonDown += (_, _) => BeginTyping();
        UrlBox.TextChanged += (_, _) => UrlPlaceholder.Visibility = UrlBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UrlBox.LostKeyboardFocus += (_, _) => EndTyping();
        Deactivated += (_, _) => EndTyping();

        _displaysChanged = (_, _) => Dispatcher.BeginInvoke(() => { _displays = ReadDisplays(); Kick(); });
        SystemEvents.DisplaySettingsChanged += _displaysChanged;
        DpiChanged += (_, _) => Kick();
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= _displaysChanged;
        Loaded += (_, _) => Kick();
    }

    private double Now => _clock.Elapsed.TotalSeconds;
    private double Scale => _hwnd == 0 ? 1 : Native.GetDpiForWindow(_hwnd) / 96.0;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        SetActivatable(false);
    }

    /// <summary>Off by default, so clicking the widget never takes focus from the app being pointed at.</summary>
    private void SetActivatable(bool activatable)
    {
        if (_hwnd == 0) return;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW;
        if (activatable) ex &= ~Native.WS_EX_NOACTIVATE; else ex |= Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (nint)ex);
    }

    // ===== Displays =====

    private static List<Display> ReadDisplays() =>
        Native.Monitors().Select(m => new Display(
            new DRect(m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, m.Bounds.Bottom),
            new DRect(m.Work.Left, m.Work.Top, m.Work.Right, m.Work.Bottom))).ToList();

    /// <summary>A fresh start docks on the right edge of the primary display, a third of the way down.</summary>
    public static (double X, double Y) DefaultAnchor(IReadOnlyList<Display>? displays = null)
    {
        var primary = Native.Monitors().FirstOrDefault(m => m.Primary);
        var w = primary.Work;
        if (w.Right <= w.Left && displays is { Count: > 0 })
            return (displays[0].Work.R, displays[0].Work.T + (displays[0].Work.B - displays[0].Work.T) / 3);
        return (w.Right, w.Top + (w.Bottom - w.Top) / 3.0);
    }

    public void ResetPosition()
    {
        _motion.SetAnchor(DefaultAnchor(_displays));
        _saveWhenSettled = true;
        Kick();
    }

    // ===== State shown on the widget =====

    public void SetPointing(bool on)
    {
        var accent = (Brush)FindResource("AccentBrush");
        var idle = new SolidColorBrush(Color.FromRgb(0x5A, 0x56, 0x50));
        var muted = (Brush)FindResource("MutedBrush");
        Ring.Stroke = on ? accent : idle;
        Ring.Fill = on ? new SolidColorBrush(Color.FromArgb(0x29, 0xD9, 0x77, 0x57)) : Brushes.Transparent;
        Crosshair.Stroke = on ? accent : muted;
        Center.Fill = on ? accent : muted;
        PointLabel.Text = on ? "Pointing" : "Not pointing";
    }

    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>A dot under the round button while the probe Chrome is attached.</summary>
    public void SetChromeAttached(bool attached)
    {
        Dots.Children.Clear();
        if (attached)
            Dots.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Color.FromRgb(0x6A, 0xA9, 0xFF)), ToolTip = "Probe Chrome attached" });
    }

    // ===== Input =====

    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node == ancestor) return true;
        return false;
    }

    private static bool IsControl(DependencyObject? node)
    {
        for (; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is TextBox or System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (IsControl(source)) return; // the URL box and buttons work as controls, not drag handles
        _pressOnDisc = IsWithin(source, Disc);
        if (!Native.GetCursorPos(out var p)) return;
        _motion.PressAt(p.X, p.Y, Now, Dock.CollapsedSize * Scale);
        Card.CaptureMouse();
        e.Handled = true;
        Kick();
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (!_motion.Pressed || !Native.GetCursorPos(out var p)) return;
        _motion.DragTo(p.X, p.Y, Now, Scale);
        Kick();
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (!_motion.Pressed) return;
        bool wasDrag = _motion.Release(Now, Scale);
        Card.ReleaseMouseCapture();
        if (wasDrag)
        {
            // The cursor usually ends over the dropped widget; let it settle collapsed, then save its edge.
            _motion.ForgetHover();
            _saveWhenSettled = true;
        }
        else if (_pressOnDisc)
        {
            _app.TogglePointMode();
        }
        _pressOnDisc = false;
        e.Handled = true;
        Kick();
    }

    private void OnMenu(object sender, MouseButtonEventArgs e)
    {
        var menu = _app.BuildMenu();
        menu.PlacementTarget = Card;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.Opened += (_, _) => { _menuOpen = true; UpdateHold(); };
        menu.Closed += (_, _) => { _menuOpen = false; UpdateHold(); };
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void BeginTyping()
    {
        // Typing needs keyboard focus, so the widget becomes activatable just for that.
        SetActivatable(true);
        Activate();
        UrlBox.Focus();
        _typing = true;
        UpdateHold();
    }

    private void EndTyping()
    {
        if (!_typing) return;
        _typing = false;
        SetActivatable(false);
        UpdateHold();
    }

    private void UpdateHold()
    {
        _motion.HoldOpen = _menuOpen || _typing;
        Kick();
    }

    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnOpen(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndTyping(); Keyboard.ClearFocus(); e.Handled = true; }
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        var url = UrlInput.Normalize(UrlBox.Text);
        if (url is null) { SetStatus("Type a URL, such as localhost:5173 or example.com."); return; }
        UrlBox.Text = url;
        _app.Settings.Data.LastUrl = url;
        _app.Settings.Save();
        await _app.OpenUrlAsync(url);
    }

    private void OnSettings(object sender, RoutedEventArgs e) => _app.OpenSettings();

    public void PrefillUrl(string url)
    {
        UrlBox.Text = url;
    }

    // ===== Animation =====

    private void Kick()
    {
        if (_timer.IsEnabled) return;
        _lastTick = Now;
        _timer.Start();
        OnFrame();
    }

    private void OnFrame()
    {
        double now = Now, dt = now - _lastTick;
        _lastTick = now;
        double s = Scale;

        Body.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
        double contentH = Body.DesiredSize.Height;
        var frame = _motion.Tick(now, dt, _displays, s, ExpandedWidth * s, contentH * s);
        if (frame is not { } f) return;
        Render(f, s, contentH);

        if (!f.Moving)
        {
            _timer.Stop();
            if (_saveWhenSettled)
            {
                _saveWhenSettled = false;
                _app.Settings.Data.WidgetX = _motion.Anchor.X;
                _app.Settings.Data.WidgetY = _motion.Anchor.Y;
                _app.Settings.Save();
            }
        }
    }

    private void Render(DockFrame f, double s, double contentH)
    {
        double w = f.Width / s, h = f.Height / s, c = f.Collapsed / s;
        bool docked = f.Docked;
        var side = f.Side;
        bool onRight = docked && side == Side.Right, onBottom = docked && side == Side.Bottom;

        // Flat and borderless on the edge it sits on, rounded everywhere else.
        Card.CornerRadius = !docked ? new CornerRadius(Radius) : side switch
        {
            Side.Right => new CornerRadius(Radius, 0, 0, Radius),
            Side.Left => new CornerRadius(0, Radius, Radius, 0),
            Side.Top => new CornerRadius(0, 0, Radius, Radius),
            _ => new CornerRadius(Radius, Radius, 0, 0),
        };
        var border = new Thickness(
            docked && side == Side.Left ? 0 : 1, docked && side == Side.Top ? 0 : 1,
            docked && side == Side.Right ? 0 : 1, docked && side == Side.Bottom ? 0 : 1);
        Card.BorderThickness = border;
        Card.Width = w;
        Card.Height = h;

        // The window is a fixed canvas centred on the collapsed widget, with room for the panel to
        // expand in any direction. It moves only when the widget moves, and then the panel moves with
        // it; expanding only redraws inside a still window. (Moving the window while the panel grew
        // showed the previous frame at the new position for a frame, and click-through follows the
        // drawn pixels, so the cursor fell through, the widget saw a mouse-leave and started to
        // collapse: the vibration.)
        int winW = (int)Math.Ceiling(2 * (ExpandedWidth * 1.06 + ShadowMargin) * s);
        int winH = (int)Math.Ceiling(2 * (Math.Max(contentH, Dock.CollapsedSize) * 1.06 + ShadowMargin) * s);
        int winX = (int)Math.Round(f.CenterX - winW / 2.0), winY = (int)Math.Round(f.CenterY - winH / 2.0);
        Canvas.SetLeft(Card, (f.Left - winX) / s);
        Canvas.SetTop(Card, (f.Top - winY) / s);
        PlaceWindow(winX, winY, winW, winH);
        Shadow.BlurRadius = 18 + 20 * f.Lift;
        Shadow.Opacity = 0.35 + 0.2 * f.Lift;

        // The round button stays in the corner on the edge's side.
        Disc.Width = Disc.Height = c;
        Disc.HorizontalAlignment = onRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Disc.VerticalAlignment = onBottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;

        // The content: pinned to the same corner, the header beside the button, fading in.
        double innerW = w - border.Left - border.Right, innerH = h - border.Top - border.Bottom;
        Canvas.SetLeft(Body, onRight ? innerW - ExpandedWidth : 0);
        Canvas.SetTop(Body, onBottom ? innerH - contentH : 0);
        Body.Opacity = Math.Clamp((f.Expanded - 0.35) / 0.65, 0, 1);
        Body.IsHitTestVisible = f.Expanded > 0.9;
        HeaderLeft.Width = onRight ? new GridLength(1, GridUnitType.Star) : new GridLength(40);
        HeaderRight.Width = onRight ? new GridLength(40) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(HeaderText, onRight ? 0 : 1);
        Grid.SetRow(Header, onBottom ? 2 : 0);
        Grid.SetRow(Footer, onBottom ? 0 : 2);
        Footer.BorderThickness = onBottom ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0);
        Footer.Padding = onBottom ? new Thickness(0, 12, 0, 10) : new Thickness(0, 10, 0, 12);

    }

    private (int X, int Y, int W, int H) _placed;

    /// <summary>Moves the window only when its rectangle actually changes.</summary>
    private void PlaceWindow(int x, int y, int w, int h)
    {
        if (!IsVisible) Show();
        if (_placed == (x, y, w, h)) return;
        _placed = (x, y, w, h);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
    }
}
