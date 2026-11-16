using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace XilousProbe.Input;

/// <summary>
/// Low-level mouse and keyboard hooks, installed only while Point Mode is on.
/// Windows gives a low-level hook a few hundred milliseconds before it skips it,
/// so the callbacks only read the event, decide, and hand the work to the dispatcher.
/// </summary>
public sealed class InputHooks : IDisposable
{

    private readonly Native.HookProc _mouseProc, _keyProc; // held so the GC never collects a live hook
    private readonly Dispatcher _dispatcher;
    private nint _mouseHook, _keyHook;
    private bool _swallowingLeft;
    private bool _swallowingEsc;
    private bool _altDown;

    /// <summary>Whether a click at this point is the pointer's to take (true) or the app's (false).</summary>
    public Func<int, int, bool> ShouldCaptureClick { get; set; } = (_, _) => false;
    /// <summary>Whether a key press belongs to the pointer: false while a web page under the pointer has focus.</summary>
    public Func<bool> ShouldCaptureKeys { get; set; } = () => true;

    public event Action<int, int>? Moved;
    public event Action<int, int>? Clicked;
    public event Action? Escape;
    public event Action<bool>? AltChanged;

    public bool IsInstalled => _mouseHook != 0;

    public InputHooks(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
    }

    public void Install()
    {
        if (IsInstalled) return;
        var module = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, module, 0);
        if (_mouseHook == 0 || _keyHook == 0)
        {
            int err = Marshal.GetLastWin32Error();
            Uninstall();
            throw new InvalidOperationException($"Windows refused the input hooks (error {err}).");
        }
    }

    public void Uninstall()
    {
        if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != 0) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = 0;
        _swallowingLeft = _swallowingEsc = false;
        if (_altDown) { _altDown = false; AltChanged?.Invoke(false); }
    }

    private nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            int x = data.pt.X, y = data.pt.Y;
            switch ((int)wParam)
            {
                case Native.WM_MOUSEMOVE:
                    _dispatcher.BeginInvoke(() => Moved?.Invoke(x, y), DispatcherPriority.Input);
                    break;
                case Native.WM_LBUTTONDOWN:
                    if (ShouldCaptureClick(x, y))
                    {
                        _swallowingLeft = true;
                        _dispatcher.BeginInvoke(() => Clicked?.Invoke(x, y), DispatcherPriority.Input);
                        return 1;
                    }
                    break;
                case Native.WM_LBUTTONUP when _swallowingLeft:
                    _swallowingLeft = false;
                    return 1;
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private nint KeyProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (data.vkCode != 0xE8) // our own menu-mask tap
            {
                int msg = (int)wParam;
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                int vk = (int)data.vkCode;

                if (vk == Native.VK_ESCAPE)
                {
                    if (down && ShouldCaptureKeys())
                    {
                        _swallowingEsc = true;
                        _dispatcher.BeginInvoke(() => Escape?.Invoke(), DispatcherPriority.Input);
                        return 1;
                    }
                    if (up && _swallowingEsc)
                    {
                        _swallowingEsc = false;
                        return 1;
                    }
                }
                else if (vk is Native.VK_MENU or Native.VK_LMENU or Native.VK_RMENU)
                {
                    if (down && !_altDown && ShouldCaptureKeys())
                    {
                        _altDown = true;
                        // Without this the app under the cursor opens its menu bar when Alt is released.
                        _dispatcher.BeginInvoke(() => { Native.TapMenuMaskKey(); AltChanged?.Invoke(true); });
                    }
                    else if (up && _altDown)
                    {
                        _altDown = false;
                        _dispatcher.BeginInvoke(() => AltChanged?.Invoke(false));
                    }
                }
            }
        }
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
