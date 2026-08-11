// Edit Mode token resolver — spec + corpus sweep for the pure half.
//
// The five functions below are transcribed from the "Edit Tokens" section of
// content.js — content scripts are classic scripts, so nothing can be
// imported, only mirrored (same arrangement as placement.mjs / redline.mjs).
// Change them there and change them here.
//
// The DOM-bound half (collectTokenSources, findWinningDeclaration,
// detectPropertyToken) is not mirrored: it reads document.styleSheets and has
// no meaning without a page. What is mirrored is everything that decides
// *which token a value is on*, because that is the judgement the delta block
// reports to Claude Code and a wrong claim sends it editing a token that does
// not exist.
//
// Run: node test/edit-tokens.mjs   (exit 1 on any failure)

// ===== Mirror of the Edit Tokens pure core (content.js) =====

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

export function computeSpecificity(selector) {
  if (typeof selector !== "string") return [0, 0, 0];

  let s = selector;
  let carried = [0, 0, 0];
  const fn = /:(where|is|not|has|matches|any)\(/gi;
  let guard = 0;
  for (;;) {
    fn.lastIndex = 0;
    const m = fn.exec(s);
    if (!m || guard++ > 32) break;
    let depth = 1, i = m.index + m[0].length;
    for (; i < s.length && depth > 0; i++) {
      if (s[i] === "(") depth++;
      else if (s[i] === ")") depth--;
    }
    const inner = s.slice(m.index + m[0].length, i - 1);
    if (m[1].toLowerCase() !== "where") {
      for (const branch of inner.split(",")) {
        const b = computeSpecificity(branch);
        if (b[0] > carried[0] ||
            (b[0] === carried[0] && b[1] > carried[1]) ||
            (b[0] === carried[0] && b[1] === carried[1] && b[2] > carried[2])) {
          carried = b;
        }
      }
    }
    s = s.slice(0, m.index) + " " + s.slice(i);
  }

  s = s.replace(/\[[^\]]*\]/g, "[]");
  const pseudoElements = (s.match(/::[\w-]+/g) || []).length;
  s = s.replace(/::[\w-]+/g, " ");

  const ids = (s.match(/#[\w-]+/g) || []).length;
  const classes = (s.match(/\.[\w-]+/g) || []).length;
  const attrs = (s.match(/\[\]/g) || []).length;
  const pseudoClasses = (s.match(/:[\w-]+/g) || []).length;
  const types = (s.match(/(^|[\s>+~(,])([a-z][\w-]*)/gi) || []).length;

  return [
    ids + carried[0],
    classes + attrs + pseudoClasses + carried[1],
    types + pseudoElements + carried[2],
  ];
}

export function compareSpecificity(a, b) {
  for (let i = 0; i < 3; i++) if (a[i] !== b[i]) return a[i] - b[i];
  return 0;
}

export function splitTokenName(name) {
  if (typeof name !== "string") return null;
  const cut = name.lastIndexOf("-");
  if (cut <= 0 || (name.startsWith("--") && cut < 3)) return null;
  const step = name.slice(cut + 1);
  const prefix = name.slice(0, cut);
  if (!step) return null;
  return { prefix, step };
}

export function groupTokenFamilies(entries) {
  const byPrefix = new Map();
  for (const e of entries || []) {
    if (!e || typeof e.resolved !== "number" || !isFinite(e.resolved)) continue;
    const split = splitTokenName(e.name);
    if (!split) continue;
    if (!byPrefix.has(split.prefix)) byPrefix.set(split.prefix, []);
    byPrefix.get(split.prefix).push({ name: e.name, step: split.step, resolved: e.resolved });
  }

  const families = [];
  for (const [prefix, members] of byPrefix) {
    const seenName = new Set();
    const unique = members.filter((m) => (seenName.has(m.name) ? false : seenName.add(m.name)));
    unique.sort((a, b) => a.resolved - b.resolved || a.name.localeCompare(b.name));
    const seenValue = new Set();
    const rungs = unique.filter((m) => (seenValue.has(m.resolved) ? false : seenValue.add(m.resolved)));
    if (rungs.length < 2) continue;
    families.push({ prefix, members: rungs });
  }
  families.sort((a, b) => a.prefix.localeCompare(b.prefix));
  return families;
}

export function matchToken(members, resolved, tolerance) {
  if (!members || typeof resolved !== "number") return null;
  const tol = typeof tolerance === "number" ? tolerance : 0.5;
  let best = null, bestD = Infinity;
  for (const m of members) {
    const d = Math.abs(m.resolved - resolved);
    if (d <= tol && d < bestD) { best = m; bestD = d; }
  }
  return best;
}

export function stepToken(members, resolved, dir) {
  if (!members || members.length === 0) return null;
  const exact = matchToken(members, resolved);
  if (exact) {
    const i = members.indexOf(exact);
    const next = Math.min(members.length - 1, Math.max(0, i + (dir > 0 ? 1 : -1)));
    return members[next];
  }
  if (dir > 0) return members.find((m) => m.resolved > resolved) || members[members.length - 1];
  for (let i = members.length - 1; i >= 0; i--) {
    if (members[i].resolved < resolved) return members[i];
  }
  return members[0];
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
const names = (f) => f.members.map((m) => m.name);

// ===== Corpora — three real design systems, plus one that isn't =====

// Tailwind's spacing scale, as its utility classes resolve at a 16px root.
const TAILWIND_SPACE = [
  ["p-0", "0"], ["p-0.5", "0.125rem"], ["p-1", "0.25rem"], ["p-1.5", "0.375rem"],
  ["p-2", "0.5rem"], ["p-2.5", "0.625rem"], ["p-3", "0.75rem"], ["p-4", "1rem"],
  ["p-5", "1.25rem"], ["p-6", "1.5rem"], ["p-8", "2rem"], ["p-10", "2.5rem"],
  ["p-12", "3rem"], ["p-16", "4rem"], ["p-24", "6rem"], ["p-96", "24rem"],
].map(([name, v]) => ({ name, resolved: parseCssLength(v, 16) }));

const TAILWIND_TEXT = [
  ["text-xs", "0.75rem"], ["text-sm", "0.875rem"], ["text-base", "1rem"],
  ["text-lg", "1.125rem"], ["text-xl", "1.25rem"], ["text-2xl", "1.5rem"],
  ["text-3xl", "1.875rem"], ["text-4xl", "2.25rem"], ["text-9xl", "8rem"],
].map(([name, v]) => ({ name, resolved: parseCssLength(v, 16) }));

// A hand-rolled custom-property system, the other half of the resolver's job.
const CUSTOM_PROPS = [
  ["--title-sm", "18px"], ["--title-md", "22px"], ["--title-lg", "28px"],
  ["--space-1", "4px"], ["--space-2", "8px"], ["--space-3", "12px"],
  ["--space-4", "16px"], ["--space-5", "24px"],
  ["--radius-sm", "4px"], ["--radius-md", "8px"], ["--radius-lg", "16px"],
].map(([name, v]) => ({ name, resolved: parseCssLength(v, 16) }));

// ===== 1. parseCssLength =====

check("length · units it can resolve", (fail) => {
  const cases = [
    ["16px", 16], ["0", 0], ["0px", 0], ["-4px", -4], ["1.5px", 1.5],
    ["1rem", 16], ["0.875rem", 14], ["2rem", 32],
    ["  12px  ", 12], ["12PX", 12],
  ];
  for (const [input, want] of cases) {
    const got = parseCssLength(input, 16, 20);
    if (got !== want) fail(`${input} → ${got}, want ${want}`);
  }
  if (parseCssLength("2em", 16, 20) !== 40) fail("em should resolve against the element's own size");
});

// A value the resolver cannot pin to a pixel count must be null, not a guess —
// null means "no token", and no token means the panel scrubs raw px instead.
check("length · refuses what it cannot pin down", (fail) => {
  const rejects = [
    "auto", "50%", "calc(1rem + 2px)", "var(--space-4)", "inherit", "",
    "16", "px", "16 px", "1e", "clamp(1rem, 2vw, 3rem)", "min(4px, 1em)",
    "10vh", "10vw", "2ch", "1ex", null, undefined, 16, {},
  ];
  for (const input of rejects) {
    const got = parseCssLength(input, 16, 20);
    if (got !== null) fail(`${JSON.stringify(input)} → ${got}, want null`);
  }
  // rem/em with no base to resolve against is equally unknowable.
  if (parseCssLength("1rem", undefined) !== null) fail("rem without a base should be null");
  if (parseCssLength("1em", 16, undefined) !== null) fail("em without a base should be null");
});

// ===== 2. computeSpecificity =====

check("specificity · the standard ladder", (fail) => {
  const cases = [
    ["*", [0, 0, 0]],
    ["li", [0, 0, 1]],
    ["ul li", [0, 0, 2]],
    ["ul > li + li", [0, 0, 3]],
    [".card", [0, 1, 0]],
    ["li.card", [0, 1, 1]],
    ["#main", [1, 0, 0]],
    ["#main .card li", [1, 1, 1]],
    ["a:hover", [0, 1, 1]],
    ["[data-open]", [0, 1, 0]],
    ['input[type="text"]', [0, 1, 1]],
    ["p::first-line", [0, 0, 2]],
    ["div.a.b.c", [0, 3, 1]],
  ];
  for (const [sel, want] of cases) {
    const got = computeSpecificity(sel);
    if (!eq(got, want)) fail(`${sel} → ${JSON.stringify(got)}, want ${JSON.stringify(want)}`);
  }
});

// The functional pseudo-classes are where naive counting goes wrong, and they
// are everywhere in modern resets — miscounting them picks the wrong winning
// rule and reads a var() that isn't the one in effect.
check("specificity · functional pseudo-classes", (fail) => {
  const cases = [
    [":where(.a, #b) p", [0, 0, 1]],          // :where() contributes nothing
    [":is(.a, #b) p", [1, 0, 1]],             // :is() takes its most specific branch
    [":not(.a) p", [0, 1, 1]],
    [":not(#a, .b) p", [1, 0, 1]],
    ["li:has(> .child)", [0, 1, 1]],          // li's type, plus :has()'s .child
    ["li:has(> div.child)", [0, 1, 2]],       // ... and the type inside counts too
    [".card:not(:where(.ghost))", [0, 1, 0]], // nested: the :where() inside still counts 0
  ];
  for (const [sel, want] of cases) {
    const got = computeSpecificity(sel);
    if (!eq(got, want)) fail(`${sel} → ${JSON.stringify(got)}, want ${JSON.stringify(want)}`);
  }
});

// An attribute selector's *value* is arbitrary text and must never be counted.
check("specificity · attribute values are not selectors", (fail) => {
  const cases = [
    ['[href="#anchor"]', [0, 1, 0]],
    ['[class=".fake"]', [0, 1, 0]],
    ['a[href="#x"].real', [0, 2, 1]],
  ];
  for (const [sel, want] of cases) {
    const got = computeSpecificity(sel);
    if (!eq(got, want)) fail(`${sel} → ${JSON.stringify(got)}, want ${JSON.stringify(want)}`);
  }
});

check("specificity · ordering is a total ladder", (fail) => {
  const ladder = ["*", "li", "ul li", ".card", "li.card", ".a.b", "#main", "#main .card"];
  for (let i = 1; i < ladder.length; i++) {
    const lo = computeSpecificity(ladder[i - 1]);
    const hi = computeSpecificity(ladder[i]);
    if (compareSpecificity(hi, lo) <= 0) {
      fail(`${ladder[i]} ${JSON.stringify(hi)} should outrank ${ladder[i - 1]} ${JSON.stringify(lo)}`);
    }
  }
});

// ===== 3. splitTokenName / groupTokenFamilies =====

check("family · splitting names into prefix and step", (fail) => {
  const cases = [
    ["text-lg", { prefix: "text", step: "lg" }],
    ["p-4", { prefix: "p", step: "4" }],
    ["p-1.5", { prefix: "p", step: "1.5" }],       // Tailwind's fractional steps
    ["blue-500", { prefix: "blue", step: "500" }],
    ["text-2xl", { prefix: "text", step: "2xl" }], // not the number 2
    ["--space-4", { prefix: "--space", step: "4" }],
    ["--title-sm", { prefix: "--title", step: "sm" }],
    ["font-bold", { prefix: "font", step: "bold" }],
    ["theme-color-lg", { prefix: "theme-color", step: "lg" }], // only the last segment
  ];
  for (const [name, want] of cases) {
    const got = splitTokenName(name);
    if (!eq(got, want)) fail(`${name} → ${JSON.stringify(got)}, want ${JSON.stringify(want)}`);
  }
});

// Only names with nothing to split are rejected here. A step no longer has to
// look like a scale position — "shadow-card" and "grid-cols" split cleanly and
// are turned away later, by groupTokenFamilies, on the evidence of their values
// rather than on the shape of their names. The vocabulary this used to check
// against was a guess, and it was wrong about most real design systems.
check("family · names with nothing to split", (fail) => {
  const rejects = [
    "card", "flex", "--brand", "--accent", "text-",
    "-lg", "", null, 42,
  ];
  for (const name of rejects) {
    const got = splitTokenName(name);
    if (got !== null) fail(`${JSON.stringify(name)} → ${JSON.stringify(got)}, want null`);
  }
});

// The names the old word list turned away. Each one is a real token from a real
// system, and each was silently invisible: --gap-xxs and --space-small because
// the step was not in the vocabulary, --sds-size-space-200 because only the
// last segment is read and "200" had to survive on its own.
check("family · steps outside the old vocabulary now group", (fail) => {
  const cases = [
    ["--gap-xxs", { prefix: "--gap", step: "xxs" }],
    ["--space-small", { prefix: "--space", step: "small" }],
    ["--radius-DEFAULT", { prefix: "--radius", step: "DEFAULT" }],
    ["--font-size-body", { prefix: "--font-size", step: "body" }],
    ["--sds-size-space-200", { prefix: "--sds-size-space", step: "200" }],
  ];
  for (const [name, want] of cases) {
    const got = splitTokenName(name);
    if (!eq(got, want)) fail(`${name} → ${JSON.stringify(got)}, want ${JSON.stringify(want)}`);
  }

  const fams = groupTokenFamilies([
    { name: "--gap-xxs", resolved: 4 },
    { name: "--gap-small", resolved: 8 },
    { name: "--gap-huge", resolved: 32 },
  ]);
  if (fams.length !== 1) return fail(`want one family, got ${fams.length}`);
  if (fams[0].prefix !== "--gap") fail(`prefix ${fams[0].prefix}`);
  if (fams[0].members.map((m) => m.step).join(",") !== "xxs,small,huge") {
    fail(`want value order xxs,small,huge — got ${fams[0].members.map((m) => m.step).join(",")}`);
  }
});

// The other half of the new rule: splitting permissively only works because
// the family gate is real. A prefix that never varies is not a scale.
check("family · a prefix with one value is not a scale", (fail) => {
  const oneName = groupTokenFamilies([{ name: "shadow-card", resolved: 4 }]);
  if (oneName.length) fail(`a lone name formed a family: ${JSON.stringify(oneName)}`);

  // Two names, one value — a stepper here would have nowhere to go.
  const oneValue = groupTokenFamilies([
    { name: "--gap-sm", resolved: 8 },
    { name: "--gap-md", resolved: 8 },
  ]);
  if (oneValue.length) fail(`two aliases at one value formed a family: ${JSON.stringify(oneValue)}`);

  // An alias alongside a real second rung collapses onto it rather than
  // sitting on it twice, so one press of the stepper always moves the page.
  const withAlias = groupTokenFamilies([
    { name: "--gap-sm", resolved: 8 },
    { name: "--gap-md", resolved: 8 },
    { name: "--gap-lg", resolved: 16 },
  ]);
  if (withAlias.length !== 1) return fail(`want one family, got ${withAlias.length}`);
  if (withAlias[0].members.length !== 2) {
    fail(`want 2 rungs, got ${withAlias[0].members.map((m) => m.name).join(",")}`);
  }
});

check("family · Tailwind spacing groups and sorts", (fail) => {
  const fams = groupTokenFamilies(TAILWIND_SPACE);
  if (fams.length !== 1) return fail(`expected one family, got ${fams.map((f) => f.prefix)}`);
  if (fams[0].prefix !== "p") fail(`prefix ${fams[0].prefix}`);
  if (fams[0].members.length !== 16) fail(`members ${fams[0].members.length}`);
  const vals = fams[0].members.map((m) => m.resolved);
  for (let i = 1; i < vals.length; i++) {
    if (vals[i] < vals[i - 1]) return fail(`not sorted at ${i}: ${vals[i - 1]} then ${vals[i]}`);
  }
  // Sorting is by resolved value, not by name — p-10 comes after p-8.
  const order = names(fams[0]);
  if (order.indexOf("p-10") < order.indexOf("p-8")) fail("p-10 sorted before p-8 (lexical, not numeric)");
});

check("family · text scale keeps word steps in size order", (fail) => {
  const fams = groupTokenFamilies(TAILWIND_TEXT);
  if (fams.length !== 1) return fail(`expected one family, got ${fams.length}`);
  const order = names(fams[0]);
  const want = ["text-xs", "text-sm", "text-base", "text-lg", "text-xl",
                "text-2xl", "text-3xl", "text-4xl", "text-9xl"];
  if (!eq(order, want)) fail(`order ${JSON.stringify(order)}`);
});

check("family · custom properties split into their own scales", (fail) => {
  const fams = groupTokenFamilies(CUSTOM_PROPS);
  const prefixes = fams.map((f) => f.prefix);
  if (!eq(prefixes, ["--radius", "--space", "--title"])) fail(`prefixes ${JSON.stringify(prefixes)}`);
  const title = fams.find((f) => f.prefix === "--title");
  if (!eq(names(title), ["--title-sm", "--title-md", "--title-lg"])) {
    fail(`title order ${JSON.stringify(names(title))}`);
  }
});

// A scale you cannot step along is not a scale. Offering a stepper for a
// one-member family would promise a move that cannot happen.
check("family · singletons are dropped", (fail) => {
  const fams = groupTokenFamilies([
    { name: "text-lg", resolved: 18 },
    { name: "gap-4", resolved: 16 },
    { name: "shadow-md", resolved: 4 },
  ]);
  if (fams.length !== 0) fail(`expected none, got ${JSON.stringify(fams.map((f) => f.prefix))}`);
});

check("family · junk entries are skipped, not crashed on", (fail) => {
  const fams = groupTokenFamilies([
    { name: "p-1", resolved: 4 },
    { name: "p-2", resolved: 8 },
    { name: "p-3", resolved: NaN },        // unparseable value
    { name: "p-4", resolved: null },
    { name: "p-2", resolved: 999 },        // duplicate name, first resolution wins
    null,
    { resolved: 12 },                      // no name
    { name: "card", resolved: 5 },         // not a scale position
  ]);
  if (fams.length !== 1) return fail(`families ${fams.length}`);
  if (!eq(names(fams[0]), ["p-1", "p-2"])) fail(`members ${JSON.stringify(names(fams[0]))}`);
  const p2 = fams[0].members.find((m) => m.name === "p-2");
  if (p2.resolved !== 8) fail(`duplicate should keep the first resolution, got ${p2.resolved}`);
});

if (typeof groupTokenFamilies([]) !== "object") failures++;
check("family · empty input is a family-less page, not an error", (fail) => {
  for (const input of [[], null, undefined]) {
    const got = groupTokenFamilies(input);
    if (!Array.isArray(got) || got.length !== 0) fail(`${JSON.stringify(input)} → ${JSON.stringify(got)}`);
  }
});

// ===== 4. matchToken =====

const SPACE = groupTokenFamilies(CUSTOM_PROPS).find((f) => f.prefix === "--space").members;

check("match · exact values land on their token", (fail) => {
  for (const m of SPACE) {
    const got = matchToken(SPACE, m.resolved);
    if (!got || got.name !== m.name) fail(`${m.resolved} → ${got && got.name}, want ${m.name}`);
  }
});

// Sub-pixel drift is rounding (a 15.996px computed value is still --space-4);
// a whole pixel is a different intent and must not be claimed.
check("match · tolerance is sub-pixel, not sloppy", (fail) => {
  if (matchToken(SPACE, 16.4)?.name !== "--space-4") fail("16.4 should still be --space-4");
  if (matchToken(SPACE, 15.6)?.name !== "--space-4") fail("15.6 should still be --space-4");
  if (matchToken(SPACE, 17) !== null) fail("17 is between rungs and must not claim a token");
  if (matchToken(SPACE, 14) !== null) fail("14 is between rungs and must not claim a token");
  if (matchToken(SPACE, 100) !== null) fail("100 is off the scale entirely");
});

check("match · picks the nearest when rungs are close", (fail) => {
  const tight = [
    { name: "a", resolved: 10 },
    { name: "b", resolved: 10.4 },
  ];
  if (matchToken(tight, 10.35)?.name !== "b") fail("should pick the nearer rung");
  if (matchToken(tight, 10.05)?.name !== "a") fail("should pick the nearer rung");
});

check("match · degenerate input returns null", (fail) => {
  if (matchToken(null, 16) !== null) fail("null members");
  if (matchToken(SPACE, null) !== null) fail("null value");
  if (matchToken([], 16) !== null) fail("empty members");
});

// ===== 5. stepToken =====

check("step · walks the scale one rung at a time", (fail) => {
  let cur = SPACE[0].resolved;
  const walked = [SPACE[0].name];
  for (let i = 0; i < SPACE.length - 1; i++) {
    const next = stepToken(SPACE, cur, 1);
    walked.push(next.name);
    cur = next.resolved;
  }
  if (!eq(walked, names({ members: SPACE }))) fail(`walked ${JSON.stringify(walked)}`);
});

check("step · clamps at both ends rather than wrapping", (fail) => {
  const first = SPACE[0], last = SPACE[SPACE.length - 1];
  if (stepToken(SPACE, first.resolved, -1).name !== first.name) fail("stepping below the floor should stay");
  if (stepToken(SPACE, last.resolved, 1).name !== last.name) fail("stepping above the ceiling should stay");
});

// An off-scale value is the common case after a raw scrub. Stepping must move
// in the direction asked, onto the nearest rung that way — never backwards.
check("step · off-scale values step in the direction of travel", (fail) => {
  // SPACE is 4, 8, 12, 16, 24.
  if (stepToken(SPACE, 10, 1).resolved !== 12) fail(`10 up → ${stepToken(SPACE, 10, 1).resolved}, want 12`);
  if (stepToken(SPACE, 10, -1).resolved !== 8) fail(`10 down → ${stepToken(SPACE, 10, -1).resolved}, want 8`);
  if (stepToken(SPACE, 1, 1).resolved !== 4) fail("below the scale, up → the floor");
  if (stepToken(SPACE, 1, -1).resolved !== 4) fail("below the scale, down → the floor (nothing lower)");
  if (stepToken(SPACE, 99, -1).resolved !== 24) fail("above the scale, down → the ceiling");
  if (stepToken(SPACE, 99, 1).resolved !== 24) fail("above the scale, up → the ceiling (nothing higher)");
});

check("step · degenerate input returns null", (fail) => {
  if (stepToken([], 16, 1) !== null) fail("empty members");
  if (stepToken(null, 16, 1) !== null) fail("null members");
});

// ===== 6. Sweep — every rung of every corpus, both directions =====
// The invariant that matters end to end: from any value on any scale, a step
// lands on a real member of that same family, moves the right way, and never
// leaves the scale.

check("sweep · step lands on the scale from every value", (fail) => {
  const corpora = [
    ["tailwind-space", groupTokenFamilies(TAILWIND_SPACE)],
    ["tailwind-text", groupTokenFamilies(TAILWIND_TEXT)],
    ["custom-props", groupTokenFamilies(CUSTOM_PROPS)],
  ];
  let checked = 0;
  for (const [label, fams] of corpora) {
    for (const fam of fams) {
      const lo = fam.members[0].resolved;
      const hi = fam.members[fam.members.length - 1].resolved;
      // On-rung values, between-rung values, and well outside both ends.
      const probes = [lo - 50, lo - 0.1, hi + 0.1, hi + 50];
      for (const m of fam.members) probes.push(m.resolved, m.resolved + 0.3);
      for (let i = 1; i < fam.members.length; i++) {
        probes.push((fam.members[i - 1].resolved + fam.members[i].resolved) / 2);
      }
      for (const v of probes) {
        for (const dir of [1, -1]) {
          checked++;
          const got = stepToken(fam.members, v, dir);
          if (!got) { fail(`${label}/${fam.prefix} @${v} dir ${dir} → null`); continue; }
          if (!fam.members.includes(got)) {
            fail(`${label}/${fam.prefix} @${v} left the family`);
            continue;
          }
          const onRung = matchToken(fam.members, v);
          if (onRung && got !== onRung) {
            // From a rung, the move is exactly one index unless clamped.
            const delta = fam.members.indexOf(got) - fam.members.indexOf(onRung);
            if (delta !== dir) fail(`${label}/${fam.prefix} @${v} moved ${delta}, want ${dir}`);
          }
          if (!onRung && v > lo && v < hi) {
            // Between rungs, the move must be strictly in the asked direction.
            if (dir > 0 && got.resolved <= v) fail(`${label}/${fam.prefix} @${v} up went to ${got.resolved}`);
            if (dir < 0 && got.resolved >= v) fail(`${label}/${fam.prefix} @${v} down went to ${got.resolved}`);
          }
        }
      }
    }
  }
  if (checked < 200) fail(`sweep only covered ${checked} probes — corpus wired up wrong?`);
});

// ===== Report =====

for (const r of rows) {
  console.log(`${r.result.padEnd(5)} ${r.case}${r.detail ? " — " + r.detail : ""}`);
}
console.log(failures === 0 ? "\nedit-tokens: all checks passed" : `\nedit-tokens: ${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
