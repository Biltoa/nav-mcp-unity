title: Navigation and pathfinding
covers: what is baked, whether a path exists, baking NavMeshSurfaces
excludes: agent behaviour at runtime, which needs Play mode and is out of scope

# Navigation

Project: **{{project}}** · Unity {{unityVersion}}.

## Reading before baking

```
unity_run("navmesh.info")
unity_run("navmesh.path", { from: [0,0,0], to: [12,0,30] })
```

`navmesh.info` says whether anything is baked at all — vertices, triangles, the areas in use, the
`NavMeshSurface` components present, and a sample of agents. `navmesh.path` answers the question
that actually matters when an agent "does not move": **is there a path**, and if not, which end is
off the mesh.

Its statuses are worth knowing exactly:

| status | meaning |
|---|---|
| `PathComplete` | a full path exists |
| `PathPartial` | the destination is unreachable; this is as close as an agent gets |
| `PathInvalid` | one or both points could not be placed on the navmesh |

`snapDistance` controls how far from each point the tool looks for the mesh. A point one metre above
the floor is normal; a point that needs ten metres of snapping is telling you the mesh is not where
you think.

## Baking

`navmesh.bake` builds every `NavMeshSurface` in the open scenes, or one named surface.

Two constraints, both from Unity rather than from this tool:

- **`NavMeshSurface` belongs to `com.unity.ai.navigation`**, which is optional. Without it the tool
  returns `E_PACKAGE_MISSING` and tells you what to install. Querying still works — the query API
  is core engine.
- **The scene holds the reference to the baked data.** Baking and not saving the scene loses the
  bake, and this tool never saves your scene for you.

Baking is synchronous and can take a while on a large scene. Queued operations wait behind it, which
is visible as a slow call rather than as a failure, and `unity_projects` will report the Editor as
`degraded` while it runs — that is a bake, not a hang.
