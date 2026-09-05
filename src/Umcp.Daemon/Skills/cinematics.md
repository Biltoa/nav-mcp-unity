title: Timeline and Cinemachine
covers: reading timelines and their tracks, listing virtual cameras, choosing the live camera
excludes: authoring timeline tracks, camera framing (adjust the vcam in the Inspector)

# Cinematics

## Cinemachine

```
unity_run("cinemachine.cameras")
unity_run("cinemachine.setPriority", { target: "CM vcam Follow", priority: 20 })
```

`cinemachine.cameras` lists every virtual camera with its priority, follow and look-at targets, and
which one is currently live. Two facts it reports that explain most "the camera does nothing"
reports:

- **No CinemachineBrain** on the main camera means virtual cameras do nothing whatsoever. The tool
  says so explicitly when it finds none.
- **Priority decides the live camera**, not hierarchy order and not which one you selected last.
  Highest enabled priority wins.

`cinemachine.setPriority` handles both package generations: Cinemachine 2 stores a plain `int`,
Cinemachine 3 wraps it in a settings struct. If a version exposes neither writably, the tool says
that instead of appearing to succeed.

## Timeline

`timeline.info` reads the PlayableDirectors in the scene: the asset each one plays, its duration,
its wrap mode, and every track with its clips and their start times.

**There is no timeline authoring here, deliberately.** A timeline is a structure with its own
editor; a tool that adds a clip here and a track there produces a sequence nobody can reason about,
and the value of an authored cutscene is precisely in the parts a schema cannot express. Read it,
understand it, then change it in the Timeline window — or in `unity_script`, where the whole API is
available at once.
