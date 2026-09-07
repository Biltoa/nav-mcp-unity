title: Animator controllers and state machines
covers: reading a controller, creating one, states, parameters, transitions
excludes: authoring animation clips (not exposed), Timeline, playing animations (needs Play mode)

# Animation

`animator.controller` is one tool with an `action` discriminator, not six tools:

```
animator.controller({ action: "info",         path })
animator.controller({ action: "create",       path })
animator.controller({ action: "addState",     path, state, clip? })
animator.controller({ action: "addParameter", path, parameter, parameterType })
animator.controller({ action: "addTransition",path, from, to, parameter?, greaterThan?/lessThan?/equals? })
animator.controller({ action: "setDefault",   path, state })
```

## The order that works

1. `create` the controller (its first state becomes the default).
2. `addState` for each state, giving `clip` where there is one.
3. `addParameter` **before** any transition that uses it — a transition condition naming an
   undeclared parameter is refused with the parameter list, rather than silently creating one.
4. `addTransition` between states, with a condition.
5. `setDefault` if the entry state should not be the first one added.

## Conditions, and the one that catches everyone

A transition with **no** condition gets `hasExitTime = true`: it fires when the clip finishes. That
is right for Attack → Idle and wrong for Idle → Run, which should fire on a parameter. This tool
sets `hasExitTime` to false as soon as you give it any condition, and reports which it chose.

`equals` behaves differently by parameter type, deliberately: on a `Trigger` it means "when
triggered", on a `Bool` it means "when true" (or "when false" for `equals: false`). Passing
`greaterThan` on a Bool is a schema-valid nonsense the Animator will simply never satisfy.

## Reading first

`action: "info"` returns every layer, state, clip, parameter and transition with its conditions —
usually a few hundred tokens, and always cheaper than guessing at state names. State and parameter
names are case-sensitive, and a wrong one comes back as an error listing what exists.

## Undo

Controller edits are asset writes. They are **not** on Unity's undo stack, and this tool says so in
its metadata rather than implying a Ctrl+Z that will not work. Version control is the undo here.

# Clips, blend trees and layers

Three more tools, same shape — one `action` each.

```
animation.clip     ({ action: "info" | "create" | "setCurve" | "addEvent" | "sample", path, … })
animation.blendTree({ action: "info" | "create" | "addMotion", path, state, … })
animation.layer    ({ action: "list" | "add" | "setWeight" | "setMask", path, layer, … })
```

## Clips

`create` makes an empty clip asset. **Pass `loop: true` at creation if it should loop** — looping
lives in the clip's import settings, not on the clip object, and setting `wrapMode` afterwards does
nothing for an Animator. This is the most common "my animation does not loop".

`setCurve` takes a property path and matched `times` / `values` arrays:

```
animation.clip({ action: "setCurve", path: "Assets/Animation/Bob.anim",
                 property: "m_LocalPosition.y", times: [0, 0.5, 1], values: [0, 0.4, 0] })
```

The property is the *serialised* name — `m_LocalPosition.y`, not `position.y`. Curves get smoothed
tangents, because linear tangents on a hand-made curve look like a bug to whoever opens the clip.
`sample` evaluates the curves at a time without entering Play mode, which is the only way to check
"does this actually reach 1.0" from a tool call.

## Blend trees

A blend tree is a *state* whose motion is a tree, so `create` makes both at once:

```
animator.controller({ action: "addParameter", path, parameter: "Speed", parameterType: "Float" })
animation.blendTree({ action: "create",  path, state: "Locomotion", parameter: "Speed" })
animation.blendTree({ action: "addMotion", path, state: "Locomotion", clip: ".../Idle.anim", threshold: 0 })
animation.blendTree({ action: "addMotion", path, state: "Locomotion", clip: ".../Run.anim",  threshold: 6 })
```

The blend parameter **must already exist**; a tree bound to a parameter nothing declares looks
correct and never blends, so this is refused rather than auto-created. 1D children are kept sorted
by threshold, because Unity blends between *adjacent* children and an unsorted tree blends the
wrong pair. For 2D trees pass `position: [x, y]` instead of `threshold` and set `blendType` and
`parameterY`.

## Layers

`add` creates a layer with a weight and a blending mode; `setMask` points it at an AvatarMask so an
upper-body layer only drives the upper body. Weight 0 means the layer does nothing — that is the
usual reason a second layer "has no effect".

Layers are read back and written whole: `controller.layers` is a copy, and editing an element in
place is a no-op. That is handled here, but it is the same trap in any `unity_script` that touches
layers.
