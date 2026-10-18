# Watched KV Store — Design Notes

A *watched KV store* is an in-memory mirror of a NATS KV bucket kept live via `WatchAsync`. Reads are served
entirely from the local dictionary with zero NATS round-trips after the initial hydration; writes go directly to
NATS and optimistically update the mirror.

## Core idea

```
┌──────────────────────────────────────────────────────────────────┐
│                    NatsWatchedValueStore                         │
│                                                                  │
│  ┌──────────────┐   WatchAsync    ┌───────────────────────────┐  │
│  │  NATS KV     │ ─────────────▶  │  _mirror                  │  │
│  │  Bucket      │                 │  ConcurrentDictionary     │  │
│  │              │ ◀─────────────  │  string → Entry           │  │
│  └──────────────┘   PutAsync /    └───────────────────────────┘  │
│                     TryCreateAsync   ▲                           │
│                     TryUpdateAsync   │                           │
│                     TryDeleteAsync   │ Get() reads here          │
└──────────────────────────────────────┼───────────────────────────┘
                                       │
                                  callers
```

`Entry` is a private record struct: `(V? Value, ulong Revision, int Generation)`. `Generation` is an
internal counter used to detect entries that were not refreshed by the latest re-hydration replay (see
*Generation sweep* below).

The `_ready` gate (`TaskCompletionSource`) blocks `Get()` callers only until the **first** snapshot is fully
loaded. After that it stays permanently set — reconnects do not reset it.

## Hydration and the `_ready` gate

On `StartAsync`, a background loop calls `WatchAsync` on the whole bucket. NATS replays all existing entries
first (the *initial snapshot*), then switches to delivering live updates. `_ready` is completed when the snapshot
ends, releasing any callers blocked on `Get()`.

### Detecting end-of-snapshot: `Delta`, not `OnNoData`

The natural signal for "no more pending entries" looks like it should be `NatsKVWatchOpts.OnNoData`. It is not.
`OnNoData` fires **only when the bucket is empty at watcher creation** (`NumPending == 0` at init). For a
non-empty bucket it is never invoked during the snapshot, so relying on it alone causes every `Get()` to block
forever.

The correct signal is `NatsKVEntry<V>.Delta`: the number of entries still pending *after* this one in the
initial dump. When `Delta == 0` the current entry is the last one in the snapshot. `Delta` is a bucket-wide
pending count from the underlying JetStream consumer, not a per-key counter.

### Two-phase WatchLoop

`WatchLoop` separates the initial snapshot from the live-update stream by holding the enumerator explicitly
and driving it in two passes:

```csharp
private async Task WatchLoop(CancellationToken token)
{
    var generation = Interlocked.Increment(ref _generation);
    var hydrated = false;

    await using var stream = _store
        .WatchAsync(_deserializer, new NatsKVWatchOpts { OnNoData = _ => new(EndOfHistory()) }, token)
        .GetAsyncEnumerator(token);

    await Apply(stream, generation, stopOnNoData: true);  // snapshot phase
    EndOfHistory();                                        // explicit signal for non-empty bucket
    await Apply(stream, generation, stopOnNoData: false); // live-update phase

    return;

    bool EndOfHistory()
    {
        if (hydrated) return false;
        Sweep(generation);
        _ready.TrySetResult();
        hydrated = true;
        return false;
    }
}
```

`Apply` reads entries from the enumerator and optionally stops when `Delta == 0`:

```csharp
private async ValueTask Apply(IAsyncEnumerator<NatsKVEntry<V>> stream, int generation, bool stopOnNoData)
{
    while (await stream.MoveNextAsync().ConfigureAwait(false))
    {
        var entry = stream.Current;
        Apply(entry, generation);
        if (stopOnNoData && entry.Delta == 0) break;
    }
}
```

`EndOfHistory()` is a local function shared by two code paths. The `hydrated` flag ensures it is idempotent —
the second caller is always a no-op:

| Bucket state at watcher creation | What signals end-of-snapshot  |
|----------------------------------|-------------------------------|
| Empty                            | `OnNoData` callback           |
| Non-empty                        | explicit call after Phase 1   |

For an **empty bucket**: NATS fires `OnNoData` → `EndOfHistory()` runs (sweep + `TrySetResult`) → the
enumerator yields a sentinel entry with `Delta == 0` → Phase 1 breaks → explicit `EndOfHistory()` is a no-op.

For a **non-empty bucket**: Phase 1 processes snapshot entries until `Delta == 0` then breaks → explicit
`EndOfHistory()` runs for the first time → sweep + `TrySetResult`.

In both cases `EndOfHistory` runs exactly once.

## Reconnect / rehydration

The background loop is managed by a `Supervisor` that restarts it on failure. On restart the mirror is **not**
cleared — it keeps serving last-known-good data while the new snapshot replays. `_ready` is also not reset;
once completed it stays set permanently.

The restart path increments the generation counter and re-enters `WatchLoop` as normal:

```csharp
var generation = Interlocked.Increment(ref _generation);
```

At the end of each restart's snapshot, `Sweep(generation)` removes any entries not touched by this replay
(see *Generation sweep*), then `_ready.TrySetResult()` is called — a no-op if already set.

**First-start failure**: if the watch loop throws before completing a snapshot, `_ready` stays incomplete.
On the supervised restart a fresh hydration run will complete it, and callers blocked since before the failure
are unblocked normally — they do not receive `OperationCanceledException`.

## Generation sweep

NATS KV history has a configurable depth. If a key is deleted while the store is disconnected and the delete
tombstone is compacted away before reconnect, the re-hydration replay delivers no event for that key. Without
extra bookkeeping the mirror would silently retain a ghost entry forever.

The fix is a *mark-and-sweep* on each replay:

1. **Mark**: at `WatchLoop` start, increment `_generation`. Every entry applied during this replay (by `Apply`)
   is stamped with the new generation value. Write-path `MirrorUpdate` also stamps with the current
   `_generation` (via `Volatile.Read`), so optimistic updates that land during re-hydration survive the sweep.

2. **Sweep**: after the replay ends, snapshot the mirror and remove any entry whose generation is *older* than
   the current run — these are keys that existed before restart but were not seen in the replay (deleted in
   NATS, tombstone compacted).

```csharp
private void Sweep(int generation)
{
    foreach (var (natsKey, entry) in _mirror.ToArray())
        if (entry.Generation < generation)
            MirrorDelete(natsKey, entry);
}
```

`_mirror.ToArray()` snapshots the dictionary before iteration so the sweep walks a stable set. `MirrorDelete`
uses the entry-based conditional overload — an atomic compare-and-remove that is a no-op if the slot was
updated concurrently.

## Race conditions on write-path mirror updates

Every write method (`Add`, `Update`, `Overwrite`, `Invalidate`, `Delete`) optimistically caches the result in
the mirror so the next `Get()` returns it immediately without waiting for the watch event to arrive. This
introduces a race: between the NATS call returning and the mirror assignment, the watch loop may deliver a
*newer* revision for the same key.

### Writes — revision-guarded `MirrorUpdate`

Direct assignment would silently overwrite a higher revision already placed by the watch. `MirrorUpdate` only
applies the value when the incoming revision is strictly greater:

```csharp
private void MirrorUpdate(string natsKey, V? value, ulong revision, int generation) =>
    _mirror.AddOrUpdate(
        natsKey,
        static (_, e) => e,
        static (_, existing, e) => e.Revision > existing.Revision ? e : existing,
        new Entry(value, revision, generation));
```

The write-path overload (no `generation` parameter) reads the current generation with `Volatile.Read`:

```csharp
private void MirrorUpdate(string natsKey, V value, ulong revision) =>
    MirrorUpdate(natsKey, value, revision, Volatile.Read(ref _generation));
```

If the watch raced ahead to a newer revision, the write-path update is discarded — the mirror already has
fresher data.

### Deletes — two `MirrorDelete` overloads

`MirrorDelete` has two overloads with different roles:

**Entry-based (compare-and-remove)** — used by `Delete` and `Sweep`:

```csharp
private bool MirrorDelete(string key, Entry entry) =>
    _mirror.TryRemove(KeyValuePair.Create(key, entry));
```

`Delete` snapshots the current entry *before* the NATS call and passes it here. `TryRemove(KeyValuePair<K,V>)`
is atomic: if the watch delivered a new `Put` between the snapshot and the remove, the value changed and the
remove is a no-op — the fresh value is preserved.

**Revision-based (newer-tombstone guard)** — used by `Apply` (watch tombstones) and `Invalidate`:

```csharp
private bool MirrorDelete(string natsKey, ulong revision) =>
    _mirror.TryGetValue(natsKey, out var existing) &&
    existing.Revision < revision &&
    MirrorDelete(natsKey, existing);
```

Only removes when the mirror entry is *older* than the incoming revision. For watch tombstones `revision` is
the tombstone's own revision (always > the Put revision), so this correctly evicts older entries while
preserving a higher revision placed by a concurrent write-path.

## Apply — revision guard in the watch loop

`Apply` stamps entries with the current generation and guards against stale revisions:

```csharp
private void Apply(NatsKVEntry<V> entry, int generation)
{
    if (entry.Operation == NatsKVOperation.Put)
        MirrorUpdate(entry.Key, entry.Value, entry.Revision, generation);
    else
        MirrorDelete(entry.Key, entry.Revision);
}
```

Put entries go through `MirrorUpdate`'s revision guard (keeps the higher revision). Delete tombstones go
through the revision-based `MirrorDelete` — evicts only if the mirror entry is older than the tombstone.

## Get — fast path for the hot case

`Get` avoids allocating a `WaitAsync` continuation when `_ready` is already set:

```csharp
public async Task<ValueRecord<V>?> Get(K key, CancellationToken token = default)
{
    if (!_ready.Task.IsCompletedSuccessfully)
        await _ready.Task.WaitAsync(token);
    ...
}
```

After first hydration every `Get` call takes the fast path and reads directly from the dictionary.

## Implementation checklist

When porting to another project:

- `_mirror` must be keyed by the **NATS string key**, not the application key type, because `WatchAsync` entries
  carry the string key only.
- `_ready` is set **once** (first hydration) and never reset. Make it `readonly`.
- `_generation` must be incremented with `Interlocked.Increment` at the top of each `WatchLoop` invocation.
- `Sweep` must snapshot the dictionary (`ToArray()`) before iterating and use the entry-based `TryRemove` for
  atomic conditional removes.
- `Sweep` must run **before** `_ready.TrySetResult()` — both inside `EndOfHistory()`.
- `EndOfHistory()` must be idempotent (`hydrated` flag); it is called from both `OnNoData` and explicitly
  after Phase 1.
- `MirrorUpdate` (write-path overload) must read `_generation` with `Volatile.Read` so optimistic updates
  during re-hydration are stamped with the current generation and survive the sweep.
- `OnNoData` must return `false` (keep watching) not `true` (stop).
- `IHostedService.StopAsync` must await `Supervisor.Done` to ensure the watch loop has exited before the
  instance is disposed.
- For keyed DI registrations, use `AddSingleton<IHostedService>(p => p.GetRequiredKeyedService<...>(key))`
  instead of `AddHostedService<T>()`. `AddHostedService` uses `TryAddEnumerable` which deduplicates by
  implementation type — two keyed registrations of the same `K,V` pair would silently drop the second.
