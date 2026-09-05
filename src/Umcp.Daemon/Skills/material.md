title: Materials and shaders
covers: creating materials, setting colours, floats and textures, pipeline-correct shader names
excludes: shader graphs, post-processing volumes, lighting setup
parent: rendering

# Materials

**This project's render pipeline is {{pipeline}}.** Shader names are not portable between
pipelines, and picking the wrong one is the usual cause of a magenta "error shader" material.

| Pipeline | Lit shader | Base colour property |
|---|---|---|
| URP | `Universal Render Pipeline/Lit` | `_BaseColor` |
| HDRP | `HDRP/Lit` | `_BaseColor` |
| Built-in | `Standard` | `_Color` |

`material.create` picks the right one for the detected pipeline when you omit `shader`. Omit it
unless you have a specific reason.

## Setting properties

Properties are grouped by type, because Unity's setters are:

```json
{ "path": "Assets/Materials/Rock.mat",
  "colors":   { "_BaseColor": [0.4, 0.4, 0.42, 1] },
  "floats":   { "_Metallic": 0.1, "_Smoothness": 0.35 },
  "textures": { "_BaseMap": "Assets/Art/rock_albedo.png" } }
```

A property the shader does not have is reported in `warnings` rather than failing the call — check
them. URP's names are `_BaseMap`, `_BaseColor`, `_BumpMap`, `_Metallic`, `_Smoothness`,
`_EmissionColor`; the Built-in names (`_MainTex`, `_Color`) will silently do nothing on a URP
material.

## Emission, and a project-specific warning

Emission needs the keyword as well as the colour, and HDR emissive values are not linear in
appearance. **In this project specifically, colour grading runs through ACES, and coloured
emissives above roughly 1.8 intensity shift towards yellow as they clip.** If a designer asks for a
"brighter red glow", raising intensity past that point makes it oranger, not brighter — raise
bloom or change the tonemapping instead.

## Assigning to a renderer

A material asset is not applied until something references it:

```json
{ "op": "component.set",
  "args": { "target": "Crate", "type": "MeshRenderer",
            "props": { "sharedMaterial": "Assets/Materials/Rock.mat" } } }
```

Use `sharedMaterial`, not `material`. Reading `.material` in the Editor **instantiates a copy** and
leaks it into the scene; `sharedMaterial` edits the asset, which is what an edit-mode tool should do.

## Bulk changes

Re-materialising many renderers is a loop — use `unity.script`:

```csharp
var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Rock.mat");
var hits = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)
    .Where(r => r.sharedMaterial == null).ToArray();
Undo.RecordObjects(hits, "Assign material");
foreach (var r in hits) r.sharedMaterial = mat;
return new { assigned = hits.Length };
```
