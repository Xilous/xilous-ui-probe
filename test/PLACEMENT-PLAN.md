# Selection chrome placement — design & implementation plan

How `#ccp-label` and `#ccp-toolbar` get positioned when an element is hovered or
selected. Replaces two independent positioners with one layout pass.

---

## The defect

Two positioners, written separately, that don't know each other exists — and
that disagree about which side they prefer:

| | `updateLabel` (`content.js:587`) | `positionToolbar` (`content.js:773`) |
|---|---|---|
| 1st choice | **above** the element | **below** the element |
| 2nd choice | below | above |
| 3rd choice | — | pin to viewport bottom |
| Clamping | horizontal only | horizontal + vertical |

They avoid each other in exactly one configuration — label above, toolbar below —
which requires room on *both* sides at once:

```
rect.top >= labelHeight + 6     and     rect.bottom <= viewportHeight - 54
```

Anything else collides or escapes the viewport. With a five-line label that safe
band is `vh - 152` px, so **any element taller than that fails on every page**.

### Four failure regimes

1. **Both flip below** — element top < ~98px. The label can't fit above, so it
   flips to `rect.bottom + 6`; the toolbar independently picks `rect.bottom + 8`.
   They land 2px apart. Overlap is structural — it does not depend on their heights.
2. **Both stay above** — element bottom within ~54px of the fold. The toolbar
   can't fit below, flips above, and lands on the label. *This is the reported bug.*
3. **Label escapes the viewport** — bottom cut off *and* no room above. The label
   flips to `rect.bottom + 6`, which is off-screen, and nothing clamps it
   vertically. Selecting `<body>` puts the label ~6800px below the fold.
   *This is the reported "info panel can't be seen" bug.*
4. **Narrow window** — under ~470px the label (460px max-width) and toolbar
   (~430px) exceed the viewport; the clamp computes a negative left, then
   `if (left < 4) left = 4` forces both to hang off the right edge.

Plus one non-geometric issue: there is no `scroll` or `resize` listener anywhere
in `content.js`. While an element is selected `onMouseMove` early-returns, so
scrolling leaves the highlight, label, and toolbar frozen at stale coordinates.

### Measured

The harness in `test/` runs a 23-case geometry matrix against both algorithms.

| | current | proposed |
|---|---|---|
| 6 viewports × 23 cases | **30 / 138** | **138 / 138** |
| stress: 10 viewports × 6 label heights × 3 toolbar heights, selected + hover | — | **8280 / 8280** |

---

## The design

### 1. Anchor to the visible rect, not the element rect

```js
visible = intersect(elementRect, viewport)
```

Every regime-3 bug comes from anchoring to a `rect.bottom` that is thousands of
pixels below the fold. Anchoring to `visible` makes the whole-page case stop
being special: `visible` *is* the viewport, so the chrome lands on its edges.

If `visible` is empty — selected, then scrolled away — the chrome docks to the
viewport edge the element disappeared behind.

### 2. Choose whole layouts, not per-box positions

One function places both boxes together. Each strategy is accepted only if every
box it renders lands inside the viewport and clears the other. **Collision
becomes impossible by construction rather than by luck.**

Strategies, in order — ordered by how little they intrude:

| # | Strategy | Placement | When it wins |
|---|---|---|---|
| 1 | `outside-split` | label above, toolbar below | the ordinary case |
| 2 | `cluster-below` | both stacked below | element near the top |
| 3 | `cluster-above` | both stacked above | element near the bottom |
| 4 | `inside-split` | label hugs visible top, toolbar hugs visible bottom | element bigger than the viewport |
| 5 | `cluster-inside-top` | both stacked inside the top edge | too short to split inside |
| 6 | `docked` | pinned to a viewport edge | element scrolled out of view |

Strategies 1–3 leave the element completely unobscured. Across realistic desktop
viewports that's **81% of configurations**; the rest are cases where the element
is larger than the screen, so covering ~145px of it costs nothing you could see
anyway. (The case matrix is deliberately weighted toward edge cases — in ordinary
browsing `outside-split` dominates.)

### 3. The toolbar is the hard constraint; the label yields

The toolbar is interactive — clipping it breaks the tool. The label is
informational and degrades gracefully. So when both cannot fit, the label yields:

1. **shrinks** — capped to `vh - 2*M - toolbarH - PAIR` via CSS `max-height` + `overflow: hidden`
2. **disappears** — below `MIN_LABEL_H` (24px) it's hidden entirely and the toolbar takes the whole budget

This makes "the toolbar is never clipped" a total invariant, at any viewport size.

### 4. Hover places the label alone

`updateLabel` runs on hover as well as selection, and there is no toolbar while
hovering. The layout function therefore takes a nullable toolbar box; with it
null, the same strategy ladder places the label by itself. Verified across the
full matrix in both states.

---

## Implementation

### `content.js`

1. **Add `layoutChrome(el, { instant })`** — one pass returning both boxes.
   Port from `test/placement.mjs` (`layoutChrome`), which is the validated
   reference implementation.
2. **Strip positioning out of `updateLabel`** (`content.js:587-603`). It keeps
   building content and drops its own placement entirely.
3. **Delete `positionToolbar`** (`content.js:773-806`); its callers
   (`showToolbar`, `selectParent`) call `layoutChrome` instead.
4. **Measure before placing.** `showToolbar` currently calls `positionToolbar`
   *before* the `requestAnimationFrame` that locks each button's `minWidth`, so
   the toolbar is positioned against a width that then changes. Measure both
   boxes after that frame, or lock widths synchronously.
5. **Track the viewport.** Add rAF-throttled `scroll` (capture + passive) and
   `resize` listeners, active only while `probeActive`. Both re-run
   `updateOverlay` + `layoutChrome`.
6. **Split animate from track.** Selection changes keep the 150ms glide;
   viewport-driven relayout must be instant or the chrome smears while scrolling.
   Toggle a `.ccp-no-transition` class rather than writing inline `transition`.

### `content.css`

7. `#ccp-label` — `max-width: min(460px, calc(100vw - 8px))`, plus `max-height`
   (set inline by the layout pass) and `overflow: hidden` for the shrink step.
8. `#ccp-toolbar` — collapse buttons to icon-only below ~470px so it stops
   exceeding narrow viewports.
9. Add `.ccp-no-transition { transition: none !important; }`.

### Keeping the spec honest

`content.js` can't import `test/placement.mjs` (MV3 content scripts are classic
scripts), so the algorithm exists in two places. The harness's **live sweep is
the anti-drift check**: it drives the real extension and reconciles the measured
rects against the simulation, reporting any case where they disagree. Run it
after any change to placement. If drift becomes a recurring problem, generate a
classic-script `placement.js` from the module in `build.sh`.

---

## Verification

1. Iterate in the simulator until the matrix is green — **done, 8280/8280**
2. Port to `content.js` / `content.css`
3. Reload the extension, open the harness, turn on probe mode, press `r`
4. Confirm the live sweep reports `sim matches live on every case`
5. Spot-check the two originally reported cases by hand

## Out of scope

- Repositioning on DOM mutation (element moves or resizes while selected)
- Chrome placement inside cross-origin iframes
- Touch / coarse-pointer interaction

## Harness

| file | role |
|---|---|
| `test/placement.mjs` | executable spec — current + proposed algorithms, 23-case matrix, evaluator |
| `test/sim.mjs` | headless before/after comparison across viewports |
| `test/harness.html` | browser harness: simulate (`s`), live sweep (`r`), walk cases (`n`/`p`) |

Serve over HTTP (`python3 -m http.server 8765`) — `file://` needs the extension's
"Allow access to file URLs". The harness is keyboard-driven because probe mode
captures every click on the page.
