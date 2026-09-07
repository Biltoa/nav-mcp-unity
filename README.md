<div align="center">

<img src="docs/images/wordmark.png" alt="NAV MCP" width="360">

**Let an AI drive your Unity Editor — without paying 56,800 tokens for the privilege.**

[![ci](https://github.com/Biltoa/nav-mcp-unity/actions/workflows/ci.yml/badge.svg)](https://github.com/Biltoa/nav-mcp-unity/actions/workflows/ci.yml)
[![licence: MIT](https://img.shields.io/badge/licence-MIT-FF8723)](LICENSE)
[![Unity 6000.0+](https://img.shields.io/badge/Unity-6000.0%2B-black)](https://unity.com)

</div>

---

NAV MCP is a local [MCP](https://modelcontextprotocol.io) server for the Unity Editor, plus a
desktop app so you never have to open a terminal to use it. **One server drives every open Unity
Editor at once.**

<img src="docs/images/overview.png" alt="The NAV MCP window, showing the server running" width="100%">

## Why it exists

An MCP server that exposes every Unity operation as its own tool charges the model for the whole
catalog on every single turn. The best-known Unity MCP server ships 356 tools: their names,
descriptions and schemas cost **about 56,800 tokens before the model has done anything**.

NAV MCP exposes **six** tools. The 99 Editor operations are reached *through* them, and guidance
loads on demand, per domain.

|  | NAV MCP | A 356-tool server |
|---|---|---|
| Tool surface, every turn | **~910 tokens** | ~56,800 tokens |
| A scene query | **~1 ms** (from the daemon's live scene model) | ~95 ms |
| 32 operations | **≈ the cost of one** (one Editor tick, one undo step) | 32 round trips |
| Pre-flight a WebGL build | **307 ms** | a ~25-minute build, then the error |

## What you get

- **One server, every project.** Link as many Unity projects as you like; the daemon keeps a
  connection to each open Editor and routes by project id, never by port number.
- **It survives recompiles.** The queue, the catalog and the scene model live outside Unity, so a
  domain reload pauses work instead of losing it. Reads keep answering while Unity is compiling.
- **Batching as the primitive.** The Editor drains its whole message queue in one tick, so
  `unity_batch` sends N operations for roughly the price of one — as a single undo step.
- **You decide what it may touch.** Three modes (`readonly` / `standard` / `full`), plus a tick box
  per tool. Turn off `assets.delete` and the next call to it is refused by the server, not by a
  prompt.
- **Undo.** Every batch is one named undo step, and the app has a button for it.
- **It tells you when Unity is stuck.** "Blocked" means a modal dialog is open, and the app names
  the dialog.

## Install

Download the latest [release](https://github.com/Biltoa/nav-mcp-unity/releases) — the app carries
its own .NET, so there is nothing to install first.

**Windows** — run `NAV-MCP-Setup.exe`. SmartScreen appears once because the build is not
code-signed: **More info → Run anyway**.

**macOS** — open the `.dmg`, drag **NAV MCP** to Applications, then **right-click → Open** the
first time. That is how macOS lets you approve an app that has not been notarised.

Then, in the app:

1. **Start server** — it starts by itself on first launch.
2. **Projects → Link a project…** and pick a Unity project folder. This writes the package
   reference into that project's `Packages/manifest.json` and keeps a backup.
3. **Connections → Connect** next to Claude Desktop, Claude Code or Cursor. Restart that app.

Ask your assistant to run `unity_projects`. It should list your editors and their health.

Full guide, including running the server without the app: **[docs/INSTALL.md](docs/INSTALL.md)**.

## The six tools

| Tool | What it does |
|---|---|
| `unity_run` | One Editor operation by id |
| `unity_batch` | N operations, one Editor tick, one undo step. `"$1"` refers to op 1's result |
| `unity_script` | C# compiled and run in the Editor, returning its conclusion (`full` mode) |
| `unity_find` | Search tools and guidance |
| `unity_skill` | A domain's guidance and schemas, on demand |
| `unity_projects` | Editors, health, and open / close / restart |

The 99 Editor tools behind them are listed in **[docs/TOOLS.md](docs/TOOLS.md)**.

## How it fits together

```
AI client ──stdio──▶ umcp-stdio ──http──▶ umcpd ──tcp──▶ UnityAgent (one per Editor)
                       (shim)            (daemon)              connects out
                                            ▲
                              NAV MCP app ──┘
```

Everything that must survive a script recompile lives in the daemon, not in the Unity AppDomain
that dies on every one. Both listeners bind `127.0.0.1` only, and every request carries a bearer
token that is readable by your account alone.

<img src="docs/images/projects.png" alt="Four Unity projects linked to one server" width="100%">

## Building it yourself

```bash
dotnet build UnityMcpTool.sln
dotnet test  UnityMcpTool.sln          # 156 tests, no Unity licence needed
dotnet run --project src/Umcp.Gui      # the app
```

Packaging: `pwsh scripts/publish.ps1` (Windows) or `scripts/publish.sh --arch both` (macOS). Both
refuse to build a drop whose generated catalog differs from its sources, or whose tests fail.

Contributions welcome — **[CONTRIBUTING.md](CONTRIBUTING.md)** covers the three rules the build
enforces. Design rationale and every measurement quoted above:
**[UNITY_MCP_TOOL_PLAN.md](UNITY_MCP_TOOL_PLAN.md)** and **[PROGRESS.md](PROGRESS.md)**.

## Status

Version 1.0.0. The Windows build is used daily against real projects. The macOS build is produced
and render-checked by CI on every push, but has not yet been opened by a human on a Mac — if you
run one, that feedback is the most useful thing you could send.

Neither build is code-signed yet, hence the one-time warning on each platform.

## Licence

[MIT](LICENSE).
