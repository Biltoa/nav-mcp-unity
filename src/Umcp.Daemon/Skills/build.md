title: Build targets and pre-build validation
covers: checking a platform before building, build scenes, graphics APIs, platform-specific asset traps
excludes: making a build (not exposed), player settings editing (not exposed)
tools: build.validateTarget, build.settings, build.scenes, build.lastReport

# Build

Project: **{{project}}** · pipeline **{{pipeline}}** · active target **{{platform}}**.

## Why this exists

A WebGL build of this project takes about 25 minutes, and none of the failures below produce a
build error. They produce jittering audio, magenta geometry, grey text and clipped highlights *in
the player*, which is to say they are discovered by a human looking at a device.

`build.validateTarget` checks them statically, in seconds:

| Check | Finds |
|---|---|
| `scenes` | no scenes in the build, all scenes disabled, a scene file that no longer exists |
| `audio` | WebGL clips that will be resampled — AAC re-encoding adds padding, so a loop audibly jitters unless the sample rate is pinned |
| `shader` | `#pragma target 4.5` or higher while the target includes GLES3; the material silently becomes the error shader on device |
| `text` | TextMeshPro text below size 18 using a *Mobile* SDF shader, which renders small glyphs as grey boxes |
| `emissive` | materials whose emission peaks above ~1.8 while an ACES tonemapper is in the project — above that, ACES clips and yellows, and further intensity does nothing |

```
unity_run("build.validateTarget", { platform: "WebGL" })
unity_run("build.validateTarget", { platform: "Android", checks: ["shader", "emissive"] })
```

Every finding names the asset, says what will go wrong, and says what to change. Severity is
`error` when the thing is certainly broken on that platform, `warning` when it depends on content,
`info` when it is a judgement call.

## What it does not do

- It does not build, and it does not change any setting. It is a read.
- It does not enter Play mode. Nothing in this tool set does — that is how the Editor lost its
  D3D12 device and crashed during the evaluation that started this project.
- The ACES check only fires when an ACES tonemapper is actually found in an open scene's volumes.
  Absence of evidence is reported as no finding, not as "safe".
- It knows nothing about your gameplay. It is a list of traps this codebase has fallen into, not a
  certification.

## Reading the graphics APIs

The report includes the target's graphics API list, because half the checks depend on it. If
`OpenGLES3` is in the list for Android, the shader-model check applies; drop GLES3 (Vulkan only)
and it does not. That is a real choice with device-support consequences — the tool states the
trade-off and leaves it to you.

## The build list and the last build

`build.settings` reads the target, the scripting backend, the colour space and the scene list, with
each scene's `enabled` flag and whether the file still exists on disk. `build.scenes` edits that
list — `add`, `remove`, `enable`, `disable`.

`build.lastReport` summarises the most recent build from `Library/LastBuild.buildreport`: result,
duration, total size, and the largest content grouped by file extension. That last number is the one
worth acting on — it is usually textures, and usually two or three of them.

The report is copied out of `Library/` to be imported and **deleted again immediately**, because
nothing this tool writes may stay under `Assets/`: Unity's importer will try to read a file while it
is still being written, which is exactly the "Amount of processed bytes does not match file size"
failure recorded in the plan.
