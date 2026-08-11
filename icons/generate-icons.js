#!/usr/bin/env node
// Generates simple crosshair/probe icon PNGs for the extension.
// Run: node icons/generate-icons.js

const { createCanvas } = require("canvas");
const fs = require("fs");
const path = require("path");

const sizes = [16, 32, 48, 128];

for (const size of sizes) {
  const canvas = createCanvas(size, size);
  const ctx = canvas.getContext("2d");

  const cx = size / 2;
  const cy = size / 2;
  const r = size * 0.35;
  const lineW = Math.max(1.5, size * 0.06);
  const crossGap = size * 0.12;
  const crossLen = size * 0.2;

  // Background circle
  ctx.beginPath();
  ctx.arc(cx, cy, r, 0, Math.PI * 2);
  // Matches --ccp-accent in tokens.css (terracotta-dark). Was #7C3AED, left
  // over from before the terracotta identity — the shipped PNGs never used it.
  ctx.fillStyle = "#d97757";
  ctx.fill();

  // Inner ring
  ctx.beginPath();
  ctx.arc(cx, cy, r * 0.65, 0, Math.PI * 2);
  ctx.strokeStyle = "#ffffff";
  ctx.lineWidth = lineW;
  ctx.stroke();

  // Center dot
  ctx.beginPath();
  ctx.arc(cx, cy, lineW, 0, Math.PI * 2);
  ctx.fillStyle = "#ffffff";
  ctx.fill();

  // Crosshair lines
  ctx.strokeStyle = "#ffffff";
  ctx.lineWidth = lineW;
  ctx.lineCap = "round";

  // Top
  ctx.beginPath();
  ctx.moveTo(cx, cy - r * 0.65 - crossGap);
  ctx.lineTo(cx, cy - r * 0.65 - crossGap - crossLen);
  ctx.stroke();

  // Bottom
  ctx.beginPath();
  ctx.moveTo(cx, cy + r * 0.65 + crossGap);
  ctx.lineTo(cx, cy + r * 0.65 + crossGap + crossLen);
  ctx.stroke();

  // Left
  ctx.beginPath();
  ctx.moveTo(cx - r * 0.65 - crossGap, cy);
  ctx.lineTo(cx - r * 0.65 - crossGap - crossLen, cy);
  ctx.stroke();

  // Right
  ctx.beginPath();
  ctx.moveTo(cx + r * 0.65 + crossGap, cy);
  ctx.lineTo(cx + r * 0.65 + crossGap + crossLen, cy);
  ctx.stroke();

  const out = path.join(__dirname, `icon-${size}.png`);
  fs.writeFileSync(out, canvas.toBuffer("image/png"));
  console.log(`Generated ${out}`);
}
