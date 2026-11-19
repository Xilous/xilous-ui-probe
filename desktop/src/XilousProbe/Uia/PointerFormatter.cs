using System.Text;

namespace XilousProbe.Uia;

/// <summary>
/// The text Copy puts on the clipboard for a desktop element. It is a pointer, in the
/// same shape as the web pointer's: one fact per line, enough for a coding agent to
/// find the control in source (automation id, class, the text it contains, path) and
/// enough for anyone else to find it on screen (app, window, position, bounds).
/// </summary>
public static class PointerFormatter
{
    public static string Format(ElementInfo e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("```");

        var window = string.IsNullOrWhiteSpace(e.WindowTitle) ? "" : $", window \"{ElementStep.Clip(e.WindowTitle, 80)}\"";
        sb.AppendLine($"# app: {Or(e.ProcessName, "unknown")} (pid {e.ProcessId}){window}");
        sb.AppendLine($"# framework: {e.Framework}");

        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.AutomationId)) ids.Add($"automationId: {e.AutomationId}");
        if (!string.IsNullOrWhiteSpace(e.ClassName)) ids.Add($"class: {e.ClassName}");
        sb.AppendLine($"# element: {e.Brief}{(ids.Count > 0 ? $" ({string.Join(", ", ids)})" : "")}");

        // The text to search the source for: inside the element, or failing that, around it.
        if (e.Contents.Count > 0)
            sb.AppendLine($"# contains: {string.Join(", ", e.Contents)}");
        else if (e.Nearby.Count > 0)
        {
            var where = e.NearbyIn is null ? "" : $" (in {e.NearbyIn}, {Levels(e.NearbyLevels)})";
            sb.AppendLine($"# nearby: {string.Join(", ", e.Nearby)}{where}");
        }

        if (e.Ancestors.Count > 0)
            sb.AppendLine($"# path: {string.Join(" > ", e.Ancestors.Select(a => a.ToString()).Append(e.Brief))}");

        if (e.Index > 0 && e.SiblingCount > 0 && e.Ancestors.Count > 0)
        {
            sb.AppendLine($"# position: child {e.Index} of {e.SiblingCount} in {e.Ancestors[^1]}");
            var around = new List<string>();
            if (e.Previous is not null) around.Add($"after {e.Previous}");
            if (e.Next is not null) around.Add($"before {e.Next}");
            if (around.Count > 0) sb.AppendLine($"#   {string.Join(", ", around)}");
        }

        if (!e.Bounds.IsEmpty)
        {
            var inWindow = e.WindowBounds.IsEmpty
                ? ""
                : $"{e.Bounds.X - e.WindowBounds.X},{e.Bounds.Y - e.WindowBounds.Y} {e.Bounds.Width}x{e.Bounds.Height} in window, ";
            sb.AppendLine($"# bounds: {inWindow}screen {e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height} (physical px)");
        }

        var state = new List<string> { e.IsEnabled ? "enabled" : "disabled" };
        if (e.IsKeyboardFocusable) state.Add("focusable");
        if (e.IsOffscreen) state.Add("offscreen");
        var patterns = e.Patterns.Count > 0 ? $" · patterns: {string.Join(", ", e.Patterns)}" : "";
        sb.AppendLine($"# state: {string.Join(", ", state)}{patterns}");

        if (!string.IsNullOrWhiteSpace(e.Value) && e.Value != e.Name)
            sb.AppendLine($"# value: {ElementStep.Clip(e.Value, 200)}");
        if (!string.IsNullOrWhiteSpace(e.HelpText))
            sb.AppendLine($"# help: {ElementStep.Clip(e.HelpText, 200)}");

        sb.Append("```");
        return sb.ToString();
    }

    /// <summary>The short readout on the hover label: identity, then what it is made of.</summary>
    public static (string Title, string Detail, string Source) Label(ElementInfo e)
    {
        var detail = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.AutomationId)) detail.Add($"#{e.AutomationId}");
        if (!string.IsNullOrWhiteSpace(e.ClassName)) detail.Add(ElementStep.Clip(e.ClassName, 40));
        if (!e.Bounds.IsEmpty) detail.Add($"{e.Bounds.Width}×{e.Bounds.Height}");
        return (e.Brief, string.Join("  ·  ", detail), $"{Or(e.ProcessName, "?")}  ·  {e.Framework}");
    }

    private static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;

    private static string Levels(int n) => n == 1 ? "the parent" : $"{n} levels up";
}
