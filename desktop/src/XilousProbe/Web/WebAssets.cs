using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace XilousProbe.Web;

/// <summary>
/// The web pointer the app shares with the Chrome extension (content.js and its
/// stylesheets, embedded from the repo root at build time) and the scripts that
/// carry it into a page.
/// </summary>
public static class WebAssets
{
    private static readonly Assembly Asm = typeof(WebAssets).Assembly;

    public static string Read(string logicalName)
    {
        using var s = Asm.GetManifestResourceStream(logicalName)
            ?? throw new FileNotFoundException($"Embedded asset missing: {logicalName}");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static string? _shim;
    public static string Shim => _shim ??= Read("web/chrome-shim.js").Trim().TrimEnd(';');

    /// <summary>
    /// The script injected into every attached page. It runs in the page's main world, so
    /// React and Vue component chains are readable, and it runs in the top frame only, as the
    /// extension's content script does.
    /// </summary>
    /// <param name="bindingName">The CDP Runtime binding the page sends through. It is removed from the
    /// page's globals as soon as the script holds it.</param>
    /// <param name="receiverName">The non-enumerable global the app calls to deliver replies and events.</param>
    public static string BuildPageBootstrap(string bindingName, string receiverName)
    {
        var sb = new StringBuilder(512 * 1024);
        sb.Append("(function(){\n\"use strict\";\n");
        sb.Append("if (window.top !== window) return;\n");
        // Browser pages and DevTools itself are not pages to point at.
        sb.Append("if (/^(chrome|chrome-untrusted|chrome-extension|devtools|edge|about|chrome-search):$/.test(location.protocol)) return;\n");
        sb.Append($"var KEY = {Js(receiverName)}, BIND = {Js(bindingName)};\n");
        sb.Append("if (Object.prototype.hasOwnProperty.call(window, KEY)) return;\n");
        sb.Append("var bind = window[BIND];\n");
        sb.Append("if (typeof bind !== 'function') return;\n");
        sb.Append("try { delete window[BIND]; } catch (e) {}\n");
        sb.Append("var shim = (").Append(Shim).Append(")({ send: function (s) { bind(s); } });\n");
        sb.Append("Object.defineProperty(window, KEY, { value: shim.receive, enumerable: false });\n");
        sb.Append($"var CSS_TOKENS = {Js(Read("web/tokens.css"))};\n");
        sb.Append($"var CSS_CONTENT = {Js(Read("web/content.css"))};\n");
        sb.Append("function addStyle(id, text) { var s = document.createElement('style'); s.id = id; s.textContent = text; (document.head || document.documentElement).appendChild(s); }\n");
        sb.Append("function boot() {\n");
        // The web pointer builds its UI with innerHTML. Under enforced Trusted Types with a
        // fixed list of policy names (VS Code does this) it cannot, so it says so and stays
        // out; the app then points at the window through UI Automation instead.
        sb.Append("try { document.createElement('div').innerHTML = '<i></i>'; } catch (e) { shim.notify('blocked', { url: location.href, reason: 'Trusted Types' }); return; }\n");
        sb.Append("addStyle('ccp-xp-tokens', CSS_TOKENS); addStyle('ccp-xp-content', CSS_CONTENT);\n");
        // html2canvas is UMD: given a CommonJS module it leaves the page's globals alone.
        sb.Append("var html2canvas = (function () { var module = { exports: {} }; var exports = module.exports; var define;\n");
        sb.Append(Read("web/lib/html2canvas.min.js"));
        sb.Append("\n; return module.exports; }).call({});\n");
        sb.Append("(function (chrome, navigator, html2canvas) {\n");
        sb.Append(Read("web/content.js"));
        sb.Append("\n}).call(window, shim.chrome, shim.navigator, html2canvas);\n");
        sb.Append("shim.notify('ready', { url: location.href, title: document.title });\n");
        sb.Append("}\n");
        sb.Append("if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, { once: true }); else boot();\n");
        sb.Append("})();\n");
        return sb.ToString();
    }

    /// <summary>The script the settings window runs before settings.js: chrome.storage over WebView2's postMessage.</summary>
    public static string BuildSettingsBootstrap()
    {
        return "(function(){\n" +
               "var wv = window.chrome && window.chrome.webview; if (!wv) return;\n" +
               "var shim = (" + Shim + ")({ send: function (s) { wv.postMessage(s); } });\n" +
               "wv.addEventListener('message', function (e) { shim.receive(e.data); });\n" +
               "window.chrome.storage = shim.chrome.storage;\n" +
               "window.chrome.runtime = shim.chrome.runtime;\n" +
               "})();\n";
    }

    /// <summary>Writes the settings page and the stylesheets it links to a folder WebView2 can serve.</summary>
    public static string ExtractSettingsSite(string root)
    {
        Directory.CreateDirectory(root);
        foreach (var name in Asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("web/", StringComparison.Ordinal)) continue;
            var rel = name["web/".Length..];
            if (!(rel.StartsWith("settings/", StringComparison.Ordinal) || rel is "tokens.css" or "content.css")) continue;
            var dest = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var s = Asm.GetManifestResourceStream(name)!;
            using var f = File.Create(dest);
            s.CopyTo(f);
        }
        return root;
    }

    public static string Js(string value) => JsonSerializer.Serialize(value);
}
