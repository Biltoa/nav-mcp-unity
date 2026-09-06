# Installing the Unity MCP Tool

There are two ways in. Pick one.

* **[The app](#the-app)** — download, double-click, click three buttons. No terminal, no JSON.
* **[By hand](#by-hand)** — the daemon, the package and the client config, done yourself.

Requirements either way: Windows 10/11 or macOS 11+, and Unity **6000.0 or newer**. The app builds
carry their own .NET; a hand install needs the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
(`dotnet --list-runtimes` should show `Microsoft.NETCore.App 8.x`).

---

## The app

### 1. Download and open it

**Windows** — unzip anywhere (`Documents\UnityMCP` is fine) and run **Unity MCP Tool.exe**.

The first launch shows a blue **"Windows protected your PC"** box. That is SmartScreen saying the
app has no paid code-signing certificate, not that anything is wrong with it. Click **More info**,
then **Run anyway**. It appears once.

**macOS** — drag **Unity MCP Tool.app** to your Applications folder. The first launch must be
**right-click (or Control-click) the app → Open → Open**. Double-clicking it the first time gives
*"cannot be opened because the developer cannot be verified"* with no way past; the right-click
route is the way macOS lets you approve an app that has not been notarised. After that, it opens
normally forever.

If macOS says the app is **damaged**, it was quarantined during download. In Terminal:

```bash
xattr -dr com.apple.quarantine "/Applications/Unity MCP Tool.app"
```

### 2. Start the server

The window opens and starts the server for you. The dot at the top turns green and the line
underneath says the version, the port, the number of Editor tools and how many Unity editors are
connected.

**Stop server** stops it. Closing the window does not — the app goes to the tray (Windows) or the
menu bar (macOS) and the server keeps running, which is the point: it is meant to outlive the
things that talk to it. **Quit** from the tray menu closes the app itself.

### 3. Link your Unity projects

Click **Link a project…** and choose a Unity project folder — the one with `Assets` and
`ProjectSettings` inside it. Repeat for every project you want to control. **One server handles
all of them at the same time**; there is nothing to start per project.

Linking writes one line into that project's `Packages/manifest.json` and keeps the original as
`manifest.json.umcp-backup`. Nothing is written under `Assets/`.

Each project then shows its own state, and the state tells you what to do:

| What it says | What it means |
|---|---|
| **Ready** | Connected. Your AI can work in it. |
| **Click the Unity window** | Unity is open but has not noticed the new package. Click the Unity window once — that is when Unity re-reads its package list. |
| **Unity not open** | Linked and waiting. **Open in Unity** starts it. |
| **Compiling** / **Busy** | Unity is compiling or importing. Work is queued, not lost. |
| **Waiting on you** | Unity is showing a dialog, and it names it. Answer it in the Editor. |
| **Not linked** | The project's package list no longer mentions the agent. Link it again. |

### 4. Connect your AI assistant

Under **Connect your AI assistant**, click **Connect** next to Claude Desktop, Claude Code or
Cursor. Restart that app.

It writes one entry into that app's config and leaves everything else in the file alone, backing
the original up first. Nothing secret is written: the entry points at the `umcp-stdio` helper,
which reads the access token from disk itself each time it starts.

Using something else? Open **Another app, or do it by hand** and copy the snippet into that
client's MCP config.

### 5. Check it

In your AI client, ask it to run `unity_projects`. It should list your editors and their health.

### What the app decides for you

* **Port 8730**, loopback only. Change it under **Settings** if something else has it.
* **Profile `standard`**: the AI may change your project, but not run arbitrary C# and not delete
  assets or overwrite scenes. `readonly` is look-but-do-not-touch; `full` allows everything,
  including `unity.script`, `assets.delete`, `scene.save` and `editor.quit`. Use `full` when you
  have version control and mean it.
* **Pause** keeps the server up and holds new operations. It is the safe stop mid-task.

---

## By hand

The same three pieces, done yourself: the **daemon**, the **Unity package**, the **client config**.

### 1. The daemon

From a release drop, or built here:

```powershell
pwsh scripts/publish.ps1        # Windows: runs toolgen, the main-thread check and the tests first
```

```bash
scripts/publish.sh              # macOS: the same gates, then the .app bundle
```

Start it:

```powershell
umcpd.exe --profile standard
```

One line names the ports, the tool count and the token file:

```
[umcpd] 1.0.0 · http 127.0.0.1:8730/mcp · agents 127.0.0.1:8731 · 92 tools · profile standard · token in C:\Users\you\AppData\Local\UnityMCP\token
```

Check it: `curl http://127.0.0.1:8730/health` — that endpoint needs no token, everything else does.

| Profile | Allows | Use it when |
|---|---|---|
| `readonly` | reads only | you want an agent that can look and not touch |
| `standard` **(default)** | mutations, but no arbitrary code and no irreversible writes | day to day |
| `full` | everything: `unity.script`, `assets.delete`, `scene.save`, `editor.quit` | you have version control and you mean it |

The daemon binds `127.0.0.1` only — there is no option to change that — and every request outside
`/health` carries a bearer token, minted at start with owner-only permissions (an ACL on Windows,
mode 0600 on macOS). Loopback alone is not enough: any local process, and any web page that
resolves a name to 127.0.0.1, can reach a loopback listener.

Where it keeps its files:

| | |
|---|---|
| Windows | `%LOCALAPPDATA%\UnityMCP` |
| macOS | `~/Library/Application Support/UnityMCP` |

### Starting it at login

Windows: Task Scheduler, "At log on", action `umcpd.exe`, arguments `--profile standard`.
macOS: a LaunchAgent, or just leave the app running — it is in the menu bar anyway.

Or start it by hand when you want it; the shim below starts it for you if it is not running, and
passes your `--profile` through.

### 2. The Unity package

Add the package to the project's `Packages/manifest.json`:

```jsonc
{
  "dependencies": {
    "com.umcp.agent": "file:C:/Users/you/Documents/UnityMCP/com.umcp.agent",
    // ... everything else
  }
}
```

Absolute path, forward slashes. On macOS the package is inside the bundle, at
`/Applications/Unity MCP Tool.app/Contents/Resources/com.umcp.agent`.

Unity resolves the manifest **at startup**; a *running* Editor only re-resolves when its window
regains focus, so if you edit the manifest while Unity is open, click the Editor once.

**What the agent does in your project:** connects out to `127.0.0.1:8731`, pumps operations on the
main thread, and writes one file — `ProjectSettings/UnityMCP.json`, holding the project's GUID and
the daemon port. It never writes under `Assets/`. It never raises a modal dialog.

To turn it off in a project: set `enabled: false` in that file, or set the `UMCP_DISABLE`
environment variable before launching Unity.

### 3. The MCP client

Point the client at the shim, which reads the token itself and starts the daemon if it is not up:

```jsonc
{
  "mcpServers": {
    "unity": {
      "command": "C:/Users/you/Documents/UnityMCP/umcp-stdio.exe",
      "args": ["--port", "8730"]
    }
  }
}
```

Or straight over HTTP, if you would rather manage the token yourself:

```powershell
claude mcp add unity --transport http http://127.0.0.1:8730/mcp `
  --header "Authorization: Bearer $(Get-Content $env:LOCALAPPDATA\UnityMCP\token)"
```

A token is minted fresh on every daemon start, so a config file with a token in it stops working
the next time the server restarts. The shim exists to avoid exactly that.

---

## Check it end to end

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

**The app says "A different server is on this port."** An older `umcpd` is holding 8730 — started
by hand, or by an AI client through the shim. Stop it, or move the app to another port under
Settings.

**A project stays on "Click the Unity window".** That is the instruction, not a description: Unity
re-reads its package list when its window regains focus. If clicking it does not help, check
`Packages/packages-lock.json` for `com.umcp.agent` and Unity's console for `[umcp] agent up`.

**Everything returns `E_PROFILE_DENIED`.** The server is on `standard` and something asked for a
`full` operation. That is the tool doing its job; change the profile in Settings and restart the
server if you mean it.

**Everything returns `E_EDITOR_BLOCKED`.** Unity is showing a modal dialog — often "Recovering
Scene Backups" after a crash. The app names it. Dismiss it in the Editor and queued operations run.
The tool never clicks your dialogs away: "Recover scene backups?" is not its answer to give.

**Port 8730 is in use.** Something else has it; `--port` / `--agent-port`, or the Settings panel,
move this one.

**A build fails after an agent edited the project.** Run `build.validateTarget` — it reports the
platform failures that produce no build error, in about a second, against a build that takes
twenty-five minutes.

**Everything was fine and now the Editor is gone.** The server does not exit with it, by design.
**Open in Unity** brings it back; per-project auto-restart is bounded to two restarts in ten
minutes.

---

## Uninstalling

In the app: **Unlink** each project, **Disconnect** each AI client, then Quit and delete the app.

By hand: stop the daemon, remove the `com.umcp.agent` line from each project's manifest (the
`.umcp-backup` beside it is the original), delete each project's `ProjectSettings/UnityMCP.json`,
and delete `%LOCALAPPDATA%\UnityMCP` (Windows) or `~/Library/Application Support/UnityMCP` (macOS).
Nothing else was written anywhere.
