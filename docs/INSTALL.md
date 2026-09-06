# Installing the Unity MCP Tool

Three pieces, in this order: the **daemon**, the **Unity package**, and the **MCP client
registration**. Fifteen minutes, most of it Unity importing.

Requirements: Windows 10/11, the **.NET 8 runtime** (`dotnet --list-runtimes` should show
`Microsoft.NETCore.App 8.x`), and Unity **6000.0 or newer**.

---

## 1. The daemon

From a release drop (`dist/`), or built here:

```powershell
pwsh scripts/publish.ps1        # runs toolgen, the main-thread check and the tests first
```

Put `dist/` wherever you keep tools — `%LOCALAPPDATA%\Programs\UnityMCP` is a reasonable choice —
and start it:

```powershell
umcpd.exe --profile standard
```

You should see one line naming the ports, the tool count and the token file:

```
[umcpd] 1.0.0 · http 127.0.0.1:8730/mcp · agents 127.0.0.1:8731 · 92 tools · profile standard · token in C:\Users\you\AppData\Local\UnityMCP\token
```

Check it: `curl http://127.0.0.1:8730/health` — that endpoint needs no token, everything else does.

### Profiles

| Profile | Allows | Use it when |
|---|---|---|
| `readonly` | reads only | you want an agent that can look and not touch |
| `standard` **(default)** | mutations, but no arbitrary code and no irreversible writes | day to day |
| `full` | everything: `unity.script`, `assets.delete`, `scene.save`, `editor.quit` | you have version control and you mean it |

The daemon binds `127.0.0.1` only — there is no option to change that — and every request outside
`/health` carries a bearer token, minted at start into `%LOCALAPPDATA%\UnityMCP\token` with an ACL
that grants your account only. Loopback alone is not enough: any local process, and any web page
that resolves a name to 127.0.0.1, can reach a loopback listener.

### Starting it at login

Task Scheduler, "At log on", action `umcpd.exe`, arguments `--profile standard --tray`. The tray
build gives you a pause switch and the token. Or start it by hand when you want it; the shim below
starts it for you if it is not running, and passes your `--profile` through — so a client-only
install works, and an explicitly started daemon is used as-is.

---

## 2. The Unity package

Add the package to the project's `Packages/manifest.json`:

```jsonc
{
  "dependencies": {
    "com.umcp.agent": "file:C:/Users/you/AppData/Local/Programs/UnityMCP/com.umcp.agent",
    // ... everything else
  }
}
```

Absolute path, forward slashes. Unity resolves the manifest **at startup**; a *running* Editor only
re-resolves when its window regains focus, so if you edit the manifest while Unity is open, click
the Editor once.

The daemon can do this for you when it opens a project:

```
unity_projects(open: "D:/Projects/My Game")
```

which writes the dependency, keeps a `manifest.json.umcp-backup`, and launches the Editor.

**What the agent does in your project:** connects out to `127.0.0.1:8731`, pumps operations on the
main thread, and writes one file — `ProjectSettings/UnityMCP.json`, holding the project's GUID and
the daemon port. It never writes under `Assets/`. It never raises a modal dialog.

To turn it off in a project: set `enabled: false` in that file, or set the `UMCP_DISABLE`
environment variable before launching Unity.

---

## 3. The MCP client

### Claude Code

```powershell
claude mcp add unity --transport http http://127.0.0.1:8730/mcp `
  --header "Authorization: Bearer $(Get-Content $env:LOCALAPPDATA\UnityMCP\token)"
```

### A client that speaks stdio only

Point it at the shim, which forwards to the daemon:

```jsonc
{
  "mcpServers": {
    "unity": {
      "command": "C:/Users/you/AppData/Local/Programs/UnityMCP/umcp-stdio.exe",
      "args": ["--port", "8730"]
    }
  }
}
```

The shim reads the token file itself, and **starts the daemon if it is not already up** — passing
through `--profile`, `--agent-port`, `--package-path`, `--tray` and `--auto-restart` if you gave
them. The daemon it starts outlives the shim: closing the client kills the client, not the daemon,
and not your Editor's connection. Its output goes to `%LOCALAPPDATA%\UnityMCP\logs\daemon.log`.

---

## 4. Check it end to end

With Unity open on a project that has the package:

```
unity_projects()                      -> the editor, its health, its projectId
unity_run("editor.ping")              -> { pong: true } and a frame number
unity_skill()                         -> the domain map, about 800 tokens
```

`health` is the **last completed round trip**, never "the socket is open":

| health | meaning |
|---|---|
| `ok` | ticking |
| `degraded` | no main-thread tick for 5 s — a bake, an import, a big query |
| `blocked` | no tick for 8 s or more. Unity is showing a modal dialog. **A human has to dismiss it.** |
| `reloading` | mid domain reload; reads still work, from the daemon's mirror |

---

## Troubleshooting

**`unity_projects` shows no editors.** The Editor has not resolved the package. Check
`Packages/packages-lock.json` for `com.umcp.agent`, click the Editor window once to make it
re-resolve, and look in Unity's console for a line starting `[umcp] agent up`.

**Everything returns `E_PROFILE_DENIED`.** The daemon is on `standard` and you are calling something
that needs `full`. That is the tool doing its job; restart with `--profile full` if you mean it.

**Everything returns `E_EDITOR_BLOCKED`.** Unity is showing a modal dialog — often "Recovering Scene
Backups" after a crash, or an import prompt. `unity_projects` names the window when it can read the
title. Dismiss it in the Editor; queued operations then run. The daemon never raises modals of its
own and never clicks yours away.

**Port 8730 is in use.** Another daemon is already running; it says which, and `--port` / `--agent-port`
move this one.

**A build fails after an agent edited the project.** Run `build.validateTarget` — it reports the
platform failures that produce no build error, and it takes about a second against a build that
takes twenty-five minutes.

**Everything was fine and now the Editor is gone.** The daemon does not exit with it, by design.
`unity_projects(restart: "<projectId>")` reopens the project; `unity_projects(use: "<id>",
autoRestart: true)` makes it automatic, bounded to two restarts in ten minutes.

---

## Uninstalling

Stop the daemon, remove the `com.umcp.agent` line from each project's manifest (the backup beside it
is the original), and delete `%LOCALAPPDATA%\UnityMCP` (token, logs, audit) and each project's
`ProjectSettings/UnityMCP.json`. Nothing else was written anywhere.
