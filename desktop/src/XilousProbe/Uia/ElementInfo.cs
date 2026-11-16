namespace XilousProbe.Uia;

/// <summary>A rectangle in physical screen pixels, as UI Automation reports it.</summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    public static PxRect Union(PxRect a, PxRect b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new PxRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }
}

/// <summary>One step of an element's ancestry: its control type and name.</summary>
public sealed record ElementStep(string ControlType, string Name, string AutomationId)
{
    public override string ToString() => Brief(ControlType, Name, AutomationId);

    public static string Brief(string controlType, string name, string automationId)
    {
        if (!string.IsNullOrWhiteSpace(name)) return $"{controlType} \"{Clip(name, 40)}\"";
        if (!string.IsNullOrWhiteSpace(automationId)) return $"{controlType} #{automationId}";
        return controlType;
    }

    public static string Clip(string s, int max)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

/// <summary>
/// Everything the pointer reports about a desktop element, read once from UI Automation
/// so formatting and drawing never go back to the (slow, cross-process) automation tree.
/// </summary>
public sealed record ElementInfo
{
    public string ControlType { get; init; } = "Custom";
    public string Name { get; init; } = "";
    public string AutomationId { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string FrameworkId { get; init; } = "";
    public string HelpText { get; init; } = "";
    public PxRect Bounds { get; init; }

    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string WindowTitle { get; init; } = "";
    public PxRect WindowBounds { get; init; }

    /// <summary>Ancestors from the top-level window down to the element's parent.</summary>
    public IReadOnlyList<ElementStep> Ancestors { get; init; } = [];

    /// <summary>1-based position among the parent's children, and how many there are; 0 when unknown.</summary>
    public int Index { get; init; }
    public int SiblingCount { get; init; }
    public ElementStep? Previous { get; init; }
    public ElementStep? Next { get; init; }

    public bool IsEnabled { get; init; } = true;
    public bool IsKeyboardFocusable { get; init; }
    public bool IsOffscreen { get; init; }
    public IReadOnlyList<string> Patterns { get; init; } = [];
    public string Value { get; init; } = "";

    /// <summary>Set when this is the top-level window itself: Select Parent has nowhere to go.</summary>
    public bool IsTopLevel { get; init; }

    public ElementStep Step => new(ControlType, Name, AutomationId);
    public string Brief => Step.ToString();

    /// <summary>A readable framework name from UIA's FrameworkId.</summary>
    public string Framework => FrameworkId switch
    {
        "WPF" => "WPF",
        "WinForm" => "WinForms",
        "Win32" => "Win32",
        "XAML" => "WinUI/UWP (XAML)",
        "DirectUI" => "DirectUI",
        "Chrome" => "Chromium (Chrome, Edge or Electron)",
        "" => "unknown",
        var other => other,
    };
}
