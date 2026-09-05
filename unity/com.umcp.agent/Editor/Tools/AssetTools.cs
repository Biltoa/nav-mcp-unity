using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    internal static class AssetTools
    {
        [UnityTool(Id = "assets.find", Summary = "Search the AssetDatabase. Bounded output.", Retry = RetryClass.Read)]
        [Example("{ \"filter\": \"t:Material\", \"limit\": 25 }")]
        [Example("{ \"filter\": \"Rock t:Texture2D\", \"folders\": [\"Assets/Art\"] }")]
        public static object Find(
            [Doc("AssetDatabase search filter, e.g. \"t:Material Rock\"")] string filter,
            [Doc("Restrict to these folders")] string[] folders = null,
            [Doc("Maximum results (default 100)")] int limit = 100,
            [Doc("Skip this many results")] int offset = 0)
        {
            var guids = folders == null || folders.Length == 0
                ? AssetDatabase.FindAssets(filter)
                : AssetDatabase.FindAssets(filter, folders.Select(f => Resolve.AssetPath(f, "folders")).ToArray());

            var page = guids.Skip(offset).Take(Bounds.Limit(limit)).Select(g =>
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                return new { guid = g, path = p, type = AssetDatabase.GetMainAssetTypeAtPath(p)?.Name };
            }).ToArray();

            return Res.Page(page, guids.Length, offset, page.Length);
        }

        [UnityTool(Id = "assets.info", Summary = "Read one asset's identity, type and direct dependencies.", Retry = RetryClass.Read)]
        [Example("{ \"path\": \"Assets/Materials/Rock.mat\" }")]
        public static object Info(
            [Doc("Asset path")] string path,
            [Doc("Include direct dependencies")] bool dependencies = false)
        {
            var p = Resolve.AssetPath(path);
            var main = AssetDatabase.LoadMainAssetAtPath(p);
            if (main == null)
                throw new UmcpToolException("E_ASSET_NOT_FOUND", "No asset at '" + p + "'.", "path", path, null,
                    "Use assets.find to locate it.");

            var abs = Path.GetFullPath(p);
            return new
            {
                path = p,
                guid = AssetDatabase.AssetPathToGUID(p),
                type = main.GetType().Name,
                name = main.name,
                bytes = File.Exists(abs) ? new FileInfo(abs).Length : (long?)null,
                isFolder = AssetDatabase.IsValidFolder(p),
                dependencies = dependencies
                    ? AssetDatabase.GetDependencies(p, false).Where(d => d != p).Take(50).ToArray()
                    : null
            };
        }

        [UnityTool(Id = "assets.createFolder", Summary = "Create a folder, creating intermediate folders as needed.",
            Mutating = true, Retry = RetryClass.Write, NoUndoReason = "AssetDatabase folder creation is not undoable.")]
        [Example("{ \"path\": \"Assets/Generated/Materials\" }")]
        public static object CreateFolder([Doc("Folder path under Assets/")] string path)
        {
            var p = Resolve.AssetPath(path).TrimEnd('/');
            if (AssetDatabase.IsValidFolder(p)) return new { path = p, created = false };

            var parts = p.Split('/');
            var acc = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = acc + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(acc, parts[i]);
                acc = next;
            }
            return new { path = p, created = true };
        }

        [UnityTool(Id = "assets.move", Summary = "Move or rename an asset, preserving its GUID.",
            Mutating = true, Retry = RetryClass.Write, NoUndoReason = "AssetDatabase moves are not undoable.")]
        [Example("{ \"from\": \"Assets/A/Rock.mat\", \"to\": \"Assets/B/Rock.mat\" }")]
        public static object Move(
            [Doc("Current asset path")] string from,
            [Doc("Destination asset path")] string to)
        {
            var src = Resolve.AssetPath(from, "from");
            var dst = Resolve.AssetPath(to, "to");
            var err = AssetDatabase.ValidateMoveAsset(src, dst);
            if (!string.IsNullOrEmpty(err))
                throw new UmcpToolException("E_ASSET_MOVE_INVALID", err, "to", to, null,
                    "Make sure the destination folder exists (assets.createFolder).");
            AssetDatabase.MoveAsset(src, dst);
            return new { from = src, to = dst, guid = AssetDatabase.AssetPathToGUID(dst) };
        }

        [UnityTool(Id = "assets.delete", Summary = "Delete an asset. Destructive.",
            Mutating = true, Retry = RetryClass.None, NoUndoReason = "AssetDatabase deletion is not undoable.")]
        [Example("{ \"path\": \"Assets/Generated/Temp.mat\", \"confirm\": true }")]
        public static object Delete(
            [Doc("Asset path")] string path,
            [Doc("Must be true. Deletion cannot be undone.")] bool confirm = false)
        {
            var p = Resolve.AssetPath(path);
            if (!confirm)
                throw new UmcpToolException("E_CONFIRM_REQUIRED",
                    "Deleting '" + p + "' cannot be undone. Pass confirm=true.", "confirm", "false");
            if (AssetDatabase.LoadMainAssetAtPath(p) == null && !AssetDatabase.IsValidFolder(p))
                throw new UmcpToolException("E_ASSET_NOT_FOUND", "No asset at '" + p + "'.", "path", path);
            bool ok = AssetDatabase.DeleteAsset(p);
            return new { path = p, deleted = ok };
        }

        [UnityTool(Id = "assets.refresh", Summary = "Refresh the AssetDatabase. May trigger a compile and a domain reload.",
            Mutating = true, Retry = RetryClass.Compile, Cost = Cost.Expensive,
            NoUndoReason = "An import is not an undoable operation.")]
        [Example("{ }")]
        public static object Refresh([Doc("Force a full reimport of changed assets")] bool force = false)
        {
            AssetDatabase.Refresh(force ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
            return new { refreshed = true };
        }

        [UnityTool(Id = "prefab.create", Summary = "Save a scene GameObject as a prefab asset.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Create Prefab")]
        [Example("{ \"target\": \"Enemy\", \"path\": \"Assets/Prefabs/Enemy.prefab\" }")]
        public static object PrefabCreate(
            [Doc("Path, name or #instanceId of the scene object")] string target,
            [Doc("Destination prefab path")] string path,
            [Doc("Replace the scene object with an instance of the new prefab")] bool connect = true)
        {
            var go = Resolve.GameObject(target);
            var p = Resolve.AssetPath(path);
            var dir = p.Substring(0, p.LastIndexOf('/'));
            if (!AssetDatabase.IsValidFolder(dir)) CreateFolder(dir);

            var prefab = connect
                ? PrefabUtility.SaveAsPrefabAssetAndConnect(go, p, InteractionMode.AutomatedAction)
                : PrefabUtility.SaveAsPrefabAsset(go, p);
            return new { path = p, guid = AssetDatabase.AssetPathToGUID(p), name = prefab.name };
        }

        [UnityTool(Id = "prefab.instantiate", Summary = "Instantiate a prefab into the active scene.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Instantiate Prefab")]
        [Example("{ \"path\": \"Assets/Prefabs/Enemy.prefab\", \"position\": [0, 0, 5] }")]
        public static object PrefabInstantiate(
            [Doc("Prefab asset path")] string path,
            [Doc("Parent path or #instanceId")] string parent = null,
            [Doc("Local position as [x, y, z]")] float[] position = null,
            [Doc("Name override for the instance")] string name = null)
        {
            var p = Resolve.AssetPath(path);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(p);
            if (asset == null)
                throw new UmcpToolException("E_ASSET_NOT_FOUND", "No prefab at '" + p + "'.", "path", path);

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            Undo.RegisterCreatedObjectUndo(inst, "Instantiate Prefab");
            if (!string.IsNullOrEmpty(name)) inst.name = name;
            if (!string.IsNullOrEmpty(parent))
                Undo.SetTransformParent(inst.transform, Resolve.GameObject(parent, "parent").transform, "Instantiate Prefab");
            if (position != null) inst.transform.localPosition = Vec.V3(position, "position");
            return Res.Ref(inst);
        }

        [UnityTool(Id = "material.create", Summary = "Create a material asset. Defaults to the project's active pipeline shader.",
            Mutating = true, Retry = RetryClass.Write, NoUndoReason = "Asset creation is not undoable.")]
        [Example("{ \"path\": \"Assets/Materials/Rock.mat\", \"color\": [0.5, 0.5, 0.5, 1] }")]
        public static object MaterialCreate(
            [Doc("Destination .mat path")] string path,
            [Doc("Shader name. Defaults to the render pipeline's lit shader.")] string shader = null,
            [Doc("Base colour as [r, g, b, a] in 0..1")] float[] color = null)
        {
            var p = Resolve.AssetPath(path);
            var shaderName = shader ?? DefaultLitShader();
            var sh = Shader.Find(shaderName);
            if (sh == null)
                throw new UmcpToolException("E_SHADER_NOT_FOUND", "Shader '" + shaderName + "' is not in this project.",
                    "shader", shaderName, null, "This project's pipeline is " + PipelineName() + ".");

            var dir = p.Substring(0, p.LastIndexOf('/'));
            if (!AssetDatabase.IsValidFolder(dir)) CreateFolder(dir);

            var mat = new Material(sh);
            if (color != null)
            {
                var c = new Color(color[0], color[1], color[2], color.Length > 3 ? color[3] : 1f);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
                else if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            }
            AssetDatabase.CreateAsset(mat, p);
            return new { path = p, shader = sh.name, guid = AssetDatabase.AssetPathToGUID(p) };
        }

        [UnityTool(Id = "material.set", Summary = "Set shader properties on a material asset.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set Material Properties")]
        [Example("{ \"path\": \"Assets/Materials/Rock.mat\", \"floats\": { \"_Metallic\": 0.2 } }")]
        public static object MaterialSet(
            [Doc("Material asset path")] string path,
            [Doc("Colour properties, each [r, g, b, a]")] Newtonsoft.Json.Linq.JObject colors = null,
            [Doc("Float properties")] Newtonsoft.Json.Linq.JObject floats = null,
            [Doc("Texture properties, each an asset path or null")] Newtonsoft.Json.Linq.JObject textures = null)
        {
            var p = Resolve.AssetPath(path);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (mat == null)
                throw new UmcpToolException("E_ASSET_NOT_FOUND", "No material at '" + p + "'.", "path", path);

            Undo.RecordObject(mat, "Set Material Properties");
            var warnings = new System.Collections.Generic.List<string>();

            if (colors != null)
                foreach (var kv in colors)
                {
                    if (!mat.HasProperty(kv.Key)) { warnings.Add("no property " + kv.Key); continue; }
                    var a = ((Newtonsoft.Json.Linq.JArray)kv.Value).Select(x => x.ToObject<float>()).ToArray();
                    mat.SetColor(kv.Key, new Color(a[0], a[1], a[2], a.Length > 3 ? a[3] : 1f));
                }
            if (floats != null)
                foreach (var kv in floats)
                {
                    if (!mat.HasProperty(kv.Key)) { warnings.Add("no property " + kv.Key); continue; }
                    mat.SetFloat(kv.Key, kv.Value.ToObject<float>());
                }
            if (textures != null)
                foreach (var kv in textures)
                {
                    if (!mat.HasProperty(kv.Key)) { warnings.Add("no property " + kv.Key); continue; }
                    var tp = kv.Value.Type == Newtonsoft.Json.Linq.JTokenType.Null ? null : kv.Value.ToObject<string>();
                    mat.SetTexture(kv.Key, tp == null ? null : AssetDatabase.LoadAssetAtPath<Texture>(Resolve.AssetPath(tp, "textures")));
                }

            EditorUtility.SetDirty(mat);
            return new { path = p, warnings = warnings.Count == 0 ? null : warnings.ToArray() };
        }

        internal static string PipelineName()
        {
            var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (rp == null) return "Built-in";
            var n = rp.GetType().Name;
            if (n.Contains("Universal")) return "URP";
            if (n.Contains("HD")) return "HDRP";
            return n;
        }

        static string DefaultLitShader()
        {
            switch (PipelineName())
            {
                case "URP": return "Universal Render Pipeline/Lit";
                case "HDRP": return "HDRP/Lit";
                default: return "Standard";
            }
        }
    }
}
