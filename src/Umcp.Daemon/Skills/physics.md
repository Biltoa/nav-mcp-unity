title: Physics settings and spatial queries
covers: gravity and solver settings, raycasts, overlap queries, layer collision
excludes: adding rigidbodies and colliders (component.add), moving objects (transform)

# Physics

Project: **{{project}}** · pipeline {{pipeline}}.

There are no wrappers here for adding a Rigidbody or setting its mass — that is `component.add` and
`component.set`, and a wrapper that adds nothing is a schema you pay for plus a second place for the
behaviour to drift. What is here is what those cannot express.

## Queries run in edit mode, on the real scene

`physics.raycast` and `physics.overlap` read the colliders in the open scenes. **Nothing enters Play
mode** — an Editor that has to be running to answer a question has changed the thing you asked
about.

```
unity_run("physics.raycast", { origin: [0, 10, 0], direction: [0, -1, 0], maxDistance: 50 })
unity_run("physics.overlap", { center: [0, 1, 0], radius: 5 })
```

Two things routinely surprise people here:

- **Colliders must exist.** A mesh with no collider is invisible to every query, so an empty result
  usually means "nothing has a collider there", not "nothing is there". Check with
  `scene.query` and `[has:Collider]`.
- **Static batching and prefab instances do not matter.** Queries hit collider geometry, which is
  independent of how the object is drawn.

`layers` restricts the query by layer *name*, not by mask arithmetic; an unknown name is an error
listing the layers this project actually has, rather than a silent zero mask.

## Settings

`physics.settings` reads gravity, solver iterations, contact offsets and the simulation mode.
`layerMatrix: true` adds the layer pairs set to ignore each other — off by default because it is a
32×32 matrix and almost always noise.

Those are project settings, not scene state: they apply to every scene in the project, and this tool
set does not write them. Change them in Project Settings, deliberately, with the reason recorded
somewhere a future reader can find it.
