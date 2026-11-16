using System.Windows.Threading;
using XilousProbe.Capture;
using XilousProbe.Input;
using XilousProbe.Overlay;

namespace XilousProbe.Uia;

/// <summary>
/// Point Mode for desktop windows: hover outlines through UI Automation, click to
/// select, then Copy, Screenshot, Parent, and Alt to measure. Windows the web pointer
/// covers (the probe Chrome, attached Electron apps) are left alone: the pointer
/// inside the page handles them, mouse and keys included.
/// </summary>
public sealed class DesktopPointer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly UiaEngine _engine = new();
    private readonly InputHooks _hooks;
    private readonly Func<uint, bool> _isWebProcess;
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    private readonly HighlightWindow _hover = new();
    private readonly HighlightWindow _selection = new();
    private readonly HighlightWindow _measureTarget = new();
    private readonly InfoWindow _info = new();
    private readonly RedlineWindow _redline = new();

    private ElementInfo? _selected;
    private bool _alt;
    private bool _active;
    private readonly DispatcherTimer _poll;
    private (int X, int Y) _lastCursor = (int.MinValue, int.MinValue);

    /// <summary>Esc at the last layer: the user wants Point Mode off.</summary>
    public event Action? ExitRequested;

    public DesktopPointer(Dispatcher dispatcher, Func<uint, bool> isWebProcess)
    {
        _dispatcher = dispatcher;
        _isWebProcess = isWebProcess;
        _hooks = new InputHooks(dispatcher)
        {
            ShouldCaptureClick = ShouldCaptureClick,
            ShouldCaptureKeys = ShouldCaptureKeys,
        };
        _hooks.Moved += OnCursor;
        // The hook only sees real input. A cursor moved by SetCursorPos (remote desktop
        // tools, automation, pen drivers) never reaches it, so the position is also polled.
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Input, (_, _) =>
        {
            if (Native.GetCursorPos(out var p)) OnCursor(p.X, p.Y);
        }, dispatcher);
        _poll.Stop();
        _hooks.Clicked += (x, y) => _ = SelectAtAsync(x, y);
        _hooks.Escape += OnEscape;
        _hooks.AltChanged += OnAlt;
        _engine.Hovered += info => _dispatcher.BeginInvoke(() => OnHovered(info));
        _info.CopyClicked += () => _ = CopyAsync();
        _info.ScreenshotClicked += () => _ = ScreenshotAsync();
        _info.ParentClicked += () => _ = ParentAsync();
    }

    public bool Active => _active;

    public void Start()
    {
        if (_active) return;
        _active = true;
        _hooks.Install();
        _lastCursor = (int.MinValue, int.MinValue);
        _poll.Start();
        if (Native.GetCursorPos(out var p)) OnCursor(p.X, p.Y);
    }

    /// <summary>A cursor position from either source; only an actual move counts.</summary>
    private void OnCursor(int x, int y)
    {
        if ((x, y) == _lastCursor) return;
        _lastCursor = (x, y);
        OnMoved(x, y);
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;
        _poll.Stop();
        _hooks.Uninstall();
        _selected = null;
        _alt = false;
        _engine.ClearSelection();
        HideAll();
    }

    private void HideAll()
    {
        _hover.HideOverlay();
        _selection.HideOverlay();
        _measureTarget.HideOverlay();
        _redline.HideOverlay();
        _info.HideOverlay();
    }

    private static uint ProcessAt(int x, int y)
    {
        var hwnd = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
        if (hwnd == 0) return 0;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        return Native.ProcessIdOf(root != 0 ? root : hwnd);
    }

    private bool ShouldCaptureClick(int x, int y)
    {
        uint pid = ProcessAt(x, y);
        return pid != 0 && pid != _ownPid && !_isWebProcess(pid);
    }

    private bool ShouldCaptureKeys()
    {
        var fg = Native.GetForegroundWindow();
        return fg == 0 || !_isWebProcess(Native.ProcessIdOf(fg));
    }

    private void OnMoved(int x, int y)
    {
        if (!_active) return;
        uint pid = ProcessAt(x, y);
        if (pid == _ownPid) return; // over our own toolbar or launcher: keep what is shown
        if (pid == 0 || _isWebProcess(pid))
        {
            // The page's own pointer takes over inside a web window.
            _hover.HideOverlay();
            _measureTarget.HideOverlay();
            _redline.HideOverlay();
            if (_selected is null) _info.HideOverlay();
            return;
        }
        if (_selected is not null && !_alt) return; // the selection holds still until Esc or another click
        _engine.RequestHover(x, y);
    }

    private void OnHovered(ElementInfo? info)
    {
        if (!_active) return;
        // A lookup that finished after the cursor moved into a web window is stale.
        if (info is not null && Native.GetCursorPos(out var cur) && _isWebProcess(ProcessAt(cur.X, cur.Y))) return;
        if (info is null || info.Bounds.IsEmpty)
        {
            _hover.HideOverlay();
            _measureTarget.HideOverlay();
            _redline.HideOverlay();
            if (_selected is null) _info.HideOverlay();
            return;
        }

        if (_selected is not null)
        {
            if (!_alt) return;
            _measureTarget.Show(info.Bounds, HighlightStyle.MeasureTarget);
            _redline.Show(_selected.Bounds, info.Bounds);
            return;
        }

        _hover.Show(info.Bounds, HighlightStyle.Hover);
        _info.SetInfo(info);
        _info.SetSelected(false, false);
        _info.PlaceBeside(info.Bounds);
    }

    private async Task SelectAtAsync(int x, int y)
    {
        var info = await _engine.SelectAtAsync(x, y);
        if (!_active) return;
        ShowSelection(info);
    }

    private void ShowSelection(ElementInfo? info)
    {
        _selected = info;
        _hover.HideOverlay();
        _measureTarget.HideOverlay();
        _redline.HideOverlay();
        if (info is null || info.Bounds.IsEmpty)
        {
            _selection.HideOverlay();
            _info.HideOverlay();
            _selected = null;
            return;
        }
        _selection.Show(info.Bounds, HighlightStyle.Selected);
        _info.SetInfo(info);
        _info.SetSelected(true, _alt);
        _info.PlaceBeside(info.Bounds);
    }

    private void OnEscape()
    {
        if (!_active) return;
        if (_selected is not null)
        {
            _selected = null;
            _engine.ClearSelection();
            _selection.HideOverlay();
            _measureTarget.HideOverlay();
            _redline.HideOverlay();
            _info.HideOverlay();
            if (Native.GetCursorPos(out var p)) OnMoved(p.X, p.Y);
            return;
        }
        ExitRequested?.Invoke();
    }

    private void OnAlt(bool down)
    {
        _alt = down;
        if (_selected is null) return;
        _info.SetSelected(true, down);
        if (down)
        {
            if (Native.GetCursorPos(out var p)) _engine.RequestHover(p.X, p.Y);
        }
        else
        {
            _measureTarget.HideOverlay();
            _redline.HideOverlay();
        }
    }

    private async Task CopyAsync()
    {
        var info = await _engine.RefreshSelectedAsync() ?? _selected;
        if (info is null) return;
        try
        {
            await ClipboardService.SetTextAsync(PointerFormatter.Format(info));
            _info.Toast("Copied pointer");
        }
        catch (Exception ex)
        {
            _info.Toast($"Copy failed: {ex.Message}", error: true);
        }
    }

    private async Task ScreenshotAsync()
    {
        if (_selected is null) return;
        try
        {
            var png = await Task.Run(() => ScreenCapture.CapturePng(_selected.Bounds));
            await ClipboardService.SetPngAsync(png);
            _info.Toast("Copied screenshot");
        }
        catch (Exception ex)
        {
            _info.Toast($"Screenshot failed: {ex.Message}", error: true);
        }
    }

    private async Task ParentAsync()
    {
        var info = await _engine.SelectParentAsync();
        if (!_active) return;
        ShowSelection(info);
    }

    public void Dispose()
    {
        Stop();
        _hooks.Dispose();
        _engine.Dispose();
        foreach (var w in new System.Windows.Window[] { _hover, _selection, _measureTarget, _info, _redline }) w.Close();
    }
}
