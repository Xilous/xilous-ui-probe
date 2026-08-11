// Tether solver spec + sweep.
//
// computeTether below is transcribed from content.js — content scripts are
// classic scripts, so the algorithm cannot be imported, only mirrored (same
// arrangement as placement.mjs and redline.mjs). Change it there and change it
// here.
//
// GAP / TICK / TICK_LOUD / THICK / STUB must match GEOMETRY.tetherGap /
// .tetherTick / .tetherTickLoud / .tetherThick / .tetherStub in content.js.
//
// The tether is Edit Mode's association chrome: four ticks at the element's
// edge midpoints plus a dashed run from the edit panel to the tick on the
// facing edge. It exists because the selection ring sat on top of the four
// properties the panel writes (border-width, border-color, border-radius,
// box-shadow), so THE invariant this file is here to defend is:
//
//   nothing the tether emits may ever enter the element's box.
//
// Everything else is legibility. That one is correctness, and it is swept
// exhaustively rather than spot-checked.
//
// Run: node test/tether.mjs   (exit 1 on any failure)

const GAP = 8;
const TICK = 16;
const TICK_LOUD = 26;
const THICK = 2;
const STUB = 10;

// ===== Mirror of computeTether (content.js) =====

function tetherSide(r, tx, ty, inset) {
  const cx = r.x + r.w / 2;
  const cy = r.y + r.h / 2;
  const slide = (lo, len, v) => {
    const i = Math.min(inset, Math.max(0, len / 2 - 1));
    return Math.max(lo + i, Math.min(lo + len - i, v));
  };
  if (Math.abs(tx - cx) * r.h >= Math.abs(ty - cy) * r.w) {
    const y = slide(r.y, r.h, ty);
    return tx >= cx
      ? { x: r.x + r.w, y, nx: 1, ny: 0 }
      : { x: r.x, y, nx: -1, ny: 0 };
  }
  const x = slide(r.x, r.w, tx);
  return ty >= cy
    ? { x, y: r.y + r.h, nx: 0, ny: 1 }
    : { x, y: r.y, nx: 0, ny: -1 };
}

function tetherFacingTick(box, ax, ay) {
  const cx = box.x + box.w / 2;
  const cy = box.y + box.h / 2;
  if (Math.abs(ax - cx) * box.h >= Math.abs(ay - cy) * box.w) {
    return ax >= cx
      ? { x: box.x + box.w, y: cy, nx: 1, ny: 0 }
      : { x: box.x, y: cy, nx: -1, ny: 0 };
  }
  return ay >= cy
    ? { x: cx, y: box.y + box.h, nx: 0, ny: 1 }
    : { x: cx, y: box.y, nx: 0, ny: -1 };
}

function tetherSegHits(a, b, box) {
  const x0 = Math.min(a.x, b.x);
  const x1 = Math.max(a.x, b.x);
  const y0 = Math.min(a.y, b.y);
  const y1 = Math.max(a.y, b.y);
  return x1 > box.x && x0 < box.x + box.w && y1 > box.y && y0 < box.y + box.h;
}

function tetherLeg(a, b, corner) {
  const segs = [];
  const push = (p, q) => {
    const w = Math.abs(q.x - p.x);
    const h = Math.abs(q.y - p.y);
    if (w < 0.5 && h < 0.5) return;
    segs.push({ x: Math.min(p.x, q.x), y: Math.min(p.y, q.y), w, h });
  };
  push(a, corner);
  push(corner, b);
  return segs;
}

export function computeTether(rect, panel, vw, vh, opts) {
  const o = opts || {};
  const gap = o.gap !== undefined ? o.gap : GAP;
  const len = o.tick !== undefined ? o.tick : TICK;
  const th = o.thick !== undefined ? o.thick : THICK;

  const box = {
    x: rect.left - gap,
    y: rect.top - gap,
    w: rect.width + gap * 2,
    h: rect.height + gap * 2,
  };
  const bcx = box.x + box.w / 2;
  const bcy = box.y + box.h / 2;

  const half = len / 2;
  const ht = th / 2;
  const ticks = [
    { x: bcx - half, y: box.y - ht, w: len, h: th },
    { x: bcx - half, y: box.y + box.h - ht, w: len, h: th },
    { x: box.x - ht, y: bcy - half, w: th, h: len },
    { x: box.x + box.w - ht, y: bcy - half, w: th, h: len },
  ];

  const result = { box, ticks, segs: [] };
  if (!panel) return result;

  if (panel.x < box.x + box.w && panel.x + panel.w > box.x &&
      panel.y < box.y + box.h && panel.y + panel.h > box.y) return result;

  const a = tetherSide(panel, bcx, bcy, STUB);
  const s = tetherFacingTick(box, a.x, a.y);
  const b = { x: s.x, y: s.y };

  const perp = s.nx !== 0 ? { x: a.x, y: b.y } : { x: b.x, y: a.y };
  if (!tetherSegHits(a, perp, box) && !tetherSegHits(perp, b, box)) {
    result.segs = tetherLeg(a, b, perp);
    return result;
  }

  const out = { x: b.x + s.nx * STUB, y: b.y + s.ny * STUB };
  const elbow = s.nx !== 0 ? { x: out.x, y: a.y } : { x: a.x, y: out.y };
  const legs = [[a, elbow], [elbow, out], [out, b]];
  if (!legs.some(([p, q]) => tetherSegHits(p, q, box))) {
    result.segs = [...tetherLeg(a, out, elbow), ...tetherLeg(out, b, out)];
  }
  return result;
}

// Which tick does a run land on, and what is its normal axis? Mirrors the
// ordering of the ticks array: top, bottom, left, right.
function facingTick(out) {
  const [top, bottom, left, right] = out.ticks;
  return [
    { tick: top, axis: "y", cx: top.x + top.w / 2, cy: top.y + top.h / 2 },
    { tick: bottom, axis: "y", cx: bottom.x + bottom.w / 2, cy: bottom.y + bottom.h / 2 },
    { tick: left, axis: "x", cx: left.x + left.w / 2, cy: left.y + left.h / 2 },
    { tick: right, axis: "x", cx: right.x + right.w / 2, cy: right.y + right.h / 2 },
  ];
}

// The far end of the run: whichever endpoint of the last segment is NOT shared
// with the previous one (or, for a lone segment, whichever lands on a tick).
function runTerminus(out) {
  const segs = out.segs;
  if (!segs.length) return null;
  const last = segs[segs.length - 1];
  const ends = [
    { x: last.x, y: last.y },
    { x: last.x + last.w, y: last.y + last.h },
  ];
  const axis = last.w >= last.h ? "x" : "y"; // the axis the segment runs along
  for (const t of facingTick(out)) {
    for (const e of ends) {
      if (Math.abs(e.x - t.cx) < 0.5 && Math.abs(e.y - t.cy) < 0.5) {
        return { end: e, tick: t, segAxis: axis };
      }
    }
  }
  return { end: null, tick: null, segAxis: axis };
}

// ===== Helpers =====

const rows = [];
let failures = 0;

function check(name, fn) {
  const errs = [];
  try {
    fn((cond, msg) => { if (!cond) errs.push(msg); });
  } catch (e) {
    errs.push("threw: " + e.message);
  }
  if (errs.length) failures++;
  rows.push({ case: name, result: errs.length ? "FAIL" : "ok", detail: errs[0] || "" });
}

// A rect overlaps the element's own box (NOT the clearance box) if both axes
// overlap by more than a hair. This is the thing that must never happen.
function hitsElement(r, el) {
  const ox = Math.min(el.left + el.width, r.x + r.w) - Math.max(el.left, r.x);
  const oy = Math.min(el.top + el.height, r.y + r.h) - Math.max(el.top, r.y);
  return ox > 0.01 && oy > 0.01;
}

const EL = { left: 400, top: 300, width: 250, height: 140 };

// ===== Shape =====

check("four ticks, one per edge, centred on the midpoints", (t) => {
  const { ticks, box } = computeTether(EL, null, 1280, 800);
  t(ticks.length === 4, `expected 4 ticks, got ${ticks.length}`);
  const bcx = box.x + box.w / 2;
  const bcy = box.y + box.h / 2;
  const [top, bottom, left, right] = ticks;
  t(Math.abs(top.x + top.w / 2 - bcx) < 0.01, "top tick not centred on x");
  t(Math.abs(bottom.x + bottom.w / 2 - bcx) < 0.01, "bottom tick not centred on x");
  t(Math.abs(left.y + left.h / 2 - bcy) < 0.01, "left tick not centred on y");
  t(Math.abs(right.y + right.h / 2 - bcy) < 0.01, "right tick not centred on y");
  t(top.w === TICK && top.h === THICK, "top tick should lie along its edge");
  t(left.w === THICK && left.h === TICK, "left tick should lie along its edge");
});

check("ticks clear the element by the full gap", (t) => {
  const { ticks } = computeTether(EL, null, 1280, 800);
  const [top, bottom, left, right] = ticks;
  t(Math.abs(top.y + top.h / 2 - (EL.top - GAP)) < 0.01, "top tick off the clearance");
  t(Math.abs(bottom.y + bottom.h / 2 - (EL.top + EL.height + GAP)) < 0.01, "bottom tick off the clearance");
  t(Math.abs(left.x + left.w / 2 - (EL.left - GAP)) < 0.01, "left tick off the clearance");
  t(Math.abs(right.x + right.w / 2 - (EL.left + EL.width + GAP)) < 0.01, "right tick off the clearance");
});

check("the loud tick is longer but no closer", (t) => {
  const quiet = computeTether(EL, null, 1280, 800);
  const loud = computeTether(EL, null, 1280, 800, { tick: TICK_LOUD });
  t(loud.ticks[0].w > quiet.ticks[0].w, "loud tick should be longer");
  t(loud.ticks[0].y === quiet.ticks[0].y, "loud tick moved toward the element");
  t(loud.ticks[2].x === quiet.ticks[2].x, "loud tick moved toward the element");
});

check("the run lands on the facing tick", (t) => {
  const panel = { x: 900, y: 320, w: 216, h: 338 };
  const { ticks, segs } = computeTether(EL, panel, 1280, 800);
  t(segs.length >= 1, "expected a run");
  const right = ticks[3];
  const rx = right.x + right.w / 2;
  const ry = right.y + right.h / 2;
  const ends = segs.flatMap((s) => [
    { x: s.x, y: s.y }, { x: s.x + s.w, y: s.y + s.h },
  ]);
  // Exactly, not nearly. The first cut of this used tetherSide()'s along-edge
  // slide for the far anchor, whose inset arithmetic landed up to a pixel off
  // the midpoint on a short edge — invisible on a big element, and plainly not
  // the middle of a 16px tick on a small one.
  t(ends.some((p) => Math.abs(p.x - rx) < 0.01 && Math.abs(p.y - ry) < 0.01),
    "no segment endpoint lands exactly on the facing tick's centre");
});

check("every segment is axis-aligned", (t) => {
  for (const panel of [
    { x: 900, y: 320, w: 216, h: 338 },
    { x: 60, y: 40, w: 216, h: 338 },
    { x: 430, y: 620, w: 216, h: 150 },
  ]) {
    for (const s of computeTether(EL, panel, 1280, 800).segs) {
      t(s.w < 0.5 || s.h < 0.5, `segment ${JSON.stringify(s)} is not axis-aligned`);
    }
  }
});

check("a panel overlapping the element draws ticks only", (t) => {
  // Not an exotic case: placeEditPanel() anchors the panel to the element, so a
  // tall panel beside a short element opens on top of it.
  for (const panel of [
    { x: EL.left + 20, y: EL.top + 20, w: 100, h: 60 },      // wholly inside
    { x: EL.left - 180, y: EL.top + 40, w: 216, h: 542 },    // clipping one edge
    { x: EL.left + 30, y: EL.top - 40, w: 248, h: 542 },     // the harness's own case
  ]) {
    const out = computeTether(EL, panel, 1280, 800);
    t(out.segs.length === 0, `a run from ${JSON.stringify(panel)} would have to cross the element`);
    t(out.ticks.length === 4, "the ticks should still stand alone");
  }
});

check("a separated panel does get a run", (t) => {
  const out = computeTether(EL, { x: 900, y: 320, w: 216, h: 338 }, 1280, 800);
  t(out.segs.length >= 1, "a panel clear of the element should be tethered to it");
});

check("no panel means no run", (t) => {
  const out = computeTether(EL, null, 1280, 800);
  t(out.segs.length === 0, "expected no segments without a panel");
});

// ===== The invariant, swept =====
//
// Element × panel across the viewport, at both tick lengths, including the
// element partly and wholly off-screen. placement.mjs sweeps 8280 configs and
// sim.mjs 138; this is the same idea applied to the one property that matters.

check("sweep · no tick or segment ever enters the element", (t) => {
  const VW = 1280;
  const VH = 800;
  let configs = 0;
  let bad = 0;
  let first = "";

  const elXs = [-120, 0, 60, 400, 900, 1200];
  const elYs = [-80, 0, 40, 300, 700, 780];
  const elSizes = [[40, 30], [250, 140], [900, 500]];
  const panelXs = [4, 300, 700, 1060];
  const panelYs = [4, 200, 460, 700];
  const panelSizes = [[216, 338], [216, 120]];

  for (const [ew, eh] of elSizes) {
    for (const ex of elXs) {
      for (const ey of elYs) {
        const el = { left: ex, top: ey, width: ew, height: eh };
        for (const [pw, ph] of panelSizes) {
          for (const px of panelXs) {
            for (const py of panelYs) {
              const panel = { x: px, y: py, w: pw, h: ph };
              for (const tick of [TICK, TICK_LOUD]) {
                configs++;
                const out = computeTether(el, panel, VW, VH, { tick });
                const offenders = [...out.ticks, ...out.segs].filter((r) => hitsElement(r, el));
                if (offenders.length) {
                  bad++;
                  if (!first) {
                    first = `el=${JSON.stringify(el)} panel=${JSON.stringify(panel)} ` +
                            `tick=${tick} offender=${JSON.stringify(offenders[0])}`;
                  }
                }
              }
            }
          }
        }
      }
    }
  }

  rows.push({ case: `  (swept ${configs} configs)`, result: "info", detail: "" });
  t(bad === 0, `${bad} configs put chrome inside the element; first: ${first}`);
});

// The property this whole routing exists for. A run that ends at the tick's
// centre but arrives ALONG the tick lies on top of its near half and reads as
// starting at the tick's end — endpoint right, angle wrong. Both halves are
// asserted together, because either alone passes the broken version.
check("sweep · every run meets its tick square-on, at the centre", (t) => {
  let runs = 0;
  let bad = 0;
  let first = "";

  const elXs = [-120, 0, 60, 400, 900, 1200];
  const elYs = [-80, 0, 40, 300, 700, 780];
  const elSizes = [[40, 30], [250, 140], [900, 500]];
  const panelXs = [4, 300, 700, 1060];
  const panelYs = [4, 200, 460, 700];
  const panelSizes = [[216, 338], [216, 120]];

  for (const [ew, eh] of elSizes) {
    for (const ex of elXs) {
      for (const ey of elYs) {
        const el = { left: ex, top: ey, width: ew, height: eh };
        for (const [pw, ph] of panelSizes) {
          for (const px of panelXs) {
            for (const py of panelYs) {
              for (const tick of [TICK, TICK_LOUD]) {
                const out = computeTether(el, { x: px, y: py, w: pw, h: ph }, 1280, 800, { tick });
                if (!out.segs.length) continue;
                runs++;
                const term = runTerminus(out);
                const why = !term.tick
                  ? "the run does not end on any tick's centre"
                  // the last segment must run ALONG the tick's normal axis,
                  // which is the axis the tick does NOT span
                  : term.segAxis !== term.tick.axis
                    ? `last segment runs on ${term.segAxis}, tick normal is ${term.tick.axis} (parallel arrival)`
                    : "";
                if (why) {
                  bad++;
                  if (!first) {
                    first = `el=${JSON.stringify(el)} panel=${JSON.stringify({ x: px, y: py, w: pw, h: ph })} ` +
                            `tick=${tick}: ${why}`;
                  }
                }
              }
            }
          }
        }
      }
    }
  }

  rows.push({ case: `  (${runs} configs emitted a run)`, result: "info", detail: "" });
  t(runs > 0, "no configuration produced a run at all — the sweep proves nothing");
  t(bad === 0, `${bad} runs arrived wrong; first: ${first}`);
});

check("the run is never collinear with the tick it lands on", (t) => {
  // The exact failure this replaced: panel low and right of a short element
  // produced a vertical run down a vertical tick.
  for (const panel of [
    { x: 900, y: 600, w: 216, h: 200 },
    { x: 900, y: 40, w: 216, h: 200 },
    { x: 60, y: 640, w: 216, h: 140 },
    { x: 430, y: 620, w: 216, h: 150 },
  ]) {
    const out = computeTether(EL, panel, 1280, 800);
    if (!out.segs.length) continue;
    const term = runTerminus(out);
    t(!!term.tick, `run from ${JSON.stringify(panel)} does not land on a tick centre`);
    if (term.tick) {
      t(term.segAxis === term.tick.axis,
        `run from ${JSON.stringify(panel)} arrives parallel to its tick`);
    }
  }
});

check("sweep · segments stay axis-aligned and non-negative", (t) => {
  let bad = 0;
  let first = "";
  for (const ex of [-100, 100, 600, 1100]) {
    for (const ey of [-60, 100, 500, 760]) {
      const el = { left: ex, top: ey, width: 250, height: 140 };
      for (const px of [4, 400, 1000]) {
        for (const py of [4, 300, 640]) {
          const out = computeTether(el, { x: px, y: py, w: 216, h: 338 }, 1280, 800);
          for (const s of out.segs) {
            if (s.w < 0 || s.h < 0 || (s.w >= 0.5 && s.h >= 0.5)) {
              bad++;
              if (!first) first = `el=${JSON.stringify(el)} seg=${JSON.stringify(s)}`;
            }
          }
        }
      }
    }
  }
  t(bad === 0, `${bad} malformed segments; first: ${first}`);
});

for (const r of rows) {
  console.log(`${r.result.padEnd(5)} ${r.case}${r.detail ? " — " + r.detail : ""}`);
}
console.log(failures === 0 ? "\ntether: all checks passed" : `\ntether: ${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
