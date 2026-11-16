namespace XilousProbe.Launcher;

public static class UrlInput
{
    /// <summary>Accepts what people type: bare hosts, localhost:port, or full URLs.</summary>
    public static string? Normalize(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Contains("://")) return s;
        bool local = s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                     s.StartsWith("127.", StringComparison.Ordinal) || s.StartsWith("[::1]", StringComparison.Ordinal);
        return (local ? "http://" : "https://") + s;
    }
}
