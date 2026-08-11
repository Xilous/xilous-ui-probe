// Design-token checks. Run: node test/tokens.mjs
//
// Four things that are invisible until a user hits them:
//
//   1. A literal colour left behind in content.css — one element that silently
//      ignores the theme while everything around it follows.
//   2. A theme missing a token — it inherits the previous theme's value, which
//      reads as a rendering bug rather than a missing declaration.
//   3. A var(--ccp-…) that no theme declares — a typo resolves to nothing and
//      the property is simply dropped.
//   4. Text that cannot be read against its own surface. Eight hand-tuned
//      palettes is exactly how an unreadable theme ships.
//
// No assertion library, same as test/placement.mjs — verdicts are computed and
// printed as an aligned table, and the exit code is the result.

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const read = (p) => readFileSync(join(ROOT, p), "utf8");

// Comments hold example values and prose hex codes; strip them before scanning.
const stripComments = (css) => css.replace(/\/\*[\s\S]*?\*\//g, "");

// Statement at-rules (@import) end in `;`, not a block, so the naive
// selector-is-everything-since-the-last-brace split swallows them into the next
// selector — which is how :root stopped being recognised the first time.
const stripAtStatements = (css) => css.replace(/@(?:import|charset|namespace)[^;]*;/g, "");

const TOKENS_CSS = read("tokens.css");
const CONSUMERS = ["content.css", "settings/settings.css"];

// ===== Parsing =====

// Every `selector { … }` block, with the custom properties it declares.
function parseBlocks(css) {
  const blocks = [];
  const re = /([^{}]+)\{([^}]*)\}/g;
  let m;
  while ((m = re.exec(css))) {
    const selector = m[1].trim().replace(/\s+/g, " ");
    const decls = {};
    for (const line of m[2].split(";")) {
      const i = line.indexOf(":");
      if (i < 0) continue;
      const name = line.slice(0, i).trim();
      if (name.startsWith("--")) decls[name] = line.slice(i + 1).trim();
    }
    blocks.push({ selector, decls });
  }
  return blocks;
}

const blocks = parseBlocks(stripAtStatements(stripComments(TOKENS_CSS)));

// A theme block is one whose selector carries [data-ccp-theme="…"]. Anything
// else that targets :root is tier 1.
const themes = [];
const scales = {};
for (const b of blocks) {
  const ids = [...b.selector.matchAll(/\[data-ccp-theme="([^"]+)"\]/g)].map((x) => x[1]);
  if (ids.length) {
    for (const id of ids) themes.push({ id, decls: b.decls });
  } else if (b.selector.split(",").some((s) => s.trim().endsWith(":root"))) {
    Object.assign(scales, b.decls);
  }
}

const REFERENCE = "terracotta-dark";
const reference = themes.find((t) => t.id === REFERENCE);

const results = [];
const pass = (name, detail) => results.push({ name, detail, verdict: "PASS" });
const warn = (name, detail) => results.push({ name, detail, verdict: "WARN" });
const fail = (name, detail) => results.push({ name, detail, verdict: "FAIL" });

// ===== 1. No literal colours in the consumers =====

// rgb(from …) is a derived colour, not a literal — that is the whole point of
// the alpha-derivation rule, so it must not be flagged.
const LITERAL = /#[0-9a-fA-F]{3,8}\b|\brgba?\((?!\s*from\b)|\bhsla?\(/g;

for (const file of CONSUMERS) {
  const body = stripComments(read(file));
  const hits = [];
  for (const line of body.split("\n")) {
    const found = line.match(LITERAL);
    if (found) hits.push(`${found.join(", ")} in "${line.trim().slice(0, 56)}"`);
  }
  if (hits.length === 0) {
    pass(`no literal colours · ${file}`, "every colour comes from a token");
  } else {
    for (const h of hits) fail(`literal colour · ${file}`, h);
  }
}

// ===== 2. Every theme declares the full semantic set =====

if (!reference) {
  fail("theme completeness", `no [data-ccp-theme="${REFERENCE}"] block to compare against`);
} else {
  const required = Object.keys(reference.decls).sort();
  for (const theme of themes) {
    if (theme.id === REFERENCE) continue;
    const have = new Set(Object.keys(theme.decls));
    const missing = required.filter((k) => !have.has(k));
    const extra = [...have].filter((k) => !required.includes(k));
    if (missing.length) {
      fail(`theme complete · ${theme.id}`, `missing ${missing.join(", ")}`);
    } else if (extra.length) {
      // Not fatal, but it means one theme can style something the others can't.
      warn(`theme complete · ${theme.id}`, `declares extra ${extra.join(", ")}`);
    } else {
      pass(`theme complete · ${theme.id}`, `all ${required.length} tokens`);
    }
  }
}

// ===== 3. Every var() used is actually declared =====

const declared = new Set([
  ...Object.keys(scales),
  ...(reference ? Object.keys(reference.decls) : []),
]);

for (const file of CONSUMERS) {
  const used = new Set(
    [...stripComments(read(file)).matchAll(/var\((--ccp-[a-z0-9-]+)/g)].map((m) => m[1])
  );
  const undeclaredVars = [...used].filter((v) => !declared.has(v)).sort();
  if (undeclaredVars.length) {
    fail(`vars declared · ${file}`, `undeclared ${undeclaredVars.join(", ")}`);
  } else {
    pass(`vars declared · ${file}`, `${used.size} tokens referenced, all declared`);
  }
}

// ===== 4. The badge map has not drifted =====
//
// background.js cannot read tokens.css — the action badge is browser chrome, not
// page chrome — so BADGE_ACCENT is the one place a theme's accent is necessarily
// written twice. Guard it, or a new theme gets the wrong badge and nobody
// notices because the badge is two pixels of colour.

const badgeSrc = read("background.js");
const badgeBlock = badgeSrc.match(/const BADGE_ACCENT = \{([\s\S]*?)\};/);

if (!badgeBlock) {
  fail("badge map", "could not find BADGE_ACCENT in background.js");
} else {
  const mapped = {};
  for (const m of badgeBlock[1].matchAll(/"?([a-z-]+)"?\s*:\s*"(#[0-9a-fA-F]{3,8})"/g)) {
    mapped[m[1]] = m[2].toLowerCase();
  }
  const problems = [];
  for (const theme of themes) {
    const want = (theme.decls["--ccp-accent"] || "").toLowerCase();
    const got = mapped[theme.id];
    if (!got) problems.push(`${theme.id} absent from BADGE_ACCENT`);
    else if (got !== want) problems.push(`${theme.id} badge ${got} but accent ${want}`);
  }
  for (const id of Object.keys(mapped)) {
    if (!themes.some((t) => t.id === id)) problems.push(`${id} in BADGE_ACCENT but no theme block`);
  }
  if (problems.length) for (const p of problems) fail("badge map", p);
  else pass("badge map", `${themes.length} accents match background.js`);
}

// ===== 5. Contrast =====

function hexToRgb(hex) {
  const h = hex.trim().replace("#", "");
  const full = h.length === 3 ? [...h].map((c) => c + c).join("") : h.slice(0, 6);
  if (full.length !== 6 || /[^0-9a-fA-F]/.test(full)) return null;
  return [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16));
}

// WCAG 2.1 relative luminance.
function luminance([r, g, b]) {
  const lin = [r, g, b]
    .map((v) => v / 255)
    .map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
  return 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2];
}

function contrast(fg, bg) {
  const a = hexToRgb(fg);
  const b = hexToRgb(bg);
  if (!a || !b) return null;
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

// `floor` fails the run; `target` only warns. --ccp-text-faint and --ccp-accent
// sit at the lower floor deliberately: the faint grey is a de-emphasised 10px
// readout that the shipped 1.2.0 design already set at 3.4:1, and raising it to
// 4.5 would collapse the label's four-step text hierarchy into two. The accent
// doubles as a border and icon colour, where 3:1 is the correct non-text bar.
// Both are reported rather than hidden. See DESIGN.md.
const PAIRS = [
  { fg: "--ccp-text", bg: "--ccp-surface", floor: 4.5, target: 4.5 },
  { fg: "--ccp-text-dim", bg: "--ccp-surface", floor: 4.5, target: 4.5 },
  { fg: "--ccp-text-muted", bg: "--ccp-surface", floor: 4.5, target: 4.5 },
  { fg: "--ccp-text-faint", bg: "--ccp-surface", floor: 3.0, target: 4.5 },
  { fg: "--ccp-syntax-id", bg: "--ccp-surface", floor: 4.5, target: 4.5 },
  { fg: "--ccp-syntax-class", bg: "--ccp-surface", floor: 4.5, target: 4.5 },
  { fg: "--ccp-accent", bg: "--ccp-surface", floor: 3.0, target: 4.5 },
  { fg: "--ccp-on-accent", bg: "--ccp-accent", floor: 4.5, target: 4.5 },
  { fg: "--ccp-on-error", bg: "--ccp-error", floor: 4.5, target: 4.5 },
  // --ccp-accent-dark is only ever the :active fill under the parent button — a
  // fill that exists for as long as a mouse button is held down. Held to the
  // lower floor for that reason, and reported so the weakest state is on record
  // rather than unmeasured.
  { fg: "--ccp-on-accent", bg: "--ccp-accent-dark", floor: 3.0, target: 4.5 },
];

const contrastRows = [];

for (const theme of themes) {
  for (const pair of PAIRS) {
    const fg = theme.decls[pair.fg];
    const bg = theme.decls[pair.bg];
    if (!fg || !bg) continue; // check 2 already reported the gap
    const ratio = contrast(fg, bg);
    if (ratio === null) continue; // non-hex (an rgba() border, say) — not text
    const verdict =
      ratio < pair.floor ? "FAIL" : ratio < pair.target ? "WARN" : "PASS";
    contrastRows.push({
      theme: theme.id,
      pair: `${pair.fg.replace("--ccp-", "")} on ${pair.bg.replace("--ccp-", "")}`,
      ratio,
      floor: pair.floor,
      verdict,
    });
  }
}

for (const row of contrastRows) {
  const detail = `${row.ratio.toFixed(2)}:1 (floor ${row.floor.toFixed(1)})`;
  const name = `contrast · ${row.theme} · ${row.pair}`;
  if (row.verdict === "FAIL") fail(name, detail);
  else if (row.verdict === "WARN") warn(name, detail);
  else pass(name, detail);
}

// ===== Report =====

const pad = (s, n) => String(s).padEnd(n);
const counts = { PASS: 0, WARN: 0, FAIL: 0 };
for (const r of results) counts[r.verdict]++;

console.log(`\n  ${themes.length} themes · ${Object.keys(scales).length} scale tokens\n`);

// Contrast is the interesting table, so print it in full.
const themeWidth = Math.max(...contrastRows.map((r) => r.theme.length), 5);
const pairWidth = Math.max(...contrastRows.map((r) => r.pair.length), 4);
let lastTheme = null;
console.log(`  ${pad("theme", themeWidth)}  ${pad("pair", pairWidth)}  ratio   verdict`);
console.log(`  ${"-".repeat(themeWidth)}  ${"-".repeat(pairWidth)}  ------  -------`);
for (const r of contrastRows) {
  const label = r.theme === lastTheme ? "" : r.theme;
  lastTheme = r.theme;
  const mark = r.verdict === "PASS" ? "" : `  ${r.verdict}`;
  console.log(
    `  ${pad(label, themeWidth)}  ${pad(r.pair, pairWidth)}  ` +
      `${r.ratio.toFixed(2).padStart(5)}  ${r.verdict === "PASS" ? "ok" : ""}${mark}`
  );
}

const structural = results.filter((r) => !r.name.startsWith("contrast"));
console.log("");
for (const r of structural) {
  const mark = r.verdict === "PASS" ? "ok  " : `${r.verdict}`;
  console.log(`  ${mark}  ${pad(r.name, 40)}  ${r.detail}`);
}

console.log(
  `\n  ${counts.PASS} pass · ${counts.WARN} warn · ${counts.FAIL} fail\n`
);

if (counts.FAIL > 0) {
  console.error(`  FAILED — ${counts.FAIL} check${counts.FAIL === 1 ? "" : "s"}\n`);
  process.exit(1);
}
