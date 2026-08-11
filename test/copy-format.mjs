// The shape of the payload the copy buttons put on the clipboard.
//
// Two axes meet here — which header fields ride along, and how much of the
// rendered subtree comes with them — and both are now settings. That makes the
// interesting question not "does it work" but "does every combination still
// produce something true": a field switched off must vanish without disturbing
// the order of the ones that stay, and the HTML block must come back when
// nothing in the payload points at the source.
//
// The four functions below are transcribed from content.js (mirror-drift.mjs
// keeps them honest). They are pure because they take the preference object
// rather than reading a module's — the same discipline computeRedline follows,
// and the reason this sweep is possible at all. Everything they *call* is
// DOM-bound and lives in test/cdp.mjs instead.
//
// The one thing this file exists to prove above all others: the shipped
// defaults produce byte-for-byte what the extension produced before the
// Copying section existed. A settings page that quietly changes what everyone
// already has is not a settings page, it is a regression with a UI.
//
// Run: node test/copy-format.mjs   (exit 1 on any failure)

// Mirror of COPY_PREFS in content.js — change both.
const COPY_PREFS = {
  copySource: ["on", "off"],
  copyComponent: ["on", "off"],
  copyPage: ["on", "off"],
  copyAnchor: ["on", "off"],
  copyHandlers: ["on", "off"],
  copySelector: ["on", "off"],
  copyPosition: ["on", "off"],
  copyRepeated: ["on", "off"],
  copyText: ["on", "off"],
  copyLayout: ["off", "on"],
  copyStyles: ["off", "on"],
  copyProps: ["off", "on"],
  copyHtml: ["root", "shape", "full", "none"],
  copyDepth: ["3", "2", "1"],
  copyHtmlFallback: ["on", "off"],
  copyFence: ["on", "off"],
};

// Mirror of COPY_ORDER in content.js — change both.
const COPY_ORDER = [
  ["source", "copySource"],
  ["component", "copyComponent"],
  ["page", "copyPage"],
  ["anchor", "copyAnchor"],
  ["handlers", "copyHandlers"],
  ["selector", "copySelector"],
  ["position", "copyPosition"],
  ["repeated", "copyRepeated"],
  ["layout", "copyLayout"],
  ["styles", "copyStyles"],
  ["props", "copyProps"],
  ["text", "copyText"],
];

// Mirror of renderCopyHeader in content.js — change both.
function renderCopyHeader(fields) {
  return fields
    .map((f) => f.lines.map((l, i) => (i === 0 ? `# ${f.key}: ` : "#   ") + l).join("\n"))
    .join("\n");
}

// Mirror of copyTrim in content.js — change both.
function copyTrim(prefs, located) {
  return (!located && prefs.copyHtmlFallback === "on") ? "full" : prefs.copyHtml;
}

// Mirror of fenceBlock in content.js — change both.
function fenceBlock(prefs, body) {
  if (!body) return "";
  return prefs.copyFence === "on" ? "```\n" + body + "\n```" : body;
}

// Mirror of assemblePayload in content.js — change both.
function assemblePayload(prefs, header, html) {
  if (prefs.copyFence === "on") {
    return fenceBlock(prefs, [header, html].filter(Boolean).join("\n"));
  }
  return [header, html && "```html\n" + html + "\n```"].filter(Boolean).join("\n\n");
}

// ===== the stand-in for buildPointerHeader's DOM half =====
// One element's worth of extractor output. The gating loop below is
// buildPointerHeader with the page taken out of it: same order, same skip on an
// empty value, same "off is never even computed".

const FOUND = {
  source: ["src/components/InvoiceRow.tsx:18:4"],
  component: ["InvoiceRow <- InvoiceTable"],
  page: ["localhost:5173/billing"],
  anchor: ['data-testid="invoice-row" (12 matches - one per row)', 'text "INV-2043" (unique in page)'],
  handlers: ["onClick=openInvoice"],
  selector: ["main#app > table.invoices > tbody > tr:nth-child(4)"],
  position: ["child 4 of 12 in InvoiceTable <tbody>", "after INV-2042, before INV-2044"],
  repeated: ["4 of 12 identical siblings - likely one template; change",
             "the component or the data unless this instance alone is meant"],
  layout: ["box 880x52 - display table-row", "parent display table-row-group"],
  styles: ["invoices.css  tbody tr { border-bottom: 1px solid var(--line) }"],
  props: ['id: "INV-2043" - amount: 124000 - status: "paid"'],
  text: ["INV-2043 $1,240.00 View"],
};

const defaults = () => {
  const out = {};
  for (const key of Object.keys(COPY_PREFS)) out[key] = COPY_PREFS[key][0];
  return out;
};

// buildPointerHeader's gating loop, over `found` instead of over the page.
function headerFor(prefs, found = FOUND) {
  const fields = [];
  for (const [key, pref] of COPY_ORDER) {
    if (prefs[pref] !== "on") continue;
    const v = found[key];
    if (!v || v.length === 0) continue;
    fields.push({ key, lines: v });
  }
  return renderCopyHeader(fields);
}

// A field's opening line, told apart from its continuations by what follows the
// hash: "# key: " opens, "#   " continues. Deliberately not `startsWith("# ")`,
// which matches both and is the mistake this helper exists to stop making.
const OPENS = /^# ([a-z]+): /;
const keysOf = (header) =>
  header.split("\n").map((l) => l.match(OPENS)).filter(Boolean).map((m) => m[1]);

// ===== harness =====

const rows = [];
let failures = 0;

function check(name, fn) {
  const errs = [];
  try {
    fn((msg) => errs.push(msg));
  } catch (err) {
    errs.push(String(err.message || err));
  }
  failures += errs.length;
  rows.push({ case: name, result: errs.length ? "FAIL" : "ok", detail: errs.slice(0, 3).join("; ") });
}

const eq = (fail, actual, expected, what) => {
  if (actual !== expected) fail(`${what}: got ${JSON.stringify(actual)}, want ${JSON.stringify(expected)}`);
};

// ===== 1. The defaults are not a taste — they are the old behaviour =====

check("defaults · the nine shipped fields, in the shipped order", (fail) => {
  const keys = keysOf(headerFor(defaults()));
  const want = ["source", "component", "page", "anchor", "handlers",
                "selector", "position", "repeated", "text"];
  eq(fail, keys.join(","), want.join(","), "field keys");
});

check("defaults · the three diagnosis fields are off", (fail) => {
  const keys = keysOf(headerFor(defaults()));
  for (const key of ["layout", "styles", "props"]) {
    if (keys.includes(key)) fail(`${key} emitted by default — it can only be opted into`);
  }
});

check("defaults · the block is the pre-settings block, byte for byte", (fail) => {
  const prefs = defaults();
  // What content.js did before any of this existed: the pointer header, then
  // the root tag when located, wrapped in a bare fence.
  const root = '<tr class="border-b" data-testid="invoice-row"> … 3 children </tr>';
  const was = "```\n" + headerFor(prefs) + "\n" + root + "\n```";
  eq(fail, assemblePayload(prefs, headerFor(prefs), root), was, "default payload");
  eq(fail, copyTrim(prefs, true), "root", "located trim");
  eq(fail, copyTrim(prefs, false), "full", "unlocated trim");
});

// ===== 2. The dialect =====

check("dialect · a field opens with '# key: '", (fail) => {
  const line = headerFor(defaults()).split("\n")[0];
  eq(fail, line, "# source: src/components/InvoiceRow.tsx:18:4", "first line");
});

check("dialect · continuation lines are '#' and three spaces", (fail) => {
  const lines = headerFor(defaults()).split("\n");
  const cont = lines.filter((l) => !OPENS.test(l));
  if (cont.length === 0) return fail("no continuation lines in the sample at all");
  for (const l of cont) {
    if (!l.startsWith("#   ")) fail(`continuation ${JSON.stringify(l)} is not '#' + three spaces`);
    if (l.startsWith("#    ")) fail(`continuation ${JSON.stringify(l)} has four spaces, not three`);
  }
});

check("dialect · every line is a comment, so nothing reads as prose", (fail) => {
  for (const l of headerFor(defaults()).split("\n")) {
    if (!l.startsWith("#")) fail(`${JSON.stringify(l)} would run into the surrounding sentence`);
  }
});

// ===== 3. Field gating =====

check("gating · switching one field off removes exactly that field", (fail) => {
  const before = keysOf(headerFor(defaults()));
  const prefs = { ...defaults(), copySelector: "off" };
  const after = keysOf(headerFor(prefs));
  eq(fail, after.join(","), before.filter((k) => k !== "selector").join(","), "keys after");
});

check("gating · order is COPY_ORDER's, whichever fields are on", (fail) => {
  const order = COPY_ORDER.map(([key]) => key);
  // Every single-field-removed variant, plus everything on.
  const worlds = [{ ...defaults(), copyLayout: "on", copyStyles: "on", copyProps: "on" }];
  for (const [, pref] of COPY_ORDER) worlds.push({ ...defaults(), [pref]: "off" });
  for (const prefs of worlds) {
    const keys = keysOf(headerFor(prefs));
    const sorted = [...keys].sort((a, b) => order.indexOf(a) - order.indexOf(b));
    if (keys.join(",") !== sorted.join(",")) fail(`out of order: ${keys.join(",")}`);
  }
});

check("gating · turning every field on adds the three and reorders nothing", (fail) => {
  const prefs = { ...defaults(), copyLayout: "on", copyStyles: "on", copyProps: "on" };
  const keys = keysOf(headerFor(prefs));
  const want = ["source", "component", "page", "anchor", "handlers", "selector",
                "position", "repeated", "layout", "styles", "props", "text"];
  eq(fail, keys.join(","), want.join(","), "field keys");
});

check("gating · a field found empty is skipped even when switched on", (fail) => {
  // repeated only exists when the element has twins; the switch cannot conjure it.
  const found = { ...FOUND, repeated: null };
  const keys = keysOf(headerFor(defaults(), found));
  if (keys.includes("repeated")) fail("emitted a field the page had nothing for");
  if (!keys.includes("position")) fail("dropped the neighbouring field too");
});

check("gating · everything off leaves no header at all", (fail) => {
  const prefs = defaults();
  for (const [, pref] of COPY_ORDER) prefs[pref] = "off";
  eq(fail, headerFor(prefs), "", "header");
});

// ===== 4. The HTML block and its fallback =====

check("html · the chosen block is used when the pointer resolves", (fail) => {
  for (const trim of COPY_PREFS.copyHtml) {
    eq(fail, copyTrim({ ...defaults(), copyHtml: trim }, true), trim, `trim ${trim}`);
  }
});

check("html · with nothing pointing at source, the full subtree comes back", (fail) => {
  for (const trim of COPY_PREFS.copyHtml) {
    eq(fail, copyTrim({ ...defaults(), copyHtml: trim }, false), "full", `trim ${trim} unlocated`);
  }
});

check("html · the fallback can be switched off, and then it is off", (fail) => {
  for (const trim of COPY_PREFS.copyHtml) {
    const prefs = { ...defaults(), copyHtml: trim, copyHtmlFallback: "off" };
    eq(fail, copyTrim(prefs, false), trim, `trim ${trim} with the fallback off`);
  }
});

check("html · the fallback truth table, in full", (fail) => {
  for (const trim of COPY_PREFS.copyHtml) {
    for (const fallback of COPY_PREFS.copyHtmlFallback) {
      for (const located of [true, false]) {
        const got = copyTrim({ ...defaults(), copyHtml: trim, copyHtmlFallback: fallback }, located);
        const want = (!located && fallback === "on") ? "full" : trim;
        eq(fail, got, want, `${trim} · fallback ${fallback} · located ${located}`);
      }
    }
  }
});

// ===== 5. The wrapper =====

check("fence · on, one bare fence wraps header and HTML together", (fail) => {
  const out = assemblePayload(defaults(), "# page: x", "<tr></tr>");
  eq(fail, out, "```\n# page: x\n<tr></tr>\n```", "fenced payload");
});

check("fence · off, the HTML takes a block of its own", (fail) => {
  const prefs = { ...defaults(), copyFence: "off" };
  const out = assemblePayload(prefs, "# page: x", "<tr></tr>");
  eq(fail, out, "# page: x\n\n```html\n<tr></tr>\n```", "unfenced payload");
});

check("fence · off with no HTML leaves the header bare", (fail) => {
  const prefs = { ...defaults(), copyFence: "off" };
  eq(fail, assemblePayload(prefs, "# page: x", ""), "# page: x", "header only");
});

check("fence · on with no HTML still fences the header", (fail) => {
  eq(fail, assemblePayload(defaults(), "# page: x", ""), "```\n# page: x\n```", "header only");
});

check("fence · on with no header still fences the HTML", (fail) => {
  eq(fail, assemblePayload(defaults(), "", "<tr></tr>"), "```\n<tr></tr>\n```", "HTML only");
});

// ===== 6. The empty payload =====
// Reachable, and it has to come out as the empty string rather than as a fence
// wrapped around nothing: the copy buttons test for it to decide whether to
// write to the clipboard or say why they did not.

check("empty · nothing in, empty string out — never an empty fence", (fail) => {
  for (const fence of COPY_PREFS.copyFence) {
    const prefs = { ...defaults(), copyFence: fence };
    eq(fail, assemblePayload(prefs, "", ""), "", `fence ${fence}`);
    eq(fail, fenceBlock(prefs, ""), "", `fenceBlock fence ${fence}`);
  }
});

check("empty · every field off and no HTML block is the empty payload", (fail) => {
  const prefs = defaults();
  for (const [, pref] of COPY_ORDER) prefs[pref] = "off";
  prefs.copyHtml = "none";
  prefs.copyHtmlFallback = "off";
  eq(fail, copyTrim(prefs, false), "none", "trim");
  eq(fail, assemblePayload(prefs, headerFor(prefs), ""), "", "payload");
});

// ===== 7. The sweep =====
// Every combination of the four shape settings against both pointer states,
// with the field switches at their default. Nothing here asserts a particular
// string — it asserts that no combination produces something malformed, which
// is the property a settings page has to hold across a space nobody will
// click through by hand.

check("sweep · no combination produces a malformed block", (fail) => {
  let seen = 0;
  for (const html of COPY_PREFS.copyHtml) {
    for (const depth of COPY_PREFS.copyDepth) {
      for (const fallback of COPY_PREFS.copyHtmlFallback) {
        for (const fence of COPY_PREFS.copyFence) {
          for (const located of [true, false]) {
            seen++;
            const prefs = { ...defaults(), copyHtml: html, copyDepth: depth,
                            copyHtmlFallback: fallback, copyFence: fence };
            const trim = copyTrim(prefs, located);
            const block = trim === "none" ? "" : "<tr></tr>";
            const out = assemblePayload(prefs, headerFor(prefs), block);

            const ticks = (out.match(/^```/gm) || []).length;
            if (ticks % 2 !== 0) fail(`unbalanced fences in ${html}/${fence}/${located}`);
            if (out.includes("```\n```")) fail(`empty fence in ${html}/${fence}/${located}`);
            if (out.endsWith("\n")) fail(`trailing newline in ${html}/${fence}/${located}`);
            if (/\n\n\n/.test(out)) fail(`blank run in ${html}/${fence}/${located}`);
            if (block && !out.includes(block)) fail(`HTML lost in ${html}/${fence}/${located}`);
          }
        }
      }
    }
  }
  if (seen !== 4 * 3 * 2 * 2 * 2) fail(`swept ${seen} combinations, expected 96`);
});

// ===== report =====

console.table(rows);
if (failures) {
  console.log(`\n${failures} failure${failures === 1 ? "" : "s"}`);
  process.exit(1);
}
console.log("\ncopy-format: all checks passed");
