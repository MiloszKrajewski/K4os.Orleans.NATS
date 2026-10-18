## Context

Orleans `IMembershipTable` is the persistence layer for cluster membership — it stores which silos are alive, their status, and their last heartbeat time. The NATS KV store provides per-key optimistic concurrency (revision-based CAS), making it a natural fit.

Three previous iterations (vPrev1/2/3) were abandoned mid-implementation. vPrev3 (the most recent) used a single-blob strategy (entire cluster state in one key + separate heartbeat keys), which has O(N) write amplification and creates contention on `UpdateIAmAlive` since every heartbeat requires a CAS retry loop on the full cluster blob.

The NATS .NET client (v2.6.10) already uses per-key TTL in `StoreLock` (`TryCreateAsync` / `TryUpdateAsync` with `TimeSpan` TTL parameter), confirming client support for NATS Server 2.10+ per-key TTL.

## Goals / Non-Goals

**Goals:**
- Correct, complete `IMembershipTable` implementation passing Orleans membership protocol requirements
- O(1) directory CAS payload regardless of cluster size
- Zero contention on `UpdateIAmAlive` (independent per-silo key, no CAS)
- Automatic orphan cleanup via per-key TTL (no background sweep needed)
- Consistent with patterns established in `NatsGrainStorage` (options, hosting extensions, KV store creation)

**Non-Goals:**
- WatchedKV in-memory mirror (deferred to v2 — poll mode only in v1)
- StoreLock distributed lock (not needed in CoW strategy)
- Compatibility with vPrev1/2/3 NATS key layouts
- Supporting NATS Server < 2.10 (per-key TTL required)

## Decisions

### D1: GUID Copy-on-Write over single-blob (Strategy A)

Single-blob (vPrev3) stores the entire cluster as one key. Every `InsertRow`/`UpdateRow` rewrites O(N) bytes; every `UpdateIAmAlive` retries CAS on the full blob under contention.

CoW separates concerns: a small directory blob (O(1)) is the CAS point; each silo's data lives in its own keyed blob. The directory points to blobs by GUID — a new GUID is written before the directory CAS, so the CAS is the sole atomic commit point (equivalent to shadow paging).

Trade-off: `ReadAll` requires 1 + 2N Gets instead of 1. Mitigated by parallel fan-out and a future WatchedKV option that reduces this to zero round-trips.

### D2: Key hierarchy with `.` as separator

NATS KV uses `.` for wildcard subject matching (`prefix.>`). Using `.` throughout allows:
- `{cId}.struct.>` — single watch subscription covering directory + all silo blobs (critical for WatchedKV ordering guarantee)
- `{cId}.alive.>` — independent watch for heartbeat keys

All user-provided strings (serviceId, clusterId, siloAddress) are Url64-encoded before use as key components, ensuring no accidental `.` in key paths.

### D3: `struct.silo.{encodedAddr}.{guid}` over flat `struct.silo.{guid}`

The per-address prefix `struct.silo.{encodedAddr}.` enables safe orphan sweep during `CleanupDefunctSiloEntries`: `GetKeysAsync("{cId}.struct.silo.{encodedAddr}.>")` finds ALL blobs for a defunct silo (current + orphaned from failed CAS attempts). This is safe because defunct silos are not writing new blobs.

Flat layout (`struct.silo.{guid}`) has no safe way to identify orphans for a specific silo without a full bucket scan and cross-referencing against the directory — which has a race condition on freshly-written in-flight GUIDs.

### D4: Url64 encoding for SiloAddress in key paths

`SiloAddress.ToParsableString()` produces `ip:port@generation` (e.g., `10.0.0.1:11111@12345`), containing `.`, `:`, and `@` — all problematic in NATS key paths. Url64 (`ToNatsId()`) is the established project encoding convention, produces `[A-Za-z0-9_-]` output, and is already used for all other IDs. The canonical string form is preserved in JSON blobs for readability.

### D5: 7-day TTL + hourly refresh on silo blobs

Per-key TTL provides automatic orphan cleanup: a GUID blob from a failed CAS (no refresh loop, owner silo never owns it) expires after 7 days. The owning silo's `NatsMembershipTable` instance tracks its current GUID and revision, refreshing via `TryUpdateAsync(guid, data, revision, ttl=7days)` every hour — restarting the 7-day clock.

Alternative (no TTL, explicit delete only) leaks one blob per "CAS fail + orphan delete fail" double-failure, accumulating indefinitely. At 7 days, even leaked keys are bounded.

### D6: 2-hour TTL on alive keys, reset on each UpdateIAmAlive

`UpdateIAmAlive` is called every few seconds per silo. Each call uses `TryCreate`/`TryUpdate` with `ttl=2h`, resetting the countdown. A dead silo's key expires 2 hours after its last heartbeat.

2 hours is >> the failure detection threshold (typically 90–180 seconds) so transient write failures don't cause false expiry. 2 hours is << a typical operational day, so dead silo keys don't linger indefinitely. Explicit deletion in `CleanupDefunctSiloEntries` is retained as defense-in-depth.

Single-writer property (only the local silo writes its own alive key) allows revision tracking for `TryUpdateAsync` without CAS conflicts.

### D7: Per-silo etag as integer counter in the silo blob

Orleans' `UpdateRow(entry, etag, tableVersion)` validates the per-silo etag before committing a change. The NATS revision of the silo blob cannot serve as the etag because each `UpdateRow` writes a new GUID key (new revision, new key). The etag must survive GUID rotation.

An integer counter field `SiloEtag` in the silo blob, incremented on each `UpdateRow` (starting at 1 from `InsertRow`), satisfies this requirement. The counter is validated by reading the current silo blob before the CoW write.

### D8: ClusterId and ServiceId from ClusterOptions

`NatsClusteringOptions` does not redeclare Orleans-owned configuration. Both `ServiceId` (bucket name) and `ClusterId` (key prefix) are read from `IOptions<ClusterOptions>`, matching the `NatsGrainStorage` pattern.

## Risks / Trade-offs

- **TOCTOU in poll mode** — Between reading the directory and fetching silo blobs, a concurrent `UpdateRow` could CAS the directory and delete the old GUID blob. Mitigation: on 404 for a referenced GUID, re-read the directory and retry once. If still missing after retry, the silo was legitimately removed concurrently — skip it.

- **NATS Server 2.10+ required for per-key TTL** — `TryCreateAsync`/`TryUpdateAsync` with `TimeSpan` TTL are already used by `StoreLock`, confirming .NET client support. Server version must be documented in README. Running against an older server will cause TTL parameters to be silently ignored (blobs persist forever).

- **UpdateRow requires 2 Gets before the write** — Reading the directory (to validate revision + find current GUID) and the silo blob (to validate silo etag) adds 2 round-trips. In WatchedKV mode (v2), both reads are zero-cost mirror lookups.

- **Orphan blob on double-failure** — A failed CAS (Step 2) immediately attempts to delete the orphaned GUID blob (Step cleanup). If that delete also fails, the blob leaks for up to 7 days. Not a correctness issue — the orphaned blob is never referenced by the directory.

## Migration Plan

No migration required — this is a new implementation with a new key layout. vPrev1/2/3 files remain in their subdirectories and do not interfere (they are excluded from compilation by namespace isolation, not build exclusion — verify no naming conflicts at integration time).
