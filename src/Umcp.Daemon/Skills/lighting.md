title: Lighting, lightmapping and interior setups
covers: scene lighting settings, baking, reflection probes, the litInterior composite
excludes: materials and emission (see material), post-processing volumes (not exposed)

# Lighting

Project: **{{project}}** · pipeline **{{pipeline}}**.

## Reading

`lighting.settings` gives the ambient mode, fog, skybox, lightmapper, whether a bake is running,
and a breakdown of the scene's lights by bake type — realtime, mixed, baked. That last count is
usually the answer to "why is this scene slow": realtime shadow-casting lights are the expensive
thing, and they are easy to add by accident.

## Baking

```
unity_run("lighting.bake", { action: "start" })
unity_run("lighting.bake", { action: "status" })
unity_run("lighting.bake", { action: "cancel" })
```

**The bake is asynchronous, always.** A synchronous bake would block the Editor's main thread for
minutes, and every health check in this system reads main-thread ticks — a blocked pump is
indistinguishable from a wedged Editor, and the daemon would start reporting `blocked` on a bake
that is working perfectly. So `start` returns immediately and `status` reports progress.

Two things a bake needs that this tool will not do for you: the scene must be **saved** (lightmaps
are written next to the scene asset), and Auto Generate must be off in the Lighting window.

## `setup.litInterior`

A composite: it reads the room's renderer bounds and places a key light, two dimmer fills on the
opposite side, and a box-projected reflection probe sized to the room.

The proportions are deliberate — fills at roughly a third and a quarter of the key, and the key
off-centre. Two lights of equal intensity from opposite sides read as *no* lighting: everything
flattens, and the room looks worse than it did with one light. If you change anything afterwards,
change the fills, not the key.

Defaults are `Mixed` bake mode at 4000 K, which is a warm interior. `Mixed` and `Baked` lights do
nothing visible until you bake, and the tool says so in its result rather than leaving you to
wonder why the room is dark.
