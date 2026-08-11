// Edit Mode colour conversions — spec + round-trip sweep.
//
// The four functions below are transcribed from the "Edit Color" section of
// content.js — content scripts are classic scripts, so nothing can be
// imported, only mirrored (same arrangement as placement.mjs / redline.mjs).
// Change them there and change them here.
//
// What this pins down:
//   · HSV is the picker's space; rgb/hex is the page's. Every colour the
//     picker can reach must survive the round trip within one 8-bit step,
//     or dragging the saturation square would drift the colour under the
//     cursor.
//   · parseCssColor must accept everything getComputedStyle emits for a
//     colour (rgb() and rgba(), space- or comma-separated, %-alpha) plus
//     what a user can type into the hex field (#rgb, #rgba, #rrggbb,
//     #rrggbbaa). Anything else must be null — never a guess, because a
//     wrong parse silently rewrites the page's colour.
//
// Run: node test/edit-color.mjs   (exit 1 on any failure)

// ===== Mirror of the Edit Color section (content.js) =====

export function rgbToHsv(r, g, b) {
  const rn = r / 255, gn = g / 255, bn = b / 255;
  const max = Math.max(rn, gn, bn), min = Math.min(rn, gn, bn);
  const d = max - min;
  let h = 0;
  if (d !== 0) {
    if (max === rn) h = 60 * (((gn - bn) / d) % 6);
    else if (max === gn) h = 60 * ((bn - rn) / d + 2);
    else h = 60 * ((rn - gn) / d + 4);
    if (h < 0) h += 360;
  }
  return { h, s: max === 0 ? 0 : d / max, v: max };
}

export function hsvToRgb(h, s, v) {
  const c = v * s;
  const x = c * (1 - Math.abs(((h / 60) % 2) - 1));
  const m = v - c;
  const [r, g, b] =
    h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x] :
    h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x];
  return {
    r: Math.round((r + m) * 255),
    g: Math.round((g + m) * 255),
    b: Math.round((b + m) * 255),
  };
}

export function parseCssColor(str) {
  if (typeof str !== "string") return null;
  const s = str.trim().toLowerCase();

  let m = s.match(/^#([0-9a-f]{3,8})$/);
  if (m) {
    const hex = m[1];
    if (hex.length === 3 || hex.length === 4) {
      const [r, g, b, a] = hex.split("").map((c) => parseInt(c + c, 16));
      return { r, g, b, a: hex.length === 4 ? a / 255 : 1 };
    }
    if (hex.length === 6 || hex.length === 8) {
      return {
        r: parseInt(hex.slice(0, 2), 16),
        g: parseInt(hex.slice(2, 4), 16),
        b: parseInt(hex.slice(4, 6), 16),
        a: hex.length === 8 ? parseInt(hex.slice(6, 8), 16) / 255 : 1,
      };
    }
    return null;
  }

  m = s.match(/^rgba?\(\s*([\d.]+)\s*[, ]\s*([\d.]+)\s*[, ]\s*([\d.]+)\s*(?:[,/]\s*([\d.]+%?)\s*)?\)$/);
  if (m) {
    const clampByte = (n) => Math.min(255, Math.max(0, Math.round(parseFloat(n))));
    const a = m[4] === undefined ? 1
      : m[4].endsWith("%") ? parseFloat(m[4]) / 100
      : parseFloat(m[4]);
    return { r: clampByte(m[1]), g: clampByte(m[2]), b: clampByte(m[3]), a: Math.min(1, Math.max(0, a)) };
  }

  return null;
}

export function formatHex(c) {
  const h = (n) => Math.round(n).toString(16).padStart(2, "0");
  const base = "#" + h(c.r) + h(c.g) + h(c.b);
  return c.a === undefined || c.a >= 1 ? base : base + h(c.a * 255);
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

// ===== 1. RGB → HSV → RGB round trip, exhaustive-ish grid =====
// Every 4th value on each channel: 64³ = 262,144 colours. A colour that
// cannot survive its own round trip would drift while the user drags.

check("round trip · rgb grid (262144 colours)", (fail) => {
  let worst = 0, worstAt = null, bad = 0;
  for (let r = 0; r < 256; r += 4) {
    for (let g = 0; g < 256; g += 4) {
      for (let b = 0; b < 256; b += 4) {
        const { h, s, v } = rgbToHsv(r, g, b);
        const out = hsvToRgb(h, s, v);
        const d = Math.max(Math.abs(out.r - r), Math.abs(out.g - g), Math.abs(out.b - b));
        if (d > worst) { worst = d; worstAt = [r, g, b, out]; }
        if (d > 1) bad++;
      }
    }
  }
  if (bad) fail(`${bad} colours drifted >1; worst ${worst} at ${JSON.stringify(worstAt)}`);
});

// The greys are the degenerate case: saturation is 0, so hue is meaningless
// and must not be allowed to leak a colour cast back in.
check("round trip · greys keep zero saturation", (fail) => {
  for (let n = 0; n < 256; n++) {
    const hsv = rgbToHsv(n, n, n);
    if (hsv.s !== 0) fail(`rgb(${n},${n},${n}) reported s=${hsv.s}`);
    const out = hsvToRgb(hsv.h, hsv.s, hsv.v);
    if (out.r !== n || out.g !== n || out.b !== n) fail(`grey ${n} → ${JSON.stringify(out)}`);
  }
});

// ===== 2. HSV → RGB → HSV round trip across the picker's own surface =====
// This is the direction the picker actually drives: the user moves a point
// in HSV and we must be able to read that same point back off the colour.

// The only error allowed is 8-bit quantisation, and that error has a closed
// form. Writing v = max/255 and chroma C = s·v·255 in channel counts, half a
// step of rounding per channel gives:
//
//   |dv| ≤ 0.5/255            v is one channel, read back directly
//   |ds| ≤ 1/(v·255)          s = 1 − min/max, so both ends carry rounding —
//                             (0.5 + 0.5(1−s))/max, bounded by 1/max
//   |dh| ≤ 60/C               hue interpolates 60° across the chroma span
//
// Which is why a dark, desaturated colour reads back a coarser hue than a
// vivid one: it is made of fewer distinct channel values. These bounds are
// tight — the sweep reaches ratio 1.00 on all three — so a real regression
// in either conversion cannot hide behind them.
check("round trip · hsv surface (h×s×v grid)", (fail) => {
  let bad = 0, first = "";
  for (let h = 0; h < 360; h += 5) {
    for (let s = 0.1; s <= 1.0001; s += 0.1) {
      for (let v = 0.1; v <= 1.0001; v += 0.1) {
        const rgb = hsvToRgb(h, s, v);
        const back = rgbToHsv(rgb.r, rgb.g, rgb.b);
        const EPS = 1e-9; // float comparison only, not slack
        const hueTol = 60 / (s * v * 255) + EPS;
        const satTol = 1 / (v * 255) + EPS;
        const valTol = 0.5 / 255 + EPS;
        let dh = Math.abs(back.h - h);
        if (dh > 180) dh = 360 - dh;
        const ok = dh <= hueTol && Math.abs(back.s - s) <= satTol && Math.abs(back.v - v) <= valTol;
        if (!ok) {
          bad++;
          if (!first) first = `h=${h} s=${s.toFixed(1)} v=${v.toFixed(1)} → ${JSON.stringify(back)}`;
        }
      }
    }
  }
  if (bad) fail(`${bad} points drifted; first: ${first}`);
});

// The six hue corners are exact by construction — if these move, the hue
// rail's gradient no longer lines up with the colour it produces.
check("hue corners are exact", (fail) => {
  const corners = [
    [0, { r: 255, g: 0, b: 0 }], [60, { r: 255, g: 255, b: 0 }],
    [120, { r: 0, g: 255, b: 0 }], [180, { r: 0, g: 255, b: 255 }],
    [240, { r: 0, g: 0, b: 255 }], [300, { r: 255, g: 0, b: 255 }],
  ];
  for (const [h, want] of corners) {
    const got = hsvToRgb(h, 1, 1);
    if (!eq(got, want)) fail(`h=${h} → ${JSON.stringify(got)}, want ${JSON.stringify(want)}`);
  }
});

// ===== 3. parseCssColor — what it must accept =====

check("parse · hex forms", (fail) => {
  const cases = [
    ["#fff", { r: 255, g: 255, b: 255, a: 1 }],
    ["#000", { r: 0, g: 0, b: 0, a: 1 }],
    ["#d97757", { r: 217, g: 119, b: 87, a: 1 }],
    ["#D97757", { r: 217, g: 119, b: 87, a: 1 }],   // case-insensitive
    ["  #d97757  ", { r: 217, g: 119, b: 87, a: 1 }], // trimmed
    ["#f0a8", { r: 255, g: 0, b: 170, a: 136 / 255 }],
    ["#d9775780", { r: 217, g: 119, b: 87, a: 128 / 255 }],
    ["#ffffff00", { r: 255, g: 255, b: 255, a: 0 }],
  ];
  for (const [input, want] of cases) {
    const got = parseCssColor(input);
    if (!eq(got, want)) fail(`${input} → ${JSON.stringify(got)}, want ${JSON.stringify(want)}`);
  }
});

check("parse · rgb() / rgba() forms", (fail) => {
  const cases = [
    ["rgb(217, 119, 87)", { r: 217, g: 119, b: 87, a: 1 }],
    ["rgb(217 119 87)", { r: 217, g: 119, b: 87, a: 1 }],          // modern space syntax
    ["rgba(217, 119, 87, 0.5)", { r: 217, g: 119, b: 87, a: 0.5 }],
    ["rgb(217 119 87 / 0.5)", { r: 217, g: 119, b: 87, a: 0.5 }],  // slash alpha
    ["rgb(217 119 87 / 50%)", { r: 217, g: 119, b: 87, a: 0.5 }],  // percent alpha
    ["rgba(0, 0, 0, 0)", { r: 0, g: 0, b: 0, a: 0 }],              // the transparent default
    ["RGB(217, 119, 87)", { r: 217, g: 119, b: 87, a: 1 }],        // case-insensitive
    ["rgb(217.6, 119.2, 87.5)", { r: 218, g: 119, b: 88, a: 1 }],  // Chrome emits fractions
  ];
  for (const [input, want] of cases) {
    const got = parseCssColor(input);
    if (!eq(got, want)) fail(`${input} → ${JSON.stringify(got)}, want ${JSON.stringify(want)}`);
  }
});

// A wrong parse silently rewrites the page's colour, so anything unrecognised
// has to be null rather than a best effort.
check("parse · rejects what it cannot know", (fail) => {
  const rejects = [
    "", "   ", "red", "transparent", "currentColor", "inherit",
    "#ff", "#fffff", "#fffffff", "#gggggg",
    "color(display-p3 1 0 0)", "oklch(70% 0.1 30)", "hsl(20, 50%, 40%)",
    "rgb(1, 2)", "rgb()", "var(--ccp-accent)", "url(x.png)",
    null, undefined, 42, {},
  ];
  for (const input of rejects) {
    const got = parseCssColor(input);
    if (got !== null) fail(`${JSON.stringify(input)} → ${JSON.stringify(got)}, want null`);
  }
});

// Out-of-range channels are clamped rather than rejected: a page can hand us
// rgb(300, -5, 0) through a stylesheet and the picker still has to open.
check("parse · clamps out-of-range channels", (fail) => {
  const got = parseCssColor("rgb(300, 0, 999)");
  if (!eq(got, { r: 255, g: 0, b: 255, a: 1 })) fail(`clamp → ${JSON.stringify(got)}`);
  const a = parseCssColor("rgba(0, 0, 0, 5)");
  if (a.a !== 1) fail(`alpha clamp → ${a.a}`);
});

// ===== 4. formatHex =====

check("format · hex output", (fail) => {
  const cases = [
    [{ r: 217, g: 119, b: 87 }, "#d97757"],
    [{ r: 217, g: 119, b: 87, a: 1 }, "#d97757"],       // opaque drops the alpha pair
    [{ r: 0, g: 0, b: 0 }, "#000000"],                  // channels always padded
    [{ r: 255, g: 255, b: 255, a: 0 }, "#ffffff00"],
    [{ r: 217, g: 119, b: 87, a: 0.5 }, "#d9775780"],
    [{ r: 217.6, g: 119.2, b: 87.5 }, "#da7758"],       // fractional input rounds
  ];
  for (const [input, want] of cases) {
    const got = formatHex(input);
    if (got !== want) fail(`${JSON.stringify(input)} → ${got}, want ${want}`);
  }
});

// The loop the hex field actually runs: type a value, see it applied, read it
// back off the element, and find the same text in the field.
check("format ∘ parse is stable over the hex field", (fail) => {
  for (let r = 0; r < 256; r += 7) {
    for (let g = 0; g < 256; g += 11) {
      for (let b = 0; b < 256; b += 13) {
        const text = formatHex({ r, g, b });
        const back = parseCssColor(text);
        if (!back || back.r !== r || back.g !== g || back.b !== b) {
          fail(`${text} → ${JSON.stringify(back)}`);
          return;
        }
        if (formatHex(back) !== text) { fail(`reformat drifted at ${text}`); return; }
      }
    }
  }
});

// Alpha survives a full trip through the 8-bit hex representation.
check("format ∘ parse is stable over alpha", (fail) => {
  for (let a8 = 0; a8 < 256; a8++) {
    const a = a8 / 255;
    const text = formatHex({ r: 20, g: 20, b: 19, a });
    const back = parseCssColor(text);
    if (!back || Math.abs(back.a - a) > 1 / 255) {
      fail(`a=${a.toFixed(4)} → ${text} → ${back && back.a}`);
      return;
    }
  }
});

// ===== Report =====

for (const r of rows) {
  console.log(`${r.result.padEnd(5)} ${r.case}${r.detail ? " — " + r.detail : ""}`);
}
console.log(failures === 0 ? "\nedit-color: all checks passed" : `\nedit-color: ${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
