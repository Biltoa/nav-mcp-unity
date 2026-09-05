# Unity MCP Tool

An MCP server for the Unity Editor, built around one measured finding: **the Editor drains its
whole message queue in a single tick**, so 32 operations cost about the wall time of one. Batching
is the primitive, not a convenience.

Three processes, and the important line is that **the durable state lives outside Unity**:

```
AI client ──stdio──▶ umcp-stdio ──http──▶ umcpd ──tcp──▶ UnityAgent (in-editor package)
                       (shim)            (daemon)         one per project
```

Everything that must survive a recompile — the request queue, the catalog, the retry logic — lives
in `umcpd`, not in the Unity AppDomain that dies on every script change.

Design rationale and the measurements behind it: [UNITY_MCP_TOOL_PLAN.md](UNITY_MCP_TOOL_PLAN.md).
Current status and per-phase results: [PROGRESS.md](PROGRESS.md).
Generated tool reference: [docs/TOOLS.md](docs/TOOLS.md).

## Layout

| Path | What |
|---|---|
| `src/Umcp.Daemon` | `umcpd` — MCP over stdio and HTTP, editor registry, dispatcher, health, tray UI |
| `src/Umcp.Stdio` | `umcp-stdio` — the shim a client spawns; starts the daemon if it isn't up |
| `src/Umcp.ToolGen` | `umcp-toolgen` — reads the `[UnityTool]` methods and generates dispatch, catalog and docs |
| `src/Umcp.Bench` | `umcp-bench` — the measurement harness; every claim in PROGRESS.md comes from it |
| `unity/com.umcp.agent` | the Unity package: connect out, pump the main thread, execute, stream scene deltas |
| `tests/Umcp.Tests` | the quality bar as tests |

## Build

```bash
dotnet run --project src/Umcp.ToolGen   # regenerate dispatch + catalog + docs from the tools
dotnet build UnityMcpTool.sln
dotnet test tests/Umcp.Tests/Umcp.Tests.csproj
```

`umcp-toolgen` must be re-run after adding or changing a tool. It fails the build if a tool has no
input example, or if a mutating tool declares neither an undo group nor a reason it cannot have one.

## Run

```bash
dotnet run --project src/Umcp.Daemon -- --port 8730 --agent-port 8731 --tray
```

Add `--profile full` to enable code mode.

Both ports bind `127.0.0.1` explicitly. A bearer token is minted at start and written to
`%LOCALAPPDATA%\UnityMCP\token` with an ACL granting the current user only; `/health` is the one
unauthenticated endpoint.

## Install the Unity package

Add a `file:` dependency to the project's `Packages/manifest.json`:

```json
"com.umcp.agent": "file:D:/Unity MCP Tool/unity/com.umcp.agent"
```

Nothing is copied into `Assets/`, and the daemon never writes there — Unity will try to import a log
file mid-write if you do. The only file the package adds to a project is
`ProjectSettings/UnityMCP.json`, holding the project's GUID.

Unity resolves a manifest change when the Editor regains focus. The agent connects out on load and
retries with jittered backoff forever, so daemon and Editor can start in either order.

## Register with a client

Direct HTTP (no shim):

```json
{ "mcpServers": { "unity": {
    "type": "http",
    "url": "http://127.0.0.1:8730/mcp",
    "headers": { "Authorization": "Bearer <token from %LOCALAPPDATA%\\UnityMCP\\token>" }
} } }
```

Or via the shim, which starts the daemon on demand:

```json
{ "mcpServers": { "unity": {
    "command": "umcp-stdio.exe",
    "args": ["--port", "8730"]
} } }
```

## The MCP surface

Six tools, ~904 tokens at baseline. The 51 Editor tools are reached through them rather than
exposed individually — the surface being replaced costs ~56,800 tokens before the model does
anything.

| Tool | What |
|---|---|
| `unity_run` | run one Editor tool by id |
| `unity_batch` | N operations, one Editor tick, one undo group; `"$1"` refers to op 1's result |
| `unity_script` | C# executed in the Editor, returning only its conclusion (`full` profile only) |
| `unity_find` | BM25 search across tools and skills |
| `unity_skill` | a domain's guidance plus its tools' schemas; also one tool's schema |
| `unity_projects` | connected editors, their health, and the default target |

### The mirror

The daemon keeps a live model of each project's scene hierarchy, updated by push from Unity's own
`ObjectChangeEvents` stream. `scene.query` and `scene.count` are answered from it — about **1 ms**
instead of ~95 ms — and keep working while the Editor is recompiling, so a domain reload stops
mutations rather than everything.

Every response says where its answer came from:

```jsonc
"meta": { "source": "mirror", "staleMs": 42, "epoch": 19, "revision": 118 }
```

The model holds identity, parentage and sibling order, active state, tag, layer, and component
*type* names. It does not hold component property values, so a query asking for `Rigidbody.mass`
goes live automatically rather than being answered approximately. `verify: true` on `unity_run`
forces a live round trip. `unity_projects(reconcile: true)` compares the model against per-subtree
hashes from the Editor and repairs any drift, reporting exactly which node and which field differed.

Both sides compute selectors and hashes from the same two source files
(`SceneSelector.cs`, `MirrorHash.cs`), compiled into the Unity package and linked into the daemon —
a second copy would drift, and a drifting hash makes reconcile meaningless.

### The skill tree

`unity_skill()` returns a ~400-token map of domains. `unity_skill("material")` returns that domain's
guidance and schemas — around 1,200 tokens — including caveats for the render pipeline the connected
project *actually* uses, read from the Editor rather than assumed. A typical task that loads three
domains costs about 5,000 tokens against a 56,800-token baseline.

Content lives in `src/Umcp.Daemon/Skills/*.md` and is embedded in the binary.

## Profiles

`--profile readonly | standard | full`, default `standard`.

| Profile | Allows |
|---|---|
| `readonly` | non-mutating tools only |
| `standard` | mutations, but not arbitrary code or irreversible writes |
| `full` | everything |

`unity.script`, `assets.delete`, `scene.save`, `scene.create` and `editor.stall` require `full`.
This is a second lock: the first is that both listeners bind `127.0.0.1` and every request carries a
bearer token.

## Benchmarks

```bash
dotnet run --project src/Umcp.Bench -- --port 8730 --json bench-results/run.json
```

Every result records the Editor's focus state, because focus alone is worth 3.3× and mixing focused
and unfocused numbers invalidates the comparison.
