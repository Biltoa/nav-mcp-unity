using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace Umcp.Agent
{
    /// <summary>
    /// Navigation: what has been baked, whether a path exists, and baking itself.
    ///
    /// Querying is core engine (<c>UnityEngine.AIModule</c>) and always available. *Baking*, in
    /// Unity 6, belongs to the optional <c>com.unity.ai.navigation</c> package and its
    /// <c>NavMeshSurface</c> components, so it is reached by reflection and its absence is
    /// reported as a result rather than as a missing method.
    /// </summary>
    internal static class NavTools
    {
        [UnityTool(Skill = "navmesh", Id = "navmesh.info",
            Summary = "What navigation data the open scenes actually contain: triangulation size, areas, agent types, surfaces.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ }")]
        public static object Info()
        {
            var triangulation = NavMesh.CalculateTriangulation();
            var areas = triangulation.areas ?? new int[0];

            var surfaces = Resolve.AllGameObjects()
                .SelectMany(go => go.GetComponents<Component>())
                .Where(c => c != null && c.GetType().Name == "NavMeshSurface")
                .Select(c => new
                {
                    path = Resolve.Path(c.transform),
                    agentTypeId = Read(c, "agentTypeID"),
                    collectObjects = Read(c, "collectObjects") == null ? null : Read(c, "collectObjects").ToString(),
                    hasData = Read(c, "navMeshData") != null
                })
                .ToArray();

            var agents = Resolve.AllGameObjects()
                .Select(go => go.GetComponent<NavMeshAgent>())
                .Where(a => a != null)
                .Select(a => new { path = Resolve.Path(a.transform), radius = a.radius, speed = a.speed, onNavMesh = a.isOnNavMesh })
                .Take(50)
                .ToArray();

            return new
            {
                baked = triangulation.vertices != null && triangulation.vertices.Length > 0,
                vertices = triangulation.vertices == null ? 0 : triangulation.vertices.Length,
                triangles = triangulation.indices == null ? 0 : triangulation.indices.Length / 3,
                areasUsed = areas.Distinct().OrderBy(a => a).Select(a => new { area = a, name = NavMesh.GetAreaNames().ElementAtOrDefault(a) }).ToArray(),
                surfaces,
                agents = new { count = agents.Length, sample = agents },
                packageInstalled = SurfaceType() != null,
                _hint = triangulation.vertices == null || triangulation.vertices.Length == 0
                    ? "Nothing is baked. navmesh.bake bakes the NavMeshSurface components in the scene."
                    : null
            };
        }

        [UnityTool(Skill = "navmesh", Id = "navmesh.path",
            Summary = "Whether a path exists between two world points on the baked navmesh, and how long it is.",
            Retry = RetryClass.Read)]
        [Example("{ \"from\": [0, 0, 0], \"to\": [10, 0, 12] }")]
        public static object Path(
            [Doc("Start point [x, y, z]")] float[] from,
            [Doc("End point [x, y, z]")] float[] to,
            [Doc("How far from each point to look for the navmesh (default 2)")] float snapDistance = 2f,
            [Doc("Return the corner positions as well as the summary")] bool corners = false)
        {
            var a = Vec.V3(from, "from");
            var b = Vec.V3(to, "to");

            NavMeshHit startHit, endHit;
            bool startOk = NavMesh.SamplePosition(a, out startHit, snapDistance, NavMesh.AllAreas);
            bool endOk = NavMesh.SamplePosition(b, out endHit, snapDistance, NavMesh.AllAreas);

            if (!startOk || !endOk)
                return new
                {
                    pathExists = false,
                    reason = !startOk && !endOk ? "neither point is near the navmesh"
                          : !startOk ? "the start point is not near the navmesh"
                          : "the end point is not near the navmesh",
                    snapDistance,
                    _hint = "Raise snapDistance, or check navmesh.info — nothing may be baked."
                };

            var path = new NavMeshPath();
            NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, path);

            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);

            return new
            {
                pathExists = path.status == NavMeshPathStatus.PathComplete,
                status = path.status.ToString(),
                length,
                cornerCount = path.corners.Length,
                straightLineDistance = Vector3.Distance(startHit.position, endHit.position),
                snappedStart = Vec.Arr(startHit.position),
                snappedEnd = Vec.Arr(endHit.position),
                corners = corners ? path.corners.Take(200).Select(Vec.Arr).ToArray() : null,
                _hint = path.status == NavMeshPathStatus.PathPartial
                    ? "A partial path means the destination is unreachable and this is as close as an agent gets."
                    : null
            };
        }

        [UnityTool(Skill = "navmesh", Id = "navmesh.bake",
            Summary = "Bake the NavMeshSurface components in the open scenes. Requires the AI Navigation package.",
            Mutating = true, Retry = RetryClass.None, Cost = Cost.Expensive,
            NoUndoReason = "A bake writes navmesh data assets; Unity does not put it on the undo stack.")]
        [Example("{ }")]
        public static object Bake([Doc("Only this surface (path or #id). Omit to bake them all.")] string target = null)
        {
            var surfaceType = SurfaceType();
            if (surfaceType == null)
                throw new UmcpToolException("E_PACKAGE_MISSING",
                    "NavMeshSurface is part of com.unity.ai.navigation, which is not installed.",
                    "target", target, null,
                    "Install AI Navigation from the Package Manager, then add a NavMeshSurface to the scene.");

            var surfaces = (target == null
                    ? Resolve.AllGameObjects()
                    : new[] { Resolve.GameObject(target, "target") })
                .SelectMany(go => go.GetComponents<Component>())
                .Where(c => c != null && c.GetType().Name == "NavMeshSurface")
                .ToArray();

            if (surfaces.Length == 0)
                throw new UmcpToolException("E_NO_SURFACES",
                    target == null ? "No NavMeshSurface components in the open scenes."
                                   : "'" + target + "' has no NavMeshSurface component.",
                    "target", target, null,
                    "Add a NavMeshSurface with component.add, then bake.");

            var baked = 0;
            foreach (var surface in surfaces)
            {
                var method = surface.GetType().GetMethod("BuildNavMesh", BindingFlags.Public | BindingFlags.Instance);
                if (method == null) continue;
                method.Invoke(surface, null);
                EditorUtility.SetDirty(surface);
                baked++;
            }

            var triangulation = NavMesh.CalculateTriangulation();
            return new
            {
                baked,
                surfaces = surfaces.Select(s => Resolve.Path(s.transform)).ToArray(),
                vertices = triangulation.vertices == null ? 0 : triangulation.vertices.Length,
                triangles = triangulation.indices == null ? 0 : triangulation.indices.Length / 3,
                _hint = "The scene holds the reference to the baked data; save it to keep the bake."
            };
        }

        static Type SurfaceType()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType("Unity.AI.Navigation.NavMeshSurface", false); }
                catch { continue; }
                if (type != null) return type;
            }
            return null;
        }

        static object Read(object target, string member)
        {
            var type = target.GetType();
            var prop = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null) { try { return prop.GetValue(target, null); } catch { return null; } }
            var field = type.GetField(member, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) { try { return field.GetValue(target); } catch { return null; } }
            return null;
        }
    }
}
