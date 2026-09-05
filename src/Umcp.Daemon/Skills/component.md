title: Components and their properties
covers: adding and removing components, reading and writing serialized properties
excludes: creating the GameObject itself (see gameobject), materials (see material)

# Components

## Property values are real JSON

Properties go in as the types they are. Numbers are numbers, vectors are arrays, object references
are paths or ids:

```json
{ "target": "Enemy", "type": "Rigidbody",
  "props": { "mass": 80, "useGravity": false, "centerOfMass": [0, 0.5, 0] } }
```

Writes go through `SerializedObject` so they participate in undo and mark the object dirty
correctly, falling back to reflection for properties Unity does not serialise. If a property name
does not exist on either path, the call succeeds and reports it in `warnings` rather than failing
the whole operation — check `warnings` when a change appears not to have landed.

Naming: pass the inspector-ish name (`mass`, `useGravity`, `isTrigger`). Unity's internal
`m_`-prefixed names also work (`m_Mass`), and the reader returns the friendly form.

## Object references

Anywhere a property takes a Unity object:

| Value | Means |
|---|---|
| `"Assets/Materials/Rock.mat"` | that asset |
| `"Level/Props/Crate"` | that scene object |
| `"#-9298"` | that instance id |
| `null` | clear the reference |

## Reading without drowning

`component.get` elides properties still at their default value unless you pass
`includeDefaults: true`. Most of a naive component dump is defaults, which is where the bulk of a
138 KB scene read came from. Better still, ask for exactly what you want:

```json
{ "target": "Player", "type": "Rigidbody", "fields": ["mass", "drag", "constraints"] }
```

Or read across many objects at once with `scene.query` and `Type.property` fields, which is one
round trip instead of N.

## Gotchas

- `component.add` refuses to add a second component to a type marked
  `[DisallowMultipleComponent]`, and says so — use `component.set` on the existing one.
- `component.remove` removes the first match; pass `all: true` for every match.
- `component.list` reports missing scripts as `<missing script>` rather than skipping them, which
  is usually the thing you were looking for.
- Enum properties accept the name (`"Continuous"`) or the index. Names are matched
  case-insensitively and a wrong one lists the valid values.
- Some property types (arrays, nested structs, curves, gradients) are not settable this way. They
  return `E_PROP_UNSUPPORTED`; use `unity.script` for those.
