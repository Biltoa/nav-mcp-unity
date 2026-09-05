title: Code mode — running C# in the Editor
covers: when to use unity.script, what is in scope, return values, undo, errors
excludes: creating .cs files in the project (that is an asset edit, and triggers a compile)
tools: none

# Code mode

`unity.script` compiles C# on the daemon and runs it inside the Editor, returning **only what you
return**. It is the biggest single lever on context cost: measured on this project, 20 operations
cost 145 bytes through code mode versus 6,020 bytes as tool calls — 41× less — at the same wall
time as a batch.

Availability: code mode is arbitrary code execution, so it requires the `full` profile. On
`standard` (the default) it returns `E_PROFILE_DENIED`.

## When

| Situation | Use |
|---|---|
| One discrete change | `unity.run` |
| Several known changes | `unity.batch` |
| A loop, a filter, a conditional, or an aggregate | **`unity.script`** |
| A question whose answer is a number, not rows | **`unity.script`** |

The rule of thumb: if you would otherwise fetch a list in order to reason over it and then act,
send the reasoning to the Editor instead of bringing the list back.

## Shape

Write a body, not a class. These are already imported: `System`, `System.Collections`,
`System.Collections.Generic`, `System.Linq`, `UnityEngine`, `UnityEditor`,
`UnityEditor.SceneManagement`, `UnityEngine.SceneManagement`, with `Object` and `Debug` aliased to
their `UnityEngine` versions.

A bare expression is returned automatically. A statement block that returns nothing yields `null`.

```csharp
var bad = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
    .Where(r => r.sharedMaterial != null && r.sharedMaterial.mainTexture == null)
    .Select(r => r.gameObject.name).ToArray();
return new { count = bad.Length, sample = bad.Take(10) };
```

Return anonymous objects, arrays or primitives — anything Newtonsoft can serialise. Return a
**summary**, not the raw set: returning 4,000 names undoes the entire point.

## Rules that still apply

- The script runs **on the main thread**, inside the ordinary message pump. Do not start threads
  that touch Unity APIs, and do not block: a long `Sleep` is indistinguishable from a modal dialog
  and will be reported as `E_EDITOR_BLOCKED`.
- **Undo is yours to register.** `Undo.RegisterCreatedObjectUndo`, `Undo.RecordObject(s)`,
  `Undo.DestroyObjectImmediate`. Nothing is undoable unless you say so.
- Never call `EditorUtility.DisplayDialog` or anything else modal. It blocks the message pump and
  wedges the bridge while the socket still looks healthy.
- Do not enter Play mode.
- Prefer `sharedMaterial` over `material`: reading `.material` in edit mode instantiates a copy.

## Errors

Compile failures return `E_SCRIPT_COMPILE` with `{line, column, id, message}` diagnostics whose line
numbers are **relative to your code**, not to the generated wrapper. A runtime exception returns
`E_SCRIPT_THREW` with the exception's own message and the first frame inside your script.

## Cost

Compiled assemblies are cached by source hash on the daemon, so re-running the same script skips
compilation entirely and survives domain reloads. Inside the Editor, Mono cannot unload an
assembly, so each *distinct* script costs one assembly until the next domain reload — which is
another reason to write one good script rather than twenty variations.
