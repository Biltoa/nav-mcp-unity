title: Scenes and the hierarchy
covers: reading the hierarchy, selecting objects, opening and saving scenes
excludes: creating or editing objects (see gameobject), components (see component), assets (see assets)
tools: scene.query, scene.count, scene.info, scene.list, scene.roots, scene.children, scene.open, scene.setActive, scene.save, scene.create

# Scenes and the hierarchy

Project: **{{project}}** · Unity {{unityVersion}} · render pipeline **{{pipeline}}** · build target {{platform}}.

## Read with a selector, not a dump

`scene.query` is the tool to reach for. It takes a path selector and a field projection, and returns
only what you asked for. Dumping a scene is how a single read turns into tens of thousands of
tokens; a projected query answers the same question in a few hundred bytes.

```json
{ "select": "//Canvas//Button[active]", "fields": ["name", "path"] }
```

`scene.count` is cheaper still when you only need "how many".

Selector grammar (full detail in the `scene.query` sub-skill):

| Form | Meaning |
|---|---|
| `/Root` | a scene root with that name |
| `/Root/Child` | a direct child |
| `//Button` | any descendant named Button, at any depth |
| `//Canvas//Button` | Buttons under any Canvas |
| `//*` | everything |

Predicates chain and all must hold: `[active]` `[inactive]` `[root]` `[leaf]` `[has:T]`
`[missing:T]` `[tag:T]` `[layer:L]` `[name*:s]` `[name^:s]` `[name$:s]`.

Fields are built-ins (`id name path active activeInHierarchy tag layer parent childCount position
worldPosition rotation scale components prefab`) or `Component.property`, e.g. `Rigidbody.mass`,
`Image.sprite`. A `Component.property` on an object without that component returns nothing for that
object rather than failing the query.

## Where a read is answered

`scene.query` and `scene.count` are normally answered from the daemon's mirror — about 1 ms rather
than ~95 ms — and keep working while the Editor recompiles. Every response says so in
`meta.source`, with `staleMs`. Queries that need component property values go live automatically.
Pass `verify: true` to force a live round trip. Details in the `mirror` sub-skill.

## Choosing a read

| Question | Tool |
|---|---|
| How many objects match X? | `scene.count` |
| Which objects match X, and what are their Y? | `scene.query` |
| What scenes are open, and are they dirty? | `scene.list` |
| Quick orientation in an unfamiliar scene | `scene.info` |
| Immediate children of one object | `scene.children` |

## Writing to scenes

`scene.open` refuses to replace unsaved scenes unless you open additively — it will never prompt,
because a modal dialog in the Editor blocks the message pump and wedges the whole bridge.

`scene.save` overwrites the user's file, so it requires `confirm: true` **and** the `full` profile.
Do not save a scene the user did not ask you to save. Creating and deleting objects does not need a
save to be visible; the user can undo everything in one step if each of your changes went through a
batch.

## Worked examples

Count every renderer that has no material assigned:

```json
{ "select": "//*[has:Renderer]", "fields": ["path", "MeshRenderer.sharedMaterial"], "limit": 200 }
```

Find inactive UI under a specific canvas:

```json
{ "select": "//HUD//*[inactive]", "fields": ["path", "components"] }
```

Everything at the scene root, one level only:

```json
{ "select": "/*", "fields": ["name", "childCount"] }
```
