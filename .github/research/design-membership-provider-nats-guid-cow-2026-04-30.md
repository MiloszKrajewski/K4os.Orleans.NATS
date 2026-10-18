# Orleans Membership Provider: NATS GUID Copy-on-Write Strategy

**Date:** 2026-04-30  
**Scope:** NATS JetStream KV implementation of `IMembershipTable`  
**Predecessor:** `code-analysis-membership-provider-nats-2026-04-12.md` (original analysis + Strategy A single-blob recommendation)  
**Goal:** Document the refined GUID copy-on-write (CoW) strategy, assess atomicity and scalability, and replace the single-blob Strategy A recommendation

---

## 1. Motivation

The original analysis concluded that Strategy A (single blob) was the most viable NATS implementation — storing the entire cluster membership as one NATS KV value under `{clusterId}/membership`. Correctness is preserved via single-key CAS on the NATS revision, but the strategy has significant scalability drawbacks:

- Every write (`InsertRow`, `UpdateRow`, `UpdateIAmAlive`) rewrites the **entire cluster state** — O(N silos) payload on each write
- The single key becomes a **hot key** with large payload; CAS round-trips grow with cluster size
- `UpdateIAmAlive` becomes a CAS retry loop on a large blob, creating contention under simultaneous heartbeats

The GUID CoW strategy retains the atomicity guarantee of Strategy A while reducing the main-row write payload from O(N) to O(1) and separating heartbeat writes entirely from the CAS machinery.

---

## 2. Key Layout

```
bucket: orleans-{serviceId}

{clusterId}.struct.membership         → directory blob (small, CAS-guarded)
{clusterId}.struct.silo.{guid}        → silo entry blob (per-silo, CoW)
{clusterId}.alive.{encodedSiloAddr}   → IAmAlive timestamp (monotonic, dirty-write)
```

**Key separator:** `.` is used throughout because NATS KV uses it as the hierarchy separator for wildcard subject matching (`>`). Keys using `/` are valid in NATS KV but do not participate in `.`-delimited wildcard filtering — a single `WatchAsync("{clusterId}.struct.>")` would not match `/`-separated keys.

**SiloAddress encoding:** `SiloAddress` strings (e.g., `10.0.0.1:11111/12345`) contain `.` which would be interpreted as hierarchy separators. Replace `.` with `_` and sanitize other special characters when using siloAddress as a key component. The directory stores the canonical `siloAddress` string; only the key encoding is transformed.

**The `struct` prefix:** Groups directory + silo entry blobs — the two key types with commit-order dependency on each other (see §5.1.1). Alive keys sit under `alive.` with no commit-order relationship to `struct.` keys, which enables independent watch scoping.

### Directory Blob (`{clusterId}.struct.membership`)

```json
{
  "version": 5,
  "versionEtag": "42",
  "silos": [
    { "siloAddress": "10.0.0.1:11111/12345", "entryKey": "a1b2c3d4-..." },
    { "siloAddress": "10.0.0.2:11111/67890", "entryKey": "e5f6a7b8-..." }
  ]
}
```

The NATS KV `revision` of this key is the CAS guard for all structured membership writes. It maps directly to `TableVersion.VersionEtag`.

### Silo Entry Blob (`{clusterId}.struct.silo.{guid}`)

```json
{
  "siloAddress": "10.0.0.1:11111/12345",
  "hostName": "...",
  "siloName": "...",
  "status": 3,
  "proxyPort": 30000,
  "startTime": "...",
  "suspectTimes": [...],
  "siloEtag": "7"
}
```

Keyed by a GUID generated at write time. `IAmAliveTime` is intentionally absent — it is stored separately (see §6).

### IAmAlive Key (`{clusterId}.alive.{encodedSiloAddr}`)

```
value: "2026-04-30T10:15:00.000Z"   (ISO 8601 UTC)
```

Plain dirty-write key. Never CAS-guarded. Merged into `MembershipEntry` at read time.

---

## 3. Operation Mapping

| Orleans Operation | NATS KV Operations | Notes |
|---|---|---|
| `ReadAll()` | mirror lookup (WatchedKV) or `Get(struct.membership)` → parallel `Get(struct.silo.{guid})` × N → merge alive keys | see §5 |
| `ReadRow(silo)` | mirror lookups or `Get(struct.membership)` → find guid → `Get(struct.silo.{guid})` → `Get(alive.{addr})` | see §5 |
| `InsertRow(entry, ver)` | `Put(struct.silo.{newGuid}, data)` → `Update(struct.membership, newDir, revision)` | CoW; see §4 |
| `UpdateRow(entry, etag, ver)` | `Put(struct.silo.{newGuid}, data)` → `Update(struct.membership, newDir, revision)` | CoW; see §4 |
| `UpdateIAmAlive(entry)` | `Put(alive.{addr}, iAmAliveTime)` | Fire-and-forget; see §6 |
| `DeleteMembershipTableEntries` | `Delete(struct.membership)` + purge `{clusterId}.*` | Cleanup on cluster teardown |
| `CleanupDefunctSiloEntries` | `Get(struct.membership)` → filter → `Update(struct.membership, newDir, revision)` | No silo guid writes needed |

---

## 4. Write Protocol (InsertRow / UpdateRow)

The copy-on-write pattern makes the directory CAS the single atomic commit point:

```
Step 1 — Write new silo data (non-contentious, new key):
    kv.Put({clusterId}.struct.silo.{newGuid}, newSiloBlob)
    → always succeeds; no contention on a fresh GUID

Step 2 — Atomic pointer swap (CAS on directory):
    read current directory → revision R
    validate: version matches, silo etag matches (for UpdateRow), silo not exists (for InsertRow)
    build newDirectory with {siloAddress → newGuid} replacing old reference
    kv.Update({clusterId}.struct.membership, newDirectory, R)
    → returns NatsKVWrongLastRevisionException on conflict → return false to Orleans

Step 3 — Cleanup (async, best-effort):
    if Step 2 succeeded: kv.Delete({clusterId}.struct.silo.{oldGuid})   ← old entry no longer referenced
    if Step 2 failed:    (newGuid key is orphaned — expires via TTL; no action needed)
```

### Atomicity Guarantee

The directory `kv.Update` at Step 2 is the **sole commit point**. Before the CAS succeeds, no reader can see `newGuid` (it is not in the directory). After the CAS succeeds, `newGuid` is the authoritative silo state. The orphaned `newGuid` from a failed Step 2 is invisible to all readers.

This is structurally equivalent to shadow paging: write the new page, then swap the pointer. Correctness matches Strategy A — the same invariant (single-key CAS guards the version + pointer atomically) is preserved.

**On `NatsKVWrongLastRevisionException`**: return `false` to the Orleans runtime. The runtime retries with exponential backoff (unlimited retries per `MembershipTableManager.NUM_CONDITIONAL_WRITE_CONTENTION_ATTEMPTS = -1`). The orphaned `newGuid` key from the failed attempt is discarded at TTL.

---

## 5. Read Strategy

Two read modes operate on the same key layout without schema changes, allowing a future switch from watch to poll (or a hybrid) as cluster scale grows.

### 5.1 WatchedKV Mode (Primary)

A `WatchedKV` instance mirrors the bucket into a local `ConcurrentDictionary` kept live via `WatchAsync`. After initial hydration, all reads are served from the mirror with zero NATS round-trips. See `ref-watched-kv-store.md` for the implementation contract.

**Watch scope — two instances on the same bucket:**

| Instance | Watch pattern | Content | Churn |
|---|---|---|---|
| Structural watcher | `{clusterId}.struct.>` | directory + all silo entry blobs | low — only on membership changes |
| Alive watcher | `{clusterId}.alive.>` | IAmAlive timestamps | high — one event per silo per heartbeat |

**ReadAll (WatchedKV mode):**
```
1. structMirror.Get(struct.membership)           → directory (zero round-trips)
2. structMirror.Get(struct.silo.{guid_i}) × N    → silo blobs (zero round-trips)
3. aliveMirror.Get(alive.{addr_i}) × N           → alive timestamps (zero round-trips)
4. merge into MembershipTableData
```

**ReadRow (WatchedKV mode):** Three mirror lookups — zero round-trips.

#### 5.1.1 Single-Watcher Ordering Constraint

The CoW write protocol publishes two events to NATS in this sequence:

```
Event 1: Put(struct.silo.{newGuid}, siloData)         ← silo data written first
Event 2: Update(struct.membership, newDir, revision)   ← directory now references newGuid
```

NATS guarantees these arrive in publish order on a single stream. The structural watcher (`{clusterId}.struct.>`) processes both events sequentially: the silo blob lands in the mirror **before** the directory update that references it. A `ReadAll` that sees the new GUID in the directory is therefore guaranteed to find the silo blob already present in the mirror.

**Two separate watchers break this guarantee.** If the directory and the silo blobs were watched by independent subscriptions, the directory update could be delivered and processed before the silo blob arrives in its watcher. A `ReadAll` between those two deliveries would see a GUID reference in the directory but a cache miss for the silo blob.

NATS does not support exclusion patterns (`{clusterId}.struct.> EXCEPT {clusterId}.struct.alive.>`); there is no additive filter expression either. The only way to cover both `struct.membership` and `struct.silo.*` with a single subscription is a shared prefix — hence `struct.`. Alive keys are under a separate `alive.` prefix because they have **no commit-order relationship** with `struct.` keys; their watcher is independent without any ordering risk.

#### 5.1.2 N² Alive Fan-Out

With N silos each writing one heartbeat per period T, the bucket receives N/T alive-key writes per second. All N silos watch the bucket, so total event deliveries across the cluster are N²/T per second.

| Cluster size | Heartbeat period | Per-watcher event rate | Assessment |
|---|---|---|---|
| N=10 | 2s | 5/s | negligible |
| N=100 | 2s | 50/s | trivial |
| N=500 | 1s | 500/s | monitor |
| N=1000 | 1s | 1000/s | consider poll fallback |

The `alive.` / `struct.` prefix separation means the alive watcher can be independently replaced with on-demand polling for large clusters without touching the structural watcher or the key schema.

### 5.2 Poll Mode Fallback (Future Option)

If alive key churn at scale makes the alive watcher impractical, the read path can switch to explicit `kv.Get` calls. No schema changes are needed — the key layout already supports both modes.

```
1. kv.Get(struct.membership)                          → directory
2. parallel kv.Get(struct.silo.{guid_i}) × N          → silo blobs
3. parallel kv.Get(alive.{addr_i}) × N                → alive timestamps
4. merge into MembershipTableData
```

**TOCTOU risk (poll mode only):** A concurrent writer could CAS the directory and delete an old silo GUID between steps 1 and 2. Mitigation: apply TTL on silo blobs (see §9) rather than immediate deletion. On a 404 for a referenced GUID, re-read the directory and retry. This is the same pattern DynamoDB uses for its eventual-consistency window. In WatchedKV mode this risk does not exist — stream ordering ensures the silo blob is in the mirror before the directory update that references it.

### 5.3 Snapshot Freshness

Neither mode guarantees a strictly linearizable snapshot across all three key types. Concurrent writes can interleave with reads. The Orleans membership protocol tolerates this: `ReadAll` is used for silo failure detection via `IAmAliveTime`, not as a strict atomic snapshot.

---

## 6. UpdateIAmAlive — Monotonic Dirty Write

`UpdateIAmAlive` is documented in the interface as:

> "There is no scenario where this operation could fail due to table semantical reasons."

It is a **best-effort heartbeat write** — no CAS, no version bump, no etag required. In Strategy A (single blob) this was incorrectly handled as a CAS retry loop, a consequence of everything being in one key.

In the GUID CoW strategy, `IAmAliveTime` is stored in a **dedicated per-silo key** outside the directory:

```
kv.Put({clusterId}.alive.{encodedSiloAddr}, utcTimestamp)
```

Properties that make this safe as a pure fire-and-forget write:

1. **Monotonically increasing** — `IAmAliveTime` is always `DateTime.UtcNow`; last-write-wins is always correct
2. **Single writer** — only the silo itself writes its own heartbeat; no concurrent writer race per key
3. **Benign loss** — if two heartbeat ticks from the same silo race, the later timestamp wins; either outcome is correct

The alive key is stable for the lifetime of the silo address, independent of GUID rotation in the structural keys. A concurrent `UpdateRow` that creates a new GUID and swaps the directory pointer has no effect on the alive key — `UpdateIAmAlive` never touches the directory.

**Zero main-row contention.** N silos heartbeating simultaneously produce N independent `kv.Put` operations on N distinct keys. No serialization, no retries.

---

## 7. Comparison with Strategy A

| Aspect | Strategy A (Single Blob) | GUID CoW Strategy |
|---|---|---|
| Atomicity mechanism | Single-key CAS on full blob | Single-key CAS on small directory |
| Main-row write payload | O(N silos) — full cluster JSON | O(1) — directory of references only |
| Write amplification | Every update rewrites all silo data | Only changed silo data + directory |
| CAS round-trip cost | Grows with cluster size | Constant regardless of cluster size |
| `UpdateIAmAlive` | CAS retry on full blob (contention) | Fire-and-forget on isolated key (zero contention) |
| `ReadAll` round-trips (poll) | 1 (`kv.Get`) | 2N+1 parallel (mitigated by parallel fan-out + WatchedKV) |
| `ReadAll` round-trips (watch) | N/A (blob still requires CAS loop) | 0 (mirror lookup) |
| Read latency (large N) | Grows with blob deserialization | Zero (mirror) or parallel fan-out |
| TOCTOU risk | None | Poll mode only; eliminated in WatchedKV mode |
| Orphan management | None | TTL-based (operational concern, not correctness) |
| Implementation complexity | Low | Medium |
| Correctness | Equivalent | Equivalent |

Strategy A remains simpler and correct. The GUID CoW strategy is preferred for any cluster expected to exceed ~50 silos or to have frequent membership changes, where Strategy A's payload growth becomes the dominant cost.

---

## 8. Silo Etag Semantics

The Orleans protocol requires a per-silo etag returned from `ReadAll` and validated in `UpdateRow`. In the GUID CoW strategy:

- **Per-silo etag**: an integer counter stored in the silo blob, incremented on each `UpdateRow`
- `InsertRow` starts the counter at `1`; `UpdateRow` increments it
- `ReadRow` / `ReadAll` return this counter as the silo's etag string
- `UpdateRow` validates that the blob's counter matches the provided etag before writing the new GUID

The NATS revision of the directory key serves as `TableVersion.VersionEtag`. The two etags are independent:

| Etag | Source | Validates |
|---|---|---|
| `TableVersion.VersionEtag` | NATS revision of `struct.membership` key | Version row CAS in `InsertRow`/`UpdateRow` |
| Per-silo etag string | Integer field in silo blob | Silo row CAS in `UpdateRow` |

---

## 9. TTL Configuration

TTL is needed on silo GUID keys to reclaim orphaned and retired entries without requiring reliable deletion.

| Key type | Recommended TTL | Rationale |
|---|---|---|
| `{clusterId}.struct.silo.{guid}` | 5–10 min (WatchedKV mode) / 30 min (poll mode) | WatchedKV mode has no TOCTOU risk so TTL is cleanup-only; poll mode needs TTL > max ReadAll duration |
| `{clusterId}.alive.{addr}` | ~2× silo heartbeat period | Absence detection: a silo that stops heartbeating becomes suspectable within this window |
| `{clusterId}.struct.membership` | None | Permanent cluster state; deleted only by `DeleteMembershipTableEntries` |

NATS KV per-key TTL is available as of NATS Server 2.10. See gap §10.1.

---

## 10. Gaps & Uncertainties

- 🔴 **10.1 NATS KV per-key TTL (NATS 2.10+)**: Per-key TTL must be confirmed in the NATS .NET client (`NATS.Client.JetStream`). If per-key TTL is unavailable, GUID cleanup requires either explicit deletion in Step 3 (with failure tolerance) or a background sweep. Bucket-level TTL is not a substitute since it would expire the directory key.

- 🟡 **10.2 SiloAddress key encoding**: `SiloAddress` strings contain `.`, `:`, and `/` which conflict with NATS key hierarchy separators or subject syntax. A consistent encoding scheme (e.g., replace `.` → `_`, `:` → `-`, `/` → `~`) must be defined and applied wherever siloAddress appears as a key component. The canonical siloAddress string in directory blobs is unaffected.

- 🟡 **10.3 WatchedKV startup hydration**: On first start, the structural watcher replays all existing `struct.silo.*` entries (active + recently-retired GUIDs within TTL). For a cluster with high turnover, the number of expired-but-not-yet-purged GUID keys could be large. The `Generation sweep` in WatchedKV handles this correctly (retired GUIDs not in the replay are swept), but replay duration scales with NATS KV history depth.

- 🟡 **10.4 IAmAlive merge on ReadRow**: `ReadRow` must merge the silo blob with the alive key. If the alive key has never been written (new silo before first heartbeat), `IAmAliveTime` defaults to `StartTime`. This edge case should be confirmed against Orleans runtime expectations.

- 🟡 **10.5 Alive watcher at large scale**: The N² fan-out of alive key events (see §5.1.2) is manageable at typical Orleans cluster sizes (< 200 silos). For larger clusters, switching to on-demand polling for alive keys requires no schema change — the `alive.` prefix already provides the necessary isolation. This should be made a configurable option.

- 🔴 **10.6 NATS .NET client KV API verification**: As noted in the original analysis, the full KV API (`Create`, `Update`, `Get` with revision, `Delete` with revision, `WatchAsync` with key pattern) must be confirmed against the actual NATS .NET client version in use. Specifically, wildcard watch patterns (`prefix.>`) must be verified to filter by `.`-delimited key prefix as expected.

---

## 11. References

| Resource | Purpose |
|---|---|
| `code-analysis-membership-provider-nats-2026-04-12.md` | Original implementation analysis, Strategy A/B/C comparison, NATS KV capabilities |
| `ref-watched-kv-store.md` | WatchedKV implementation contract — hydration, generation sweep, ordering guarantees (copied from `K4os.NatsTransit`) |
| `src/Orleans.Core/SystemTargetInterfaces/IMembershipTable.cs` | Interface definition — authoritative semantics for each operation |
| `src/Orleans.Runtime/MembershipService/MembershipTableManager.cs` | Runtime consumer — retry logic, `NUM_CONDITIONAL_WRITE_CONTENTION_ATTEMPTS` |
| `src/Redis/Orleans.Clustering.Redis/Storage/RedisMembershipTable.cs` | Closest analog: single-hash trick, per-silo etag as counter in blob |
| `src/AWS/Orleans.Clustering.DynamoDB/Membership/DynamoDBMembershipTable.cs` | ReadAll retry pattern under eventual consistency (§5.2 poll mode analog) |
