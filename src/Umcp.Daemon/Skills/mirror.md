title: The mirror — how reads are answered
covers: source and staleMs, verify, what the mirror holds, reconcile, reads during recompiles
excludes: mutations, which always go live
tools: mirror.snapshot, mirror.hashes

# The mirror

Reads are answered from a model the daemon keeps of the scene hierarchy, updated by push from
Unity's own `ObjectChangeEvents` stream. That is why a read costs about **1 ms** instead of ~95 ms,
and why reads keep working while the Editor is recompiling.

**Mutations always go live.** The mirror is never authoritative for writes.

## Reading the provenance

Every response says where its answer came from:

```jsonc
"meta": { "source": "mirror", "staleMs": 42, "epoch": 19, "revision": 118 }
```

| Field | Meaning |
|---|---|
| `source` | `"mirror"` or `"live"` |
| `staleMs` | how long since anything changed the model |
| `epoch` | bumped on every domain reload; ids from an older epoch are meaningless |
| `revision` | bumped on every applied delta |
| `stale: true` | the Editor is gone or reloading, so the model cannot currently be refreshed |

A cache that silently lies is worse than no cache. If you need certainty, pass `verify: true` on
`unity_run` and the read takes a live round trip instead.

## What the mirror holds, and what it does not

Held: instance id, name, parentage and sibling order, active state, tag, layer, and the list of
component **type names**.

Not held: component **property values**. So `scene.query` with `fields: ["Rigidbody.mass"]` goes
live automatically — it is not answered approximately. Likewise a `[has:Foo]` predicate naming a
component that appears nowhere in the mirror goes live, so the Editor can return
`E_TYPE_NOT_FOUND` with suggestions rather than an empty result that looks like a true answer.

## Reads during a recompile

A domain reload destroys every instance id, so the daemon re-seeds rather than patching. While the
Editor is away, reads are still answered — flagged `stale: true` with the reason — and mutations are
held and replayed. Measured: 60 reads across 5 consecutive reloads, zero failures.

Practical consequence: **instance ids do not survive a recompile.** If a sequence spans a compile,
address objects by path or re-query for ids afterwards.

## Reconcile

`ObjectChangeEvents` is not guaranteed to describe every mutation, so the daemon periodically asks
the Editor for per-root subtree hashes and compares them against its model, re-seeding on any
mismatch. Both sides compute that hash from the same shared source file, so a match means agreement
rather than coincidence.

You can force it and see the result:

```json
{ "tool": "unity_projects", "reconcile": true }
```

`unity_projects` also reports `mirror: { seeded, nodes, staleMs, resyncs, reconciles, driftRepairs }`
per editor. A rising `driftRepairs` is worth investigating; `resyncs` simply counts reloads and
scene changes.
