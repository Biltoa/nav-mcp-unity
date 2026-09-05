title: Positions, rotations and scale
covers: setting and reading transforms, local vs world space, translate, rotate, look-at
excludes: parenting (see gameobject.setParent), RectTransform layout for UI

# Transforms

## Local vs world

Every position and rotation tool takes `space`, which defaults to `"local"`. This trips people up:
setting `position` on a child object with `space: "local"` positions it relative to its parent, not
to the scene origin.

- `transform.set` writes `position`, `rotation` (euler degrees) and `scale`. Scale is always local —
  Unity has no settable world scale.
- `transform.get` returns both spaces at once, plus `lossyScale`, so one read answers both questions.
- `transform.translate` and `transform.rotate` apply a **delta**, not an absolute.
- `transform.lookAt` takes either `at` (another object) or `point` (a world position), not both.

Vectors are JSON arrays of numbers: `[0, 1.5, 0]`. Not strings, not objects.

## Placing many objects

Placing a grid or a ring is a loop, so it is a `unity.script` job, not thirty tool calls:

```csharp
var parent = GameObject.Find("Spawns").transform;
for (int i = 0; i < 24; i++) {
    var go = new GameObject("Spawn_" + i);
    Undo.RegisterCreatedObjectUndo(go, "Spawn ring");
    go.transform.SetParent(parent, false);
    var a = i / 24f * Mathf.PI * 2f;
    go.transform.localPosition = new Vector3(Mathf.Cos(a) * 8f, 0, Mathf.Sin(a) * 8f);
}
return new { placed = 24 };
```

Note `Undo.RegisterCreatedObjectUndo` — scripts are not automatically undoable, so register what you
create if the user might want it back.

## Reading many transforms cheaply

Do not fetch transforms one at a time. Project them:

```json
{ "select": "/Level/Props/*", "fields": ["name", "position", "scale"] }
```
