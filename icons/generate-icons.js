#!/usr/bin/env node
// Generates the extension's icon PNGs: the Xilous Probe mark, a ring gauge on a
// Graphite tile with its arc in the 7d blue and a dot at the centre, the same
// mark the desktop app's app.ico carries.
// Run: node icons/generate-icons.js

const { createCanvas } = require("canvas");
const fs = require("fs");
const path = require("path");

const TILE = "#1c1c1e"; // Graphite background
const TRACK = "#3a3b3f"; // Graphite track
const ARC = "#38a3f5"; // Graphite 7d colour, the probe's accent
const DOT = "#eceff2"; // Graphite text

for (const size of [16, 32, 48, 128]) {
  const canvas = createCanvas(size, size);
  const ctx = canvas.getContext("2d");
  const pad = size * 0.04;
  const c = size / 2;
  const r = size * 0.3;
  const w = size * (size <= 24 ? 0.13 : 0.105);

  ctx.fillStyle = TILE;
  ctx.beginPath();
  ctx.roundRect(pad, pad, size - 2 * pad, size - 2 * pad, size * 0.22);
  ctx.fill();

  ctx.lineWidth = w;
  ctx.strokeStyle = TRACK;
  ctx.beginPath();
  ctx.arc(c, c, r, 0, Math.PI * 2);
  ctx.stroke();

  // From twelve o'clock clockwise through 252 degrees, round caps.
  ctx.strokeStyle = ARC;
  ctx.lineCap = "round";
  ctx.beginPath();
  ctx.arc(c, c, r, -Math.PI / 2, -Math.PI / 2 + (252 * Math.PI) / 180);
  ctx.stroke();

  ctx.fillStyle = DOT;
  ctx.beginPath();
  ctx.arc(c, c, size * (size <= 24 ? 0.075 : 0.06), 0, Math.PI * 2);
  ctx.fill();

  const out = path.join(__dirname, `icon-${size}.png`);
  fs.writeFileSync(out, canvas.toBuffer("image/png"));
  console.log(`Generated ${out}`);
}
