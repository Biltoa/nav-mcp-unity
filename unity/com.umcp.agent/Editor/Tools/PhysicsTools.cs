using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Physics: the project settings, and the two spatial queries that are genuinely awkward to
    /// express any other way.
    ///
    /// Adding a Rigidbody or a Collider is <c>component.add</c>, and setting mass is
    /// <c>component.set</c> — there are no wrappers for those here, because a wrapper that adds
    /// nothing is a schema you pay for and a second place for the behaviour to drift.
    /// </summary>
    internal static class PhysicsTools
    {
        [UnityTool(Skill = "physics", Id = "physics.settings",
            Summary = "Read the project's 3D physics settings: gravity, layer collision matrix, solver, contact offsets.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object Settings([Doc("Include the full 32x32 layer collision matrix")] bool layerMatrix = false)
        {
            var ignored = new System.Collections.Generic.List<object>();
            var ignored2D = new System.Collections.Generic.List<object>();
            if (layerMatrix)
                for (int a = 0; a < 32; a++)
                    for (int b = a; b < 32; b++)
                    {
                        var na = LayerMask.LayerToName(a);
                        var nb = LayerMask.LayerToName(b);
                        if (string.IsNullOrEmpty(na) || string.IsNullOrEmpty(nb)) continue;
                        if (Physics.GetIgnoreLayerCollision(a, b)) ignored.Add(new { a = na, b = nb });
                        if (Physics2D.GetIgnoreLayerCollision(a, b)) ignored2D.Add(new { a = na, b = nb });
                    }

            return new
            {
                gravity = Vec.Arr(Physics.gravity),
                defaultSolverIterations = Physics.defaultSolverIterations,
                defaultSolverVelocityIterations = Physics.defaultSolverVelocityIterations,
                bounceThreshold = Physics.bounceThreshold,
                sleepThreshold = Physics.sleepThreshold,
                defaultContactOffset = Physics.defaultContactOffset,
                queriesHitTriggers = Physics.queriesHitTriggers,
                simulationMode = Physics.simulationMode.ToString(),
                fixedTimestep = Time.fixedDeltaTime,
                ignoredLayerPairs = layerMatrix ? ignored.ToArray() : null,
                physics2D = new
                {
                    gravity = new[] { Physics2D.gravity.x, Physics2D.gravity.y },
                    velocityIterations = Physics2D.velocityIterations,
                    positionIterations = Physics2D.positionIterations,
                    queriesHitTriggers = Physics2D.queriesHitTriggers,
                    queriesStartInColliders = Physics2D.queriesStartInColliders,
                    simulationMode = Physics2D.simulationMode.ToString(),
                    ignoredLayerPairs = layerMatrix ? ignored2D.ToArray() : null
                },
                _hint = layerMatrix ? null : "pass layerMatrix:true for the pairs that are set to ignore each other"
            };
        }

        [UnityTool(Skill = "physics", Id = "physics.raycast",
            Summary = "Cast a ray in the Editor scene and return what it hits. Does not enter Play mode.",
            Retry = RetryClass.Read)]
        [Example("{ \"origin\": [0, 10, 0], \"direction\": [0, -1, 0], \"maxDistance\": 50 }")]
        public static object Raycast(
            [Doc("World-space origin [x, y, z]")] float[] origin,
            [Doc("Direction [x, y, z]; normalised for you")] float[] direction,
            [Doc("Maximum distance (default 1000)")] float maxDistance = 1000f,
            [Doc("Layer names to hit. Omit for all layers.")] string[] layers = null,
            [Doc("Return every hit along the ray, not just the first")] bool all = false,
            [Doc("Maximum hits when all is true (default 20)")] int limit = 20)
        {
            var o = Vec.V3(origin, "origin");
            var d = Vec.V3(direction, "direction");
            if (d == Vector3.zero)
                throw new UmcpToolException("E_ARG_VALUE", "'direction' cannot be the zero vector.",
                    "direction", "[0,0,0]", null, "For a downward ray use [0, -1, 0].");

            int mask = ~0;
            if (layers != null && layers.Length > 0)
            {
                mask = 0;
                foreach (var name in layers)
                {
                    int index = LayerMask.NameToLayer(name);
                    if (index < 0)
                        throw new UmcpToolException("E_LAYER_NOT_FOUND", "No layer named '" + name + "'.",
                            "layers", name,
                            Enumerable.Range(0, 32).Select(LayerMask.LayerToName).Where(s => !string.IsNullOrEmpty(s)).ToArray(),
                            null);
                    mask |= 1 << index;
                }
            }

            // Physics queries read the *scene's* colliders, which exist in edit mode. Nothing here
            // simulates, and nothing enters Play mode: an Editor that has to be running to answer a
            // question is not answering a question, it is changing the state you asked about.
            Physics.SyncTransforms();
            if (!all)
            {
                RaycastHit hit;
                if (!Physics.Raycast(o, d.normalized, out hit, maxDistance, mask))
                    return new { hit = false, origin, direction, maxDistance };

                return new { hit = true, first = Describe(hit), origin, direction, maxDistance };
            }

            var allHits = Physics.RaycastAll(o, d.normalized, maxDistance, mask)
                                 .OrderBy(h => h.distance)
                                 .ToArray();
            var hits = allHits.Take(Bounds.Limit(limit)).Select(Describe).ToArray();
            var truncated = allHits.Length > hits.Length;
            return new
            {
                hit = hits.Length > 0,
                count = hits.Length,
                total = allHits.Length,
                returned = hits.Length,
                hits,
                _truncated = truncated,
                _hint = truncated ? "raise limit to return more hits" : null
            };
        }

        [UnityTool(Skill = "physics", Id = "physics.overlap",
            Summary = "Find colliders overlapping a sphere or box in the Editor scene.",
            Retry = RetryClass.Read)]
        [Example("{ \"center\": [0, 1, 0], \"radius\": 5 }")]
        public static object Overlap(
            [Doc("World-space centre [x, y, z]")] float[] center,
            [Doc("Sphere radius. Give this or halfExtents.")] float radius = 0f,
            [Doc("Box half-extents [x, y, z]. Give this or radius.")] float[] halfExtents = null,
            [Doc("Maximum results (default 50)")] int limit = 50)
        {
            var c = Vec.V3(center, "center");
            Collider[] found;

            Physics.SyncTransforms();
            if (halfExtents != null) found = Physics.OverlapBox(c, Vec.V3(halfExtents, "halfExtents"));
            else if (radius > 0f) found = Physics.OverlapSphere(c, radius);
            else throw new UmcpToolException("E_ARG_REQUIRED",
                "Give either 'radius' (sphere) or 'halfExtents' (box).", "radius", null, null,
                "For example: { \"center\": [0,1,0], \"radius\": 5 }");

            var page = found.Take(Bounds.Limit(limit)).Select(col => new
            {
                path = Resolve.Path(col.transform),
                collider = col.GetType().Name,
                isTrigger = col.isTrigger,
                layer = LayerMask.LayerToName(col.gameObject.layer),
                distance = Vector3.Distance(c, col.ClosestPoint(c))
            }).ToArray();

            return Res.Page(page, found.Length, 0, page.Length);
        }

        static object Describe(RaycastHit hit)
        {
            return new
            {
                path = hit.collider == null ? null : Resolve.Path(hit.collider.transform),
                collider = hit.collider == null ? null : hit.collider.GetType().Name,
                distance = hit.distance,
                point = Vec.Arr(hit.point),
                normal = Vec.Arr(hit.normal),
                layer = hit.collider == null ? null : LayerMask.LayerToName(hit.collider.gameObject.layer)
            };
        }
    }
}
