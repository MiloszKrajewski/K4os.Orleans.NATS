# Orleans Membership Provider Analysis: Architecture & NATS Feasibility

**Date:** 2026-04-12  
**Scope:** `src/Redis/Orleans.Clustering.Redis`, `src/AWS/Orleans.Clustering.DynamoDB`, `src/AdoNet/Orleans.Clustering.AdoNet`, plus ZooKeeper and Consul providers for comparison  
**Goal:** Understand how existing membership providers are implemented and assess feasibility of a NATS-based implementation

---

## 1. The `IMembershipTable` Contract

**File:** `src/Orleans.Core/SystemTargetInterfaces/IMembershipTable.cs`

The interface has 7 methods with **very precise semantics** that drive the entire distributed membership protocol:

```csharp
Task InitializeMembershipTable(bool tryInitTableVersion);
Task<MembershipTableData> ReadRow(SiloAddress key);          // must be atomic
Task<MembershipTableData> ReadAll();                         // must be atomic
Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion);  // CAS, atomic
Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion); // CAS, atomic
Task UpdateIAmAlive(MembershipEntry entry);                  // dirty write, no CAS
Task DeleteMembershipTableEntries(string clusterId);
Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate);
```

The linchpin is `InsertRow` and `UpdateRow`. Their documentation is explicit:

> **All those changes to the table — insert/update of a row AND update of the table version — should happen atomically, or fail atomically with no side effects.**

This means **two independent records must be written atomically, under version guard on both**. This is the fundamental challenge for any store without multi-key transactions.

### Return Semantics

- `InsertRow` / `UpdateRow` return `false` on a version/etag conflict — **not an exception**. The runtime treats `false` as contention and retries with exponential backoff (`MembershipTableManager`: `NUM_CONDITIONAL_WRITE_CONTENTION_ATTEMPTS = -1`, unlimited retries).
- `UpdateIAmAlive` is explicitly documented: *"There is no scenario where this operation could fail due to table semantical reasons."* It is a best-effort heartbeat write.

### Supporting Types

```csharp
// TableVersion: the cluster-wide monotonic version counter
public sealed class TableVersion
{
    public int Version { get; }      // monotonically increasing
    public string VersionEtag { get; } // CAS guard for the version row
}

// MembershipTableData: snapshot returned from reads
public sealed class MembershipTableData
{
    public IReadOnlyList<Tuple<MembershipEntry, string>> Members { get; } // (entry, etag)
    public TableVersion Version { get; }
}
```

---

## 2. Data Model Across Providers

All providers maintain two logical entities:

| Entity | Purpose | Cardinality |
|---|---|---|
| **Table Version Row** | Monotonically incrementing cluster-wide version + etag | One per `clusterId` |
| **Silo Entry Row** | Silo state, addresses, IAmAlive, suspect lists | One per `SiloAddress` |

The `MembershipEntry` payload includes:
- `SiloAddress` (IP + port + generation)
- `HostName`, `SiloName`
- `Status` (Created / Joining / Active / ShuttingDown / Stopping / Dead)
- `ProxyPort` (gateway port, > 0 means the silo is a gateway)
- `StartTime`, `IAmAliveTime`
- `SuspectTimes` — list of `(SiloAddress, DateTime)` pairs recording which silos have suspected this silo

---

## 3. Provider Implementations

### 3.1 AdoNet (SQL) — The Reference Model

**Files:**
- `src/AdoNet/Orleans.Clustering.AdoNet/Messaging/AdoNetClusteringTable.cs`
- `src/AdoNet/Orleans.Clustering.AdoNet/SQLServer-Clustering.sql`

**Schema:**

```sql
-- One per clusterId
CREATE TABLE OrleansMembershipVersionTable (
    DeploymentId NVARCHAR(150) NOT NULL PRIMARY KEY,
    Timestamp    DATETIME2(3)  NOT NULL DEFAULT GETUTCDATE(),
    Version      INT           NOT NULL DEFAULT 0
);

-- One per silo
CREATE TABLE OrleansMembershipTable (
    DeploymentId NVARCHAR(150) NOT NULL,
    Address      VARCHAR(45)   NOT NULL,
    Port         INT           NOT NULL,
    Generation   INT           NOT NULL,
    SiloName     NVARCHAR(150) NOT NULL,
    HostName     NVARCHAR(150) NOT NULL,
    Status       INT           NOT NULL,
    ProxyPort    INT           NULL,
    SuspectTimes VARCHAR(8000) NULL,
    StartTime    DATETIME2(3)  NOT NULL,
    IAmAliveTime DATETIME2(3)  NOT NULL,
    CONSTRAINT PK_MembershipTable PRIMARY KEY (DeploymentId, Address, Port, Generation),
    CONSTRAINT FK_MembershipTable_VersionTable FOREIGN KEY (DeploymentId)
        REFERENCES OrleansMembershipVersionTable (DeploymentId)
);
```

**InsertRow** — single transaction:
```sql
SET XACT_ABORT, NOCOUNT ON;
BEGIN TRANSACTION;
  INSERT INTO OrleansMembershipTable (...) WHERE NOT EXISTS (same row);
  UPDATE OrleansMembershipVersionTable SET Version = Version + 1
    WHERE DeploymentId = @DeploymentId AND Version = @Version  -- CAS
    AND @@ROWCOUNT > 0;  -- only if insert succeeded
  IF @@ROWCOUNT = 0 ROLLBACK ELSE COMMIT;
SELECT @@ROWCOUNT;
```

**UpdateRow** — single transaction, CAS on version first:
```sql
BEGIN TRANSACTION;
  UPDATE OrleansMembershipVersionTable SET Version = Version + 1
    WHERE DeploymentId = @DeploymentId AND Version = @Version;  -- CAS
  UPDATE OrleansMembershipTable SET Status=..., SuspectTimes=..., IAmAliveTime=...
    WHERE ... AND @@ROWCOUNT > 0;  -- only if version update succeeded
COMMIT;
```

**UpdateIAmAlive** — plain `UPDATE`, no transaction, no version bump (dirty write).

The `AdoNetClusteringTable` class itself is a thin delegate to `RelationalOrleansQueries`, which wraps all query execution.

---

### 3.2 Redis — Single-Hash Trick

**Files:**
- `src/Redis/Orleans.Clustering.Redis/Storage/RedisMembershipTable.cs`
- `src/Redis/Orleans.Clustering.Redis/Providers/RedisGatewayListProvider.cs`

**Data model:** All silo entries and the version are stored as **fields of one Redis hash** under a single cluster key:

```
HSET {serviceId}:{clusterId}  "Version"              "5"
HSET {serviceId}:{clusterId}  "10.0.0.1:11111/12345" "{...silo json...}"
HSET {serviceId}:{clusterId}  "10.0.0.2:11111/67890" "{...silo json...}"
```

🔑 **Key architectural insight**: Redis avoids the multi-key atomicity problem by putting **everything in one hash**. Redis `MULTI/EXEC` transactions with `Condition` guards operate on multiple fields within a single key atomically.

**InsertRow / UpdateRow** (`UpsertRowInternal`):
```csharp
var tx = _db.CreateTransaction();

// CAS: table version must equal predecessor
tx.AddCondition(Condition.HashEqual(_clusterKey, "Version", SerializeVersion(Predeccessor(tableVersion))));

// Conditional: silo entry must NOT exist (insert-only mode)
if (allowInsertOnly)
    tx.AddCondition(Condition.HashNotExists(_clusterKey, rowKey));

// Write new version + new silo entry atomically
tx.HashSetAsync(_clusterKey, "Version", SerializeVersion(tableVersion)).Ignore();
tx.HashSetAsync(_clusterKey, rowKey, Serialize(entry)).Ignore();

var success = await tx.ExecuteAsync();
```

- Returns `UpsertResult.Success`, `UpsertResult.Conflict`, or `UpsertResult.Failure`
- `InsertRow` uses `allowInsertOnly: true` → adds `HashNotExists` condition
- `UpdateRow` uses `allowInsertOnly: false`

**UpdateIAmAlive** (dirty write):
1. Read existing entry + current version via `MULTI/EXEC` (for consistency of the read)
2. Patch `IAmAliveTime` in the deserialized entry
3. Call `UpsertRowInternal` with `updateTableVersion: false` — writes only the silo field, no version bump

**ReadRow**: `MULTI/EXEC` reads both `Version` field and the silo field atomically.

**ReadAll**: `HGETALL` — single command returns all hash fields atomically.

**Per-row etag**: The `TableVersion.VersionEtag` is used as the etag for all rows (stored in the version field, not per-silo). The version monotonically increases and the predecessor check provides CAS semantics.

---

### 3.3 DynamoDB — True Multi-Item Transactions

**Files:**
- `src/AWS/Orleans.Clustering.DynamoDB/Membership/DynamoDBMembershipTable.cs`
- `src/AWS/Orleans.Clustering.DynamoDB/Membership/SiloInstanceRecord.cs`
- `src/AWS/Orleans.Clustering.DynamoDB/Membership/DynamoDBGatewayListProvider.cs`

**Data model:** Each silo is a separate DynamoDB item. The table version is a special item with `SiloIdentity = "VersionRow"`. Primary key: `(DeploymentId [hash], SiloIdentity [range])`.

```
DeploymentId=myCluster, SiloIdentity=VersionRow      → MembershipVersion=5, ETag=42
DeploymentId=myCluster, SiloIdentity=10.0.0.1-11111-12345 → Status=3, ETag=7, ...
```

Per-silo `ETag` is an integer stored as a DynamoDB attribute, incremented on each write.

**InsertRow** — DynamoDB `TransactWrite` with 2 operations:
```csharp
var tableEntryInsert = new Put {
    Item = tableEntry.GetFields(true),
    ConditionExpression = "attribute_not_exists(DeploymentId) AND attribute_not_exists(SiloIdentity)",
    TableName = options.TableName
};
var versionEntryUpdate = new Update {
    Key = versionEntry.GetKeys(),
    ConditionExpression = "ETag = :currentETag",  // CAS on version ETag
    // UpdateExpression bumps ETag and MembershipVersion
};
await storage.WriteTxAsync(new[] { tableEntryInsert }, new[] { versionEntryUpdate });
// Catches TransactionCanceledException with "ConditionalCheckFailed" → returns false
```

**UpdateRow** — `TransactWrite` with 2 `Update` operations:
```csharp
// Update silo entry (CAS on silo's ETag)
var siloEntryUpdate = new Update { ConditionExpression = "ETag = :currentETag" /* silo etag */ };
// Update version row (CAS on version's ETag)
var versionEntryUpdate = new Update { ConditionExpression = "ETag = :currentETag" /* version etag */ };
await storage.WriteTxAsync(updates: new[] { siloEntryUpdate, versionEntryUpdate });
```

**ReadRow**: `GetEntriesTxAsync` — DynamoDB `TransactGet` for silo entry + version row atomically.

**ReadAll**: Non-transactional. Reads version row first, then queries all items for the cluster. If any item's `MembershipVersion > versionRow.MembershipVersion`, retries `ReadAll` (handles eventual consistency window).

**UpdateIAmAlive**: Plain `UpsertEntryAsync` — updates only `IAmAliveTime` attribute with `attribute_exists` condition (no ETag check, no version bump).

---

### 3.4 ZooKeeper — Version as Node Stat

**File:** `src/Orleans.Clustering.ZooKeeper/ZooKeeperBasedMembershipTable.cs`

**Data model:** ZooKeeper tree structure:
```
/clusterId/                    ← version is ZK stat.version of this node
/clusterId/IP:Port@Gen         ← silo entry data, version is stat.version
/clusterId/IP:Port@Gen/IAmAlive ← IAmAlive timestamp (separate child node)
```

ZK's node stat `version` is the etag — automatically incremented on `setData`.

**InsertRow / UpdateRow** — ZK multi-op transaction:
```csharp
// InsertRow
zk.transaction()
  .setData("/", null, expectedTableVersion)     // CAS + bumps root version
  .create(rowPath, data, OPEN_ACL, PERSISTENT)  // fails if exists
  .create(rowIAmAlivePath, data, OPEN_ACL, PERSISTENT)
  .commitAsync()

// UpdateRow
zk.transaction()
  .setData("/", null, expectedTableVersion)     // CAS + bumps root version
  .setData(rowPath, newData, expectedRowVersion) // CAS on row version
  .setData(rowIAmAlivePath, newIAmAliveData)
  .commitAsync()
// KeeperException.BadVersionException → returns false
```

**UpdateIAmAlive**: Unconditional `setData` on `/IAmAlive` child — no version check, no table version bump.

**ReadAll**: `getChildrenAsync("/")` → parallel `getDataAsync` for each child — not strictly atomic but ZK linearizability provides strong consistency within a session.

---

### 3.5 Consul — KV Multi-Transactions

**File:** `src/Orleans.Clustering.Consul/ConsulBasedMembershipTable.cs`

**Data model:**
```
{kvRootFolder}/{clusterId}/version            ← table version row
{kvRootFolder}/{clusterId}/{siloAddress}      ← silo entry
{kvRootFolder}/{clusterId}/{siloAddress}-IAmAlive ← IAmAlive (separate key)
```

Consul KV `ModifyIndex` is the per-key etag (monotonically incrementing u64).

**InsertRow / UpdateRow** — `KV.Txn` with multi-key CAS:
```csharp
// InsertRow: CAS with index=0 means "insert if not exists"
var rowInsert = new KVTxnOp(siloKey, KVTxnVerb.CAS) { Index = 0, Value = data };
var versionUpdate = new KVTxnOp(versionKey, KVTxnVerb.CAS) { Index = versionModifyIndex, Value = newVersion };
await consulClient.KV.Txn(new List<KVTxnOp> { rowInsert, versionUpdate });
// .Response.Success == false → returns false
```

**UpdateIAmAlive**: Plain `KV.Put` — unconditional write to `{siloAddress}-IAmAlive` key.

---

## 4. IGatewayListProvider

Companion interface implemented by all providers:

```csharp
public interface IGatewayListProvider
{
    Task InitializeGatewayListProvider();
    Task<IList<Uri>> GetGateways();  // Returns active silos with ProxyPort > 0
    TimeSpan MaxStaleness { get; }
    bool IsUpdatable { get; }        // Obsolete, all providers return true
}
```

All implementations filter `ReadAll()` for entries where `Status == Active && ProxyPort != 0`, then build gateway URIs via `SiloAddress.ToGatewayUri()`.

Redis: reuses `RedisMembershipTable` singleton (injected into `RedisGatewayListProvider`).  
DynamoDB: `DynamoDBGatewayListProvider` is independent — does a table `ScanAsync` with filter expression.  
AdoNet: calls `RelationalOrleansQueries.ActiveGatewaysAsync` (dedicated SQL query).

---

## 5. Feature Matrix

| Feature Required | SQL | Redis | DynamoDB | ZooKeeper | Consul | **NATS KV** |
|---|---|---|---|---|---|---|
| Per-key CAS | ✅ (WHERE etag) | ✅ (Condition) | ✅ (ConditionExpr) | ✅ (node version) | ✅ (ModifyIndex) | ✅ (revision) |
| **Multi-key atomic write** | ✅ (SQL TXN) | ✅ (1 hash) | ✅ (TransactWrite) | ✅ (multi-op) | ✅ (KV.Txn) | ❌ |
| Conditional insert (if-not-exists) | ✅ | ✅ (HashNotExists) | ✅ (attribute_not_exists) | ✅ (create throws) | ✅ (index=0 CAS) | ✅ (Create) |
| Dirty write | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ (Put) |
| Atomic snapshot read of N keys | ✅ (JOIN) | ✅ (HGETALL) | ✅ (TransactGet) | 🟡 (session) | ✅ (KV.List) | ❌ |
| Scan / list all entries | ✅ (SELECT) | ✅ (HGETALL) | ✅ (Query) | ✅ (getChildren) | ✅ (KV.List) | ✅ (Keys) |
| Per-key TTL / expiry | ❌ | ✅ (KeyExpire) | ❌ | ❌ | ✅ (Session TTL) | ✅ (2.10+) |

---

## 6. NATS JetStream KV Capabilities (Relevant Subset)

NATS JetStream Key-Value Store (each bucket is backed by a JetStream stream) provides:

| Operation | Method | Notes |
|---|---|---|
| Conditional insert | `kv.Create(key, value)` | Fails if key already exists |
| CAS update | `kv.Update(key, value, revision)` | Fails if current revision ≠ expected |
| Unconditional write | `kv.Put(key, value)` | Always succeeds |
| Read with revision | `kv.Get(key)` | Returns value + revision (etag) |
| List all keys | `kv.Keys()` | Async enumerable |
| Delete with CAS | `kv.Delete(key)` + revision guard | Via `KeyValueDeleteOptions` |
| Per-key TTL | Supported in NATS 2.10+ | Via `KeyValueEntry.Expiry` |

**What NATS does NOT have:**
- ❌ Multi-key atomic transactions
- ❌ Hash-like structures (multiple named fields under one key with per-field conditions)
- ❌ Snapshot reads across multiple keys
- ❌ MVCC / row-level locking

---

## 7. NATS-Specific Challenges Per Operation

### `InsertRow` / `UpdateRow` — The Core Problem

These must atomically:
1. Insert/update the silo entry (with row-level CAS or conditional insert)
2. Update the table version (with version-level CAS)

In NATS KV these are two separate keys — there is no way to make them atomic.

**Failure scenario**: A silo crashes between step 1 and step 2. The silo entry is written but the table version was not incremented. Other silos read an inconsistent state. The Orleans protocol assumes this never happens — it is the invariant that keeps the membership protocol correct under concurrent writes.

### `ReadRow` — TOCTOU Window

Must read silo entry + table version atomically. NATS has no snapshot reads or multi-key atomic GET. Two separate `Get` calls have a time-of-check / time-of-use window.

### `ReadAll` — Snapshot Consistency

Must return all entries in a consistent snapshot. NATS has no snapshot isolation. A similar mitigation to DynamoDB's approach is needed: check if any silo entry's embedded version number exceeds the version key's value, then retry.

### `UpdateIAmAlive` — Relatively Easy

Dirty write, no etag, no version bump. NATS `Put` works perfectly. ✅

### `CleanupDefunctSiloEntries` — Relatively Easy

Read all → filter → delete. Non-transactional. ✅

---

## 8. Viable Implementation Strategies for NATS

### Strategy A: Single-Blob Approach ⭐ (Recommended)

Store the entire membership table — all silo entries plus the version — as **one JSON blob in a single NATS KV key**:

```
bucket: orleans-{serviceId}
key:    {clusterId}/membership
value:  {
          "version": 5,
          "versionEtag": "42",       // used as TableVersion.VersionEtag
          "silos": [
            { "siloAddress": "...", "etag": "7", "status": 3, ... },
            { "siloAddress": "...", "etag": "3", "status": 3, ... }
          ]
        }
```

The NATS KV `revision` becomes the single CAS guard for all writes.

**Operation mapping:**

| Orleans Operation | NATS KV Operation |
|---|---|
| `ReadAll()` | `kv.Get(clusterKey)` → deserialize |
| `ReadRow(silo)` | `kv.Get(clusterKey)` → filter silo entry |
| `InsertRow(entry, ver)` | `kv.Get(clusterKey)` → validate no duplicate + version match → mutate → `kv.Update(clusterKey, blob, revision)` |
| `UpdateRow(entry, etag, ver)` | `kv.Get(clusterKey)` → validate silo etag + version match → mutate → `kv.Update(clusterKey, blob, revision)` |
| `UpdateIAmAlive(entry)` | CAS retry loop: `kv.Get` → patch IAmAlive → `kv.Update`; retry on `NatsKVWrongLastRevisionException` |
| `DeleteMembershipTableEntries` | `kv.Delete(clusterKey)` or `kv.Purge(clusterKey)` |
| `CleanupDefunctSiloEntries` | `kv.Get` → filter out defunct → `kv.Update` |

**Pros:**
- Correctness: single-key CAS preserves atomicity of the version + silo entry update
- Simple implementation: no multi-key coordination needed
- `ReadAll` is a single network round-trip
- Maps cleanly to the etag model: NATS revision = `VersionEtag`; per-silo etag is a counter stored in the blob

**Cons:**
- Every write rewrites the entire cluster membership blob → poor scalability for large clusters (100+ silos)
- The cluster key becomes a **hot key** under concurrent writes — contention bottleneck
- `UpdateIAmAlive` becomes a CAS-with-retry (not a true dirty write) — violates the contract's intent under heavy heartbeat traffic if revisions keep conflicting
- NATS KV message size limit (configurable, default 1MB) is unlikely to be hit but theoretically constrains very large clusters

### Strategy B: Per-Silo Keys + Non-Atomic Version Update

```
bucket: orleans-{serviceId}
key:    {clusterId}/version           → version blob (version int + etag)
key:    {clusterId}/{siloAddress}     → silo entry blob (with per-silo etag)
```

**InsertRow:**
1. `kv.Create(siloKey, data)` — conditional insert (fails if exists) ✅
2. `kv.Update(versionKey, newVersion, versionRevision)` — CAS ✅

**Problem:** Steps 1 and 2 are **not atomic**. A crash between them leaves a dangling silo entry with a stale table version.

**Mitigation (incomplete):** On `ReadAll`, if any silo entry's embedded `membershipVersion > versionRow.version`, retry (like DynamoDB does). On step-2 failure, attempt to delete the silo key written in step 1 (compensation). This is fragile — the compensation may also fail.

**Verdict:** Technically feasible with compensating logic, but correctness is hard to guarantee. Not recommended unless the performance cost of Strategy A is prohibitive.

### Strategy C: Distributed Lock via NATS KV

Use a `{clusterId}/lock` key as a distributed mutex:

1. `kv.Create(lockKey, myId)` → acquire lock (fails if another holder)
2. Read-modify-write both silo and version keys
3. `kv.Delete(lockKey)` → release lock

**Problem:** NATS KV lacks per-key TTL in all versions (NATS 2.10+ adds it but with caveats). If the lock holder crashes between step 1 and step 3, the lock is held indefinitely (or until the bucket TTL expires). This is the classic distributed lock failure mode.

**Verdict:** Not recommended. Introduces a new failure mode that is more dangerous than the non-atomicity problem it solves.

---

## 9. Summary Assessment

| Requirement | NATS Fit | Notes |
|---|---|---|
| Single-key CAS | 🟢 Native (revision) | `kv.Update(key, value, revision)` |
| Conditional insert | 🟢 `kv.Create()` | Fails if key exists |
| Dirty write | 🟢 `kv.Put()` | No condition |
| **Multi-key atomic write** | 🔴 **Not supported** | Core gap |
| Atomic snapshot read (N keys) | 🔴 Not supported | TOCTOU risk |
| Per-key TTL | 🟡 NATS 2.10+ | With bucket-level configuration |
| Scalable read-all | 🟢 Single `Get` (blob) | O(1) for blob strategy |
| Scalable concurrent writes | 🔴 Hot-key bottleneck | All writes serialize on one key (blob strategy) |

### The Fundamental Mismatch

The Orleans membership protocol is built on a **database-centric model** where two records (silo entry + table version) must be mutated atomically under CAS. Every existing provider either:
- Has native multi-key transactions (SQL, DynamoDB, Consul, ZooKeeper), or
- Folds all data into one key/hash to avoid the problem (Redis)

NATS JetStream KV provides neither natively. The closest workable analog to the Redis hash trick is Strategy A — storing the whole table as one blob — which is viable but trades **per-write scalability** for **correctness**.

### Recommended Path if Implementing

**Use Strategy A (Single-Blob):**

- Store the entire membership state as one NATS KV value under `{clusterId}/membership`
- Map NATS KV `revision` → `VersionEtag` for all operations
- Track per-silo etag as an integer inside the blob (increment on each silo write)
- `InsertRow`/`UpdateRow`: deserialize → validate etag/version → mutate → `kv.Update(key, blob, revision)` → return `false` on `NatsKVWrongLastRevisionException`
- `UpdateIAmAlive`: CAS retry loop (acceptable — IAmAlive conflicts should be rare in practice)
- Accept the scalability limitation: acceptable for clusters < ~200 silos

---

## 10. Gaps & Uncertainties

- 🔴 **NATS KV per-key TTL behavior** (NATS 2.10+): Not confirmed whether per-key TTL can be set on individual entries within a bucket, or only at bucket level. Affects `CleanupDefunctSiloEntries` design.
- 🟡 **Blob size in practice**: The JSON blob for 100 silos is estimated ~100KB (well within 1MB). Not benchmarked.
- 🟡 **`UpdateIAmAlive` under heavy load**: If all silos heartbeat simultaneously against the same blob key, revision conflicts will cause CAS retries. The retry behavior under N-silo concurrency is not analyzed.
- 🔴 **No NATS-specific NATS .NET client KV API verified**: Analysis assumes the NATS .NET client (`NATS.Client.JetStream`) exposes `Create`, `Update`, and `Get` with revision semantics equivalent to the Go client. This should be confirmed before implementation.

---

## 11. References

| File | Purpose |
|---|---|
| `src/Orleans.Core/SystemTargetInterfaces/IMembershipTable.cs` | Interface definition and data types |
| `src/Orleans.Core/Messaging/IGatewayListProvider.cs` | Gateway list provider interface |
| `src/Orleans.Runtime/MembershipService/MembershipTableManager.cs` | Runtime consumer — drives read/write/retry logic |
| `src/Redis/Orleans.Clustering.Redis/Storage/RedisMembershipTable.cs` | Redis implementation (single-hash model) |
| `src/AWS/Orleans.Clustering.DynamoDB/Membership/DynamoDBMembershipTable.cs` | DynamoDB implementation (TransactWrite) |
| `src/AdoNet/Orleans.Clustering.AdoNet/Messaging/AdoNetClusteringTable.cs` | SQL implementation (SQL transactions) |
| `src/AdoNet/Orleans.Clustering.AdoNet/SQLServer-Clustering.sql` | SQL schema and query definitions |
| `src/Orleans.Clustering.ZooKeeper/ZooKeeperBasedMembershipTable.cs` | ZooKeeper implementation (ZK multi-op) |
| `src/Orleans.Clustering.Consul/ConsulBasedMembershipTable.cs` | Consul implementation (KV.Txn) |
| `src/AWS/Orleans.Clustering.DynamoDB/Membership/SiloInstanceRecord.cs` | DynamoDB row model |
