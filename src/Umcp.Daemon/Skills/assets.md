title: Assets, prefabs and the AssetDatabase
covers: searching assets, folders, moving and deleting, creating and instantiating prefabs
excludes: materials (see material), scenes (see scene)

# Assets

Project: **{{project}}** · build target **{{platform}}**.

## Paths are project-relative and confined

Every asset path must resolve under `Assets/` or `Packages/`. Relative segments are **rejected, not
resolved** — `../` is an error, not a traversal. Use forward slashes.

## Searching

`assets.find` takes Unity's own filter syntax, which is worth knowing:

| Filter | Finds |
|---|---|
| `t:Material` | every material |
| `t:Texture2D Rock` | textures whose name contains Rock |
| `t:Prefab l:Enemy` | prefabs with the label Enemy |
| `ref:Assets/Art/Rock.mat` | assets referencing that one |

Restrict with `folders` and page with `limit`/`offset`. This project has roughly 12,000 assets, so
an unfiltered search is never the right call.

## Prefabs

- `prefab.create` saves a scene object as an asset; `connect: true` (the default) also turns the
  scene object into an instance of the new prefab, which is almost always what is wanted.
- `prefab.instantiate` places an instance and keeps the prefab link. Do not "create a copy" by
  duplicating an instance when you meant to instantiate the asset.
- Intermediate folders are created for you.

## Destructive operations

`assets.delete` and `assets.move` are **not undoable** — the AssetDatabase does not participate in
Unity's undo stack. `assets.delete` therefore requires `confirm: true` and the `full` profile. Ask
the user before deleting anything you did not create in this session.

`assets.refresh` can trigger a compile and therefore a domain reload. That is safe to call
mid-sequence: the daemon holds queued operations across the reload and replays them, so the caller
sees a slow call rather than an error. It is classified as an expensive, compile-class operation and
gets a long timeout.

## Batching asset work

When a batch contains two or more asset-writing operations it is automatically wrapped in
`AssetDatabase.StartAssetEditing`, which Unity documents as taking a bulk import from hours to
minutes. It is deliberately **not** applied to smaller or mixed batches, because deferring imports
breaks any operation that reads back an asset an earlier operation just wrote. The batch result
reports `assetEditing` so this is never invisible.
