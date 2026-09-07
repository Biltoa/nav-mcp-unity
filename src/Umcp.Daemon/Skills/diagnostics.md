title: Editor state, console, compiling and health
covers: console messages, structured compile errors, editor status, selection, recompiling, project facts
excludes: profiling and build validation (not yet implemented)

# Diagnostics

Project: **{{project}}** · Unity {{unityVersion}} · {{pipeline}} · build target {{platform}}.

## Compile errors, structured

`compile.errors` returns `{file, line, column, code, message}` objects parsed out of the captured
log. Use it instead of scraping `console.read` output — the console text format is not a contract.

```json
{ "warnings": false }
```

`editor.compile` requests a recompilation and therefore a domain reload. Calling it mid-sequence is
safe: the daemon holds queued operations while the Editor is gone and replays them with their
idempotency keys when it comes back, so operations do not double-apply and the caller sees a slow
call rather than a failure. Measured: 16 operations fired into a reload, zero agent-visible errors.

## Console

`console.read` is bounded and returns the newest entries last. Filter before you read:

```json
{ "types": ["Error", "Exception"], "limit": 20 }
```

Stack traces are large and off by default. `console.clear` clears both the capture buffer and
Unity's own console window.

## Health, and what "connected" does not mean

`unity.projects` reports health as **the last completed round trip**, never as socket state. The
values mean:

| Health | Meaning |
|---|---|
| `ok` | the Editor's main thread ticked within the last 5 s |
| `degraded` | no tick for 5–8 s; the Editor is busy |
| `blocked` | no tick for 8 s or more while work is queued |
| `reloading` | domain reload in progress; operations are being held, not failed |

`blocked` almost always means **a modal dialog is open in Unity**. A modal blocks
`EditorApplication.update`, which is the message pump, so every queued operation stalls while TCP
stays perfectly healthy. When this happens you get `E_EDITOR_BLOCKED` with the tick age and, when it
can be identified, the dialog's title — not a bare timeout. The fix is for a human to dismiss the
dialog; this tool will never click someone else's modal, because "Recover scene backups?" and
"Enter safe mode?" are not ours to answer. It also never raises one.

## Things that are deliberately absent

- **Nothing enters Play mode.** A read that changes Editor state is a bug, not a feature: a
  screenshot tool that entered Play mode lost the D3D12 device and crashed this Editor during
  testing.
- `editor.stall` exists only to test blocked-editor detection. It wedges the Editor on purpose and
  requires the `full` profile.

## Undoing what you just did

`editor.undo` reverses the most recent change. Every `unity_batch` this tool runs is collapsed into
**one undo step** with a name, so undoing a batch of forty operations is one call, not forty.

```
editor.undo { "action": "peek" }                          -> what a Ctrl+Z would undo, by name
editor.undo { "action": "undo", "expect": "MCP Batch" }    -> undo it, only if that is still true
editor.undo { "action": "redo" }
```

**Always peek first, and pass `expect`.** Undo is Editor-wide, not yours: the top of the stack may
belong to a person who moved a transform in the Editor a second after your batch, and reverting
their work silently is the worst thing this tool can do. With `expect` set, a mismatch is
`E_UNDO_MISMATCH` and nothing is undone.

Name your batches — `unity_batch` takes `undoName` — so that both the peek and the user's own
Edit menu say what the step was, rather than "MCP Batch" forty times.

Two limits worth knowing. Unity exposes only the *name* of the next undo group, never the stack, so
"what will this undo" is one string and there is no way to look further back. And asset operations
are frequently not undoable at all — `assets.delete`, `scene.save` and controller edits are asset
writes that Unity's own editors do not register with `Undo`; each such tool says so in its
`NoUndoReason`. Undo is for scene and object changes.
