using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Reading shaders, including Shader Graph.
    ///
    /// A Shader Graph is a node graph, and the position is the same one taken for VFX Graph:
    /// **read it, do not author it.** What a caller needs is the interface — the exposed properties
    /// a material can set, the keywords, and the shader model the graph targets, because that last
    /// one decides whether the shader silently becomes the magenta error shader on a GLES3 device.
    ///
    /// A `.shadergraph` file is JSON, so this works without the Shader Graph package installed at
    /// all. A hand-written `.shader` is parsed for its properties and pragmas.
    /// </summary>
    internal static class ShaderTools
    {
        [UnityTool(Skill = "material", Id = "shader.info",
            Summary = "Read a shader: its properties, keywords, passes and shader-model target. Works for .shader and .shadergraph.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ \"path\": \"Assets/Shaders/Water.shadergraph\" }")]
        [Example("{ \"name\": \"Universal Render Pipeline/Lit\" }")]
        public static object Info(
            [Doc("Shader asset path")] string path = null,
            [Doc("Shader name as materials refer to it, e.g. \"Universal Render Pipeline/Lit\"")] string name = null,
            [Doc("Maximum properties returned (default 60)")] int limit = 60)
        {
            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(name))
                throw new UmcpToolException("E_ARG_REQUIRED", "Give either 'path' or 'name'.", "path", null, null,
                    "assets.find with \"t:Shader\" lists the shaders in this project.");

            Shader shader = null;
            var assetPath = path == null ? null : Resolve.AssetPath(path, "path");

            if (assetPath != null)
            {
                shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
                if (shader == null && !File.Exists(ToAbsolute(assetPath)))
                    throw new UmcpToolException("E_ASSET_NOT_FOUND", "No shader at '" + assetPath + "'.",
                        "path", assetPath,
                        AssetDatabase.FindAssets("t:Shader").Take(5).Select(AssetDatabase.GUIDToAssetPath).ToArray(), null);
            }
            else
            {
                shader = Shader.Find(name);
                if (shader == null)
                    throw new UmcpToolException("E_SHADER_NOT_FOUND", "No shader named '" + name + "'.",
                        "name", name, null,
                        "Shader names are the string in the shader's own 'Shader \"...\"' declaration, not the file name.");
                assetPath = AssetDatabase.GetAssetPath(shader);
            }

            int cap = Bounds.Limit(limit);
            var isGraph = assetPath != null && assetPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase);

            var properties = new List<object>();
            if (shader != null)
            {
                int count = shader.GetPropertyCount();
                for (int i = 0; i < Math.Min(count, cap); i++)
                    properties.Add(new
                    {
                        name = shader.GetPropertyName(i),
                        type = shader.GetPropertyType(i).ToString(),
                        description = shader.GetPropertyDescription(i),
                        flags = shader.GetPropertyFlags(i).ToString()
                    });
            }

            var source = SourceFacts(assetPath, isGraph);

            return new
            {
                path = assetPath,
                name = shader == null ? null : shader.name,
                kind = isGraph ? "shadergraph" : "shaderlab",
                isSupported = shader == null ? (object)null : shader.isSupported,
                renderQueue = shader == null ? -1 : shader.renderQueue,
                passCount = shader == null ? 0 : shader.passCount,
                propertyCount = shader == null ? properties.Count : shader.GetPropertyCount(),
                properties = properties.ToArray(),
                _truncated = shader != null && shader.GetPropertyCount() > properties.Count,
                source,
                _hint = shader != null && !shader.isSupported
                    ? "This shader does not compile on the current platform; materials using it render magenta."
                    : source is { } && (string)((JObject)JObject.FromObject(source))["shaderModel"] != null
                        ? "build.validateTarget checks this shader model against the target's graphics APIs."
                        : null
            };
        }

        /// <summary>
        /// Facts that only the file has: the pragma target for ShaderLab, the exposed properties
        /// and keywords for a graph. Both are read as text, so neither needs its package.
        /// </summary>
        static object SourceFacts(string assetPath, bool isGraph)
        {
            if (assetPath == null) return null;
            var absolute = ToAbsolute(assetPath);
            if (!File.Exists(absolute)) return null;

            string text;
            try { text = File.ReadAllText(absolute); }
            catch { return null; }

            if (!isGraph)
            {
                var pragmas = text.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.StartsWith("#pragma", StringComparison.OrdinalIgnoreCase))
                    .Take(40).ToArray();

                var target = pragmas.FirstOrDefault(p => p.StartsWith("#pragma target", StringComparison.OrdinalIgnoreCase));
                return new
                {
                    shaderModel = target == null ? null : target.Substring("#pragma target".Length).Trim(),
                    pragmas,
                    fallback = text.Split('\n').Select(l => l.Trim())
                                   .FirstOrDefault(l => l.StartsWith("Fallback", StringComparison.OrdinalIgnoreCase))
                };
            }

            // A .shadergraph is JSON: a flat list of nodes and data objects, each with a $type.
            try
            {
                var json = JObject.Parse(text);
                var objects = json["m_ObjectEntries"] as JArray;
                var types = objects == null
                    ? new string[0]
                    : objects.Select(o => (string)o["type"]?["Type"]).Where(t => t != null).ToArray();

                var properties = objects == null ? new string[0] : objects
                    .Where(o => ((string)o["type"]?["Type"] ?? "").EndsWith("ShaderProperty", StringComparison.Ordinal) ||
                                ((string)o["type"]?["Type"] ?? "").Contains("Property", StringComparison.Ordinal))
                    .Select(o => (string)o["JSONnodeData"])
                    .Where(d => d != null)
                    .Select(NameIn)
                    .Where(n => n != null)
                    .Distinct()
                    .Take(60)
                    .ToArray();

                return new
                {
                    nodeCount = types.Length,
                    exposedProperties = properties,
                    keywords = types.Count(t => t.Contains("Keyword", StringComparison.Ordinal)),
                    shaderModel = (string)null,
                    _hint = "Graph structure is edited in the Shader Graph window; materials set these properties."
                };
            }
            catch
            {
                return new { parsed = false, _hint = "The .shadergraph file could not be parsed as JSON by this version." };
            }
        }

        static string NameIn(string nodeJson)
        {
            try
            {
                var node = JObject.Parse(nodeJson);
                return (string)(node["m_Name"] ?? node["m_DisplayName"] ?? node["m_DefaultReferenceName"]);
            }
            catch { return null; }
        }

        static string ToAbsolute(string assetPath)
        {
            return Path.Combine(UmcpSettings.ProjectRoot, assetPath).Replace('\\', '/');
        }
    }
}
