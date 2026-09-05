title: The scene.query selector language
covers: selector syntax, predicates, field projection, pagination, byte budget
tools: scene.query, scene.count

# scene.query in detail

This is the single largest token saver on the read path. The measured problem it replaces: one
`get_scene_info` call on this project returned 138,205 bytes — about 34,500 tokens, roughly 17% of a
200k context window, for one call. A projected query answers the same questions in hundreds of bytes.

## Selector

A selector is a sequence of steps. `/` means "direct child of the previous step"; `//` means "any
descendant of the previous step, at any depth". A selector with no leading slash is treated as `//`.

```
/Level                 a root named Level
/Level/Props           direct children of Level named Props
//Crate                every Crate anywhere
/Level//Crate          every Crate under Level
//*                    everything
/*                     every scene root
```

`*` matches any name. Names are case-sensitive and exact; use `[name*:...]` for substring matching.

## Predicates

Zero or more per step, all must hold.

| Predicate | Matches |
|---|---|
| `[active]` / `[inactive]` | by `activeInHierarchy` |
| `[root]` / `[leaf]` | no parent / no children |
| `[has:Rigidbody]` | has that component |
| `[missing:Collider]` | does not have it |
| `[tag:Player]` | tag |
| `[layer:Water]` | layer by name |
| `[name*:Enemy]` | name contains |
| `[name^:UI_]` | name starts with |
| `[name$:_LOD0]` | name ends with |

Component names accept the short form (`Rigidbody`) or the full name
(`UnityEngine.Rigidbody`). An unknown component name is an error with suggestions, not an empty
result — an empty result would look like a true answer.

## Projection

`fields` decides the cost of the response. Ask for what you will actually use.

Built-ins: `id`, `name`, `path`, `active`, `activeInHierarchy`, `tag`, `layer`, `parent`,
`childCount`, `position`, `worldPosition`, `rotation`, `scale`, `components`, `prefab`.

Component fields use `Type.property` and are read through `SerializedObject` first, falling back to
reflection: `Rigidbody.mass`, `Image.sprite`, `Light.intensity`, `MeshRenderer.sharedMaterial`,
`RectTransform.sizeDelta`.

Default projection is `["name", "path"]`.

## Bounds

`limit` defaults to 100 and is capped at 500. `offset` pages. `maxDepth` limits how far a `//` step
descends. Every response carries `_total`, `_returned`, `_truncated` and, when truncated, a `_hint`
naming the next offset. The daemon additionally caps any response at 32 KB and says so — a
truncated response always announces itself, so a partial answer is never mistaken for a complete one.

## Patterns

Audit, not enumerate — get a count first, then a page:

```json
{ "select": "//*[has:Rigidbody]", "countOnly": true }
```

Then, if the count is small enough to be worth reading:

```json
{ "select": "//*[has:Rigidbody]", "fields": ["path", "Rigidbody.mass"], "limit": 50 }
```

If the count is large, or the question is really an aggregate ("how many of these are heavier than
10?"), stop querying and use `unity.script`: one round trip that returns the number instead of the
rows.
