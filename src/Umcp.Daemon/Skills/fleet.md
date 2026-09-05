title: Editors, projects and process lifecycle
covers: several projects at once, opening and closing editors, crashes, restarts, editor versions
excludes: anything inside a project (see scene, assets, diagnostics)

# The fleet

One daemon, one port, many editors. Everything below is reached through `unity_projects`.

Project: **{{project}}** · Unity **{{unityVersion}}**.

## Identity

A project is identified by a **GUID** minted once into `ProjectSettings/UnityMCP.json`, never by a
port. Port-per-project identity is exactly how a bridge launched from one project ends up serving
another one, and it is the failure this design exists to avoid.

Target resolution, in order:

1. the explicit `project` argument on `unity_run` / `unity_batch` / `unity_script`
2. the session default, set with `unity_projects(use: "<id>")`
3. the only connected editor
4. otherwise an error listing the candidates — never a guess

With more than one editor connected, **pass `project` explicitly**. It costs nothing and it is the
difference between "the object was created" and "the object was created somewhere".

## Looking around

- `unity_projects()` — connected editors and their health.
- `unity_projects(discover: true)` — also the projects on disk that the Hub knows about and every
  installed Editor version. Opt-in, because it reads the filesystem and returns a long list.

`health` is the **last completed round trip**, never the socket state:

| health | meaning |
|---|---|
| `ok` | ticking |
| `degraded` | no main-thread tick for 5 s |
| `blocked` | no tick for 8 s or more — almost always a modal dialog; `blockingWindow` names it when it can be read. **A human has to dismiss it.** |
| `reloading` | mid domain reload. Reads still work, from the mirror |
| `gone` | the socket closed |

## Opening a project

```
unity_projects(open: "D:/Projects/My Game")
```

What the daemon does, in order, and why each step is there:

1. Refuses a path that is not a Unity project, and refuses one whose `Temp/UnityLockfile` is
   **held** — Unity allows one Editor per project. A lockfile left behind by a crash is not held,
   and is not treated as open.
2. Matches `ProjectSettings/ProjectVersion.txt` against the installed Editors, preferring an exact
   version and falling back to the newest of the same `major.minor`. Any inexact match is reported
   as a warning, because opening a project in a different Editor upgrades it.
3. Adds `com.umcp.agent` to `Packages/manifest.json` **before launch**, keeping a
   `manifest.json.umcp-backup`. Unity resolves the manifest at startup; a *running* Editor only
   re-resolves when its window regains focus, so installing into a live Editor needs a human click.
4. Launches `Unity.exe -projectPath "<path>"` and then **reads the process's own command line back**
   to prove the path arrived intact. An unquoted path with spaces reaches Unity as several
   arguments, and Unity then exits with code 0 without opening anything — a silent success that
   looks exactly like a slow start. A mismatch kills the process rather than leaving it running
   against the wrong project.
5. Waits for the handshake, then returns the new `projectId`.

If the Editor launches but never handshakes you get `E_HANDSHAKE_TIMEOUT` with the process state and
the title of any window it is showing. The usual cause is a startup modal — a crashed session
reopens with *"Recovering Scene Backups"*, which blocked one of ours for about fourteen minutes.
The daemon never raises modals of its own, and it never clicks one away for you.

## Closing and restarting

```
unity_projects(close: "<id or path>", save: true)
unity_projects(restart: "<id or path>")
```

- Close asks the Editor to quit **from inside**, saving first when `save: true`. With unsaved
  changes and no `save`, it refuses and names the dirty scenes; nothing is discarded silently.
- `restart` works on an editor that is already dead — that is the post-crash case it is for.
- Both need the `full` profile.

## Crashes

The daemon notices a dead Editor, keeps the mirror as stale, and reports it. It does **not** restart
anything unless asked:

```
unity_projects(use: "<id>", autoRestart: true)
```

Auto-restart is bounded: **at most two restarts in ten minutes, per project**. An Editor that dies
during startup dies again on restart, and an unbounded supervisor turns one bad Library into a
launch loop. When the breaker trips it says so and stops.

Note what does *not* happen: the daemon has no parent-PID watchdog and never exits with an Editor.
Killing Unity is not an event that touches the daemon, the queue, the catalog or the mirror.

## What an operation sees while this is going on

A domain reload and a crash look identical for a few seconds — the socket closes in both. The
dispatcher holds the operation either way, then replays it with its idempotency key when an editor
for that project reappears. So a call spanning a reload is a slow call, and a call spanning a
crash-plus-auto-restart is a slower one, not an error the caller has to code around.
