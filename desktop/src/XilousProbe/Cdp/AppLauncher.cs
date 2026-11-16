using System.Diagnostics;
using System.IO;
using XilousProbe.Settings;

namespace XilousProbe.Cdp;

/// <summary>Starts an app from the Apps list with remote debugging on its port.</summary>
public static class AppLauncher
{
    public const string PortPlaceholder = "{port}";

    /// <summary>The program and argument string to run for an entry. Attach entries start nothing.</summary>
    public static (string FileName, string Arguments)? BuildCommand(AppEntry entry)
    {
        var port = entry.Port.ToString();
        switch (entry.Kind)
        {
            case AppKind.DevCommand:
            {
                // Through cmd so npm, npx, pnpm and yarn (.cmd shims) resolve the way they do in a terminal.
                var line = $"{entry.Command} {entry.Arguments}".Replace(PortPlaceholder, port).Trim();
                return ("cmd.exe", $"/d /s /c \"{line}\"");
            }
            case AppKind.Executable:
            {
                var args = entry.Arguments.Replace(PortPlaceholder, port).Trim();
                if (!args.Contains("--remote-debugging-port", StringComparison.Ordinal))
                    args = $"{args} --remote-debugging-port={port}".Trim();
                return (entry.Command, args);
            }
            default:
                return null;
        }
    }

    /// <summary>Starts the entry. Returns the process for dev commands, so its console can be shown if it fails.</summary>
    public static Process? Start(AppEntry entry)
    {
        var cmd = BuildCommand(entry);
        if (cmd is null) return null;
        var (file, args) = cmd.Value;
        if (entry.Kind == AppKind.Executable && !File.Exists(file))
            throw new FileNotFoundException($"{file} does not exist.");
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(entry.WorkingDirectory) ? entry.WorkingDirectory : Environment.CurrentDirectory,
            // A dev server's console is where its errors go; keep it visible but out of the way.
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Minimized,
        };
        if (entry.Kind == AppKind.DevCommand) psi.UseShellExecute = true;
        return Process.Start(psi);
    }
}
