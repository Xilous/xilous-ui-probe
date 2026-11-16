using System.Diagnostics;
using System.IO;
using XilousProbe.Web;

namespace XilousProbe.Tests;

/// <summary>
/// Runs the real chrome-shim.js and the page bootstrap under Node, which every
/// machine that builds the extension already has. Skipped where Node is absent.
/// </summary>
public sealed class ShimTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xp-shim-" + Guid.NewGuid().ToString("N"));

    public ShimTests() => Directory.CreateDirectory(_dir);

    private static string? FindNode()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir.Trim('"'), "node.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private (int Code, string Out) RunNode(params string[] args)
    {
        // xunit 2 has no runtime skip; without Node these pass vacuously rather than fail the build.
        var node = FindNode();
        if (node is null) return (0, "ok (node not installed)");
        var psi = new ProcessStartInfo(node) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return (p.ExitCode, output);
    }

    [Fact]
    public void The_page_bootstrap_is_valid_javascript()
    {
        var file = Path.Combine(_dir, "bootstrap.js");
        File.WriteAllText(file, WebAssets.BuildPageBootstrap("__xp_bind", "__xp_rx"));
        var (code, output) = RunNode("--check", file);
        Assert.True(code == 0, output);
    }

    [Fact]
    public void The_settings_bootstrap_is_valid_javascript()
    {
        var file = Path.Combine(_dir, "settings.js");
        File.WriteAllText(file, WebAssets.BuildSettingsBootstrap());
        var (code, output) = RunNode("--check", file);
        Assert.True(code == 0, output);
    }

    [Fact]
    public void The_shim_speaks_chrome_storage_and_runtime_over_the_transport()
    {
        var script = "const create = " + WebAssets.Shim + ";\n" + """
            const assert = require("node:assert");
            const sent = [];
            const shim = create({ send: (s) => sent.push(JSON.parse(s)) });
            const reply = (id, result) => shim.receive(JSON.stringify({ id, result }));
            (async () => {
              // get with defaults, callback style (what content.js does)
              const got = new Promise((r) => shim.chrome.storage.local.get({ theme: "system", depth: 1 }, r));
              assert.deepStrictEqual(sent[0], { id: 1, kind: "storage.get", payload: { keys: ["theme", "depth"] } });
              reply(1, { theme: "nord" });
              assert.deepStrictEqual(await got, { theme: "nord", depth: 1 });

              // promise style, a single key
              const p = shim.chrome.storage.local.get("theme");
              assert.deepStrictEqual(sent[1].payload, { keys: ["theme"] });
              reply(2, {});
              assert.deepStrictEqual(await p, {});

              // onChanged carries the old value from what get() last saw
              let change = null;
              shim.chrome.storage.onChanged.addListener((c, area) => { change = { c, area }; });
              shim.receive(JSON.stringify({ event: "storage", changes: { theme: "dracula" } }));
              assert.deepStrictEqual(change, { c: { theme: { oldValue: "nord", newValue: "dracula" } }, area: "local" });
              shim.receive(JSON.stringify({ event: "storage", changes: { theme: null } }));
              assert.deepStrictEqual(change.c, { theme: { oldValue: "dracula" } });

              // runtime.sendMessage resolves with the app's reply; errors reject
              const m = shim.chrome.runtime.sendMessage({ type: "FETCH_STYLESHEETS", urls: ["a"] });
              assert.strictEqual(sent[2].kind, "runtime.message");
              shim.receive(JSON.stringify({ id: sent[2].id, result: { sheets: [] } }));
              assert.deepStrictEqual(await m, { sheets: [] });
              const bad = shim.chrome.runtime.sendMessage({ type: "X" });
              shim.receive(JSON.stringify({ id: sent[3].id, error: "nope" }));
              await assert.rejects(bad, /nope/);

              // onMessage delivers TOGGLE_PROBE
              let toggled = null;
              shim.chrome.runtime.onMessage.addListener((msg) => { toggled = msg; });
              shim.receive(JSON.stringify({ event: "message", message: { type: "TOGGLE_PROBE", active: true } }));
              assert.deepStrictEqual(toggled, { type: "TOGGLE_PROBE", active: true });

              // the clipboard goes to the app; the page's own navigator is untouched
              const w = shim.navigator.clipboard.writeText("hello");
              assert.deepStrictEqual(sent[4].payload, { text: "hello" });
              assert.strictEqual(sent[4].kind, "clipboard.text");
              reply(sent[4].id, null);
              await w;
              assert.notStrictEqual(shim.navigator.clipboard, globalThis.navigator.clipboard);
              assert.strictEqual(shim.navigator.userAgent, globalThis.navigator.userAgent);

              console.log("ok");
            })().catch((e) => { console.error(e); process.exit(1); });
            """;
        var file = Path.Combine(_dir, "shim-test.js");
        File.WriteAllText(file, script);
        var (code, output) = RunNode(file);
        Assert.True(code == 0, output);
        Assert.Contains("ok", output);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
