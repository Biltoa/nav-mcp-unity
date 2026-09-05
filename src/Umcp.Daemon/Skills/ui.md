title: UI layout checking
covers: canvases, rect geometry, off-screen and overlapping elements, text contrast, safe area
excludes: creating UI (use gameobject and component), text content (use component.set)

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
