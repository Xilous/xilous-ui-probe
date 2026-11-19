using System.IO;
using System.Text;
using XilousProbe.Settings;

namespace XilousProbe.Capture;

/// <summary>
/// Where Copy keeps the screenshot its pointer names, so one paste gives a coding agent on
/// this machine both the text and the picture: the clipboard can hold both, but the app it
/// is pasted into takes only one. Files are named by the time they were taken, so they sort
/// in order, and only the newest <see cref="Keep"/> are kept.
/// </summary>
public sealed class ShotStore(string folder)
{
    /// <summary>How many screenshots are kept; older ones are deleted as new ones are saved.</summary>
    public const int Keep = 50;

    /// <summary>The name pattern every saved screenshot matches. Only these are ever pruned.</summary>
    private const string Pattern = "????????-??????-???-*.png";

    /// <summary>%LOCALAPPDATA%\XilousProbe\shots.</summary>
    public static ShotStore Default { get; } = new(Path.Combine(SettingsStore.LocalDataDir, "shots"));

    public string Folder { get; } = folder;

    /// <summary>Saves a PNG and prunes the oldest beyond <see cref="Keep"/>.</summary>
    /// <param name="label">What it shows, for the file name, such as "Button Save".</param>
    /// <returns>The saved file's full path.</returns>
    public string Save(byte[] png, string label, DateTime taken)
    {
        Directory.CreateDirectory(Folder);
        var stem = $"{taken:yyyyMMdd-HHmmss-fff}-{Slug(label)}";
        var path = Path.Combine(Folder, stem + ".png");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(Folder, $"{stem}-{n}.png");
        File.WriteAllBytes(path, png);
        Prune();
        return path;
    }

    /// <summary>Deletes all but the newest <see cref="Keep"/> screenshots. Other files in the folder are left alone.</summary>
    public void Prune()
    {
        if (!Directory.Exists(Folder)) return;
        // Simple matching: '?' is exactly one character and "*.png" never matches by a short 8.3 name.
        var old = Directory.GetFiles(Folder, Pattern, new EnumerationOptions { MatchType = MatchType.Simple })
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(Keep);
        foreach (var f in old)
        {
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }   // open elsewhere: next time
        }
    }

    /// <summary>A label as a short file-name part: letters and digits, words joined by hyphens.</summary>
    public static string Slug(string label)
    {
        var sb = new StringBuilder();
        foreach (var c in label)
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 40) break;
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "element" : slug;
    }
}
