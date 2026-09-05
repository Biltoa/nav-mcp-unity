title: Terrain
covers: what a terrain is made of, and the settings that decide what it costs
excludes: sculpting heights, painting layers, placing trees — all of them large irreversible writes

# Terrain

`terrain.info` reads size, heightmap and alphamap resolutions, terrain layers, tree and detail
counts, and the draw settings. `terrain.setDrawSettings` writes the performance dials.

## Why there is no sculpting here

A heightmap edit is a large write to a TerrainData **asset**, and the AssetDatabase does not
participate in Unity's undo stack — so it is irreversible in the only sense that matters. It is also
unverifiable from an agent's position: "raise the ground near the road" either looks right or it
does not, and nothing in a JSON response says which. Reading, auditing, and tuning are useful and
safe; sculpting through a schema is neither.

## The dials, in the order they pay

| Setting | Effect |
|---|---|
| `pixelError` | The cheapest win. 1 → 5 roughly halves terrain triangles with little visible change at distance. |
| `treeDistance` / `billboardStart` | Trees are usually the largest single cost; billboarding earlier is nearly free visually. |
| `detailDistance` / `detailDensity` | Grass is the second largest. Density is multiplicative with everything. |
| `drawInstanced` | GPU instancing for the terrain itself; usually a straight win on desktop. |
| `basemapDistance` | Where per-layer shading stops. Lowering it helps fragment cost on mobile. |

These are all settings on the Terrain **component**, so they are scene state: undoable, and they do
not touch the terrain data asset. `terrain.info` and `build.validateTarget` together tell you
whether a terrain is affordable on the platform you are actually shipping to.
