using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using XilousProbe.Uia;

namespace XilousProbe.Overlay;

/// <summary>
/// A borderless, transparent, always-on-top window placed in physical screen pixels,
/// which is what UI Automation reports. WPF's own Left/Top are in DIPs relative to
/// one monitor's scaling, so on a mixed-DPI desktop they would not line up; placing
/// through SetWindowPos does, on every monitor.
/// </summary>
public class OverlayWindow : Window
{
    private bool _clickThrough;

    /// <summary>Lets clicks reach the window (for the toolbar) or pass through it (for the outline).</summary>
    public void SetClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        if (Handle != 0) Native.MakeOverlay(Handle, clickThrough);
    }
    protected nint Handle { get; private set; }

    public OverlayWindow(bool clickThrough)
    {
        _clickThrough = clickThrough;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Width = 1;
        Height = 1;
        Left = -32000;
        Top = -32000;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        Native.MakeOverlay(Handle, _clickThrough);
        // Our own chrome must never end up in a screenshot of the element under it.
        // XILOUS_PROBE_SHOW_OVERLAY_IN_CAPTURE=1 turns that off, to see the overlay in recordings while developing.
        if (Environment.GetEnvironmentVariable("XILOUS_PROBE_SHOW_OVERLAY_IN_CAPTURE") != "1")
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    public void EnsureShown()
    {
        if (!IsVisible) Show();
    }

    /// <summary>Moves and sizes the window to a rectangle in physical pixels.</summary>
    public void PlacePx(PxRect r)
    {
        EnsureShown();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height),
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
    }

    /// <summary>Moves the window's top-left corner to a physical-pixel point, keeping its size.</summary>
    public void MovePx(int x, int y)
    {
        EnsureShown();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, 0, 0,
            Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
    }

    /// <summary>Physical pixels per DIP on the monitor the window is on now.</summary>
    public double Scale => Handle == 0 ? 1 : Native.GetDpiForWindow(Handle) / 96.0;

    public void HideOverlay()
    {
        if (IsVisible) Hide();
    }
}
