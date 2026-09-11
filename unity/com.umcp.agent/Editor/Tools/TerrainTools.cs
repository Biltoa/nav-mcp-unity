using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Terrain: what it is made of and what it costs.
    ///
    /// Sculpting is deliberately absent. A heightmap edit is a large, irreversible write to a
    /// terrain data asset — the AssetDatabase does not undo it — and "raise the ground here" is not
    /// a thing an agent can verify it did correctly without looking. What an agent genuinely needs
    /// is the audit: resolution, layer count, tree and detail counts, and the settings that decide
    /// whether the terrain is affordable on the target platform.
    /// </summary>
    internal static class TerrainTools
    {
        [UnityTool(Skill = "terrain", Id = "terrain.info",
            Summary = "Read the terrains in the open scenes: size, resolutions, layers, trees, details, and the draw settings that cost performance.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ }")]
        public static object Info([Doc("A specific terrain object. Omit for all of them.")] string target = null)
        {
            var terrains = (string.IsNullOrEmpty(target)
                    ? Resolve.AllGameObjects()
                    : new[] { Resolve.GameObject(target, "target") })
                .Select(go => go.GetComponent<Terrain>())
                .Where(t => t != null)
                .ToArray();

            if (terrains.Length == 0)
                return new
                {
                    count = 0,
                    returned = 0,
                    terrains = new object[0],
                    _truncated = false,
                    _hint = string.IsNullOrEmpty(target) ? "No Terrain components in the open scenes."
                                                         : "'" + target + "' has no Terrain component."
                };

            var rows = terrains.Take(10).Select(terrain =>
            {
                var data = terrain.terrainData;
                return new
                {
                    path = Resolve.Path(terrain.transform),
                    asset = data == null ? null : AssetDatabase.GetAssetPath(data),
                    size = data == null ? null : Vec.Arr(data.size),
                    heightmapResolution = data == null ? 0 : data.heightmapResolution,
                    alphamapResolution = data == null ? 0 : data.alphamapResolution,
                    detailResolution = data == null ? 0 : data.detailResolution,
                    layers = data == null ? new object[0] : data.terrainLayers.Select(l => new
                    {
                        name = l == null ? null : l.name,
                        texture = l == null || l.diffuseTexture == null ? null : l.diffuseTexture.name,
                        tileSize = l == null ? null : Vec.Arr(l.tileSize)
                    }).ToArray(),
                    trees = new
                    {
                        instances = data == null ? 0 : data.treeInstanceCount,
                        prototypes = data == null ? 0 : data.treePrototypes.Length,
                        distance = terrain.treeDistance,
                        billboardStart = terrain.treeBillboardDistance
                    },
                    details = new
                    {
                        prototypes = data == null ? 0 : data.detailPrototypes.Length,
                        distance = terrain.detailObjectDistance,
                        density = terrain.detailObjectDensity
                    },
                    drawing = new
                    {
                        pixelError = terrain.heightmapPixelError,
                        basemapDistance = terrain.basemapDistance,
                        drawInstanced = terrain.drawInstanced,
                        castShadows = terrain.shadowCastingMode.ToString(),
                        material = terrain.materialTemplate == null ? null : terrain.materialTemplate.name
                    }
                };
            }).ToArray();

            return new
            {
                count = terrains.Length,
                returned = rows.Length,
                terrains = rows,
                _truncated = terrains.Length > rows.Length,
                _hint = "heightmapPixelError is the cheapest performance dial: raising it from 1 to 5 " +
                        "roughly halves terrain triangles with little visible change at distance."
            };
        }

        [UnityTool(Skill = "terrain", Id = "terrain.setDrawSettings",
            Summary = "Set a terrain's performance dials: pixel error, basemap and tree distances, detail density, instancing.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set terrain draw settings")]
        [Example("{ \"target\": \"Terrain\", \"pixelError\": 5, \"treeDistance\": 2000 }")]
        public static object SetDrawSettings(
            [Doc("The terrain object")] string target,
            [Doc("Heightmap pixel error, 1-200. Higher is cheaper and coarser.")] float? pixelError = null,
            [Doc("Distance at which the basemap replaces per-layer shading")] float? basemapDistance = null,
            [Doc("Distance at which trees stop drawing")] float? treeDistance = null,
            [Doc("Distance at which trees become billboards")] float? billboardStart = null,
            [Doc("Distance at which detail meshes stop drawing")] float? detailDistance = null,
            [Doc("Detail density, 0-1")] float? detailDensity = null,
            [Doc("Draw the terrain with GPU instancing")] bool? drawInstanced = null)
        {
            var go = Resolve.GameObject(target, "target");
            var terrain = go.GetComponent<Terrain>();
            if (terrain == null)
                throw new UmcpToolException("E_COMPONENT_NOT_FOUND",
                    "'" + go.name + "' has no Terrain component.", "target", target, null,
                    "terrain.info lists the terrains in the open scenes.");

            Undo.RecordObject(terrain, "Set terrain draw settings");
            var changed = new System.Collections.Generic.List<string>();

            if (pixelError != null) { terrain.heightmapPixelError = Mathf.Clamp(pixelError.Value, 1f, 200f); changed.Add("pixelError"); }
            if (basemapDistance != null) { terrain.basemapDistance = Mathf.Max(0f, basemapDistance.Value); changed.Add("basemapDistance"); }
            if (treeDistance != null) { terrain.treeDistance = Mathf.Max(0f, treeDistance.Value); changed.Add("treeDistance"); }
            if (billboardStart != null) { terrain.treeBillboardDistance = Mathf.Max(0f, billboardStart.Value); changed.Add("billboardStart"); }
            if (detailDistance != null) { terrain.detailObjectDistance = Mathf.Max(0f, detailDistance.Value); changed.Add("detailDistance"); }
            if (detailDensity != null) { terrain.detailObjectDensity = Mathf.Clamp01(detailDensity.Value); changed.Add("detailDensity"); }
            if (drawInstanced != null) { terrain.drawInstanced = drawInstanced.Value; changed.Add("drawInstanced"); }

            EditorUtility.SetDirty(terrain);

            return new
            {
                path = Resolve.Path(terrain.transform),
                changed = changed.ToArray(),
                pixelError = terrain.heightmapPixelError,
                treeDistance = terrain.treeDistance,
                detailObjectDistance = terrain.detailObjectDistance,
                _hint = changed.Count == 0
                    ? "Nothing was passed to change; terrain.info reads the current values."
                    : "These are scene-object settings, so they are undoable and they do not touch the terrain data asset."
            };
        }
    }
}
