// Edit Mode integration suite — the real content.js, in a real browser.
//
// Every other test in this repo checks something *about* the source: the pure
// sweeps run a transcribed copy, edit-audit reads the file as text, tokens.mjs
// parses the CSS. None of them execute the shipped content script. That gap is
// not academic — five bugs shipped through it, and four of them were real
// browser behaviours no hand-written stub would have reproduced:
//
//   · CSS Nesting gives every CSSStyleRule a cssRules list, so the collector's
//     "is this a group rule?" test matched everything and read nothing.
//   · Chrome keeps an empty attribute node after the first removeAttribute,
//     so every reset left a style="" behind.
//
// A stub would have encoded the same wrong assumptions and passed. So this file
// drives an actual Chromium over the DevTools Protocol, loads the harness, and
// asserts against computed styles the browser really produced.
//
// Zero dependencies, deliberately — the repo has none and should keep none.
// Node 24 ships WebSocket and fetch globally, which is the whole client.
//
// Run: node test/cdp.mjs   (exit 1 on any failure)
//      node test/cdp.mjs --headful   to watch it happen

import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import { existsSync, rmSync } from "node:fs";
import { extname, join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { tmpdir } from "node:os";

const ROOT = dirname(dirname(fileURLToPath(import.meta.url)));
const HEADFUL = process.argv.includes("--headful");
const PORT = 8791;
const DEBUG_PORT = 9333;
const PROFILE = join(tmpdir(), "ccp-cdp-profile");

// browser.kill() returns before Chrome has finished letting go of its profile,
// so removing the directory straight afterwards loses a race often enough to
// turn a green run red — in the teardown, after every assertion has already
// passed. rmSync retries ENOTEMPTY and EBUSY when asked to; it just has to be
// asked. Two seconds of patience, and only when it is actually needed.
const RM_PROFILE = { recursive: true, force: true, maxRetries: 20, retryDelay: 100 };

// Any Chromium will do — the extension targets Chrome but the protocol is the
// same everywhere, and a contributor may well not have Chrome installed. The
// list is ordered by "most likely to be the browser this extension is
// developed against".
const BROWSERS = [
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
  `${process.env.HOME}/Applications/Google Chrome.app/Contents/MacOS/Google Chrome`,
  "/Applications/Chromium.app/Contents/MacOS/Chromium",
  "/Applications/Google Chrome Canary.app/Contents/MacOS/Google Chrome Canary",
  "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
  "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
  "/Applications/Aside.app/Contents/MacOS/Aside",
  "/usr/bin/google-chrome",
  "/usr/bin/chromium",
  "/usr/bin/chromium-browser",
];

function findBrowser() {
  if (process.env.CCP_CHROME) return process.env.CCP_CHROME;
  const found = BROWSERS.find((p) => existsSync(p));
  if (!found) {
    console.error(
      "No Chromium-based browser found. Install Chrome, or point CCP_CHROME at a binary:\n" +
      "  CCP_CHROME='/path/to/browser' node test/cdp.mjs"
    );
    process.exit(1);
  }
  return found;
}

// ===== static server =====
// file:// cannot be used: a file:// stylesheet throws on .cssRules exactly like
// a cross-origin one, which would disable the token layer under test.

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
};

function serve() {
  const server = createServer(async (req, res) => {
    const path = decodeURIComponent(new URL(req.url, "http://x").pathname);
    try {
      const body = await readFile(join(ROOT, path));
      const headers = {
        "content-type": MIME[extname(path)] || "application/octet-stream",
        // The harness rewrites stylesheet URLs to dodge caching; belt and
        // braces, so a stale content.css can never make a run lie.
        "cache-control": "no-store",
      };
      // The whole point of the remote-sheet fixture: a cross-origin <link> is
      // refused, while a fetch of the same URL is allowed. That is the exact
      // asymmetry the extension lives with — the page cannot read a CDN
      // stylesheet, the service worker's host permissions can fetch it — and
      // it is reproduced here by answering on Sec-Fetch-Dest rather than by
      // running a second server.
      if (req.headers["sec-fetch-dest"] !== "style") {
        headers["access-control-allow-origin"] = "*";
      }
      res.writeHead(200, headers);
      res.end(body);
    } catch {
      res.writeHead(404).end("not found");
    }
  });
  return new Promise((resolve) => server.listen(PORT, "127.0.0.1", () => resolve(server)));
}

// ===== CDP client =====

let nextId = 1;
const pending = new Map();

function connect(url) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    ws.addEventListener("open", () => resolve(ws));
    ws.addEventListener("error", reject);
    ws.addEventListener("message", (event) => {
      const msg = JSON.parse(event.data);
      const waiter = pending.get(msg.id);
      if (!waiter) return;
      pending.delete(msg.id);
      if (msg.error) waiter.reject(new Error(msg.error.message));
      else waiter.resolve(msg.result);
    });
  });
}

function send(ws, method, params = {}) {
  const id = nextId++;
  ws.send(JSON.stringify({ id, method, params }));
  return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
}

// Evaluate in the page and hand back the value. Errors inside the page become
// errors here rather than an undefined that quietly passes an assertion.
async function evaluate(ws, expression) {
  const result = await send(ws, "Runtime.evaluate", {
    expression: `(async () => { ${expression} })()`,
    awaitPromise: true,
    returnByValue: true,
  });
  if (result.exceptionDetails) {
    const e = result.exceptionDetails;
    throw new Error(e.exception?.description || e.text || "page threw");
  }
  return result.result.value;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitFor(fn, what, timeout = 5000) {
  const started = Date.now();
  for (;;) {
    if (await fn()) return;
    if (Date.now() - started > timeout) throw new Error(`timed out waiting for ${what}`);
    await sleep(50);
  }
}

// ===== harness driving =====
// Written as page-side helpers so each case reads as the user's actions rather
// than as DOM plumbing.

const HELPERS = `
  window.__t = {
    panel: () => document.getElementById("ccp-edit-panel"),
    row: (name) => document.querySelector('#ccp-edit-panel .ccp-edit-row[data-control="' + name + '"]'),
    probeOn: () => window.__ccpHarness.setState(true),
    probeOff: () => window.__ccpHarness.setState(false),
    select: (sel, dx = 5, dy = 5) => {
      const el = document.querySelector(sel);
      // Selection resolves its target with elementFromPoint, so a click aimed
      // below the fold lands on nothing. The headless window is smaller than
      // this fixture, so anything past the first screen has to be brought into
      // view before its rect means anything.
      el.scrollIntoView({ block: "center", inline: "nearest" });
      const r = el.getBoundingClientRect();
      el.dispatchEvent(new MouseEvent("click", { bubbles: true, clientX: r.left + dx, clientY: r.top + dy }));
      return el;
    },
    edit: () => document.querySelector("#ccp-toolbar .ccp-bar button.ccp-icon-btn").click(),
    esc: () => document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true })),
    undo: (shift) => document.dispatchEvent(
      new KeyboardEvent("keydown", { key: "z", metaKey: true, shiftKey: !!shift, bubbles: true })),
    // Two stepping mechanisms, one gesture: the classic rows carry ‹ ›
    // buttons, the typography grid steps its cells on the wheel.
    step: (name, dir) => {
      const row = window.__t.row(name);
      const tok = row.querySelector(".ccp-edit-tok");
      if (tok) {
        tok.querySelectorAll("button")[dir > 0 ? 1 : 0].click();
        return;
      }
      row.dispatchEvent(new WheelEvent("wheel", {
        deltaY: dir > 0 ? -100 : 100, bubbles: true, cancelable: true,
      }));
    },
    // ...and two reset affordances: the dot on classic rows, the micro-label
    // on grid cells.
    resetProp: (name) => {
      const row = window.__t.row(name);
      (row.querySelector(".ccp-edit-dot") || row.querySelector(".ccp-type-k")).click();
    },
    type: (name, value) => {
      const input = window.__t.row(name).querySelector(".ccp-edit-input");
      input.focus();
      input.value = String(value);
      input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    },
    pop: () => document.getElementById("ccp-color-picker"),
    swatch: (name) => window.__t.row(name).querySelector(".ccp-edit-swatch").click(),
    css: (sel, prop) => getComputedStyle(document.querySelector(sel)).getPropertyValue(prop).trim(),
    // The edit guards sit capture-phase on document and swallow these five, so
    // whether one arrives is the direct measurement of "is the page still
    // inert" — which is what a half-finished teardown leaves behind.
    reaches: (sel, type) => {
      const el = document.querySelector(sel);
      let got = false;
      const spy = () => { got = true; };
      el.addEventListener(type, spy);
      el.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true }));
      el.removeEventListener(type, spy);
      return got;
    },
    // A copy button holds its success state — disabled — for 1500ms, so a case
    // that copies twice would silently measure one click and one nothing.
    // Skipping the wait rather than serving it: this is exactly what the
    // button's own timeout does, and ten of them would add fifteen seconds to
    // the suite to prove a delay nobody is testing.
    press: async (sel) => {
      const btn = document.querySelector(sel);
      if (!btn) throw new Error("no button at " + sel);
      if (btn.dataset.origHtml) btn.innerHTML = btn.dataset.origHtml;
      btn.disabled = false;
      let text = null;
      const real = navigator.clipboard.writeText.bind(navigator.clipboard);
      navigator.clipboard.writeText = (t) => { text = t; return Promise.resolve(); };
      btn.click();
      await new Promise((r) => setTimeout(r, 30));
      navigator.clipboard.writeText = real;
      return text;
    },
    copy: () => window.__t.press("#ccp-edit-panel .ccp-edit-copy"),
    // The toolbar's first labelled button is Copy Code.
    copyCode: () => window.__t.press("#ccp-toolbar .ccp-bar button"),
    // The storage stub fires the content script's onChanged listener
    // synchronously, so a pref written here is in force by the time the click
    // lands — no wait, and no polling for one.
    prefs: (obj) => chrome.storage.local.set(obj),
    // The stub is backed by localStorage, so preferences outlive a reload the
    // way the real ones outlive a tab. Every copy case starts from the shipped
    // defaults, or it would be reading the previous case's settings.
    copyDefaults: () => window.__t.prefs({
      copySource: "on", copyComponent: "on", copyPage: "on", copyAnchor: "on",
      copyHandlers: "on", copySelector: "on", copyPosition: "on", copyRepeated: "on",
      copyText: "on", copyLayout: "off", copyStyles: "off", copyProps: "off",
      copyHtml: "root", copyDepth: "3", copyHtmlFallback: "on", copyFence: "on",
    }),
    // The pointer's own fields, told apart from their continuations by what
    // follows the hash — "# key: " opens one, "#   " continues it.
    keys: (payload) => (payload || "").split("\\n")
      .map((l) => l.match(/^# ([a-z]+): /)).filter(Boolean).map((m) => m[1]),
    // Everything after the header: the HTML block, fences stripped.
    block: (payload) => (payload || "").split("\\n")
      .filter((l) => !l.startsWith("#") && !l.startsWith("\`\`\`") && l.trim())
      .join("\\n"),
  };
  return true;
`;

async function loadHarness(ws) {
  await send(ws, "Page.navigate", { url: `http://127.0.0.1:${PORT}/test/edit-harness.html` });
  await waitFor(
    async () => await evaluate(ws, "return !!(window.__ccpHarness && window.__ccpProbe)"),
    "the harness to boot"
  );
  // The harness re-fetches its stylesheets with a cache-busting query, so the
  // token collector must not run until they have parsed — otherwise it reads
  // an empty document.styleSheets and every token case fails for the wrong
  // reason. This waits for the real signal rather than guessing at a delay.
  await waitFor(
    async () => await evaluate(ws, "return window.__ccpProbe.stylesheetsReady()"),
    "stylesheets to parse"
  );
  await evaluate(ws, HELPERS);
}

// ===== harness =====

const rows = [];
let failures = 0;

async function check(name, fn) {
  const errs = [];
  try {
    await fn((msg) => errs.push(msg));
  } catch (err) {
    errs.push(String(err.message || err));
  }
  failures += errs.length;
  rows.push({ case: name, result: errs.length ? "FAIL" : "ok", detail: errs.slice(0, 3).join("; ") });
}

// ===== the run =====

const server = await serve();
const browserPath = findBrowser();
rmSync(PROFILE, RM_PROFILE);

const browser = spawn(browserPath, [
  ...(HEADFUL ? [] : ["--headless=new"]),
  `--remote-debugging-port=${DEBUG_PORT}`,
  `--user-data-dir=${PROFILE}`,
  "--no-first-run",
  "--no-default-browser-check",
  "--disable-extensions",
], { stdio: "ignore" });

let ws;
try {
  let version;
  await waitFor(async () => {
    try {
      version = await (await fetch(`http://127.0.0.1:${DEBUG_PORT}/json/version`)).json();
      return true;
    } catch { return false; }
  }, "the browser to expose CDP", 15000);

  const targets = await (await fetch(`http://127.0.0.1:${DEBUG_PORT}/json/list`)).json();
  const page = targets.find((t) => t.type === "page");
  ws = await connect(page.webSocketDebuggerUrl);
  await send(ws, "Page.enable");
  await send(ws, "Runtime.enable");

  console.log(`${browserPath.split("/").pop()} — ${version.Browser}\n`);

  // ===== 1. Regression: the token layer was entirely dead =====
  // CSS Nesting gave every CSSStyleRule a cssRules list, so the collector
  // treated every ordinary rule as a group, recursed into an empty list, and
  // never read a single declaration. Nothing failed; the feature just silently
  // did nothing. The index size is the direct measurement.

  await loadHarness(ws);

  await check("token index is populated", async (fail) => {
    await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".card h2");
      window.__t.edit();
      return true;
    `);
    const size = await evaluate(ws, "return window.__ccpProbe.tokenIndexSize()");
    if (!size || size.rules === 0) fail(`collector read ${size && size.rules} rules from the page`);
    if (!size || size.varNames === 0) fail(`collector found ${size && size.varNames} custom properties`);
  });

  await check("palette offers the page's own tokens", async (fail) => {
    const names = await evaluate(ws, `
      window.__t.row("color") || window.__t.row("background-color");
      const row = document.querySelector('#ccp-edit-panel .ccp-edit-row[data-control="color"]')
        || document.querySelector('#ccp-edit-panel .ccp-edit-row[data-control="background-color"]');
      row.querySelector(".ccp-edit-swatch").click();
      const pop = document.querySelector(".ccp-edit-pop");
      return [...pop.querySelectorAll(".ccp-edit-pal")].map((b) => b.title.split(" — ")[0]);
    `);
    if (!names.length) fail("palette was empty");
    if (!names.includes("--terra")) fail(`page tokens missing; got ${JSON.stringify(names.slice(0, 5))}`);
  });

  // ===== 2. Regression: our own tokens leaked into the page's palette =====
  // tokens.css and content.css ride along on every page as content scripts, so
  // the collector was offering --ccp-accent as a fill for the user's elements.

  await check("palette excludes our own chrome tokens", async (fail) => {
    const leaked = await evaluate(ws, `
      const pop = document.querySelector(".ccp-edit-pop");
      return [...pop.querySelectorAll(".ccp-edit-pal")]
        .map((b) => b.title.split(" — ")[0]).filter((n) => n.startsWith("--ccp-"));
    `);
    if (leaked.length) fail(`leaked ${JSON.stringify(leaked.slice(0, 4))}`);
    await evaluate(ws, "window.__t.esc(); return true;"); // close the picker
  });

  // ===== 3. Regression: a token step was recorded but never applied =====
  // applyEditValue read "the current value" back out of the registry, but every
  // caller stored the new value first — so it compared the new value with
  // itself, concluded nothing had changed, and skipped the write. The delta
  // said the step happened; the page disagreed.

  await check("stepping a token moves the page", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".card h2");
      window.__t.edit();
      const before = window.__t.css(".card h2", "font-size");
      window.__t.step("font-size", 1);
      const after = window.__t.css(".card h2", "font-size");
      return { before, after };
    `);
    if (seen.before !== "18px") fail(`expected to start at 18px, got ${seen.before}`);
    if (seen.after !== "22px") fail(`step wrote ${seen.after}, not 22px — recorded but not applied`);
  });

  // ===== 4. Regression: undo walked to the beginning instead of stepping =====
  // The registry pins a property's "before" at first touch, which is right for
  // the delta. The undo entry reused it, so every entry meant "back to the
  // original" and two steps collapsed into one undo.

  await check("undo gives back one change at a time", async (fail) => {
    const seen = await evaluate(ws, `
      window.__t.step("font-size", 1);          // 22 -> 28
      const twoSteps = window.__t.css(".card h2", "font-size");
      window.__t.undo();
      const once = window.__t.css(".card h2", "font-size");
      window.__t.undo();
      const twice = window.__t.css(".card h2", "font-size");
      window.__t.undo(true);
      const redone = window.__t.css(".card h2", "font-size");
      return { twoSteps, once, twice, redone };
    `);
    if (seen.twoSteps !== "28px") fail(`two steps reached ${seen.twoSteps}, not 28px`);
    if (seen.once !== "22px") fail(`one undo landed on ${seen.once}, not 22px — jumped past a step`);
    if (seen.twice !== "18px") fail(`two undos landed on ${seen.twice}, not 18px`);
    if (seen.redone !== "22px") fail(`redo landed on ${seen.redone}, not 22px`);
  });

  // ===== 5. Regression: every reset left a style="" behind =====
  // Once an inline block has been written through CSSOM, Chrome's first
  // removeAttribute empties it but leaves the attribute node — so an element
  // the tool had finished with still carried a mark of having been touched.

  await check("every path back leaves no residue", async (fail) => {
    for (const [label, undoExpr] of [
      ["undo", `window.__t.undo();`],
      ["dirty dot", `window.__t.resetProp("font-size");`],
      ["reset all", `document.querySelector("#ccp-edit-panel .ccp-edit-resetall").click();`],
    ]) {
      // A fresh page each time: this asserts "one edit, one way back, nothing
      // left" and would otherwise inherit whatever the previous case left in
      // the undo stack.
      await loadHarness(ws);
      const seen = await evaluate(ws, `
        window.__t.probeOn();
        window.__t.select(".card h2");
        window.__t.edit();
        window.__t.step("font-size", 1);
        ${undoExpr}
        const el = document.querySelector(".card h2");
        return { style: el.getAttribute("style"), size: window.__t.css(".card h2", "font-size") };
      `);
      if (seen.style !== null) fail(`${label} left style=${JSON.stringify(seen.style)}`);
      if (seen.size !== "18px") fail(`${label} left the size at ${seen.size}`);
    }
  });

  // ===== Beyond the five: the escalation the dead index also disabled =====
  // findWinningDeclaration reads the same index, so while it was empty an edit
  // to an !important-covered property silently did nothing.

  await check("an !important page rule is matched, not lost to", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".card.stubborn", 3, 60);
      window.__t.edit();
      const before = window.__t.css(".card.stubborn", "padding-top");
      window.__t.type("padding", 44);
      // Typing commits twice — once on Enter, once on the blur that follows.
      // The second commit is what used to strip the escalation, so asserting
      // after it is the whole point.
      window.__t.type("padding", 44);
      const el = document.querySelector(".card.stubborn");
      return { before, after: window.__t.css(".card.stubborn", "padding-top"),
               priority: el.style.getPropertyPriority("padding"),
               styleAttr: el.getAttribute("style") };
    `);
    if (seen.before !== "20px") fail(`fixture changed: expected 20px, got ${seen.before}`);
    if (seen.after !== "44px") fail(`edit was overridden by the page's !important (got ${seen.after})`);
    if (seen.priority !== "important") {
      fail(`escalation lost — style=${JSON.stringify(seen.styleAttr)}`);
    }
  });

  // ===== The loop the whole feature exists for =====

  await check("the delta block reports what changed", async (fail) => {
    const block = await evaluate(ws, `
      await new Promise(r => setTimeout(r, 20));
      return await window.__t.copy();
    `);
    if (!block) return fail("copy produced nothing");
    if (!block.includes("# edits:")) fail("no edits section");
    if (!/padding[^\\n]*20px[^\\n]*→[^\\n]*44px/.test(block)) {
      fail(`padding delta missing from block: ${JSON.stringify(block.slice(0, 200))}`);
    }
  });

  // ===== Regression: switching off left Edit Mode running =====
  // deactivate() nulled selectedElement itself instead of going through
  // deselectElement(), so it never reached exitEditMode(). The panel and the
  // picker stayed on screen, `editing` stayed true, and the five capture-phase
  // pointer guards stayed on document — leaving the user's page inert until a
  // reload and the next activation unable to select anything.

  await check("switching off dismantles Edit Mode", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".card h2");
      window.__t.edit();
      window.__t.step("font-size", 1);
      window.__t.swatch("background-color");
      const hadPicker = !!window.__t.pop();
      window.__t.probeOff();
      const el = document.querySelector(".card h2");
      return {
        hadPicker,
        panel: !!window.__t.panel(),
        picker: !!window.__t.pop(),
        editingClass: document.documentElement.classList.contains("ccp-editing"),
        style: el.getAttribute("style"),
        size: window.__t.css(".card h2", "font-size"),
        // The page has to be live again, not just uncluttered.
        mousedown: window.__t.reaches(".card h2", "mousedown"),
        dblclick: window.__t.reaches(".card h2", "dblclick"),
      };
    `);
    if (!seen.hadPicker) fail("fixture never opened a picker to begin with");
    if (seen.panel) fail("the edit panel outlived the switch-off");
    if (seen.picker) fail("the colour picker outlived the switch-off");
    if (seen.editingClass) fail("ccp-editing is still on <html>");
    if (!seen.mousedown) fail("the page is still inert — mousedown never arrived");
    if (!seen.dblclick) fail("the page is still inert — dblclick never arrived");
    if (seen.style !== null) fail(`the page kept style=${JSON.stringify(seen.style)}`);
    if (seen.size !== "18px") fail(`the edit survived at ${seen.size}`);
  });

  // Switching off used to poison the next activation too: `editing` stayed true,
  // so onClick swallowed the click and then returned before selecting anything.

  await check("switching back on can still select", async (fail) => {
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".card h2");
      return { toolbar: !!document.getElementById("ccp-toolbar") };
    `);
    if (!seen.toolbar) fail("nothing was selectable after an off/on cycle");
  });

  // ===== Regression: the colour picker had no visible way out =====
  // It was a child of the panel, painted over the rows it was tuning, and the
  // only exit was an Escape nothing advertised — clicking the swatch again just
  // rebuilt it identically, so the obvious gesture looked broken.

  await check("the picker is its own surface, with a way out", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".card h2");
      window.__t.edit();

      window.__t.swatch("background-color");
      const pop = window.__t.pop();
      const detached = !!pop && !window.__t.panel().contains(pop);
      const closeBtn = !!pop && !!pop.querySelector(".ccp-edit-popclose");

      // Re-clicking the swatch closes rather than silently rebuilding.
      window.__t.swatch("background-color");
      const afterSecondClick = !!window.__t.pop();

      // ...and a third click brings it back, so the toggle goes both ways.
      window.__t.swatch("background-color");
      const reopened = !!window.__t.pop();
      window.__t.pop().querySelector(".ccp-edit-popclose").click();
      const afterCloseBtn = !!window.__t.pop();

      // Escape still steps out of the picker before it leaves Edit Mode.
      window.__t.swatch("background-color");
      window.__t.esc();
      return {
        detached, closeBtn, afterSecondClick, reopened, afterCloseBtn,
        pickerAfterEsc: !!window.__t.pop(),
        panelAfterEsc: !!window.__t.panel(),
      };
    `);
    if (!seen.detached) fail("the picker is still nested inside the panel");
    if (!seen.closeBtn) fail("the picker has no close button");
    if (seen.afterSecondClick) fail("clicking the open swatch did not close the picker");
    if (!seen.reopened) fail("the swatch stopped reopening the picker");
    if (seen.afterCloseBtn) fail("the close button did not close the picker");
    if (seen.pickerAfterEsc) fail("Escape did not close the picker");
    if (!seen.panelAfterEsc) fail("Escape closed the panel too — the ladder collapsed");
  });

  // ===== Values the pure parsers cannot read =====
  // parseCssLength took px/rem/em and parseCssColor took hex/rgb, so a Tailwind
  // v4 page — calc() spacing, oklch() palette — reported no tokens at all while
  // looking perfectly healthy. resolveLength/resolveColor hand those to the
  // browser instead. Painting the colour and reading the pixel is the only
  // thing that resolves oklch(): Chrome returns it unchanged from both computed
  // style and canvas fillStyle.

  await check("a calc() scale is detected and steppable", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".v4-card", 4, 4);
      window.__t.edit();
      const row = window.__t.row("padding");
      const tok = row && row.querySelector(".ccp-edit-tok");
      const before = window.__t.css(".v4-card", "padding-top");
      if (tok) tok.querySelectorAll("button")[1].click();   // step up one rung
      return {
        stepper: Boolean(tok),
        step: tok ? tok.querySelector("b").textContent : null,
        before,
        after: window.__t.css(".v4-card", "padding-top"),
      };
    `);
    // --pad-4 is calc(var(--spacing) * 4) = 16px. Nothing here is a bare length,
    // so the old parser saw no family and offered no stepper at all.
    if (seen.before !== "16px") fail(`fixture changed: --pad-4 is ${seen.before}, want 16px`);
    if (!seen.stepper) return fail("no stepper on a padding set from a calc() token");
    if (seen.step !== "4") fail(`stepper reads "${seen.step}", want the rung named 4`);
    if (seen.after !== "32px") fail(`stepping up gave ${seen.after}, want 32px (--pad-8)`);
  });

  await check("oklch and friends reach the palette", async (fail) => {
    const seen = await evaluate(ws, `
      window.__t.swatch("background-color");
      const pals = [...window.__t.pop().querySelectorAll(".ccp-edit-pal")];
      return Object.fromEntries(pals.map((b) => {
        const [name, hex] = b.title.split(" — ");
        return [name, hex];
      }));
    `);
    // Each of these is a syntax parseCssColor refuses outright, so each was
    // silently absent from every palette before the rasteriser.
    for (const name of ["--brand-500", "--brand-700", "--surface-raised", "--edge"]) {
      if (!(name in seen)) {
        fail(`${name} missing from the palette; got ${JSON.stringify(Object.keys(seen).slice(0, 14))}`);
      }
    }
    // rebeccapurple is exactly #663399 — a keyword, and a check that the
    // rasteriser is reporting the real colour rather than a plausible one.
    if (seen["--edge"] && seen["--edge"].toLowerCase() !== "#663399") {
      fail(`--edge resolved to ${seen["--edge"]}, want #663399`);
    }
    await evaluate(ws, "window.__t.esc(); return true;"); // close the picker
  });

  // ===== A shorthand utility class is still a scale =====
  // CSSOM lists `padding: 12px` as its four longhands, so `.p-3 { padding: … }`
  // is indexed under padding-top and never under padding — while the linked
  // padding control asks about padding. The two could not meet, so no
  // shorthand-setting utility class was ever detected or ever formed a family.
  // That is every Tailwind spacing class. `.text-lg` worked throughout, because
  // font-size is already a longhand, and that is what made a missing edge look
  // like partial support.

  await check("a spacing utility class is detected and steppable", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".p-3-only", 3, 3);
      window.__t.edit();
      const tok = window.__t.row("padding").querySelector(".ccp-edit-tok");
      const before = window.__t.css(".p-3-only", "padding-top");
      if (tok) tok.querySelectorAll("button")[1].click();   // one rung up
      return {
        step: tok ? tok.querySelector("b").textContent : null,
        before,
        after: window.__t.css(".p-3-only", "padding-top"),
        cls: document.querySelector(".p-3-only").className,
      };
    `);
    if (seen.before !== "12px") fail(`fixture changed: .p-3 is ${seen.before}, want 12px`);
    if (seen.step === null) return fail("no stepper on padding set by a utility class");
    if (seen.step !== "3") fail(`stepper reads "${seen.step}", want the rung named 3`);
    // .p-4 is the next rung at 16px. The swap is applied and then verified by
    // the existing escalation path, so this also proves the class actually won.
    if (seen.after !== "16px") fail(`stepping up gave ${seen.after}, want 16px (.p-4)`);
    if (!seen.cls.includes("p-4")) fail(`the class was not swapped: ${seen.cls}`);
  });

  // ===== A stylesheet the page is forbidden to read =====
  // A cross-origin <link> without CORS throws on .cssRules, and a content
  // script's own fetch is refused the same way, so the service worker's host
  // permissions are the only way through. The harness stands in for the worker
  // (nothing inside a page can grant itself host permissions), so what this
  // proves is the half that lives in content.js: the sheet is noticed as
  // blocked, asked for, parsed, and folded back in.
  //
  // What it buys is narrower than it first appears, and the first version of
  // this case was wrong about it. Custom properties from an unreadable sheet
  // already reach the element — the browser applies them whether or not script
  // may read the rule — so collectElementTokens finds them without any fetch.
  // A *class* rule has no such shadow: nothing about the element reports that
  // .remote-pad-2 means 14px. That, and the declaration text a var() must be
  // read out of, is what is actually recovered here.

  await check("a blocked stylesheet's custom properties need no fetch", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      const remote = [...document.querySelectorAll('link[rel="stylesheet"]')]
        .find((l) => l.href.includes("edit-harness-remote"));
      let readable = null;
      try { readable = Boolean(remote.sheet && remote.sheet.cssRules); }
      catch { readable = false; }
      return {
        linked: Boolean(remote),
        readable,
        resolves: getComputedStyle(document.documentElement)
          .getPropertyValue("--remote-brand").trim(),
      };
    `);
    if (!seen.linked) return fail("fixture broken: the remote stylesheet was never linked");
    // If CORS ever starts allowing this, every case below is testing nothing.
    if (seen.readable) fail("fixture broken: the page CAN read the remote sheet");
    if (seen.resolves !== "#b5179e") {
      fail(`--remote-brand resolves to ${JSON.stringify(seen.resolves)} — the sheet did not apply`);
    }
  });

  await check("a blocked stylesheet's utility classes are fetched and folded in", async (fail) => {
    // A fresh page: the fetched-sheet cache survives Edit Mode entries, so a
    // previous case having already paid for the round trip would make "before"
    // meaningless.
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".remote-pad-2", 4, 4);
      window.__t.edit();
      const stepper = () => {
        const row = window.__t.row("padding");
        const tok = row && row.querySelector(".ccp-edit-tok");
        return tok ? tok.querySelector("b").textContent : null;
      };
      const before = stepper();
      // The top-up is fired from enterEditMode and not awaited, so give the
      // round trip a moment; it re-renders the rows when it lands.
      await new Promise((r) => setTimeout(r, 500));
      return { before, after: stepper(), padding: window.__t.css(".remote-pad-2", "padding-top") };
    `);
    if (seen.padding !== "14px") fail(`fixture changed: .remote-pad-2 is ${seen.padding}, want 14px`);
    if (seen.before !== null) {
      fail(`a class rule from an unreadable sheet was known before the fetch (step "${seen.before}")`);
    }
    if (seen.after === null) fail("the fetched class rule never produced a stepper");
  });

  // ===== A colour delta can name the token it started on =====
  // detectPropertyToken opened with a length parse and returned null for
  // anything else, so no colour ever claimed a token and the before side of a
  // colour edit was always a bare hex — the one side of the block an agent uses
  // to find the declaration to change. The colour branch is deliberately the
  // same shape as the length one: only a declaration that actually names a
  // token may claim it, so a colour that merely equals --ink is still reported
  // as a hex.

  await check("a colour delta names the token, and only when it should", async (fail) => {
    await loadHarness(ws);
    const block = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".themed-title");   // color: var(--ink), which .themed overrides
      window.__t.edit();
      window.__t.swatch("color");
      const hexIn = window.__t.pop().querySelector(".ccp-edit-hexin");
      hexIn.focus();
      hexIn.value = "#112233";
      hexIn.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
      window.__t.esc();
      await new Promise((r) => setTimeout(r, 20));
      return await window.__t.copy();
    `);
    if (!block) return fail("copy produced nothing");
    const line = block.split("\n").find((l) => l.includes("color:"));
    if (!line) return fail(`no colour line in the block: ${JSON.stringify(block.slice(0, 220))}`);
    // .themed sets --ink to #f5f3ee, so the token *and* the value it had there.
    if (!line.includes("--ink")) fail(`before side lost the token: ${line.trim()}`);
    if (!line.includes("#f5f3ee")) fail(`before side lost the value: ${line.trim()}`);
    if (!line.includes("#112233")) fail(`after side wrong: ${line.trim()}`);
  });

  await check("a colour that merely equals a token claims nothing", async (fail) => {
    await loadHarness(ws);
    const block = await evaluate(ws, `
      window.__t.probeOn();
      // .card p is color: #57544c written as a literal — the same value as
      // --ink-dim, but the declaration does not name it.
      window.__t.select(".card p");
      window.__t.edit();
      window.__t.swatch("color");
      const hexIn = window.__t.pop().querySelector(".ccp-edit-hexin");
      hexIn.focus();
      hexIn.value = "#0a0a0a";
      hexIn.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
      window.__t.esc();
      await new Promise((r) => setTimeout(r, 20));
      return { block: await window.__t.copy(), dim: getComputedStyle(document.documentElement).getPropertyValue("--ink-dim").trim() };
    `);
    if (block.dim !== "#57544c") fail(`fixture changed: --ink-dim is ${block.dim}`);
    const line = (block.block || "").split("\n").find((l) => l.includes("color:"));
    if (!line) return fail("no colour line in the block");
    if (line.includes("--ink-dim")) {
      fail(`claimed a token the declaration never named: ${line.trim()}`);
    }
    if (!line.includes("#57544c")) fail(`before side should be the plain hex: ${line.trim()}`);
  });

  // ===== Discovery no longer depends on being able to read the stylesheet =====
  // Tokens used to be found by walking document.styleSheets for names. Anything
  // the walk could not reach was therefore invisible, however well it resolved
  // on the element: a cross-origin sheet, a shadow root, or — the case staged
  // here, because it is the one a fixture can stage honestly — an @import,
  // which is neither a member of document.styleSheets nor reachable through
  // the recursion, since CSSImportRule exposes .styleSheet rather than
  // .cssRules. Asking the element instead makes the source irrelevant.

  await check("tokens the stylesheet walk cannot reach are still offered", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".card h2");
      window.__t.edit();

      // What a stylesheet walk can see, measured from the page rather than
      // from the extension — the same argument __ccpProbe already makes.
      const reachable = new Set();
      const walk = (rules) => {
        for (const r of rules) {
          if (r.selectorText && r.style) {
            for (let i = 0; i < r.style.length; i++) {
              const p = r.style[i];
              if (p.startsWith("--")) reachable.add(p);
            }
          }
          if (r.cssRules && r.cssRules.length) walk(r.cssRules);
        }
      };
      for (const sheet of document.styleSheets) {
        try { if (sheet.cssRules) walk(sheet.cssRules); } catch { /* blocked */ }
      }

      window.__t.swatch("background-color");
      const palette = [...window.__t.pop().querySelectorAll(".ccp-edit-pal")]
        .map((b) => b.title.split(" — ")[0]);
      const step = window.__t.row("font-size").querySelector(".ccp-edit-tok");
      return {
        walkSees: [...reachable].filter((n) => n.startsWith("--imported")),
        palette,
        resolves: getComputedStyle(document.querySelector(".card h2"))
          .getPropertyValue("--imported-ink").trim(),
      };
    `);

    // The premise: these resolve on the element but the walk cannot find them.
    if (seen.resolves !== "#2b1d4a") {
      fail(`fixture broken: --imported-ink resolves to ${JSON.stringify(seen.resolves)}`);
    }
    if (seen.walkSees.length) {
      fail(`fixture broken: the walk can reach ${JSON.stringify(seen.walkSees)} — it is no longer an @import test`);
    }
    // The claim: the panel offers them anyway.
    if (!seen.palette.includes("--imported-ink")) {
      fail(`--imported-ink missing from the palette; got ${JSON.stringify(seen.palette)}`);
    }
    if (!seen.palette.includes("--imported-accent")) {
      fail("--imported-accent missing — an oklch token behind an @import");
    }
    await evaluate(ws, "window.__t.esc(); return true;");
  });

  // Scope was never the broken part — resolving a name against the selected
  // element has always respected it. This pins that, so the inversion above
  // cannot regress it by reading tokens off the document instead.

  await check("a themed subtree offers its own values", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      const palette = () => Object.fromEntries(
        [...window.__t.pop().querySelectorAll(".ccp-edit-pal")]
          .map((b) => { const [n, h] = b.title.split(" — "); return [n, h.toLowerCase()]; })
      );
      const read = (sel, dx, dy) => {
        window.__t.select(sel, dx, dy);
        window.__t.edit();
        window.__t.swatch("background-color");
        const out = palette();
        window.__t.esc();  // picker
        window.__t.esc();  // edit mode
        window.__t.esc();  // selection
        return out;
      };
      window.__t.probeOn();
      return { root: read(".card h2", 5, 5), themed: read(".themed-title", 5, 5) };
    `);

    // :root says --ink is near-black and --paper is white.
    if (seen.root["--ink"] !== "#1f1e1b") fail(`root --ink is ${seen.root["--ink"]}, want #1f1e1b`);
    if (seen.root["--paper"] !== "#ffffff") fail(`root --paper is ${seen.root["--paper"]}, want #ffffff`);

    // .themed inverts both. The old collector had one --ink in a Set and asked
    // the selected element for it, so this is the case it could not represent.
    if (seen.themed["--ink"] !== "#f5f3ee") {
      fail(`themed --ink is ${seen.themed["--ink"]}, want #f5f3ee — the theme scope was not seen`);
    }
    if (seen.themed["--paper"] !== "#16150f") {
      fail(`themed --paper is ${seen.themed["--paper"]}, want #16150f`);
    }
    // And an overridden oklch token resolves to the override, not to :root's.
    if (seen.themed["--brand-500"] === seen.root["--brand-500"]) {
      fail(`--brand-500 did not change under .themed (both ${seen.root["--brand-500"]})`);
    }
  });

  // ===== The browser fact the token layer is built on =====
  // Token discovery asks the element which custom properties are in scope on it,
  // rather than walking stylesheets to find their names. That is only possible
  // because the platform will enumerate them — and which API does so is a fact
  // about Chrome, not about this code. It is asserted here so that a browser
  // that changes its mind reports it as this line failing, rather than as
  // "tokens quietly stopped working".

  await check("the platform enumerates custom properties in scope", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      const el = document.querySelector(".card h2");
      const want = "--title-sm";   // declared on :root by the fixture

      let viaMap = null;
      if (el.computedStyleMap) {
        try {
          const names = [];
          for (const [prop] of el.computedStyleMap()) names.push(prop);
          viaMap = { total: names.length, custom: names.filter(n => n.startsWith("--")).length, has: names.includes(want) };
        } catch (e) { viaMap = { error: String(e.message || e) }; }
      }

      const cs = getComputedStyle(el);
      const csNames = Array.from(cs);
      const viaComputed = {
        total: csNames.length,
        custom: csNames.filter(n => n.startsWith("--")).length,
        has: csNames.includes(want),
      };

      return { viaMap, viaComputed, resolves: cs.getPropertyValue(want).trim(), ua: navigator.userAgent };
    `);

    // Whichever path wins, the value must still be readable by name — that is
    // what makes an enumerated name useful rather than merely present.
    if (!seen.resolves) fail(`--title-sm does not resolve on .card h2 (got ${JSON.stringify(seen.resolves)})`);

    const mapWorks = Boolean(seen.viaMap && seen.viaMap.has);
    const computedWorks = Boolean(seen.viaComputed && seen.viaComputed.has);
    if (!mapWorks && !computedWorks) {
      fail(
        "neither computedStyleMap() nor getComputedStyle() enumerated --title-sm — " +
        `map=${JSON.stringify(seen.viaMap)} computed=${JSON.stringify(seen.viaComputed)}`
      );
    }
    console.log(
      `      enumeration: computedStyleMap ${mapWorks ? "yes" : "no"}` +
      ` (${seen.viaMap ? seen.viaMap.custom : "n/a"} custom),` +
      ` getComputedStyle ${computedWorks ? "yes" : "no"} (${seen.viaComputed.custom} custom)`
    );
  });

  // ===== The palette says what it is offering =====
  // Sixteen 14px squares of colour and nothing else: the row said neither what
  // it was nor which token any square stood for. The name was on the title
  // attribute, but a native tooltip takes about a second and the target is
  // smaller than the cursor, so it was a name most people never saw.

  await check("the palette names the swatch under the pointer", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".card h2");
      window.__t.edit();
      window.__t.swatch("background-color");

      const cap = document.querySelector("#ccp-color-picker .ccp-edit-palcap");
      if (!cap) return { missing: true };
      const read = () => ({
        name: cap.querySelector("b").textContent,
        value: cap.querySelector("span").textContent,
      });
      const pals = [...document.querySelectorAll("#ccp-color-picker .ccp-edit-pal")];
      const terra = pals.find((p) => p.title.startsWith("--terra "));

      const idle = read();
      const height = cap.getBoundingClientRect().height;
      terra.dispatchEvent(new PointerEvent("pointerenter"));
      const hovered = read();
      const hoveredHeight = cap.getBoundingClientRect().height;
      terra.dispatchEvent(new PointerEvent("pointerleave"));
      const afterLeave = read();
      // Keyboard reaches these too.
      terra.dispatchEvent(new FocusEvent("focus"));
      const focused = read();
      return { idle, hovered, afterLeave, focused, height, hoveredHeight, title: terra.title };
    `);

    if (seen.missing) return fail("the palette has no caption");
    if (!seen.idle.value) fail("the caption is blank when nothing is hovered — the row is unlabelled again");
    if (seen.idle.name) fail(`the idle caption claims a token name: ${JSON.stringify(seen.idle)}`);

    if (seen.hovered.name !== "--terra") fail(`hovering gave name ${JSON.stringify(seen.hovered.name)}`);
    if (seen.hovered.value.toLowerCase() !== "#a94f30") {
      fail(`hovering gave value ${JSON.stringify(seen.hovered.value)}`);
    }
    // A caption that appears on hover would resize the picker as the pointer
    // approaches, moving the swatch out from under it.
    if (seen.height !== seen.hoveredHeight) {
      fail(`the caption changed height on hover (${seen.height} → ${seen.hoveredHeight})`);
    }
    if (seen.afterLeave.name !== "" || !seen.afterLeave.value) {
      fail(`leaving did not restore the caption: ${JSON.stringify(seen.afterLeave)}`);
    }
    if (seen.focused.name !== "--terra") fail("focusing a swatch did not name it");
    // The tooltip stays as the accessible fallback.
    if (!seen.title.includes("--terra")) fail(`the title attribute lost the name: ${seen.title}`);
    await evaluate(ws, "window.__t.esc(); return true;");
  });

  // ===== Text colour, and where typography is allowed to appear =====
  // The text guard used to sit on the typography group, so an element holding no
  // text of its own got none of its controls. That is right for size and leading
  // and wrong for colour: colour inherits, and a wrapper is exactly where an
  // inherited colour gets set. The guard now sits on the five metric controls,
  // and colour carries none.

  await check("colour joins typography, and reaches wrappers", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      const rowsFor = (sel, dx, dy) => {
        window.__t.select(sel, dx, dy);
        window.__t.edit();
        const out = [...document.querySelectorAll("#ccp-edit-panel .ccp-edit-row")]
          .map((r) => r.dataset.control);
        window.__t.esc();   // out of Edit Mode
        window.__t.esc();   // and drop the selection
        return out;
      };
      // .card holds only element children, so it is the text-less case. If the
      // click ever lands on a child instead, font-size reappears below and this
      // case fails rather than quietly testing the wrong element.
      return {
        withText: rowsFor(".card h2", 5, 5),
        wrapper: rowsFor(".card", 3, 3),
        // No text anywhere beneath — the arrangement where even colour is noise.
        blank: rowsFor(".glyph-block", 4, 4),
      };
    `);

    if (!seen.withText.includes("color")) fail("no colour row on an element with text");
    if (!seen.withText.includes("text")) fail("no text row on an element with its own words");
    if (seen.wrapper.includes("text")) {
      fail("a text field leaked onto a wrapper whose words belong to descendants");
    }
    if (seen.blank.includes("color")) fail("colour offered on an element with no text beneath");
    if (seen.blank.includes("text")) fail("text field offered on an element with no text at all");
    if (!seen.withText.includes("font-size")) fail("fixture changed: h2 lost its size row");
    // Panel order has to match EDIT_PROP_ORDER, where color follows text-align.
    if (seen.withText.indexOf("color") < seen.withText.indexOf("text-align")) {
      fail(`colour sorts before align: ${JSON.stringify(seen.withText)}`);
    }

    if (!seen.wrapper.includes("color")) fail("no colour row on a text-less wrapper");
    for (const metric of ["font-size", "font-weight", "line-height", "letter-spacing", "text-align"]) {
      if (seen.wrapper.includes(metric)) {
        fail(`${metric} leaked onto a text-less wrapper: ${JSON.stringify(seen.wrapper)}`);
      }
    }
  });

  // ===== The text field edits the words, and puts them back exactly =====
  // The third kind of host-page write. What matters end to end: keystrokes
  // land on the element live, the row carries a dirty dot, the delta reports
  // the change in quotes, and the reset dot restores the original bytes.

  await check("the text field edits the words and puts them back", async (fail) => {
    await loadHarness(ws);
    const r = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".card h2", 5, 5);
      window.__t.edit();
      const el = document.querySelector(".card h2");
      const original = el.textContent;
      const input = window.__t.row("text").querySelector(".ccp-edit-textin");
      const shown = input.value;
      input.focus();
      input.value = "Renamed by the probe";
      input.dispatchEvent(new Event("input", { bubbles: true }));
      const after = el.textContent;
      input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
      const dirty = window.__t.row("text").classList.contains("ccp-edit-dirty");
      const block = await window.__t.copy();
      window.__t.row("text").querySelector(".ccp-edit-dot").click();
      const restored = el.textContent;
      window.__t.esc();
      window.__t.esc();
      return { original, shown, after, dirty, block, restored };
    `);
    if (r.shown !== "emil-design-eng") fail(`field shows ${JSON.stringify(r.shown)}, not the element's words`);
    if (r.after !== "Renamed by the probe") fail(`typing did not land: ${JSON.stringify(r.after)}`);
    if (!r.dirty) fail("no dirty dot on an edited text row");
    if (!r.block.includes('# text: "emil-design-eng" → "Renamed by the probe"')) {
      fail(`delta line missing: ${JSON.stringify(r.block.slice(0, 260))}`);
    }
    if (r.restored !== r.original) fail("the reset dot did not put the exact words back");
  });

  // ===== Composite type styles =====
  // The style row is the composite's seat: it claims what is in force, steps
  // the whole source as one action, and conforms drift whoever shipped it.

  await check("a type style claims, steps as one action, and undoes", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".ts-on");
      window.__t.edit();
      const chipName = () => {
        const n = document.querySelector("#ccp-edit-panel .ccp-type-name");
        return n ? n.textContent : null;
      };
      const claimed = chipName();
      // ‹ steps down the ladder: type-lg → type-sm.
      document.querySelector("#ccp-edit-panel .ccp-type-st").click();
      const el = document.querySelector(".ts-on");
      const afterStep = {
        cls: el.getAttribute("class"),
        size: window.__t.css(".ts-on", "font-size"),
        lead: window.__t.css(".ts-on", "line-height"),
        name: chipName(),
      };
      const block = await window.__t.copy();
      window.__t.undo();
      const undone = { cls: el.getAttribute("class"), size: window.__t.css(".ts-on", "font-size") };
      window.__t.esc();
      window.__t.esc();
      return { claimed, afterStep, block, undone };
    `);
    if (seen.claimed !== "type-lg") fail(`claimed ${JSON.stringify(seen.claimed)}, not type-lg`);
    if (!seen.afterStep.cls.includes("type-sm") || seen.afterStep.cls.includes("type-lg")) {
      fail(`step did not swap the class: ${JSON.stringify(seen.afterStep.cls)}`);
    }
    if (seen.afterStep.size !== "14px" || seen.afterStep.lead !== "20px") {
      fail(`one step moved to ${seen.afterStep.size}/${seen.afterStep.lead}, not 14px/20px — the composite did not move together`);
    }
    if (seen.afterStep.name !== "type-sm") fail(`chip reads ${seen.afterStep.name} after the step`);
    if (!seen.block.includes("# type style: type-lg → type-sm (size 18→14, leading 28→20)")) {
      fail(`delta line missing: ${JSON.stringify(seen.block.slice(0, 300))}`);
    }
    if (!seen.undone.cls.includes("type-lg") || seen.undone.size !== "18px") {
      fail(`one undo did not give the whole step back: ${JSON.stringify(seen.undone)}`);
    }
  });

  await check("drift reads modified and the chip conforms", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".ts-drift");
      window.__t.edit();
      const chip = () => document.querySelector("#ccp-edit-panel .ccp-type-chip");
      const wasDrifted = chip().classList.contains("ccp-type-drifted");
      chip().click();
      const lead = window.__t.css(".ts-drift", "line-height");
      const block = await window.__t.copy();
      const stillDrifted = chip().classList.contains("ccp-type-drifted");
      window.__t.esc();
      window.__t.esc();
      return { wasDrifted, lead, block, stillDrifted };
    `);
    if (!seen.wasDrifted) fail("page-shipped drift did not read as modified");
    if (seen.lead !== "28px") fail(`conform left leading at ${seen.lead}`);
    if (seen.stillDrifted) fail("the chip still reads modified after conforming");
    if (!seen.block.includes("# type style: type-lg (modified) → type-lg (leading 32→28)")) {
      fail(`conform delta missing: ${JSON.stringify(seen.block.slice(0, 300))}`);
    }
  });

  await check("a var stem claims solo — named, unsteppable", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.probeOn();
      window.__t.select(".stem-title");
      window.__t.edit();
      const name = document.querySelector("#ccp-edit-panel .ccp-type-name");
      const arrows = document.querySelectorAll("#ccp-edit-panel .ccp-type-st").length;
      window.__t.esc();
      window.__t.esc();
      return { name: name ? name.textContent : null, arrows };
    `);
    if (seen.name !== "--h-md") fail(`stem claimed as ${JSON.stringify(seen.name)}`);
    if (seen.arrows !== 0) fail(`a solo style grew ${seen.arrows} stepper arrows`);
  });

  // ===== The copy payload, against a real element =====
  // test/copy-format.mjs sweeps the pure half — the order, the gating, the
  // fence, the fallback truth table. Everything below is the half that needs a
  // page: the four HTML blocks are built by walking a real subtree, and the
  // three diagnosis fields exist only because a browser computed something.

  await check("copy · the defaults are what shipped before the setting existed", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.copyDefaults();
      window.__t.probeOn();
      window.__t.select(".card");
      const payload = await window.__t.copyCode();
      return { payload, keys: window.__t.keys(payload), block: window.__t.block(payload) };
    `);
    if (!seen.payload) return fail("nothing reached the clipboard");
    if (!seen.payload.startsWith("```\n")) fail("payload is not fenced by default");
    if (!seen.payload.endsWith("\n```")) fail("payload's closing fence is missing");
    for (const key of ["page", "anchor", "selector", "position", "text"]) {
      if (!seen.keys.includes(key)) fail(`default payload is missing ${key}`);
    }
    for (const key of ["layout", "styles", "props"]) {
      if (seen.keys.includes(key)) fail(`${key} rode along without being asked for`);
    }
    // The harness has no source tooling and no framework, so nothing points at
    // the source: the default fallback is the whole reason a subtree is here.
    if (!seen.block.includes("<h2")) fail(`no subtree on an unlocated element: ${seen.block}`);
  });

  await check("copy · a field switched off leaves, and takes nothing with it", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.copyDefaults();
      window.__t.probeOn();
      window.__t.select(".card");
      const before = window.__t.keys(await window.__t.copyCode());
      window.__t.prefs({ copySelector: "off", copyText: "off" });
      const after = window.__t.keys(await window.__t.copyCode());
      return { before, after };
    `);
    if (!seen.before.includes("selector")) return fail("fixture: no selector field to switch off");
    if (seen.after.includes("selector")) fail("selector survived being switched off");
    if (seen.after.includes("text")) fail("text survived being switched off");
    const want = seen.before.filter((k) => k !== "selector" && k !== "text");
    if (seen.after.join(",") !== want.join(",")) {
      fail(`the rest moved: ${seen.after.join(",")} vs ${want.join(",")}`);
    }
  });

  await check("copy · the four HTML blocks are four different sizes", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.copyDefaults();
      window.__t.probeOn();
      window.__t.select(".card");
      const out = {};
      // The fallback would overrule every choice on this unlocated element,
      // which is exactly what the previous case proved. Off, so the choice is
      // the thing under test.
      window.__t.prefs({ copyHtmlFallback: "off" });
      for (const trim of ["root", "shape", "full", "none"]) {
        window.__t.prefs({ copyHtml: trim });
        out[trim] = window.__t.block(await window.__t.copyCode());
      }
      return out;
    `);
    if (seen.none !== "") fail(`"none" still emitted a block: ${seen.none}`);
    if (!/^<(\w+)[^>]*> … \d+ children <\/\1>$/.test(seen.root)) {
      fail(`"root" is not the one-line root tag: ${seen.root}`);
    }
    // Shape names each child on its own line — tag, classes, then the text in
    // quotes — without reproducing any of it as markup.
    const shapeLines = seen.shape.split("\n");
    if (shapeLines.length < 3) fail(`"shape" collapsed to ${shapeLines.length} lines`);
    if (!/^\s+[a-z]+(\.[\w-]+)* "/m.test(seen.shape)) {
      fail(`"shape" did not name a child and its text: ${seen.shape}`);
    }
    if (/^\s+</m.test(seen.shape)) fail(`"shape" reproduced a child as markup: ${seen.shape}`);
    // Full does reproduce them, so it is the longest of the four.
    if (!/<h2[^>]*>/.test(seen.full)) fail(`"full" did not reproduce the child: ${seen.full}`);
    if (seen.full.length <= seen.shape.length) fail("full is no longer than shape");
    if (seen.shape.length <= seen.root.length) fail("shape is no longer than root");
  });

  await check("copy · depth only bites on the full subtree", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.copyDefaults();
      window.__t.probeOn();
      window.__t.select(".card");
      window.__t.prefs({ copyHtmlFallback: "off", copyHtml: "full" });
      const out = {};
      for (const depth of ["3", "2", "1"]) {
        window.__t.prefs({ copyDepth: depth });
        out[depth] = window.__t.block(await window.__t.copyCode());
      }
      return out;
    `);
    if (seen["1"].length >= seen["3"].length) fail("depth 1 is not shorter than depth 3");
    // At depth 1 every child is summarised rather than opened: "…" when it has
    // nothing inside, a child count when it does. Asserted per line rather than
    // on the whole block, because a long text node is truncated with the same
    // ellipsis at every depth and would otherwise pass for an elided subtree.
    const kids = seen["1"].split("\n").slice(1, -1);
    if (kids.length === 0) return fail(`depth 1 produced no child lines: ${seen["1"]}`);
    for (const line of kids) {
      if (!/…<\/|<!-- \d+ children -->/.test(line)) fail(`depth 1 opened a child: ${line}`);
    }
  });

  await check("copy · the fence can be dropped, and the HTML keeps a block", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.copyDefaults();
      window.__t.probeOn();
      window.__t.select(".card");
      window.__t.prefs({ copyFence: "off" });
      const withHtml = await window.__t.copyCode();
      window.__t.prefs({ copyHtml: "none", copyHtmlFallback: "off" });
      const bare = await window.__t.copyCode();
      return { withHtml, bare };
    `);
    if (seen.withHtml.startsWith("```\n")) fail("the outer fence survived being switched off");
    if (!seen.withHtml.includes("\n\n```html\n")) {
      fail("unfenced, the HTML did not take a block of its own");
    }
    if (seen.bare.includes("```")) fail(`nothing to delimit, yet a fence appeared: ${seen.bare}`);
    if (!seen.bare.startsWith("# ")) fail("the bare payload does not start with the header");
  });

  await check("copy · configuring the payload down to nothing says so", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.copyDefaults();
      window.__t.probeOn();
      window.__t.select(".card");
      const off = {};
      for (const key of ["copySource", "copyComponent", "copyPage", "copyAnchor",
                         "copyHandlers", "copySelector", "copyPosition", "copyRepeated",
                         "copyText", "copyLayout", "copyStyles", "copyProps"]) off[key] = "off";
      window.__t.prefs({ ...off, copyHtml: "none", copyHtmlFallback: "off" });
      const payload = await window.__t.copyCode();
      const toast = document.querySelector("#ccp-toast, .ccp-toast");
      return { payload, toast: toast ? toast.textContent : null };
    `);
    if (seen.payload !== null) fail(`wrote ${JSON.stringify(seen.payload)} to the clipboard`);
    if (!seen.toast || !/Copying/.test(seen.toast)) {
      fail(`the button did nothing and said nothing: ${JSON.stringify(seen.toast)}`);
    }
  });

  await check("copy · layout diagnosis reports the box the browser made", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.copyDefaults();
      window.__t.probeOn();
      window.__t.select(".card");
      window.__t.prefs({ copyLayout: "on" });
      const payload = await window.__t.copyCode();
      const line = payload.split("\\n").find((l) => l.startsWith("# layout: ")) || "";
      const rect = document.querySelector(".card").getBoundingClientRect();
      return { line, w: Math.round(rect.width), h: Math.round(rect.height) };
    `);
    if (!seen.line) return fail("no layout line after switching it on");
    if (!seen.line.includes(`box ${seen.w}x${seen.h}`)) {
      fail(`layout disagrees with the real box ${seen.w}x${seen.h}: ${seen.line}`);
    }
    if (!/display \w/.test(seen.line)) fail(`no display in the layout line: ${seen.line}`);
  });

  await check("copy · matched CSS names the rule and where it came from", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.copyDefaults();
      window.__t.probeOn();
      window.__t.select(".card");
      window.__t.prefs({ copyStyles: "on" });
      const payload = await window.__t.copyCode();
      return payload.split("\\n").filter((l, i, a) => {
        const start = a.findIndex((x) => x.startsWith("# styles: "));
        return start >= 0 && i >= start && (i === start || l.startsWith("#   "));
      });
    `);
    if (seen.length === 0) return fail("no styles field after switching it on");
    const body = seen.join(" ");
    if (!body.includes("{")) fail(`no declarations in the styles field: ${body}`);
    if (!/\.card/.test(body)) fail(`the element's own rule is missing: ${body}`);
    // Our own stylesheets ride along on every page; they are never the page's.
    if (/ccp-/.test(body)) fail(`our own chrome leaked into the styles field: ${body}`);
  });

  await check("copy · a page with no framework offers no props", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.copyDefaults();
      window.__t.probeOn();
      window.__t.select(".card");
      window.__t.prefs({ copyProps: "on" });
      const payload = await window.__t.copyCode();
      return { keys: window.__t.keys(payload), payload };
    `);
    // The harness is plain HTML: there is nothing to snapshot, and inventing a
    // field rather than omitting it is the failure mode that matters here.
    if (seen.keys.includes("props")) fail(`props emitted with no framework on the page: ${seen.payload}`);
  });

  await check("copy · the edit panel copies in the same dialect", async (fail) => {
    await loadHarness(ws);
    const seen = await evaluate(ws, `
      window.__t.copyDefaults();
      window.__t.probeOn();
      window.__t.select(".card h2");
      window.__t.edit();
      window.__t.step("font-size", 1);
      const fenced = await window.__t.copy();
      window.__t.prefs({ copyFence: "off", copySelector: "off" });
      const bare = await window.__t.copy();
      return { fenced, bare, keys: window.__t.keys(bare) };
    `);
    if (!seen.fenced.startsWith("```\n")) fail("the delta block lost its fence");
    if (!seen.fenced.includes("# edits:")) fail("the delta block lost its edits");
    if (seen.bare.startsWith("```")) fail("the delta block ignored the fence setting");
    if (!seen.bare.includes("# edits:")) fail("switching a field off dropped the edits too");
    if (seen.keys.includes("selector")) fail("the delta block ignored the field setting");
  });
} finally {
  try { if (ws) ws.close(); } catch { /* already gone */ }
  browser.kill();
  server.close();
  rmSync(PROFILE, RM_PROFILE);
}

for (const r of rows) {
  console.log(`${r.result.padEnd(5)} ${r.case}${r.detail ? " — " + r.detail : ""}`);
}
console.log(failures === 0 ? "\ncdp: all checks passed" : `\ncdp: ${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
