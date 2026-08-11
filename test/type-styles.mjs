// Type styles — spec for the composite-token pure core.
//
// splitVarStem / resolveTypeStyle / groupTypeStyleLadders /
// matchTypeStyleConstituents / formatTypePx / formatTypeCss below are
// transcribed from the "Type Styles" section of content.js (parseCssLength
// and splitTokenName ride along from Edit Tokens — they already live in
// edit-tokens.mjs too; two copies is what mirror-drift exists to police).
// Change them there and change them here.
//
// What these pin down: a stem groups by role vocabulary and nothing else; a
// style resolves only the constituents it can honestly compare (unitless
// leading against its own size, keywords by their CSS meaning); ladders
// require the same source kind and a size axis; claiming tolerance is half a
// pixel; and the fallback formatting reads like a type spec.
//
// Run: node test/type-styles.mjs   (exit 1 on any failure)

// ===== Mirrors (Edit Tokens) =====

export function parseCssLength(value, remBase, emBase) {
  if (typeof value !== "string") return null;
  const s = value.trim().toLowerCase();
  if (s === "0") return 0;
  const m = s.match(/^(-?[\d.]+)(px|rem|em)$/);
  if (!m) return null;
  const n = parseFloat(m[1]);
  if (!isFinite(n)) return null;
  if (m[2] === "px") return n;
  if (m[2] === "rem") return typeof remBase === "number" ? n * remBase : null;
  return typeof emBase === "number" ? n * emBase : null;
}

export function splitTokenName(name) {
  if (typeof name !== "string") return null;
  const cut = name.lastIndexOf("-");
  // A leading "--" is the custom-property sigil, not a separator.
  if (cut <= 0 || (name.startsWith("--") && cut < 3)) return null;
  const step = name.slice(cut + 1);
  const prefix = name.slice(0, cut);
  if (!step) return null;
  return { prefix, step };
}

// ===== Mirrors (Type Styles) =====

export function splitVarStem(name) {
  if (typeof name !== "string" || !name.startsWith("--")) return null;
  const ROLES = [
    ["font-size", /-(font-size|size)$/i],
    ["font-weight", /-(font-weight|weight)$/i],
    ["line-height", /-(line-height|lineheight|leading)$/i],
    ["letter-spacing", /-(letter-spacing|letterspacing|tracking)$/i],
  ];
  for (const [prop, re] of ROLES) {
    const m = name.match(re);
    if (m && name.length - m[0].length > 2) {
      return { stem: name.slice(0, name.length - m[0].length), prop };
    }
  }
  return null;
}

export function resolveTypeStyle(decls, remBase, emBase) {
  const out = {};
  const d = decls || {};
  const size = d["font-size"] !== undefined
    ? parseCssLength(d["font-size"], remBase, emBase)
    : null;
  if (typeof size === "number") out["font-size"] = size;

  if (d["font-weight"] !== undefined) {
    const w = d["font-weight"].trim();
    const n = w === "normal" ? 400 : w === "bold" ? 700 : parseFloat(w);
    if (isFinite(n) && /^(normal|bold|[\d.]+)$/.test(w)) out["font-weight"] = n;
  }

  if (d["line-height"] !== undefined) {
    const raw = d["line-height"].trim();
    const asLength = parseCssLength(raw, remBase, typeof size === "number" ? size : emBase);
    if (asLength !== null) out["line-height"] = asLength;
    else if (/^[\d.]+$/.test(raw) && typeof size === "number") {
      const unitless = parseFloat(raw);
      if (isFinite(unitless)) out["line-height"] = Math.round(unitless * size * 100) / 100;
    }
  }

  if (d["letter-spacing"] !== undefined) {
    const raw = d["letter-spacing"].trim();
    if (raw === "normal") out["letter-spacing"] = 0;
    else {
      const n = parseCssLength(raw, remBase, typeof size === "number" ? size : emBase);
      if (n !== null) out["letter-spacing"] = n;
    }
  }
  return out;
}

export function groupTypeStyleLadders(styles) {
  const byKey = new Map();
  for (const s of styles || []) {
    if (!s || !s.constituents || typeof s.constituents["font-size"] !== "number") continue;
    const split = splitTokenName(s.name);
    if (!split) continue;
    const key = s.kind + " " + split.prefix;
    if (!byKey.has(key)) byKey.set(key, []);
    byKey.get(key).push(s);
  }
  const ladders = [];
  for (const [key, members] of byKey) {
    const seenName = new Set();
    const unique = members.filter((m) => (seenName.has(m.name) ? false : seenName.add(m.name)));
    unique.sort((a, b) =>
      a.constituents["font-size"] - b.constituents["font-size"] || a.name.localeCompare(b.name));
    const seenSize = new Set();
    const rungs = unique.filter((m) =>
      (seenSize.has(m.constituents["font-size"]) ? false : seenSize.add(m.constituents["font-size"])));
    if (rungs.length < 2) continue;
    ladders.push({ kind: rungs[0].kind, prefix: key.split(" ")[1], rungs });
  }
  ladders.sort((a, b) => a.prefix.localeCompare(b.prefix));
  return ladders;
}

export function matchTypeStyleConstituents(constituents, computed) {
  const matched = [];
  const drifted = [];
  for (const [prop, want] of Object.entries(constituents || {})) {
    const got = computed ? computed[prop] : undefined;
    if (typeof got === "number" && isFinite(got) && Math.abs(got - want) <= 0.5) matched.push(prop);
    else drifted.push(prop);
  }
  return { matched, drifted };
}

export function formatTypePx(prop, value) {
  return prop === "font-weight" ? String(value) : value + "px";
}

export function formatTypeCss(constituents) {
  const c = constituents || {};
  const parts = [];
  if (typeof c["font-size"] === "number") {
    parts.push(typeof c["line-height"] === "number"
      ? c["font-size"] + "px/" + c["line-height"] + "px"
      : c["font-size"] + "px");
  }
  if (typeof c["font-weight"] === "number") parts.push(String(c["font-weight"]));
  if (typeof c["letter-spacing"] === "number" && c["letter-spacing"] !== 0) {
    parts.push(c["letter-spacing"] + "px");
  }
  return parts.join(" · ");
}

// ===== Harness =====

const rows = [];
let failures = 0;

function check(name, fn) {
  const errs = [];
  fn((msg) => errs.push(msg));
  failures += errs.length;
  rows.push({ case: name, result: errs.length ? "FAIL" : "ok", detail: errs.slice(0, 3).join("; ") });
}

const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);

// ===== 1. splitVarStem =====

check("stem · the role vocabulary, all spellings", (fail) => {
  const cases = [
    ["--heading-md-size", { stem: "--heading-md", prop: "font-size" }],
    ["--heading-md-font-size", { stem: "--heading-md", prop: "font-size" }],
    ["--heading-md-weight", { stem: "--heading-md", prop: "font-weight" }],
    ["--heading-md-leading", { stem: "--heading-md", prop: "line-height" }],
    ["--heading-md-line-height", { stem: "--heading-md", prop: "line-height" }],
    ["--heading-md-tracking", { stem: "--heading-md", prop: "letter-spacing" }],
    ["--type-h1-letter-spacing", { stem: "--type-h1", prop: "letter-spacing" }],
  ];
  for (const [name, want] of cases) {
    if (!eq(splitVarStem(name), want)) {
      fail(`${name} → ${JSON.stringify(splitVarStem(name))}`);
    }
  }
});

check("stem · names outside the vocabulary are not stems", (fail) => {
  for (const name of ["--brand-primary", "--space-4", "--title-sm", "--size", "--weight", "size", "--x"]) {
    if (splitVarStem(name) !== null) {
      fail(`${name} → ${JSON.stringify(splitVarStem(name))}`);
    }
  }
});

// ===== 2. resolveTypeStyle =====

check("resolve · px, rem, keywords, unitless leading", (fail) => {
  const got = resolveTypeStyle({
    "font-size": "1.125rem",     // 18 at 16 rem base
    "font-weight": "bold",
    "line-height": "1.5",        // unitless → × own size
    "letter-spacing": "normal",
  }, 16, 16);
  const want = { "font-size": 18, "font-weight": 700, "line-height": 27, "letter-spacing": 0 };
  if (!eq(got, want)) fail(JSON.stringify(got));
});

check("resolve · what cannot be compared is absent, not guessed", (fail) => {
  // calc() sizes, percentage leading, keyword weights outside the two:
  // absent constituents, never zeroes.
  const got = resolveTypeStyle({
    "font-size": "calc(1rem + 2px)",
    "line-height": "150%",
    "font-weight": "bolder",
  }, 16, 16);
  if (Object.keys(got).length !== 0) fail(JSON.stringify(got));
  // Unitless leading with no size to multiply: absent.
  const lh = resolveTypeStyle({ "line-height": "1.5" }, 16, 16);
  if ("line-height" in lh) fail(`leading resolved against nothing: ${JSON.stringify(lh)}`);
});

// ===== 3. Ladders =====

const S = (name, kind, size, extra) => ({
  name, kind, constituents: { "font-size": size, ...(extra || {}) },
});

check("ladder · same prefix, same kind, ordered by size", (fail) => {
  const ladders = groupTypeStyleLadders([
    S("text-lg", "class", 18, { "line-height": 28 }),
    S("text-sm", "class", 14, { "line-height": 20 }),
    S("text-base", "class", 16, { "line-height": 24 }),
  ]);
  if (ladders.length !== 1) return fail(`${ladders.length} ladders`);
  const names = ladders[0].rungs.map((r) => r.name);
  if (!eq(names, ["text-sm", "text-base", "text-lg"])) fail(JSON.stringify(names));
});

check("ladder · kinds never mix", (fail) => {
  const ladders = groupTypeStyleLadders([
    S("--heading-sm", "var", 20),
    S("--heading-lg", "var", 32),
    S("--heading-md", "class", 24), // an impostor kind in the same prefix
  ]);
  if (ladders.length !== 1) return fail(`${ladders.length} ladders`);
  if (ladders[0].kind !== "var" || ladders[0].rungs.length !== 2) {
    fail(JSON.stringify(ladders[0].rungs.map((r) => r.name)));
  }
});

check("ladder · solo styles and same-size aliases do not climb", (fail) => {
  const solo = groupTypeStyleLadders([S("hero-title", "class", 48)]);
  if (solo.length !== 0) fail("a family of one is not a scale");
  const aliased = groupTypeStyleLadders([
    S("text-lg", "class", 18),
    S("text-large", "class", 18), // alias at the same rung
  ]);
  if (aliased.length !== 0) fail("two names at one value is still one rung");
});

// ===== 4. Claiming =====

check("claim · half a pixel is rounding, more is drift", (fail) => {
  const constituents = { "font-size": 18, "line-height": 28 };
  const on = matchTypeStyleConstituents(constituents, { "font-size": 18.3, "line-height": 27.8 });
  if (on.drifted.length !== 0) fail(`rounding read as drift: ${JSON.stringify(on)}`);
  const off = matchTypeStyleConstituents(constituents, { "font-size": 18, "line-height": 32 });
  if (!eq(off.drifted, ["line-height"])) fail(JSON.stringify(off));
});

check("claim · a constituent the element cannot answer is drift", (fail) => {
  // line-height: normal computes to no number; a style that declares leading
  // must not claim such an element fully.
  const { drifted } = matchTypeStyleConstituents(
    { "font-size": 18, "line-height": 28 },
    { "font-size": 18 }
  );
  if (!eq(drifted, ["line-height"])) fail(JSON.stringify(drifted));
});

// ===== 5. Formatting =====

check("format · declarations and the fallback spec", (fail) => {
  if (formatTypePx("font-weight", 600) !== "600") fail("weight grew a unit");
  if (formatTypePx("font-size", 18) !== "18px") fail("size lost its unit");
  const css = formatTypeCss({ "font-size": 20, "line-height": 28, "font-weight": 600 });
  if (css !== "20px/28px · 600") fail(css);
  if (formatTypeCss({ "font-size": 48 }) !== "48px") fail("size-only spec");
  if (formatTypeCss({}) !== "") fail("empty constituents must read as nothing");
});

// ===== Report =====

for (const r of rows) {
  console.log(`${r.result.padEnd(5)} ${r.case}${r.detail ? " — " + r.detail : ""}`);
}
console.log(failures === 0 ? "\ntype-styles: all checks passed" : `\ntype-styles: ${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
