title: UI layout checking
covers: canvases, rect geometry, off-screen and overlapping elements, text contrast, safe area
excludes: creating UI (use gameobject and component), text content (use component.set)
parent: rendering

# UI

`ui.layoutReport` checks the things about a layout that are **geometric**, and therefore decidable
without looking at a picture:

| Check | Finds |
|---|---|
| `offscreen` | a rect entirely outside its canvas — usually an anchor mistake that only shows at another resolution |
| `zerosize` | a rect with no width or height, which is invisible and unclickable |
| `overlap` | sibling graphics overlapping by more than half of the smaller one |
| `contrast` | text below the WCAG AA ratio of 4.5:1 against its nearest opaque background |
| `safearea` | content inside the inset reserved for notches and home indicators (pass `safeAreaInset`) |

```
unity_run("ui.layoutReport", { })
unity_run("ui.layoutReport", { canvas: "HUD", checks: ["offscreen", "contrast"], safeAreaInset: 44 })
```

## How to read it

- **Overlap is `info`, not an error.** A badge over an icon and a panel behind a label are both
  overlaps and both correct. It is a defect when two *interactive* elements overlap.
- **Contrast is computed, not guessed** — relative luminance, the WCAG formula — but only against
  the nearest opaque ancestor graphic. Text over a photograph gets no finding, because there is no
  single background colour to compare with, and inventing one would be worse than saying nothing.
- **Nothing here is about taste.** The report says whether a layout is broken, never whether it is
  good.

## uGUI is read by reflection

This package does not reference UnityEngine.UI, so `Graphic` colours, `Text` and TextMeshPro are
read reflectively. That keeps the agent installable in a project that has no uGUI at all; the cost
is that a custom text component this code does not recognise is simply not checked, rather than
being checked wrongly.

# Rects, layout groups and text

```
ui.rect  ({ action: "info" | "set" | "anchor" | "stretch", target, preset?, size?, position?, pivot?, margin? })
ui.layout({ action: "info" | "horizontal" | "vertical" | "grid" | "fit" | "element" | "remove", target, … })
ui.text  ({ action: "info" | "set", target, text?, fontSize?, color?, alignment?, wrap?, autoSize? })
```

## Anchors are the whole game

`ui.rect` takes the same presets as the Inspector's anchor widget — `topLeft`, `center`,
`bottomRight`, `stretch`, `stretchTop` and so on — because "anchor this to the top-left and make it
220×48" is one call:

```
ui.rect({ action: "anchor", target: "HUD/Health", preset: "topLeft", size: [220, 48], position: [24, -24] })
```

**A stretched axis has no size of its own.** On a stretched axis `sizeDelta` means *inset from the
edges*, not width — which is why setting a width on a stretched element makes it fill the screen
instead. Use `stretch` with a `margin` of `[left, top, right, bottom]` for that case, and `size`
only on axes that are not stretched. `info` reports `stretched: { x, y }` so a caller can tell which
it is dealing with.

## Layout groups

A vertical menu is one call, and the group then owns its children's positions — setting a child's
`anchoredPosition` afterwards does nothing, which is the usual "my layout ignores me":

```
ui.layout({ action: "vertical", target: "Menu/Buttons", spacing: 12, padding: [16, 16, 16, 16] })
ui.layout({ action: "fit",      target: "Tooltip", vertical: "preferred" })
ui.layout({ action: "element",  target: "Menu/Buttons/Play", preferred: [-1, 56] })
```

The group is configured to control child sizes; `expand: true` additionally stretches them along
the layout axis. `-1` in `preferred` leaves that axis alone. `remove` strips every layout component
from an object, which is the fastest way out of a fight with one.

## Text

`ui.text` covers TextMeshPro and legacy uGUI Text through one shape, because a project has one or
the other and a caller should not have to care. It reports which it found as `kind`. `alignment`
takes `left`, `center` or `right` and is mapped to whichever enum that component uses — TMP's
`TextAlignmentOptions` and uGUI's `TextAnchor` do not agree on names.

`autoSize: true` shrinks text to fit its rect. That is the fix for text that overflows on one
language and not another, and it is cheaper than measuring.

## What is not here

Sprites, fonts and materials are assets: use `assets.*` to import them and `component.set` to
assign them. Navigation order, scroll views and masks are not exposed yet — `unity.script` covers
them, and `ui.layoutReport` will still tell you when the result is geometrically wrong.
