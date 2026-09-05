title: Audio settings and clip import
covers: project audio configuration, clip import settings, fixing platform audio traps
excludes: playing audio (needs Play mode), mixers and effects (not exposed)

# Audio

Project: **{{project}}** · build target {{platform}}.

## Import settings are the half that matters

`audio.clips` lists clips with load type, compression, quality and sample-rate setting.
`audio.setImportSettings` changes them, on one clip or a whole folder, optionally as a platform
override.

The rules of thumb worth having:

| Clip | Load type | Compression |
|---|---|---|
| short SFX (< 1 s) | Decompress On Load | PCM or ADPCM |
| medium (1–10 s) | Compressed In Memory | Vorbis |
| music and ambience (> 10 s) | **Streaming** | Vorbis |

A 40 MB WAV set to Decompress On Load costs its full uncompressed size in memory at scene load. The
same clip streamed costs a buffer. That is the whole game.

## The WebGL trap, and its fix

WebGL has one compression format — AAC — and re-encodes at the platform sample rate. Encoder padding
means a clip authored to loop seamlessly **audibly jitters at the loop point** unless its rate is
pinned:

```
unity_run("build.validateTarget", { platform: "WebGL", checks: ["audio"] })
unity_run("audio.setImportSettings", { path: "Assets/Audio/Loops", platform: "WebGL", overrideSampleRate: 44100 })
```

`build.validateTarget` finds them; this tool fixes them in bulk. Nothing about this failure appears
in a build log — it is discovered by listening to a build, which on this project costs about 25
minutes.

## Not undoable

Import settings are asset metadata, and `SaveAndReimport` is not on Unity's undo stack. The previous
settings exist only in version control. Prefer a folder-at-a-time change you can review in a diff
over a project-wide one you cannot.

## One listener

`audio.settings` counts AudioListeners. More than one active listener makes Unity pick one
arbitrarily and warn — a common cause of "audio works in one scene and not the other" after
additive scene loading.
