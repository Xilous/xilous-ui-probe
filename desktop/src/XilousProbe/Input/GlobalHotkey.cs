using System.Windows.Input;
using System.Windows.Interop;

namespace XilousProbe.Input;

/// <summary>
/// A system-wide hotkey registered with RegisterHotKey on a message-only window.
/// Fires on the UI thread. Registration fails quietly (returns false) when another
/// app already owns the combination, so the caller can tell the user.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private static int _nextId = 0xB000;
    private readonly HwndSource _source;
    private readonly int _id = _nextId++;

    public event Action? Pressed;
    public bool Registered { get; }
    public string Display { get; }

    public GlobalHotkey(ModifierKeys modifiers, Key key)
    {
        _source = new HwndSource(new HwndSourceParameters("XilousProbeHotkey") { ParentWindow = new nint(-3) /* HWND_MESSAGE */ });
        _source.AddHook(WndProc);

        uint mods = Native.MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= Native.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= Native.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= Native.MOD_ALT;
        Registered = Native.RegisterHotKey(_source.Handle, _id, mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        Display = Describe(modifiers, key);
    }

    public static string Describe(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam == _id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return 0;
    }

    public void Dispose()
    {
        if (Registered) Native.UnregisterHotKey(_source.Handle, _id);
        _source.Dispose();
    }
}
