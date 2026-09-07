# Contributing

Thanks for looking. This file is the short version of what the build enforces, so that a first pull
request fails for real reasons rather than for house rules nobody wrote down.

## Getting a build

```bash
dotnet build UnityMcpTool.sln
dotnet test UnityMcpTool.sln
dotnet run --project src/Umcp.Gui         # the app
```

You need the **.NET 8 SDK** (or newer — the repo builds fine with the 9.x SDK against the 8.0
runtime) and, to exercise anything that touches an Editor, **Unity 6000.0+**.

The 156 tests need no Unity licence. The parts that do — the batch-mode run over the whole catalog
and the fleet measurements — live in `src/Umcp.Bench` and are run by hand against a real Editor,
because a licensed Unity in CI is a cost this project does not carry.

## The three rules the build actually enforces

**1. Regenerate after touching a tool.** The dispatch table, the daemon's catalog and
`docs/TOOLS.md` are generated from the `[UnityTool]` methods:

```bash
dotnet run --project src/Umcp.ToolGen
```

CI fails if the generated files differ from their sources. Do not hand-edit anything under a
`Generated/` folder.

**2. Every tool needs an example and a home.** `umcp-toolgen` refuses to build a tool that has no
`[Example]`, and the catalog tests validate every example against that tool's own generated schema.
Every tool must belong to a skill (`Skill = "scene"`), and every skill must be reachable from the
skill tree. A mutating tool must declare either an undo group or a `NoUndoReason` saying why it
cannot have one.

**3. No Unity API off the main thread.** This is checked by a Roslyn-based tool, not by review:

```bash
dotnet run --project src/Umcp.MainThreadCheck
```

The rule exists because breaking it fails silently: a socket thread that reads
`Application.productName` throws where nothing catches it, tears the connection down, and
reconnects forever with no log line.

## Adding a tool

Tools live in `unity/com.umcp.agent/Editor/Tools/`. Prefer **one tool with an `action`
discriminator** over five small ones — `animator.controller` and `assets.addressables` are the
shape to copy. The reason is in the plan: 356 tools with inconsistent envelopes is worse than 150
good ones, and a family costs one schema instead of six.

```csharp
[UnityTool(Skill = "animation", Id = "animation.clip",
    Summary = "Animation clips: read, create, set curves. action: info | create | setCurve.",
    Mutating = true, Retry = RetryClass.Write,
    NoUndoReason = "Clip edits are asset writes; Unity does not register them with Undo.")]
[Example("{ \"action\": \"info\", \"path\": \"Assets/Animation/Run.anim\" }")]
public static object Clip(
    [Doc("info | create | setCurve")] string action,
    [Doc("Clip asset path")] string path) { … }
```

Then add the guidance a model reads to `src/Umcp.Daemon/Skills/<skill>.md`, regenerate, and run the
tests.

## What good looks like here

- **Errors are for the caller.** An error carries a code, a message a person can act on, the
  parameter at fault and, where it helps, `didYouMean`. `E_TOOL_FAILED: object reference not set`
  helps nobody.
- **Say where an answer came from.** Reads may be served from the daemon's scene mirror; responses
  carry `meta.source`, staleness and epoch so a caller can tell.
- **Never block the Editor's message pump**, and never raise a modal dialog in it. A modal stops
  `EditorApplication.update`, which wedges the whole bridge while TCP stays perfectly healthy.
- **Comments explain why, not what.** Most of the comments in this codebase exist because something
  surprising was measured or something failed silently; keep that bar.

## Commits

Conventional-ish prefixes (`feat:`, `fix:`, `docs:`, `refactor:`) and a body that says what was
measured or what broke. The history is meant to be readable as an engineering log.

## Reporting a bug

The useful ones say what the Editor was doing at the time. `unity_projects` output, the daemon log
(**Settings → Open log folder**), your Unity version and whether the Editor was focused — focus
alone is worth 3.3× on round trips, and a report that mixes focused and unfocused timings cannot be
compared with anything.
