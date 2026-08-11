// Advanced section — spec for the pure core of shader/CSS mechanism tuning.
//
// uniformRange / uniformLabel / isColorUniform / advancedCssKind /
// formatUniformValue below are transcribed from the "Advanced Detection"
// section of content.js — content scripts are classic scripts, so nothing can
// be imported, only mirrored (same arrangement as edit-deltas.mjs). Change
// them there and change them here; test/mirror-drift.mjs compares the copies.
//
// What these pin down: a GLSL uniform declares no range, so the slider's
// bounds are inferred from observed values and must stay sane across
// magnitudes; a u_/iTime-style prefix is display noise but the full name is
// the greppable anchor; a vec3 is only a colour when both the range and the
// name say so; and a custom property is only a dial when its value parses as
// a number or a colour.
//
// Run: node test/advanced.mjs   (exit 1 on any failure)

// ===== Mirror of the Advanced Detection pure core (content.js) =====

export function uniformRange(samples, isInt) {
  let peak = 0;
  let negative = false;
  for (const v of samples || []) {
    if (!isFinite(v)) continue;
    if (Math.abs(v) > peak) peak = Math.abs(v);
    if (v < 0) negative = true;
  }
  if (isInt) {
    const top = Math.max(10, Math.pow(10, Math.ceil(Math.log10((peak || 1) * 1.5))));
    return { min: negative ? -top : 0, max: top, step: 1, decimals: 0 };
  }
  if (peak === 0) return { min: -1, max: 1, step: 0.01, decimals: 2 };
  const top = Math.pow(10, Math.ceil(Math.log10(peak * 1.5)));
  const raw = top / 200;
  const magnitude = Math.pow(10, Math.floor(Math.log10(raw)));
  const unit = raw / magnitude;
  const step = (unit <= 1 ? 1 : unit <= 2 ? 2 : unit <= 5 ? 5 : 10) * magnitude;
  return {
    min: negative ? -top : 0,
    max: top,
    step,
    decimals: Math.max(0, -Math.floor(Math.log10(step))),
  };
}

export function uniformLabel(name) {
  let s = String(name || "").replace(/\[0\]$/, "");
  const underscored = s.match(/^[ui]_(.+)$/);
  if (underscored) s = underscored[1];
  else if (/^[ui][A-Z]/.test(s)) s = s.slice(1);
  return s ? s[0].toLowerCase() + s.slice(1) : String(name || "");
}

export function isColorUniform(name, type, value) {
  if (type !== "vec3" && type !== "vec4") return false;
  if (!Array.isArray(value) || !value.every((v) => isFinite(v) && v >= 0 && v <= 1)) return false;
  return /color|colour|tint|albedo|diffuse|emissive/i.test(String(name || ""));
}

export function advancedCssKind(value) {
  const s = String(value || "").trim();
  if (!s) return null;
  const m = s.match(/^(-?\d*\.?\d+)(px|deg|%|rem|em|vh|vw|s|ms)?$/);
  if (m) return { kind: "number", value: parseFloat(m[1]), unit: m[2] || "" };
  if (/^#[0-9a-f]{3,8}$/i.test(s)) return { kind: "color" };
  if (/^(rgb|rgba|hsl|hsla|hwb|lab|lch|oklab|oklch|color)\(/i.test(s)) return { kind: "color" };
  return null;
}

export function formatUniformValue(value, type) {
  const isIntKind = /^(int|uint|ivec[234]|uvec[234])$/.test(type);
  const one = (v) => {
    if (!isFinite(v)) return "0";
    if (isIntKind) return String(Math.round(v));
    if (v === 0) return "0.00";
    const a = Math.abs(v);
    if (a >= 100) return String(Math.round(v * 10) / 10);
    if (a < 0.01) return String(Number(v.toPrecision(2)));
    return v.toFixed(2);
  };
  if (type === "bool") return (value && value[0]) >= 0.5 ? "on" : "off";
  const nums = (value || []).map(one);
  if (nums.length <= 1) return nums[0] || "0";
  return `${type}(${nums.join(", ")})`;
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

// ===== 1. uniformRange =====

check("range · a 0–1 parameter gets a 0–1 slider", (fail) => {
  const r = uniformRange([0.2, 0.5], false);
  if (r.min !== 0 || r.max !== 1) fail(`${JSON.stringify(r)}`);
  if (r.step > 0.01) fail(`step ${r.step} too coarse for a unit range`);
});

check("range · headroom scales with magnitude", (fail) => {
  const cases = [
    [[12.4], 100],     // a running clock: room to scrub forward
    [[3], 10],
    [[640, 480], 1000], // a resolution
  ];
  for (const [samples, wantMax] of cases) {
    const r = uniformRange(samples, false);
    if (r.max !== wantMax) fail(`${JSON.stringify(samples)} → max ${r.max}, want ${wantMax}`);
    if (r.min !== 0) fail(`${JSON.stringify(samples)} → min ${r.min}, want 0`);
  }
});

check("range · opens below zero only when a sample has been there", (fail) => {
  if (uniformRange([0.5], false).min !== 0) fail("positive samples must not open a negative range");
  const r = uniformRange([-0.4, 0.8], false);
  if (r.min >= 0) fail(`negative sample ignored: ${JSON.stringify(r)}`);
  if (r.min !== -r.max) fail(`negative range should mirror: ${JSON.stringify(r)}`);
});

check("range · all-zero samples still give a usable dial", (fail) => {
  const r = uniformRange([0, 0, 0], false);
  if (!(r.min < 0 && r.max > 0 && r.step > 0)) fail(JSON.stringify(r));
});

check("range · ints step by one and never fractionally", (fail) => {
  for (const samples of [[3], [0], [250], [-2, 7]]) {
    const r = uniformRange(samples, true);
    if (r.step !== 1 || r.decimals !== 0) fail(`${JSON.stringify(samples)} → ${JSON.stringify(r)}`);
    if (!Number.isInteger(r.max) || !Number.isInteger(r.min)) fail(`non-integer bounds: ${JSON.stringify(r)}`);
  }
});

check("range · steps land on tidy numbers", (fail) => {
  // Whatever the magnitude, the step must be 1, 2 or 5 times a power of ten —
  // a scrub that lands on 0.30000000000000004 reads as broken.
  for (const samples of [[0.7], [7], [70], [700], [0.007]]) {
    const r = uniformRange(samples, false);
    const norm = r.step / Math.pow(10, Math.floor(Math.log10(r.step)));
    if (![1, 2, 5].some((u) => Math.abs(norm - u) < 1e-9)) {
      fail(`${JSON.stringify(samples)} → step ${r.step}`);
    }
  }
});

check("range · decimals agree with the step", (fail) => {
  const cases = [
    [[0.5], 3],   // step 0.005 → three decimals
    [[12.4], 1],  // step 0.5 → one decimal
    [[640], 0],   // step 5 → whole numbers
  ];
  for (const [samples, want] of cases) {
    const r = uniformRange(samples, false);
    if (r.decimals !== want) fail(`${JSON.stringify(samples)} → decimals ${r.decimals}, want ${want} (step ${r.step})`);
  }
});

check("range · garbage samples are ignored, not propagated", (fail) => {
  const r = uniformRange([NaN, Infinity, 0.4], false);
  if (r.max !== 1) fail(`non-finite samples leaked into the range: ${JSON.stringify(r)}`);
});

// ===== 2. uniformLabel =====

check("label · the common prefixes come off", (fail) => {
  const cases = [
    ["u_time", "time"],
    ["u_amplitude", "amplitude"],
    ["uTime", "time"],
    ["iResolution", "resolution"],
    ["iTime", "time"],
    ["i_mouse", "mouse"],
    ["uLightPos", "lightPos"],
  ];
  for (const [name, want] of cases) {
    if (uniformLabel(name) !== want) fail(`${name} → ${uniformLabel(name)}, want ${want}`);
  }
});

check("label · names that merely start with those letters survive", (fail) => {
  // "intensity" starts with i, "uv_scale" starts with u — neither wears a
  // prefix, and stripping would rename them into nonsense.
  const cases = [
    ["intensity", "intensity"],
    ["uv_scale", "uv_scale"],
    ["speed", "speed"],
    ["universe", "universe"],
  ];
  for (const [name, want] of cases) {
    if (uniformLabel(name) !== want) fail(`${name} → ${uniformLabel(name)}, want ${want}`);
  }
});

check("label · array suffix and degenerate names", (fail) => {
  if (uniformLabel("u_color[0]") !== "color") fail(`u_color[0] → ${uniformLabel("u_color[0]")}`);
  if (uniformLabel("") !== "") fail("empty name should stay empty");
  // A name that is nothing but its prefix has nothing left to show; the raw
  // name is better than a blank row.
  if (uniformLabel("u_") !== "u_") fail(`u_ → ${uniformLabel("u_")}`);
});

// ===== 3. isColorUniform =====

check("colour · both gates are required", (fail) => {
  if (!isColorUniform("u_color", "vec3", [0.9, 0.2, 0.1])) fail("a named, ranged vec3 is a colour");
  if (!isColorUniform("baseAlbedo", "vec4", [1, 1, 1, 0.5])) fail("albedo counts");
  // Right range, wrong name: a normalised direction is not a colour.
  if (isColorUniform("u_lightDir", "vec3", [0.3, 0.5, 0.8])) fail("range alone must not decide");
  // Right name, wrong range: an HDR colour is out of the picker's gamut.
  if (isColorUniform("u_color", "vec3", [2.5, 0.2, 0.1])) fail("out-of-range component must disqualify");
  // Right name, wrong shape.
  if (isColorUniform("u_colorMix", "float", [0.5])) fail("a float is never a colour");
  if (isColorUniform("u_tint", "vec2", [0.5, 0.5])) fail("a vec2 is never a colour");
});

// ===== 4. advancedCssKind =====

check("css kind · numbers, with and without units", (fail) => {
  const cases = [
    ["45deg", { kind: "number", value: 45, unit: "deg" }],
    ["12px", { kind: "number", value: 12, unit: "px" }],
    ["0.5", { kind: "number", value: 0.5, unit: "" }],
    ["-3", { kind: "number", value: -3, unit: "" }],
    ["60%", { kind: "number", value: 60, unit: "%" }],
    ["1.5rem", { kind: "number", value: 1.5, unit: "rem" }],
    ["200ms", { kind: "number", value: 200, unit: "ms" }],
  ];
  for (const [input, want] of cases) {
    if (!eq(advancedCssKind(input), want)) {
      fail(`${input} → ${JSON.stringify(advancedCssKind(input))}`);
    }
  }
});

check("css kind · colours in any modern notation", (fail) => {
  for (const input of ["#a94f30", "#fff", "rgb(1 2 3)", "rgba(0,0,0,.5)", "hsl(20 70% 50%)", "oklch(0.7 0.1 40)"]) {
    const got = advancedCssKind(input);
    if (!got || got.kind !== "color") fail(`${input} → ${JSON.stringify(got)}`);
  }
});

check("css kind · what is not a dial is refused", (fail) => {
  for (const input of ["", "auto", "linear-gradient(red, blue)", "url(#x)", "12px 4px", "var(--other)", "calc(1px + 2px)"]) {
    if (advancedCssKind(input) !== null) {
      fail(`${JSON.stringify(input)} → ${JSON.stringify(advancedCssKind(input))}`);
    }
  }
});

// ===== 5. formatUniformValue =====

check("format · GLSL-shaped, two decimals for floats", (fail) => {
  const cases = [
    [[0.35], "float", "0.35"],
    [[12.4], "float", "12.40"],
    [[0], "float", "0.00"],
    [[640], "float", "640"],
    [[0.001], "float", "0.001"],
    [[0.9, 0.2, 0.1], "vec3", "vec3(0.90, 0.20, 0.10)"],
    [[1, 0.5], "vec2", "vec2(1.00, 0.50)"],
    [[3], "int", "3"],
    [[2.6], "int", "3"],
    [[1, 2], "ivec2", "ivec2(1, 2)"],
    [[1], "bool", "on"],
    [[0], "bool", "off"],
  ];
  for (const [value, type, want] of cases) {
    const got = formatUniformValue(value, type);
    if (got !== want) fail(`${type} ${JSON.stringify(value)} → ${got}, want ${want}`);
  }
});

check("format · degenerate input degrades to zero, not NaN", (fail) => {
  if (formatUniformValue([NaN], "float") !== "0") fail(`NaN → ${formatUniformValue([NaN], "float")}`);
  if (formatUniformValue([], "float") !== "0") fail(`[] → ${formatUniformValue([], "float")}`);
  if (formatUniformValue(null, "float") !== "0") fail(`null → ${formatUniformValue(null, "float")}`);
});

check("format · round trips within the print quantum", (fail) => {
  // Two-decimal display is deliberately coarser than the finest scrub step,
  // so the delta may round — but never by more than half a hundredth, or the
  // block would report a value the eye did not choose.
  const r = uniformRange([0.5], false);
  const scrubbed = 0.5 + r.step * 7;
  const printed = formatUniformValue([scrubbed], "float");
  if (Math.abs(parseFloat(printed) - scrubbed) > 0.005 + 1e-12) {
    fail(`${scrubbed} prints as ${printed}`);
  }
});

// ===== Report =====

for (const r of rows) {
  console.log(`${r.result.padEnd(5)} ${r.case}${r.detail ? " — " + r.detail : ""}`);
}
console.log(failures === 0 ? "\nadvanced: all checks passed" : `\nadvanced: ${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
