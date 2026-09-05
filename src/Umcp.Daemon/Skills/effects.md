title: Particles and VFX Graph
covers: reading and tuning particle systems, reading VFX graphs, setting exposed VFX parameters
excludes: authoring VFX graphs (a node editor, not a tool surface), materials (see material)
parent: rendering

# Effects

Project: **{{project}}** · pipeline **{{pipeline}}**.

## Particles

`particles.info` reads a system: emission, shape, lifetime, speed, size, colour, which modules are
enabled, and the renderer's material and sorting. `particles.set` writes the constants — rate,
lifetime, speed, size, colour, gravity, looping, max particles.

**Constants only.** Where a property is a curve or a two-constant range, `particles.info` says so by
name rather than flattening it to one number, and `particles.set` will not overwrite it. A curve is
a shape somebody drew; replacing it with a constant because that is what the schema could express
is a silent downgrade. Use `unity_script` for curve work.

`maxParticles` is the honest performance dial here, and the one most often left at 1000 by accident.

## VFX Graph

`vfx.info` lists the VisualEffect components, their assets and their **exposed parameters**.
`vfx.set` writes one exposed parameter.

That is the whole surface, on purpose. The implementation being replaced spends **41 tools** on VFX
Graph — `vfx_add_block`, `vfx_add_context`, `vfx_add_operator`, `vfx_connect_slots`, … — which is a
node editor rebuilt one schema at a time: enormous to load, and still not enough for a model to hold
a graph's structure through a keyhole of single-node calls.

Exposed parameters are the interface the graph's author deliberately published. They are what a
scene needs changed, they are named, and they are typed — `vfx.set` asks the graph which type each
one is rather than guessing from the value you passed. Everything else belongs in the VFX Graph
window, or in `unity_script` for the rare programmatic case.

Both tools return `E_PACKAGE_MISSING` when the package is absent, with what to install. The agent
does not reference the package, so it loads fine in a project that has never had it.
