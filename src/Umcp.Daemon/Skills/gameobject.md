title: Creating and editing GameObjects
covers: create, delete, rename, duplicate, reparent, active state, tags, layers, finding objects
excludes: transforms (see transform), components (see component), prefab assets (see assets)

# GameObjects

Project: **{{project}}** · Unity {{unityVersion}} · render pipeline **{{pipeline}}**.

## Batch, almost always

The Editor drains its whole message queue inside a single tick, so N queued operations cost about
the wall time of one. Measured on this project: 32 creates as one batch, 125 ms. The same 32 as
sequential calls, roughly 3,200 ms.

So: **one discrete change → `unity.run`; several known changes → `unity.batch`; anything needing a
loop, a filter or an aggregate → `unity.script`.**

A batch is also one undo group, so the user gets one Ctrl+Z for the whole thing rather than 32.

## Dependent operations inside a batch

`"$1"` in a later op refers to the result of op 1 (1-based). `"$1"` alone resolves to that object's
instance id; `"$1.path"` reads a named field of its result. This is what lets a batch stay a single
round trip even when its steps depend on each other.

```json
{ "atomic": true, "returns": "ids", "ops": [
  { "op": "gameobject.create", "args": { "name": "Enemy", "primitive": "Capsule" } },
  { "op": "component.add",     "args": { "target": "$1", "type": "Rigidbody" } },
  { "op": "component.set",     "args": { "target": "$1", "type": "Rigidbody", "props": { "mass": 80 } } },
  { "op": "transform.set",     "args": { "target": "$1", "position": [0, 3, 0] } }
] }
```

`atomic: true` reverts the entire group if any op fails. `returns` controls what comes back:
`"none"`, `"ids"` (default), `"summary"`, `"full"`. For mutations, prefer `none` or `ids` — you
already know what you asked for.

## Addressing an object

Every tool that takes a `target` accepts three forms:

| Form | Example | Notes |
|---|---|---|
| instance id | `"#-9298"` | exact, survives renames, does **not** survive a domain reload |
| scene path | `"Level/Props/Crate"` | exact and readable |
| bare name | `"Crate"` | first match, breadth-first over open scenes |

A name that matches nothing produces `E_TARGET_NOT_FOUND` with `didYouMean` candidates. Names are
case-sensitive. When you are about to act on several objects, get their ids from `scene.query`
first and address by id.

## Things that surprise people

- `gameobject.create` with no `primitive` makes an empty GameObject; with one it makes a primitive
  (`Cube`, `Sphere`, `Capsule`, `Cylinder`, `Plane`, `Quad`) which arrives **with a collider and a
  renderer already attached**.
- `gameobject.setTag` fails if the tag is not already defined in the Tag Manager. It will not
  create tags behind the user's back.
- `gameobject.setLayer` has `recursive` and it defaults to **false**; layer changes usually want to
  apply to children too.
- `gameobject.delete` removes children with it, and it is undoable — but a batch's undo group
  collapses everything into one step, which is usually what you want.
- Creating objects does not dirty a saved scene until the user saves. Do not save on their behalf.
