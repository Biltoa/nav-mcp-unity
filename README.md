<div align="center">

<img src="docs/images/wordmark.png" alt="NAV MCP" width="420">

**one server, every Unity Editor, six tools instead of 356**

Your Unity MCP server bills the model for its whole catalog on every single turn.<br>
NAV MCP charges for six tools and loads the rest when they are asked for.

<a href="https://github.com/Biltoa/nav-mcp-unity/releases/latest"><img src="https://img.shields.io/badge/download-Windows%20installer-FF8723?style=for-the-badge&logo=windows&logoColor=white" alt="Download for Windows"></a>
<a href="https://github.com/Biltoa/nav-mcp-unity/releases/latest"><img src="https://img.shields.io/badge/download-macOS%20.dmg-1B2027?style=for-the-badge&logo=apple&logoColor=white" alt="Download for macOS"></a>

<br>

![tool surface](https://img.shields.io/badge/tool_surface-910_tokens-FF8723)
![editor tools](https://img.shields.io/badge/editor_tools-99-1B2027)
![tests](https://img.shields.io/badge/tests-156-1B2027)
![unity](https://img.shields.io/badge/Unity-6000.0%2B-black)
[![licence](https://img.shields.io/badge/licence-MIT-3FB950)](LICENSE)
[![ci](https://github.com/Biltoa/nav-mcp-unity/actions/workflows/ci.yml/badge.svg)](https://github.com/Biltoa/nav-mcp-unity/actions/workflows/ci.yml)

[See it](#see-it) · [Install](#install) · [Numbers](#numbers) · [Tools](#the-six-tools) · [Permissions](#you-decide-what-it-may-touch) · [Build](#build-it-yourself) · [Docs](docs/INSTALL.md) · [Licence](LICENSE)

</div>

---

## See it

<img src="docs/images/overview.png" alt="The NAV MCP window with the server running" width="100%">

| | 356-tool server | NAV MCP |
|---|---|---|
| **Every turn, before you type** | 356 names + descriptions + schemas — **~56,800 tokens** | six tools — **~910 tokens** |
| **Ask for a GameObject's children** | ~95 ms, waits for Unity's main thread | **~1 ms**, answered from the daemon's live scene model |
| **Make 32 changes** | 32 round trips, 32 undo steps | **one Editor tick, one undo step** |
| **Find out WebGL will fail** | a ~25-minute build, then the error | **307 ms**, before you start |

Same Editor. Same operations. What died was the overhead.

```
  tool surface        ██░░░░░░░░░░░░░░░░░░░░░░░░░░░░   -98%
  scene reads         █░░░░░░░░░░░░░░░░░░░░░░░░░░░░░   -99%
  batched writes      █░░░░░░░░░░░░░░░░░░░░░░░░░░░░░   -97%
  things you install  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░    0
```

## Install

Grab the [latest release](https://github.com/Biltoa/nav-mcp-unity/releases/latest). Everything is
self-contained — no .NET to install first, no Node, no Python.

**Windows** — run the installer. It is per-user, so there is no admin prompt. SmartScreen appears
once because the build is not code-signed: **More info → Run anyway**.

**macOS** — open the `.dmg`, drag **NAV MCP** to Applications, then **right-click → Open** the
first time.

Then, three clicks:

1. **Start server** — already running when the window opens.
2. **Projects → Link a project…** → pick a Unity project folder.
3. **Connections → Connect** → Claude Desktop, Claude Code or Cursor. Restart that app.

Ask your assistant to run `unity_projects`. It lists your editors and their health.

<details>
<summary><b>No app, just the server</b></summary>

```bash
umcpd --profile standard
```

Then point any MCP client at the shim, which starts the daemon on demand and reads the access token
itself:

```jsonc
{
  "mcpServers": {
    "unity": { "command": "C:/path/to/umcp-stdio.exe", "args": ["--port", "8730"] }
  }
}
```

Full guide, including the manual `Packages/manifest.json` route: **[docs/INSTALL.md](docs/INSTALL.md)**.
</details>

## Numbers

Measured on one machine against a licensed Editor, with the Editor's focus state recorded every
time — focus alone is worth **3.3×** on round trips, so numbers that mix focused and unfocused runs
cannot be compared with anything.

| What | Result | Why it is that |
|---|---|---|
| Tool surface at rest | **910 tokens** | Six tools. Guidance and schemas load per domain, on demand |
| `scene.query` | **~1 ms** vs ~95 ms live | Answered from a scene model the daemon keeps, fed by Unity's own change events |
| 32 operations | **≈ one tick** | The Editor drains its whole message queue per tick, so batching is the primitive |
| Code mode, 20 ops | **145 B** vs 6,020 B | C# runs in the Editor and returns its conclusion, not its working |
| WebGL pre-flight | **8 errors, 25 warnings in 307 ms** | Statically detectable platform failures that produce no build error |
| A typical task | **~5,000 tokens** | Three domains of guidance loaded, against a 56,800-token baseline |

## The six tools

| Tool | What it does |
|---|---|
| `unity_run` | One Editor operation by id |
| `unity_batch` | N operations, one Editor tick, one undo step. `"$1"` refers to op 1's result |
| `unity_script` | C# compiled and run in the Editor, returning its conclusion |
| `unity_find` | Search tools and guidance |
| `unity_skill` | A domain's guidance and schemas, on demand |
| `unity_projects` | Editors, health, and open / close / restart |

Behind them: **99 Editor tools** across 22 domains — scene, gameobject, assets, prefabs, materials,
lighting, animation, UI, physics, navmesh, audio, terrain, cinematics, build, diagnostics. Full list
in **[docs/TOOLS.md](docs/TOOLS.md)**.

They are grouped, not enumerated. A state machine is one tool with an `action`, not six tools:

```js
animator.controller({ action: "addTransition", path, from: "Idle", to: "Run",
                      parameter: "Speed", greaterThan: 0.1 })
```

## One server, every project

<img src="docs/images/projects.png" alt="Four Unity projects linked to one server" width="100%">

Link as many projects as you like. The daemon holds a connection to each open Editor and routes by
a project id stored in the project itself — never by port number, which is how a server launched
from one project ends up serving another.

It also survives what Unity does to everything else: the request queue, the tool catalog and the
scene model live **outside** the Unity AppDomain that dies on every script recompile. A domain
reload pauses work instead of losing it, and reads keep answering while Unity is compiling.

When Unity is stuck, it says so in words — `blocked` means a modal dialog is open, and it names the
dialog rather than timing out.

## You decide what it may touch

<img src="docs/images/settings.png" alt="Mode and per-tool permissions" width="100%">

Three modes:

| Mode | Allows |
|---|---|
| `readonly` | look, never touch |
| `standard` *(default)* | changes, but no arbitrary code and nothing irreversible |
| `full` | everything, including running C# and deleting assets |

Plus a tick box per tool. Untick `assets.delete` and the next call to it comes back
`E_TOOL_DISABLED` — refused by the server, not by a prompt the model can talk its way past. No
restart.

Every batch is one named undo step, and there is a button for it.

**Security:** both listeners bind `127.0.0.1` only — there is no option to change that — and every
request outside `/health` carries a bearer token minted at start, readable by your account alone.
Loopback by itself is not enough: any local process, and any web page that resolves a name to
127.0.0.1, can reach a loopback listener.

## How it fits together

```
AI client ──stdio──▶ umcp-stdio ──http──▶ umcpd ──tcp──▶ UnityAgent (one per Editor)
                       (shim)            (daemon)              connects out
                                            ▲
                              NAV MCP app ──┘  start · link · permit
```

| Piece | What it is |
|---|---|
| `umcpd` | The daemon. .NET 8, Kestrel on loopback, MCP over stdio and HTTP, Roslyn for code mode |
| **NAV MCP** | The desktop app. Avalonia — one codebase for the Windows `.exe` and the macOS `.app` |
| `umcp-stdio` | The shim a client spawns. Starts the daemon if it is not up |
| `com.umcp.agent` | The Unity package. Connects out, pumps the main thread, streams scene deltas |

## Build it yourself

```bash
dotnet build UnityMcpTool.sln
dotnet test  UnityMcpTool.sln          # 156 tests, no Unity licence needed
dotnet run --project src/Umcp.Gui      # the app
```

```powershell
pwsh scripts/publish.ps1               # Windows drop into dist/
pwsh scripts/installer.iss             # ...and the installer
```

```bash
scripts/publish.sh --arch both         # macOS .app and .dmg, both architectures
```

Publishing refuses to build a drop whose generated catalog differs from its sources, whose tests
fail, or whose Unity package will not compile against an installed Editor.

**[CONTRIBUTING.md](CONTRIBUTING.md)** covers the three rules the build enforces. Design rationale
and every measurement above: **[UNITY_MCP_TOOL_PLAN.md](UNITY_MCP_TOOL_PLAN.md)** and
**[PROGRESS.md](PROGRESS.md)**.

## Status

**1.0.0.** The Windows build runs daily against real projects. The macOS build is produced and
render-checked by CI on every push, but nobody has opened the window on a Mac yet — if you have one,
that is the single most useful thing you could report.

Neither build is code-signed, hence the one-time warning on each platform.

## Licence

[MIT](LICENSE).
