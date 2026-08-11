# Design tokens & theming — design & implementation

How every colour, typeface, radius, shadow and duration in the probe's chrome is
declared, and how a theme replaces the ones that vary. Replaces ~45 hardcoded
values spread across four files with two tiers of CSS custom properties.

---

## The defect

The palette existed in five places at once, and none of them knew about the
others:

| | held | how it drifted |
|---|---|---|
| `content.css` | 59 colour literals, 16 distinct hex values | the source of truth by accident, not by design |
| `content.js` | 25 more, baked into `CLAWD_SVG` / `CLAWD_MINI` string literals | uppercase (`#C27C5C`), so a lowercase grep missed them |
| `background.js` | the badge colour | browser chrome, can't read CSS at all |
| `test/harness.html` | its own copy of surface / text / accent | already stale in places |
| `icons/generate-icons.js` | `#7C3AED` | **a purple from before the terracotta identity** |

That last row is the proof: the icon generator had been wrong for at least one
rebrand and nothing caught it, because there was nothing to catch it *with*.

Three more symptoms of the same cause:

1. **The font stack was written out six times** and the motion curve
   `0.15s cubic-bezier(0.4, 0, 0.2, 1)` eight times. Changing either meant a
   find-and-replace and hoping.
2. **The one token block that existed was scoped to a single selector.**
   `#ccp-label` declared `--ccp-xs` through `--ccp-gap-section` — so nothing else
   could use them, and `--ccp-lg: 14px` sat declared-but-unreferenced while
   `.ccp-parent-btn` wrote `padding: 0 14px` by hand ten lines away.
3. **Adding a second palette was impossible** without duplicating the stylesheet.

### Measured

| | before | after |
|---|---|---|
| colour literals in `content.css` | 59 | **0** |
| colour literals in `content.js` | 25 | **0** |
| font stack declarations | 6 | **1** |
| motion curve declarations | 8 | **1** |
| selectable themes | 1 | **8** |
| declarations to add a theme | a stylesheet | **19** |

Enforced by `node test/tokens.mjs` — 71 checks, 0 failures. `node test/sim.mjs`
still reports 138/138, which is the evidence that no geometry moved.

---

## The design

### 1. Two tiers, and only one of them is themed

`tokens.css` is `:root` scales followed by one block per theme.

**Tier 1 — 40 theme-invariant tokens.** Type scale, spacing, radii, motion,
z-index layers, opacity states. A theme cannot change these, which is the point:
Dracula should recolour the chrome, not resize it.

**Tier 2 — 19 semantic tokens, redeclared per theme.** Surface, a four-step text
ramp, accent trio plus its ink, two syntax colours, error plus its ink, two
shadows, a swatch border, and Clawd's body and legs.

The split is what makes the contract checkable. "Every theme declares exactly
these 19" is a test; "the theme looks right" is not.

### 2. Alpha variants are derived, never declared

The accent appeared at six different alphas (`.15 .25 .3 .5 .75 .85`) and the
surface at three. Declaring all of them per theme would make each theme ~35 lines
and near-impossible to keep coherent — nine chances to paste a slightly wrong
hex.

```css
border-color: rgb(from var(--ccp-accent) r g b / 0.5);
```

Relative colour syntax, Chrome 119+. Available unconditionally since the Firefox
target was dropped in 1.2.0. **This is the single decision that keeps a theme to
19 declarations** — every alpha follows its base automatically, so a theme cannot
have a border that disagrees with its own accent.

### 3. One attribute, because custom properties survive `all: initial`

`data-ccp-theme` on `<html>`, and every token block keys off it. All five injected
roots — `#ccp-overlay-container`, `#ccp-label`, `#ccp-toolbar`,
`#ccp-settings-btn`, `#ccp-toast` — inherit from there. No per-root plumbing.

This works because of one specific guarantee. Four of those roots set
`all: initial`, and **`all` does not reset custom properties** (CSS Cascade 4
§3.2 states it explicitly). Regular inherited properties *are* reset, which is
why `font-family` and `box-sizing` are still redundantly re-declared on
`.ccp-bar`, `.ccp-bar button` and `.ccp-parent-btn` — that redundancy is load
bearing, not leftover.

Had it gone the other way, the token block would have to be duplicated at each
`all: initial` boundary, and this document would say so instead.

Theme blocks are keyed on `[data-ccp-theme="…"]` rather than
`:root[data-ccp-theme="…"]`, so a theme can be scoped to any subtree. The
settings page relies on it: each pill's swatch carries its own
`data-ccp-theme` and draws its stripes from `var(--ccp-surface)`,
`var(--ccp-accent)` and the two syntax colours. **The swatch is not a picture of
the palette, it is the palette** — no hex is repeated in the settings page at
all.

### 4. `system` resolves in JS, not in a media query

`matchMedia("(prefers-color-scheme: dark)")` picks one of the two terracotta
blocks and writes the *resolved* id into the attribute, with a `change` listener
so it follows the OS live.

The alternative — wrapping every block in `@media` — would double the file and
put two copies of each palette one edit apart. It would also make the settings
page lie: the preview shows what the attribute selects, so resolving in JS means
the preview and the page agree by construction.

This also matches the precedent already set in `content.css` for `.ccp-compact`,
where a JS-toggled class beat a media query because the JS needed to know.

### 5. Geometry stays in JS — the one deliberate exception

`GEOMETRY` in `content.js` keeps `margin`, `gap`, `pair`, `minLabelHeight`,
`narrowToolbar`, `radiusFallback`, `maxSweepDiagonal`, `redlinePillOffset`,
`redlineGuideOvershoot`, `redlinePillMargin`, `tetherGap`, `tetherTick`,
`tetherTickLoud`, `tetherThick` and `tetherStub` as JavaScript numbers.

Not an oversight. `computeChromeLayout()` is a **pure function**, mirrored in
`test/placement.mjs` and validated over 8280 configurations with no DOM present.
A pure function cannot call `getComputedStyle`, so a CSS custom property is
unreachable from the place these values are actually consumed. Moving them would
buy tidiness and cost the spec. `computeRedline()` follows the same arrangement:
pure, mirrored in `test/redline.mjs`, swept over ten thousand element pairs. So
does `computeTether()`, mirrored in `test/tether.mjs` — and there the sweep is
not just a regression net but the safety argument itself, since `tetherGap` is
the clearance that keeps Edit Mode's chrome off the border it is editing.

The rule, stated once: **values the layout algorithm reasons about live in
`GEOMETRY`; values that only paint live in `tokens.css`.** `--ccp-ring: 2px` is a
token because it draws a stroke; `GEOMETRY.gap: 6` is not because the solver does
arithmetic on it. The redline's 1px stroke is CSS; its pill offset is `GEOMETRY`,
because the solver adds and clamps it.

`test/placement.mjs` mirrors four of them, `test/redline.mjs` the three
`redline*` keys, `test/tether.mjs` the five `tether*` keys. Change one, change
both — the harness's live sweep, the redline sweep and the tether sweep are what
catch the drift.

User preferences reach the solver the same way the constants do: as arguments.
`computeRedline()` takes an `opts` parameter (pill offset, guides, zero pills)
built by the caller from `redlinePrefs` — the solver itself never reads storage,
a token, or a global, so the sweep can parameterize it freely.

### 6. Three things are never themed, on purpose

Each carries a comment at its definition, or the next sweep absorbs it.

| value | why it stays fixed |
|---|---|
| `--ccp-checker` | the chequerboard behind a translucent swatch. The swatch reports the *page's* colour, so its backdrop must stay a neutral reference or the tool misreports what it is inspecting |
| `--ccp-mask` | a stencil, not a colour — `mask-image` reads only its alpha. Theming it could do nothing useful and could break the mask |
| `resolveBackgroundColor()`'s `#ffffff` | the browser's default page background, reported as a fact about the page |
| `--ccp-picker-white` / `--ccp-picker-black` / `--ccp-hue-ramp` | the edit panel's colour space, not its decoration. The saturation square is white toward one edge and black toward the other because that is what saturation and value *mean*, and the rail runs the spectrum because that is what hue is. Tinting any of them would make the picker report a colour the page will not get |

The colour swatch's *fill* is written inline from JS for the same reason. Only its
border follows the theme.

### 7. Motion is part of the token set, so reduced-motion is part of the contract

Four durations and one curve are tokens. That made an existing gap obvious: the
`prefers-reduced-motion` query disabled `#ccp-ants` and `.ccp-spin` but **not
Clawd's 24s walk or 1.1s bob**, so the mascot kept pacing for anyone who had
asked their OS for stillness. The marquee was missing too. Both are now in the
query.

Anything animated must be listed there. There is no automated check for this one
— it is the weakest link in this document. The settings page keeps its own
inventory under the same contract: every transition and animation it declares
dies in the `prefers-reduced-motion` block at the end of
`settings/settings.css`, zeroed rather than softened. The redline layer stays off the list
by design: its dashed guides deliberately don't march. It does *glide* — the
hover box, lines and pills tween between hover targets on the same
`--ccp-duration`/`--ccp-ease` curve as the selection overlay — but that is a
positional transition, the same category as the overlay glide, which this block
has never disabled. Redline snaps out of its glide while tracking scroll, so
the tween never reads as lag.

### 8. Contrast is checked, and one shortfall is recorded rather than hidden

Eight hand-tuned palettes is exactly how an unreadable theme ships, so
`test/tokens.mjs` computes WCAG 2.1 ratios for ten foreground/background pairs
per theme. Two tiers:

| tier | pairs | floor | rationale |
|---|---|---|---|
| text | `text`, `text-dim`, `text-muted`, `syntax-id`, `syntax-class`, `on-accent` on `accent`, `on-error` on `error` | **4.5:1** | AA for body text |
| non-text / transient | `text-faint`, `accent` on `surface`, `on-accent` on `accent-dark` | **3:1**, warn under 4.5 | `accent` doubles as a border and icon colour, where 3:1 is the correct AA bar; `accent-dark` is only the `:active` fill, held for as long as a mouse button is down |

**`--ccp-text-faint` does not reach AA in seven of the eight themes** — 3.39:1 in
the default. This is inherited, not introduced: it is the shipped 1.2.0 grey, and
`terracotta-dark` must stay pixel-identical. Raising it to 4.5:1 would collapse
the info panel's four-step text hierarchy into two, because `text-muted` sits at
5.14:1 and there is no room between them.

So it is reported as a WARN on every run rather than quietly passed. Ten warnings
stand today. The honest summary: **the de-emphasised 10px readout is below AA,
and fixing it is a redesign of the label's hierarchy, not a token change.** A
High Contrast theme is the intended answer and is not in this release.

Finding this is what the checker is for. It also caught five genuine failures in
the new palettes before they shipped — Nord's and Solarized's error inks, and
three accent/ink pairs — all corrected in `tokens.css`.

### 9. The badge is the one place a hex is legitimately duplicated

`chrome.action.setBadgeBackgroundColor` is browser chrome. It cannot read
`tokens.css`, so `background.js` holds a `BADGE_ACCENT` map. That is real
duplication, so `test/tokens.mjs` asserts it matches every theme's `--ccp-accent`
and that neither side has an entry the other lacks. A new theme with a forgotten
badge fails the run.

---

## Adding a theme

1. Copy any block in `tokens.css` and change the 19 values. Order them
   surface → text ramp → accent → syntax → status → shadows → mascot, as the
   others do, so the blocks diff cleanly.
2. Add `{ id, name }` to `THEMES` in `settings/settings.js`. No colours — the
   pill's swatch reads them from the block you just wrote.
3. Add the accent to `BADGE_ACCENT` in `background.js`.
4. `node test/tokens.mjs`. It will tell you which of the 19 you missed and which
   pairs are unreadable.

Light themes need their **shadows re-tuned, not inverted**. The dark themes'
`rgba(20, 20, 19, 0.6)` reads as a smudge on a light surface, which is why
`--ccp-shadow-card` and `--ccp-shadow-bar` are themed values rather than derived
from the surface.

---

## Adding a setting

The measuring preferences set the pattern; a new setting is four sites, all flat:

1. One `chrome.storage.local` key per setting — the `theme` convention. Add its
   roster (legal values, default first) to `REDLINE_PREFS` in `content.js` — or a
   sibling map for a new group — and to its mirror in `settings/settings.js`.
2. Consume it where it acts. Values the redline solver reasons about enter
   `computeRedline()` through the `opts` parameter, which is what keeps the
   function pure and the `test/redline.mjs` sweep honest (§5). Paint-only values
   should be a class toggled on `<html>` and a rule in `content.css`, like the
   quiet overlay.
3. A row in the settings sheet: label, leader, control — a segmented group for
   named values, switch for booleans — plus a `data-hi` hook if the section's
   preview can show the effect. The segmented control takes any number of
   options: its arrow keys walk the roster in the direction pressed and wrap,
   which is the same behaviour for a pair and the correct one for the Editing
   section's three.
4. The `storage.onChanged` listeners on both sides keep open tabs and second
   settings windows in step; a redline setting must also repaint a measurement
   the user is holding at that moment (`scheduleRedline()` on change), and an
   edit setting re-renders an open panel (`renderEditControls()`).

Everything that spans the sheets — storage reads, painting, keyboard, live sync
— runs off `ALL_PREFS`, so a new setting gets all four for free once its roster
is in the map. There are three rosters now: `REDLINE_PREFS`, `EDIT_PREFS` and
`COPY_PREFS`. Only the last needs no repaint on change — copy preferences are
read at the moment a button is pressed, so keeping the object current is the
whole of its listener.

The settings page never learns the extension's logic. The measuring vignette is
hand-drawn geometry and the only shared code is `formatRedlineValue`, mirrored
with a change-both comment, same as the placement spec. The Editing specimen
goes one step further and *is* the panel — the real markup rendered through
`content.css` — so the two settings act on it exactly as they act on the live
one, and it cannot drift into showing chrome that no longer exists.

The Copying preview is the third answer to the same problem, for a preview made
of text rather than pixels. It assembles a real payload, which means the parts
that decide a payload's *shape* — `COPY_ORDER`, `renderCopyHeader`, `copyTrim`,
`fenceBlock`, `assemblePayload` — live there too, mirrored and checked by
`test/mirror-drift.mjs` (its roster reaches `settings/settings.js` as well as
`test/`). What is *not* mirrored is any of the code that finds those values: the
fixture hands over literal strings, the same seam the vignette keeps.

Those five are pure because they take the preference object rather than reading
the module's own, exactly as `computeRedline` takes its `opts` (§5). That is
what lets `test/copy-format.mjs` sweep all 96 combinations of the shape settings
and lets the settings page preview them without either one reproducing the rule.
A new copy setting that changes the payload's shape belongs in one of those five,
or it will have to be written three times.

---

## The payload is a pointer, and props is the one exception

Everything the copy payload reports **names** something: a file and line, a
component chain, a greppable anchor, a selector, a position among siblings.
`getHandlers` is the sharpest case — it reports `onClick=handleUpgrade`, the
function's *name*, and never the expression bound to it. That is not squeamishness
about size. A pointer that names constructs can be pasted anywhere; a payload
carrying values is carrying whatever happened to be on the page.

The props snapshot breaks that rule on purpose, because sometimes the value is
the question — which of twelve identical rows, and what is actually in it. So it
exists, and it is fenced off accordingly: **off by default, in none of the three
presets, never computed unless asked for, and shallow** — a nested object prints
as `{…}` rather than as its contents, which is the difference between a shape and
an API response. The settings row says all of this in the sheet, where the
decision is being made, rather than in a document nobody reads first.

The same restraint decides `page:`, which has always reported a full URL only on
a dev origin and a bare path everywhere else (`isDevOrigin`). Switching a field
on is consent for that field; it is not consent for the tool to get looser about
everything else.

---

## Edit Mode writes to the page, and that changes the contract

Everything else this extension does is additive: it appends its own chrome and
reads the page. Edit Mode is the first feature that reaches in and changes the
user's DOM, which is a different kind of risk — a stray write that nothing
restores leaves the page altered after the tool is gone. Five rules hold it.

**One door.** Every host-page write goes through the `Edit Apply` section of
`content.js`, and `test/edit-audit.mjs` parses the file's own section banners to
prove it: `setProperty`, `removeProperty`, and `setAttribute`/`removeAttribute`
of `"style"` or `"class"` may appear there and nowhere else. Those verbs were
absent from the file before Edit Mode, which is what makes the rule exact rather
than heuristic — chrome positions itself with direct assignments and
`classList`, and the audit ignores all of it. It also refuses to pass vacuously:
if the section stops using a verb, the audit says so. **This is why the colour
picker writes `node.style.backgroundColor = …` instead of `setProperty`** — a
receiver-aware audit would be fragile, so the picker simply does not need the
verb.

**Restoring is byte-exact.** The `style` and `class` attribute strings are
recorded once at first touch and put back verbatim, so a page that shipped
`style="color:red"` gets that attribute back rather than a normalised rewrite.
The double `removeAttribute` in `restoreElement` is not redundant: once an
inline block has been written through CSSOM, Chrome's first removal empties it
but leaves the attribute node, and an element the tool had finished with would
still carry a visible `style=""`.

**Switching off puts the page back.** Edits outlive deselection and outlive
leaving Edit Mode — that is the point, since the panel is a tuning surface and
the delta block is what you take away from it. They do not outlive the tool.
`deactivate()` runs `resetAllEdits()` and empties the undo and redo stacks, so
the tool leaves nothing behind but the page it found. Note this makes the last
rung of the Escape ladder destructive: Escape steps picker → panel → selection →
off, and that final step reverts. Copy the block before taking it.

`deactivate()` reaches Edit Mode's teardown by calling `deselectElement()` rather
than nulling `selectedElement` itself. That is not tidiness — `deselectElement()`
is the only place carrying "a selection ending ends Edit Mode and redline", and
the shortcut is what once left the panel on screen with `editing` still true, the
five capture-phase pointer guards still attached, and the user's page inert until
they reloaded it.

**Never claim a token that isn't there.** The resolver reports a design token
only when its resolved value equals the computed value; a length that depends on
layout is `null` rather than a guess, a family of one is dropped because a scale
you cannot step along is not a scale, and a value between rungs claims nothing.
A utility-class step is applied and then *checked*: `.card p` outranks
`.text-lg` on specificity often enough that a swap which silently does nothing
is the common case, so when the computed value did not move, the class comes off
and the delta reports the value instead. Claiming the swap would be advice that
does nothing in the source either.

**The extension's own tokens are not the page's.** `tokens.css` and
`content.css` ride along on every page as content scripts, so the stylesheet
walk skips them by URL, with the `ccp-` namespace filtered as a backstop.
Without that, `--ccp-accent` gets offered as a fill for someone's card. The
namespace filter carries more weight now than it did: token discovery asks the
element, and the element cannot tell our custom properties from the page's.

**Ask the element, not the stylesheets.** Discovery used to walk
`document.styleSheets` for `--` names and then resolve each against whatever was
selected. That made the token layer only as good as its read access, and it was
worse than that in practice: a design system behind an `@import`, in a shadow
root, or on a CDN produced nothing at all, silently. Custom properties inherit,
so the element already knows its own token universe — including everything
declared in a sheet no script may read, because the browser applies it
regardless. `collectElementTokens` enumerates that, and the walk is left holding
only the two things an element genuinely cannot report: which class means which
value, and the text of the winning declaration, which is the only place a
`var()` can be seen.

That distinction is what decides how much the fetch below is really worth.

**A blocked stylesheet is fetched, not mourned.** A cross-origin sheet without
CORS throws on `.cssRules`, and a content script's own fetch is refused the same
way, so the service worker's `host_permissions` is the only route. It runs after
the panel has already opened, on what the page could read by itself, and folds
the result in when it lands — a slow CDN costs a stepper that appears a beat
late, not a panel that will not open. The recovered rules are re-walked in place
rather than appended, because source order is what the cascade comparison reads.

What it buys is narrower than it looks, and worth stating so nobody widens the
permission expecting more: the sheet's *custom properties already reached the
element* and needed no fetch. Only class rules and declaration text are actually
recovered. `PRIVACY.md` describes exactly what is requested and what is not.

**The cascade has more than three levels.** `findWinningDeclaration` ranked by
importance, then specificity, then source order. Layers sit above specificity —
an unlayered declaration beats a layered one however specific the layered one is
— and every Tailwind v4 or shadcn page is built out of layers, so the wrong
declaration won and the `var()` read out of it named the wrong token. A wrong
token is worse than no token. Ordering *between* named layers needs the `@layer`
statement that declares them and is not modelled; layered-versus-unlayered is.

**A shorthand utility is indexed under its longhands.** CSSOM lists
`padding: 1rem` as four `padding-*` declarations, so `.p-4` is stored under
`padding-top` and never under `padding` — while the linked padding control asks
about `padding`. The two could not meet, so no shorthand-setting utility class
was ever detected or ever formed a family. That is every Tailwind spacing class.
`.text-lg` worked the whole time because `font-size` is already a longhand, and
that is what made a missing edge look like partial support. `FIRST_LONGHAND_OF`
is the other half of `SHORTHAND_OF`, and the lookup now goes both ways.

One trap worth naming, because it silently disabled the whole token layer once:
CSS Nesting gave every `CSSStyleRule` a `cssRules` list, so `if (rule.cssRules)`
no longer means "this is a group rule". Detect by rule type, and read a style
rule's own declarations whether or not it also has children.

---

## The Advanced section reaches past CSS, and the door still holds

The panel's Advanced section tunes what the rest of the panel cannot see: a
WebGL program's uniforms behind a `<canvas>`, and the custom properties feeding
a gradient, filter or paint worklet. The CSS half is unremarkable by design —
a custom property override is `setProperty("--wave-amp", …)`, which is the same
door, the same registry, the same delta lines as any other declaration. The
shader half is the second kind of host-page write this extension has, and it
was built to keep the five rules above intact rather than to earn exceptions
from them.

**The agent, and why it exists.** A content script lives in Chrome's isolated
world: it shares the DOM but not the page's objects, so it can see a canvas and
nothing of the context, programs or uniforms behind it. `shader-agent.js` is
injected into the MAIN world on demand — first selection of a canvas in Edit
Mode — and speaks to the content script over `postMessage` with a per-probe
nonce. Overrides are applied at draw time, not by rewriting the page's uniform
calls: just before each draw on the probed context, the agent writes the
overridden values through its own uniform locations. That keeps the hook
surface to `useProgram` plus the draw calls, sidesteps the fact that locations
the page cached before injection can never be mapped back to names, and makes
freezing a page-driven `u_time` the same mechanism as nudging a constant.

**One door, still.** The two bridge messages that perform a write —
`CCP_SHADER_SET` and `CCP_SHADER_CLEAR` — are sent from the Edit Apply section
and nowhere else, and `test/edit-audit.mjs` pins the literals there exactly as
it pins `setProperty`. Undo, the reset dots, reset-all and the delta block all
work off the same registry entries as CSS edits; a driven uniform's `before` is
a sentinel meaning "the page's own loop", so undoing a takeover hands the value
back rather than pinning yesterday's clock.

**Uniform edits are session-bound, and that is honest rather than convenient.**
A CSS edit is parked in the element's style attribute; a uniform override
exists only while the agent enforces it at each draw. So leaving Edit Mode
tears the session down — the agent restores every original — and the registry
and history stop claiming those edits, because a claimed edit the page no
longer wears is exactly the lie `staleEdits` exists to catch, and no computed
style can catch it for a uniform. Copy the block before closing the panel; the
panel's copy button is only reachable while it is open, so the flow enforces
its own rule. Switching the extension off needs no special case at all.

**The failure modes are owned, not hoped away.** An extension reload kills the
isolated world silently and would strand a frozen shader, so the content script
heartbeats and the agent restores everything after ten silent seconds. A page
that wrapped the draw prototypes after us would lose its own wrapper if we
restored ours, so teardown checks the slot still holds our function and
otherwise leaves a delegating no-op behind. A relink invalidates every location
the agent holds, so `linkProgram` is watched and the panel told to let go. Two
limits are accepted and stated: a multi-pass renderer gets its dominant pass
tuned, not all of them; and in lazy mode a shader that drew once before Edit
Mode opened is recovered read-only through `CURRENT_PROGRAM` — or fully, when
the user opts into the `document_start` registration ("deep shader capture"),
which records context creation from page load and does nothing else until
probed. The one residual risk in lazy mode — `getContext("webgl2")` on a canvas
that truly has no context locks it to WebGL — is taken only for the single
canvas the user selected, only after a whole observation window saw no draws.

## Type styles: the composite is the token

The token layer's original sin was pretending a design system hands out one
number at a time. It doesn't: `.text-lg` carries size and leading together, a
`--heading-md` stem carries three values, and treating those as unrelated
dials produced a concrete bug — every value a multi-property class declared
was poured into one name-keyed family, so `text-sm`'s line-height sat as a
fake rung in the font-size ladder. The fix and the feature are the same
change: a **type style** is a first-class entity (name, source kind, resolved
constituents), and the values a style owns are carved out of the per-property
families. One value, one owner.

Three sources, equal citizens: multi-declaration single-class rules (CSSOM
pre-expands `font:` shorthand into longhands, so that third source costs
nothing), and custom-property stems grouped by a role vocabulary
(`--heading-md-size/-weight/-leading`). Ladders hold the familiar family
rules, lifted: same source kind only — stepping must never switch write
mechanisms mid-climb — font-size as the axis and sort key, aliases collapsing,
two rungs to step. A solo style is named but grows no arrows: a family of one
is still not a scale, but silence about the most designed thing on the
element would be the wrong kind of modesty.

**Claiming extends the house rule.** A style is claimed only when its source
is *in force* — the class actually worn, the vars actually referenced by
winning declarations — never on value coincidence, exactly as a colour that
merely equals a token claims nothing. In force with every constituent
matching computed reads "on"; in force with deviation reads "modified" and
names the drifted properties. The var half of "in force" walks winning
declarations, so it is cached per render; the class half is a Set lookup and
stays live.

**Two ways back, two controls.** A cell's reset keeps its sacred meaning —
revert *my* edit to its found state, even when found means drifted. The style
chip, when drifted, conforms: every drifted constituent written back to the
style's value in one gesture, meaningful precisely when the page shipped the
drift and no dot is lit. Stepping and conforming ride one registry entry
under the pseudo-property `type-style` — the same arrangement text and
uniforms use — so a composite step is one undo entry and one delta line:

    # type style: text-lg → text-xl (size 18→20)
    # type style: text-lg (modified) → text-lg (leading 32→28)

The name leads because the source edit is that name; the parenthetical echoes
only the constituents that moved. When a class swap doesn't take (the page
outranking its own utility class, the same case the single-prop stepper
guards), the step falls back to writing the rung's values and the line
reports values, not the name — a claimed swap that did nothing would be
advice that does nothing in the source either.

The typography group is the only consumer so far, wearing the round-three
grid: micro-labelled cells, filled ticks for style-owned values, hollow for a
cell's own single-prop token, a dashed border for covered-but-drifted, and a
caption that names whatever the pointer touches. Loose tokenized cells step
on the wheel — the grid has no room for the ‹ › stepper, and the caption
carries the naming. The model is deliberately property-agnostic: shadow and
spacing composites are the same entity with different constituents, waiting
on nothing but their own UI round.

## Verification

```sh
node test/tokens.mjs       # 71 checks: literals, completeness, undeclared vars,
                           # badge drift, contrast
node test/sim.mjs          # 138/138 — proves the refactor moved no geometry
node test/edit-audit.mjs   # host-page writes still live in one section
node test/edit-tokens.mjs  # the token resolver, over three real corpora
node test/edit-color.mjs   # picker round trips, bounded by 8-bit quantisation
node test/edit-deltas.mjs  # the shape of the block the panel copies
node test/cdp.mjs          # everything DOM-bound, in a real browser
```

`cdp.mjs` is where the token layer is actually tested, because every interesting
failure it has ever had was a browser behaviour rather than a logic error. The
fixture (`test/edit-harness.html`) is deliberately built out of the shapes that
used to report nothing — a `calc()` scale, an `oklch()` palette, a theme scope,
an `@import`, a grouped selector, a cross-origin sheet — because for a long time
it contained only the one shape that worked, and a fixture like that cannot fail.

When adding to it, check the new case fails before the fix as well as passing
after it. Several of these were written, seen green, and only then discovered to
be asserting something that was already true.

Then, in the browser — a `content.js` or `content.css` edit needs an *extension*
reload, not just a page reload:

1. **The default must be pixel-identical to the previous release.** This is the
   whole regression surface of the refactor; screenshot-diff if in doubt.
2. Switch theme in the options tab — an already-open probe tab re-themes with no
   reload, via `chrome.storage.onChanged`.
3. `System` follows an OS light/dark flip live.
4. Per theme: the label's swatches still show *page* colours, and light-theme
   shadows read as shadows.
5. Turn on reduced motion at the OS level: Clawd stops walking and bobbing, and
   the undo flash holds still instead of pulsing — it stays *visible*, because
   it is the only way an undo on an off-screen element announces itself, and
   zeroing it would remove information rather than motion.
6. Edit Mode, in `test/edit-harness.html` (which runs the real `content.js`
   against a fake page, so this needs no extension reload): tune something,
   Escape back out, and confirm the element carries no `style` attribute —
   through undo, the per-property dot, and Reset All alike.

## Out of scope

- Custom user-authored themes, or a colour picker.
- Per-site theme overrides — the preference is global.
- A High Contrast theme. It needs contrast *targets* driving the palette, not a
  palette hoping to clear them.
- Theming inside cross-origin iframes.
- Tokenising `test/select-parent-fixture.html`. It stands in for an arbitrary
  website under inspection; if its colours followed the theme it would stop being
  a fair test of what the info panel reports.
