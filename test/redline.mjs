// Redline solver spec + sweep.
//
// computeRedline below is transcribed from content.js — content scripts are
// classic scripts, so the algorithm cannot be imported, only mirrored (same
// arrangement as placement.mjs). Change it there and change it here.
//
// The GEOMETRY block below carries the same three keys, with the same values,
// as GEOMETRY in content.js — and computeRedline refers to them by the same
// names it does there, so test/mirror-drift.mjs can compare the two copies
// character for character. Renaming them locally would make every line that
// uses one read as drift. The opts parameter is how user preferences reach the solver
// (pill placement, guides, zero pills); DEFAULT_OPTS is shipping behaviour.
//
// Run: node test/redline.mjs   (exit 1 on any failure)

const GEOMETRY = {
  redlinePillOffset: 8,
  redlineGuideOvershoot: 4,
  redlinePillMargin: 14,
};
const PILL_OFFSET = GEOMETRY.redlinePillOffset;
const GUIDE_OVERSHOOT = GEOMETRY.redlineGuideOvershoot;
const PILL_MARGIN = GEOMETRY.redlinePillMargin;

const DEFAULT_OPTS = { pillOffset: PILL_OFFSET, guides: true, zeroPills: true };
const TOGGLED_OPTS = { pillOffset: 0, guides: false, zeroPills: false };

// ===== Mirror of computeRedline (content.js) =====

export function computeRedline(sel, hov, vw, vh, opts) {
  const clamp = (v, lo, hi) => Math.min(Math.max(v, lo), hi);
  const s = { l: sel.left, t: sel.top, r: sel.left + sel.width, b: sel.top + sel.height };
  const h = { l: hov.left, t: hov.top, r: hov.left + hov.width, b: hov.top + hov.height };

  const relate = (sLo, sHi, hLo, hHi) => {
    if (hLo >= sHi) return { gap: true, lo: sHi, hi: hLo, hovEdge: hLo };
    if (sLo >= hHi) return { gap: true, lo: hHi, hi: sLo, hovEdge: hHi };
    return { gap: false, lo: Math.max(sLo, hLo), hi: Math.min(sHi, hHi) };
  };
  const x = relate(s.l, s.r, h.l, h.r);
  const y = relate(s.t, s.b, h.t, h.b);

  const lines = [];
  const guides = [];
  const pills = [];

  const measure = (axis, lo, hi, cross) => {
    const value = hi - lo;
    const flush = Math.round(value) === 0;
    const mid = (lo + hi) / 2;
    if (!flush) {
      lines.push(axis === "x"
        ? { kind: "line", x: lo, y: cross, w: value, h: 0, value }
        : { kind: "line", x: cross, y: lo, w: 0, h: value, value });
    }
    if (!flush || opts.zeroPills) {
      const m = GEOMETRY.redlinePillMargin;
      pills.push({
        kind: "pill",
        x: clamp(axis === "x" ? mid : cross + opts.pillOffset, m, vw - m),
        y: clamp(axis === "x" ? cross + opts.pillOffset : mid, m, vh - m),
        value,
      });
    }
    return flush ? 0 : value;
  };

  if (x.gap || y.gap) {
    if (x.gap) {
      const cy = y.gap ? (s.t + s.b) / 2 : clamp((s.t + s.b) / 2, y.lo, y.hi);
      const value = measure("x", x.lo, x.hi, cy);
      if (y.gap && opts.guides && value > 0) {
        const corner = cy < h.t ? h.t : h.b;
        const past = cy + (cy < h.t ? -1 : 1) * GEOMETRY.redlineGuideOvershoot;
        guides.push({
          kind: "guide",
          x: x.hovEdge,
          y: Math.min(corner, past),
          w: 0,
          h: Math.abs(corner - past),
        });
      }
    }
    if (y.gap) {
      const cx = x.gap ? (s.l + s.r) / 2 : clamp((s.l + s.r) / 2, x.lo, x.hi);
      const value = measure("y", y.lo, y.hi, cx);
      if (x.gap && opts.guides && value > 0) {
        const corner = cx < h.l ? h.l : h.r;
        const past = cx + (cx < h.l ? -1 : 1) * GEOMETRY.redlineGuideOvershoot;
        guides.push({
          kind: "guide",
          x: Math.min(corner, past),
          y: y.hovEdge,
          w: Math.abs(corner - past),
          h: 0,
        });
      }
    }
  } else {
    const cy = clamp((s.t + s.b) / 2, y.lo, y.hi);
    const cx = clamp((s.l + s.r) / 2, x.lo, x.hi);
    measure("x", Math.min(s.l, h.l), Math.max(s.l, h.l), cy);
    measure("x", Math.min(s.r, h.r), Math.max(s.r, h.r), cy);
    measure("y", Math.min(s.t, h.t), Math.max(s.t, h.t), cx);
    measure("y", Math.min(s.b, h.b), Math.max(s.b, h.b), cx);
  }

  return [...lines, ...guides, ...pills];
}

// ===== Reference model =====
// The expected pill-value multiset, recomputed from first principles rather
// than through the solver's own span bookkeeping. Raw distances — the solver
// no longer rounds; flush values drop when zeroPills is off.

function expectedValues(sel, hov, opts) {
  const s = { l: sel.left, t: sel.top, r: sel.left + sel.width, b: sel.top + sel.height };
  const h = { l: hov.left, t: hov.top, r: hov.left + hov.width, b: hov.top + hov.height };
  const xGap = h.l >= s.r ? h.l - s.r : s.l >= h.r ? s.l - h.r : -1;
  const yGap = h.t >= s.b ? h.t - s.b : s.t >= h.b ? s.t - h.b : -1;
  const out = [];
  if (xGap >= 0 || yGap >= 0) {
    if (xGap >= 0) out.push(xGap);
    if (yGap >= 0) out.push(yGap);
  } else {
    out.push(Math.abs(s.l - h.l), Math.abs(s.r - h.r));
    out.push(Math.abs(s.t - h.t), Math.abs(s.b - h.b));
  }
  return out
    .filter((v) => opts.zeroPills || Math.round(v) !== 0)
    .sort((a, b) => a - b);
}

// ===== Invariants =====

const EPS = 0.01;
const near = (a, b) => Math.abs(a - b) < EPS;

function checkInvariants(sel, hov, vw, vh, opts, prims, fail) {
  const h = { l: hov.left, t: hov.top, r: hov.left + hov.width, b: hov.top + hov.height };
  const lines = prims.filter((p) => p.kind === "line");
  const guides = prims.filter((p) => p.kind === "guide");
  const pills = prims.filter((p) => p.kind === "pill");

  for (const l of lines) {
    if (l.w !== 0 && l.h !== 0) fail(`line not axis-aligned: ${JSON.stringify(l)}`);
    if (l.w < 0 || l.h < 0) fail(`negative span: ${JSON.stringify(l)}`);
    if (Math.round(l.value) === 0) fail(`flush line survived: ${JSON.stringify(l)}`);
  }

  // Values match the independently recomputed distances, via pills
  const got = pills.map((p) => p.value).sort((a, b) => a - b);
  const want = expectedValues(sel, hov, opts);
  const same = got.length === want.length && got.every((v, i) => near(v, want[i]));
  if (!same) fail(`values ${JSON.stringify(got)} != expected ${JSON.stringify(want)}`);

  // A line endpoint landing on a hov edge coordinate must actually touch that
  // edge — or, when guides are on, a collinear dashed guide must cover it.
  // With guides off, diagonal endpoints are allowed to float by design.
  for (const l of lines) {
    if (l.h === 0) {
      for (const ex of [l.x, l.x + l.w]) {
        if (near(ex, h.l) || near(ex, h.r)) {
          const onEdge = l.y >= h.t - EPS && l.y <= h.b + EPS;
          const guided = guides.some(
            (g) => g.w === 0 && near(g.x, ex) && g.y <= l.y + EPS && g.y + g.h >= l.y - EPS
          );
          if (!onEdge && !guided && opts.guides) {
            fail(`unanchored endpoint (${ex},${l.y}) of ${JSON.stringify(l)}`);
          }
        }
      }
    } else {
      for (const ey of [l.y, l.y + l.h]) {
        if (near(ey, h.t) || near(ey, h.b)) {
          const onEdge = l.x >= h.l - EPS && l.x <= h.r + EPS;
          const guided = guides.some(
            (g) => g.h === 0 && near(g.y, ey) && g.x <= l.x + EPS && g.x + g.w >= l.x - EPS
          );
          if (!onEdge && !guided && opts.guides) {
            fail(`unanchored endpoint (${l.x},${ey}) of ${JSON.stringify(l)}`);
          }
        }
      }
    }
  }

  for (const p of pills) {
    if (p.x < PILL_MARGIN - EPS || p.x > vw - PILL_MARGIN + EPS ||
        p.y < PILL_MARGIN - EPS || p.y > vh - PILL_MARGIN + EPS) {
      fail(`pill outside clamp margins: ${JSON.stringify(p)}`);
    }
  }

  // Count discipline per classification
  const xGap = h.l >= sel.left + sel.width || sel.left >= h.r;
  const yGap = h.t >= sel.top + sel.height || sel.top >= h.b;
  if (!opts.guides && guides.length !== 0) fail(`guides emitted while disabled: ${guides.length}`);
  if (xGap || yGap) {
    const axes = (xGap ? 1 : 0) + (yGap ? 1 : 0);
    if (pills.length > axes) fail(`separated: ${pills.length} pills for ${axes} gapped axes`);
    if (lines.length > axes) fail(`separated: ${lines.length} lines for ${axes} gapped axes`);
    if (!(xGap && yGap) && guides.length !== 0) fail(`guides without a diagonal: ${guides.length}`);
    if (guides.length > lines.length) fail(`more guides than lines: ${guides.length}/${lines.length}`);
  } else {
    if (pills.length > 4) fail(`overlap: ${pills.length} pills`);
    if (guides.length !== 0) fail(`overlap: expected 0 guides, got ${guides.length}`);
    if (lines.length > 4) fail(`overlap: ${lines.length} lines`);
  }

  // Swapping sel/hov must not change what is measured
  const swapped = computeRedline(hov, sel, vw, vh, opts)
    .filter((p) => p.kind === "pill").map((p) => p.value).sort((a, b) => a - b);
  const sym = swapped.length === got.length && swapped.every((v, i) => near(v, got[i]));
  if (!sym) fail(`asymmetric under swap: ${JSON.stringify(got)} vs ${JSON.stringify(swapped)}`);
}

// ===== Named cases =====

const VW = 1440;
const VH = 900;
const R = (left, top, width, height) => ({ left, top, width, height });

const CASES = [
  {
    id: "sep-x",
    sel: R(100, 100, 200, 100), hov: R(400, 120, 150, 150),
    expect: { lines: 1, guides: 0, pills: 1, values: [100] },
    also(prims, fail) {
      const l = prims.find((p) => p.kind === "line");
      if (l.y !== 150) fail(`line should sit at sel centerY 150, got ${l.y}`);
      if (l.x !== 300 || l.w !== 100) fail(`span should be [300,400], got [${l.x},${l.x + l.w}]`);
    },
  },
  {
    id: "sep-y",
    sel: R(100, 100, 200, 100), hov: R(150, 300, 100, 50),
    expect: { lines: 1, guides: 0, pills: 1, values: [100] },
    also(prims, fail) {
      const l = prims.find((p) => p.kind === "line");
      if (l.x !== 200) fail(`line should clamp to x-overlap edge 200, got ${l.x}`);
    },
  },
  {
    id: "sep-diagonal",
    sel: R(100, 100, 100, 100), hov: R(400, 300, 100, 100),
    expect: { lines: 2, guides: 2, pills: 2, values: [100, 200] },
    also(prims, fail) {
      const v = prims.find((p) => p.kind === "guide" && p.w === 0);
      const hg = prims.find((p) => p.kind === "guide" && p.h === 0);
      if (!v || v.x !== 400) fail(`vertical guide should extend hov's left edge x=400`);
      if (!v || Math.abs(v.y - (150 - GUIDE_OVERSHOOT)) > EPS) fail(`vertical guide should overshoot to y=146`);
      if (!hg || hg.y !== 300) fail(`horizontal guide should extend hov's top edge y=300`);
    },
  },
  {
    id: "contained",
    sel: R(100, 100, 400, 300), hov: R(150, 150, 100, 100),
    expect: { lines: 4, guides: 0, pills: 4, values: [50, 50, 150, 250] },
  },
  {
    id: "containing",
    sel: R(150, 150, 100, 100), hov: R(100, 100, 400, 300),
    expect: { lines: 4, guides: 0, pills: 4, values: [50, 50, 150, 250] },
  },
  {
    id: "partial-x",
    sel: R(100, 100, 200, 100), hov: R(250, 150, 200, 100),
    expect: { lines: 4, guides: 0, pills: 4, values: [50, 50, 150, 150] },
  },
  {
    id: "flush",
    sel: R(100, 100, 200, 100), hov: R(300, 100, 100, 100),
    expect: { lines: 0, guides: 0, pills: 1, values: [0] },
  },
  {
    id: "aligned-edge",
    sel: R(100, 100, 400, 300), hov: R(100, 150, 150, 100),
    expect: { lines: 3, guides: 0, pills: 4, values: [0, 50, 150, 250] },
  },
  {
    id: "offscreen-hov",
    sel: R(100, 100, 200, 100), hov: R(3000, 100, 100, 100),
    expect: { lines: 1, guides: 0, pills: 1, values: [2700] },
    also(prims, fail) {
      const p = prims.find((pr) => pr.kind === "pill");
      if (p.x !== VW - PILL_MARGIN) fail(`pill should clamp to ${VW - PILL_MARGIN}, got ${p.x}`);
    },
  },
  {
    id: "identical-rects",
    sel: R(100, 100, 200, 100), hov: R(100, 100, 200, 100),
    expect: { lines: 0, guides: 0, pills: 4, values: [0, 0, 0, 0] },
  },
  {
    id: "fractional-gap",
    sel: R(100, 100, 200, 100), hov: R(340.4, 120, 100, 100),
    expect: { lines: 1, guides: 0, pills: 1, values: [40.4] },
  },
  {
    id: "online-placement",
    opts: { ...DEFAULT_OPTS, pillOffset: 0 },
    sel: R(100, 100, 200, 100), hov: R(150, 300, 100, 50),
    expect: { lines: 1, guides: 0, pills: 1, values: [100] },
    also(prims, fail) {
      const l = prims.find((p) => p.kind === "line");
      const p = prims.find((pr) => pr.kind === "pill");
      if (p.x !== l.x) fail(`pillOffset 0: pill should ride its line x=${l.x}, got ${p.x}`);
      if (p.y !== l.y + l.h / 2) fail(`pillOffset 0: pill should center on its line`);
    },
  },
  {
    id: "no-guides",
    opts: { ...DEFAULT_OPTS, guides: false },
    sel: R(100, 100, 100, 100), hov: R(400, 300, 100, 100),
    expect: { lines: 2, guides: 0, pills: 2, values: [100, 200] },
  },
  {
    id: "no-zero-pills",
    opts: { ...DEFAULT_OPTS, zeroPills: false },
    sel: R(100, 100, 200, 100), hov: R(300, 100, 100, 100),
    expect: { lines: 0, guides: 0, pills: 0, values: [] },
  },
  {
    id: "no-zero-pills-aligned",
    opts: { ...DEFAULT_OPTS, zeroPills: false },
    sel: R(100, 100, 400, 300), hov: R(100, 150, 150, 100),
    expect: { lines: 3, guides: 0, pills: 3, values: [50, 150, 250] },
  },
];

// ===== Runner =====

let failures = 0;
const rows = [];

for (const c of CASES) {
  const errs = [];
  const fail = (msg) => errs.push(msg);
  const opts = c.opts || DEFAULT_OPTS;
  const prims = computeRedline(c.sel, c.hov, VW, VH, opts);

  const count = (kind) => prims.filter((p) => p.kind === kind).length;
  for (const kind of ["lines", "guides", "pills"]) {
    const got = count(kind.slice(0, -1));
    if (got !== c.expect[kind]) fail(`${kind}: got ${got}, want ${c.expect[kind]}`);
  }
  const values = prims.filter((p) => p.kind === "pill").map((p) => p.value).sort((a, b) => a - b);
  const wantV = c.expect.values;
  const okV = values.length === wantV.length && values.every((v, i) => near(v, wantV[i]));
  if (!okV) fail(`values: got ${JSON.stringify(values)}, want ${JSON.stringify(wantV)}`);
  if (c.also) c.also(prims, fail);
  checkInvariants(c.sel, c.hov, VW, VH, opts, prims, fail);

  failures += errs.length;
  rows.push({ case: c.id, result: errs.length ? "FAIL" : "ok", detail: errs.join("; ") });
}

// ===== Sweep =====
// A fixed selection, hov marched across a grid that crosses every
// classification boundary — run once with shipping options and once with
// everything toggled (pill on line, no guides, no zero pills).

const SWEEP_SEL = R(600, 400, 240, 120);

for (const [label, opts] of [["defaults", DEFAULT_OPTS], ["toggled", TOGGLED_OPTS]]) {
  let sweepConfigs = 0;
  let sweepFailures = 0;
  let firstSweepError = "";

  for (const w of [40, 240, 900]) {
    for (const hgt of [40, 120, 700]) {
      for (let left = -300; left <= 1700; left += 50) {
        for (let top = -300; top <= 1100; top += 50) {
          const hov = R(left, top, w, hgt);
          sweepConfigs++;
          const errs = [];
          const fail = (msg) => errs.push(msg);
          checkInvariants(SWEEP_SEL, hov, VW, VH, opts,
            computeRedline(SWEEP_SEL, hov, VW, VH, opts), fail);
          if (errs.length) {
            sweepFailures++;
            if (!firstSweepError) firstSweepError = `hov=${JSON.stringify(hov)}: ${errs[0]}`;
          }
        }
      }
    }
  }

  failures += sweepFailures;
  rows.push({
    case: `sweep · ${label} (${sweepConfigs} configs)`,
    result: sweepFailures ? "FAIL" : "ok",
    detail: sweepFailures ? `${sweepFailures} bad configs; first: ${firstSweepError}` : "",
  });
}

for (const r of rows) {
  console.log(`${r.result.padEnd(5)} ${r.case}${r.detail ? " — " + r.detail : ""}`);
}
console.log(failures === 0 ? "\nredline: all checks passed" : `\nredline: ${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
