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
