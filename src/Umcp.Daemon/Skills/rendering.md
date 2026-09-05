title: Rendering — materials, lighting, effects and UI
covers: routing to the right rendering domain
excludes: the hierarchy itself (see scene), assets on disk (see assets)
tools: none

# Rendering

A grouping node. It exists to keep the domain map affordable: four rendering domains as four
top-level entries cost more of the map than they earn, and the routing question they answer is one
question.

Project: **{{project}}** · pipeline **{{pipeline}}** · target {{platform}}.

| Load | For |
|---|---|
| `material` | material assets, shader properties, `shader.info` |
| `lighting` | scene lighting, lightmap bakes, `setup.litInterior` |
| `effects` | particle systems and VFX Graph |
| `ui` | canvas geometry, `ui.layoutReport`, `setup.uiScreen` |

Two things that cut across all four, and are easy to get wrong once:

- **The pipeline decides the shader names.** This project is on **{{pipeline}}**, so a material
  created with a Built-in shader name renders magenta. `material.create` defaults to the pipeline's
  own lit shader for exactly this reason.
- **`build.validateTarget` is the check that spans them.** Shader model against graphics APIs,
  emissive values against the tonemapper, small text against the mobile SDF shader — those are
  rendering failures that only appear on the target platform, and the build does not report them.
