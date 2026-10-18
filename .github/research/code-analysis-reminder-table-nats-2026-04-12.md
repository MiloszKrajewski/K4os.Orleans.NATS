# Orleans Reminder Table Analysis: Architecture & NATS Feasibility

**Date:** 2026-04-12  
**Scope:** `src/Redis/Orleans.Reminders.Redis`, `src/AWS/Orleans.Reminders.DynamoDB`, `src/AdoNet/Orleans.Reminders.AdoNet`, plus Azure Storage for comparison  
**Companion:** See `code-analysis-membership-provider-nats-2026-04-12.md` for the membership provider analysis  
**Goal:** Understand how existing reminder providers are implemented and assess feasibility of a NATS-based implementation

---

## 1. The `IReminderTable` Contract

**File:** `src/Orleans.Reminders/SystemTargetInterfaces/IReminderTable.cs`

```csharp
public interface IReminderTable
{
    Task StartAsync(CancellationToken cancellationToken = default);   // lifecycle
    Task StopAsync(CancellationToken cancellationToken = default);    // lifecycle

    Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName);  // single row
    Task<ReminderTableData> ReadRows(GrainId grainId);                  // all for grain
    Task<ReminderTableData> ReadRows(uint begin, uint end);             // hash ring range

    Task<string> UpsertRow(ReminderEntry entry);                        // unconditional upsert → returns new ETag
    Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag); // conditional delete
    Task TestOnlyClearTable();                                          // test hook only
}
```

### `ReminderEntry` — the row payload

```csharp
public sealed class ReminderEntry
{
    public GrainId GrainId { get; set; }        // Primary key part 1
    public string ReminderName { get; set; }    // Primary key part 2
    public DateTime StartAt { get; set; }       // When the reminder was first scheduled
    public TimeSpan Period { get; set; }        // How often to fire
    public string ETag { get; set; }            // CAS guard for RemoveRow
}
```

### Semantic Contracts — Critical Differences from IMembershipTable

| Operation | Semantics | Concurrency Guard |
|---|---|---|
| `UpsertRow` | Insert **or** update — **unconditional** | None — always succeeds |
| `RemoveRow` | Delete only if ETag matches — **conditional** | CAS on ETag |
| `ReadRows(uint, uint)` | Return all entries in a hash ring range (begin, end] | No consistency requirement |
| `ReadRow` | Single row lookup | No consistency requirement |

🔑 **Key insight**: The reminder table has **no multi-record atomicity requirement** and **no table-level version counter**. `UpsertRow` is unconditional. Only `RemoveRow` requires a CAS check (etag). This makes the reminder table fundamentally simpler than `IMembershipTable` for any storage backend.

---

## 2. How Reminders Are Used by the Runtime

**File:** `src/Orleans.Reminders/ReminderService/LocalReminderService.cs`

`LocalReminderService` is a `GrainService` (runs on every silo) that:

1. **On startup**: calls `reminderTable.StartAsync()`, then `ReadAndUpdateReminders()` to load its responsible hash range
2. **Periodically** (every `ReminderOptions.RefreshReminderListPeriod`): calls `ReadRows(begin, end)` for its ring sub-ranges to pick up newly registered reminders
3. **On ring ownership change** (`OnRangeChange`): re-reads reminders for its new range
4. **On `RegisterOrUpdateReminder`**: calls `reminderTable.UpsertRow(entry)` → starts a local timer
5. **On `UnregisterReminder`**: calls `reminderTable.RemoveRow(grainId, reminderName, eTag)` → stops local timer

### The Hash Ring Range Query

The `ReadRows(uint begin, uint end)` method is the **central query** used by the reminder service. Grains are distributed across a consistent hash ring by their `GetUniformHashCode()` (a `uint`). Each silo owns a contiguous arc of the ring. The query:

- **Normal range** (`begin < end`): returns entries where `hash > begin AND hash <= end`
- **Wrap-around range** (`begin >= end`): returns entries where `hash > begin OR hash <= end` — requires two queries in most implementations

This ring-range query is unique to reminders (membership has no equivalent).

---

## 3. Provider Implementations

### 3.1 Redis — Sorted Set with Lexicographic Ordering

**Files:**
- `src/Redis/Orleans.Reminders.Redis/Storage/RedisReminderTable.cs`
- `src/Redis/Orleans.Reminders.Redis/Providers/RedisReminderTableOptions.cs`

**Data model:** A single Redis Sorted Set per service:

```
Key:   {serviceId}/reminders   (type: sorted set)
Score: 0  (all entries have score 0 → relies on lexicographic ordering)
Value: "XXXXXXXX","grainId","reminderName","eTag","startAt","period"
```

The value is a comma-separated JSON array of strings (without the outer brackets), lexicographically ordered by the leading fields:

```
Index [0] = grainHash (8-char hex, e.g., "0A3F1B22")
Index [1] = grainId   (e.g., "default/XXXXXX+0x...")
Index [2] = reminderName
Index [3] = eTag      (GUID string)
Index [4] = startAt   (ISO 8601)
Index [5] = period    (TimeSpan string)
```

The sorted set's lexicographic ordering enables all query patterns via `ZRANGEBYLEX`:

**Filtering mechanics** (`GetFilter` method):
```csharp
// For a grainId+reminderName filter:
from = "\"XXXXXXXX\",\"grainId\",\"reminderName\","  + '"'   // inclusive lower bound
to   = "\"XXXXXXXX\",\"grainId\",\"reminderName\","  + '#'   // exclusive upper bound
// ASCII: '"' (34) < all printable chars < '#' (35)... wait, actually '#' = 35 > '"' = 34
// so [from, to] captures all values that start with the prefix, regardless of remaining fields
```

**ReadRow** (`ZRANGEBYLEX`):
```csharp
var (from, to) = GetFilter(grainId, reminderName);
RedisValue[] values = await _db.SortedSetRangeByValueAsync(_hashSetKey, from, to);
// Returns 0 or 1 entries matching grainId+reminderName (regardless of eTag)
```

**ReadRows(GrainId)** (`ZRANGEBYLEX`):
```csharp
var (from, to) = GetFilter(grainId);
RedisValue[] values = await _db.SortedSetRangeByValueAsync(_hashSetKey, from, to);
// Returns all entries for the grainId (all reminders for this grain)
```

**ReadRows(uint begin, uint end)** — hash ring range:
```csharp
if (begin < end)
{
    // Normal range: one ZRANGEBYLEX from begin hash to end hash
    values = await _db.SortedSetRangeByValueAsync(_hashSetKey, from, to);
}
else
{
    // Wrap-around: two queries, union the results
    values1 = await _db.SortedSetRangeByValueAsync(_hashSetKey, from, "\"FFFFFFFF\",#");
    values2 = await _db.SortedSetRangeByValueAsync(_hashSetKey, "\"00000000\",\"", to);
    values = values1.Concat(values2);
}
```

**UpsertRow** — Lua script (atomic remove-old + add-new):
```lua
local key = KEYS[1]
local from = '[' .. ARGV[1]  -- lower bound of old entries for this grain+reminder
local to   = '[' .. ARGV[2]  -- upper bound
local value = ARGV[3]        -- new serialized entry

-- Atomically remove all existing entries for this (grain, reminderName)
local remRes = redis.call('ZREMRANGEBYLEX', key, from, to)
-- Add the new entry (with a new GUID eTag baked into the value)
local addRes = redis.call('ZADD', key, 0, value)
return { key, from, to, value, remRes, addRes }
```

The Lua script ensures the remove and add are atomic at the Redis level. The new ETag is a `Guid.NewGuid()` generated in C# and included in the value string.

**RemoveRow** — CAS via exact eTag in value range:
```csharp
var (from, to) = GetFilter(grainId, reminderName, eTag);
// from/to bound exactly the one entry matching grainId + reminderName + eTag
long removed = await _db.SortedSetRemoveRangeByValueAsync(_hashSetKey, from, to);
return removed > 0;
// If eTag doesn't match, the range [from, to] will be empty → removed = 0 → returns false
```

The CAS check is implicit: the eTag is embedded in the sorted set value, so the range bounds only match the exact eTag. If the etag has changed (because the entry was updated by another caller), the range returns nothing.

**Options:**

| Option | Default | Notes |
|---|---|---|
| `ConfigurationOptions` | Required | StackExchange.Redis connection settings |
| `CreateMultiplexer` | `DefaultCreateMultiplexer` | Factory delegate for `IConnectionMultiplexer` |
| `EntryExpiry` | `null` | If set, the entire hash set expires after this duration. For test/ephemeral environments only. |

**DI registration:**
```csharp
services.AddReminders();
services.AddSingleton<IReminderTable, RedisReminderTable>();
services.Configure<RedisReminderTableOptions>(configure);
services.AddSingleton<IConfigurationValidator, RedisReminderTableOptionsValidator>();
```

---

### 3.2 DynamoDB — Separate Items with GSIs

**Files:**
- `src/AWS/Orleans.Reminders.DynamoDB/Reminders/DynamoDBReminderTable.cs`
- `src/AWS/Orleans.Reminders.DynamoDB/Reminders/DynamoDbReminderStorageOptions.cs`

**Data model:** One DynamoDB item per reminder.

**Primary key:** `(ReminderId [hash], GrainHash [range])`

```
ReminderId  = "{serviceId}_{grainId}_{reminderName}"  (string)
GrainHash   = grainId.GetUniformHashCode()            (number)
```

**Additional attributes per item:**
```
ServiceId       (string)
GrainReference  (string = grainId.ToString())
ReminderName    (string)
StartTime       (string, DateTime formatted)
Period          (string, TimeSpan formatted)
ETag            (number, random int from Random.Shared.Next())
```

**Two Global Secondary Indexes (GSIs):**

| Index Name | Hash Key | Range Key | Used For |
|---|---|---|---|
| `ServiceIdIndex` | `ServiceId` | `GrainHash` | `ReadRows(uint, uint)` — hash range queries |
| `ServiceIdGrainReferenceIndex` | `ServiceId` | `GrainReference` | `ReadRows(GrainId)` — per-grain queries |

**ReadRow** — direct table lookup by primary key:
```csharp
var reminderId = ConstructReminderId(serviceId, grainId, reminderName);
// {serviceId}_{grainId}_{reminderName}
storage.ReadSingleEntryAsync(tableName, keys: { ReminderId, GrainHash }, Resolve)
```

**ReadRows(GrainId)** — GSI query:
```csharp
expression = "ServiceId = :ServiceId AND GrainReference = :GrainReference"
storage.QueryAllAsync(..., SERVICE_ID_GRAIN_REFERENCE_INDEX, consistentRead: false)
```

**ReadRows(uint begin, uint end)** — GSI range query:
```csharp
// Normal range:
expression = "ServiceId = :ServiceId AND GrainHash BETWEEN :BeginHash AND :EndHash"

// Wrap-around (begin >= end): two queries
expression1 = "ServiceId = :ServiceId AND GrainHash <= :EndHash"
expression2 = "ServiceId = :ServiceId AND GrainHash > :BeginHash"
```
Note: `consistentRead: false` — reads are eventually consistent (acceptable for reminder service).

**UpsertRow** — unconditional `PutEntryAsync` (overwrites any existing item):
```csharp
fields[ETAG_PROPERTY_NAME] = new AttributeValue { N = Random.Shared.Next().ToString() };
await storage.PutEntryAsync(tableName, fields);
return fields[ETAG_PROPERTY_NAME].N;
```
No condition expression — this is a true blind upsert.

**RemoveRow** — conditional delete with ETag check:
```csharp
expression = "ETag = :currentETag";
await storage.DeleteEntryAsync(tableName, keys, expression, conditionalValues);
// Catches ConditionalCheckFailedException → returns false
```

---

### 3.3 AdoNet (SQL) — Single Table, UPSERT Pattern

**Files:**
- `src/AdoNet/Orleans.Reminders.AdoNet/ReminderService/AdoNetReminderTable.cs`
- `src/AdoNet/Orleans.Reminders.AdoNet/SQLServer-Reminders.sql`

**Schema:**
```sql
CREATE TABLE OrleansRemindersTable (
    ServiceId    NVARCHAR(150) NOT NULL,
    GrainId      VARCHAR(150)  NOT NULL,
    ReminderName NVARCHAR(150) NOT NULL,
    StartTime    DATETIME2(3)  NOT NULL,
    Period       BIGINT        NOT NULL,  -- TimeSpan as ticks
    GrainHash    INT           NOT NULL,
    Version      INT           NOT NULL,  -- the ETag
    CONSTRAINT PK_RemindersTable PRIMARY KEY (ServiceId, GrainId, ReminderName)
);
```

**UpsertRow** — SQL UPSERT pattern (UPDATE first, INSERT if no rows affected):
```sql
BEGIN TRANSACTION;
  UPDATE OrleansRemindersTable WITH(UPDLOCK, ROWLOCK, HOLDLOCK)
  SET StartTime=@StartTime, Period=@Period, GrainHash=@GrainHash,
      @Version = Version = Version + 1
  WHERE ServiceId=@ServiceId AND GrainId=@GrainId AND ReminderName=@ReminderName;

  INSERT INTO OrleansRemindersTable (ServiceId, GrainId, ReminderName, StartTime, Period, GrainHash, Version)
  SELECT @ServiceId, @GrainId, @ReminderName, @StartTime, @Period, @GrainHash, 0
  WHERE @@ROWCOUNT = 0;  -- only if UPDATE matched 0 rows

  SELECT @Version AS Version;  -- returns the new etag
COMMIT TRANSACTION;
```
ETag is the `Version` integer column. Starts at 0, increments on each update.

**RemoveRow** — conditional DELETE:
```sql
DELETE FROM OrleansRemindersTable
WHERE ServiceId=@ServiceId AND GrainId=@GrainId AND ReminderName=@ReminderName
  AND Version=@Version;  -- CAS: only deletes if version matches
SELECT @@ROWCOUNT;       -- returns 0 if not found or version mismatch
```

**ReadRows(uint begin, uint end)** — two SQL queries (`ReadRangeRows1Key` and `ReadRangeRows2Key`):
```sql
-- Normal range (begin < end):
WHERE ServiceId=@ServiceId AND GrainHash > @BeginHash AND GrainHash <= @EndHash

-- Wrap-around (begin >= end):
WHERE ServiceId=@ServiceId AND (GrainHash > @BeginHash OR GrainHash <= @EndHash)
```
The wrap-around is handled in a single SQL query with an `OR` clause (unlike DynamoDB which needs two separate queries).

---

### 3.4 Azure Table Storage — For Comparison

**File:** `src/Azure/Orleans.Reminders.AzureStorage/Storage/AzureBasedReminderTable.cs`

- Uses Azure Table Storage (key-value store with partition + row key)
- Primary key: `PartitionKey = hash.ToString("X8")`, `RowKey = "{serviceId}_{grainId}_{reminderName}"`
- ETag: Azure Table Storage native `ETag` from HTTP headers (string GUID)
- `UpsertRow`: `MergeAsync` or `InsertOrMergeAsync` — unconditional
- `RemoveRow`: `DeleteAsync` with `ETag` condition — CAS via Azure optimistic concurrency
- Range queries: partition key prefix filtering (hash range maps to partition key ranges)

---

## 4. Feature Matrix

| Feature Required | SQL | Redis | DynamoDB | Azure Table | **NATS KV** |
|---|---|---|---|---|---|
| Single-row lookup | ✅ | ✅ (ZRANGEBYLEX exact) | ✅ (GetItem) | ✅ | ✅ (`kv.Get`) |
| Per-grain query | ✅ (WHERE) | ✅ (ZRANGEBYLEX prefix) | ✅ (GSI query) | ✅ (row key prefix) | 🟡 (key prefix scan) |
| **Hash ring range query** | ✅ (WHERE GrainHash BETWEEN) | ✅ (ZRANGEBYLEX on hex) | ✅ (GSI BETWEEN) | ✅ (partition prefix) | ❌ (no range queries) |
| Unconditional upsert | ✅ | ✅ (Lua script) | ✅ (PutItem) | ✅ | ✅ (`kv.Put`) |
| Conditional delete (CAS) | ✅ (WHERE Version=X) | ✅ (eTag in range bounds) | ✅ (ConditionalCheck) | ✅ (ETag header) | 🟡 (Get + Delete with revision) |
| Atomic upsert (remove+insert) | ✅ (TX) | ✅ (Lua) | ✅ (PutItem replaces) | ✅ | ✅ (`kv.Put` replaces) |
| Wrap-around range | ✅ (OR clause) | ✅ (two ZRANGEBYLEX) | ✅ (two queries) | ✅ | ❌ |

---

## 5. Deep Dive: Redis Sorted Set Design

The Redis implementation deserves special attention for its elegant use of sorted set lexicographic ordering.

### Why a Sorted Set?

Redis Sorted Sets (`ZSET`) ordered by a numeric score normally. But when **all entries have the same score (0)**, Redis falls back to **lexicographic ordering of the values**. `ZRANGEBYLEX` then becomes a prefix/range scan on the value strings.

### Value Encoding

Each entry's value is a JSON array (without outer brackets) of 6 strings, serialized with `Newtonsoft.Json`:

```
"XXXXXXXX","grainId","reminderName","eTag","2024-01-01T12:00:00.000Z","00:05:00"
```

The 8-char hex grain hash prefix (`XXXXXXXX`) ensures all reminders for a given hash range are co-located lexicographically. This makes hash range queries efficient.

### Range Query Precision

The `GetFilter` method builds inclusive lower/upper bounds using the JSON prefix:

```csharp
// E.g., for grainId + reminderName filter:
from = "\"0A3F1B22\",\"default/XYZ+0x...\",\"MyReminder\","  + '"'  // = ,...,"
to   = "\"0A3F1B22\",\"default/XYZ+0x...\",\"MyReminder\","  + '#'  // = ,...,#
// ASCII 34 = '"' < all printable chars < 35 = '#'
// This range captures all entries that start with the prefix
// including any eTag/startAt/period suffix
```

This is how `RemoveRow` achieves CAS: when the eTag is included in the filter:
```
from = ...,\"MyReminder\",\"<expectedETag>\"," + '"'
to   = ...,\"MyReminder\",\"<expectedETag>\"," + '#'
// Only matches entries with exactly this eTag
```

### UpsertRow Atomicity

The Lua script is required because upsert logically requires:
1. Remove all existing entries for `(grainId, reminderName)` — regardless of eTag
2. Add the new entry with a fresh eTag

Without a Lua script, these would be two separate Redis commands with a TOCTOU window. The script makes them atomic within the Redis server's single-threaded command execution.

---

## 6. Comparison Table

| Aspect | Redis | DynamoDB | SQL | Notes |
|---|---|---|---|---|
| Data structure | Sorted set (1 per service) | Table (1 item per reminder) | Table (1 row per reminder) | Redis uses one set for everything |
| Primary key format | Lexicographic value prefix | `{svcId}_{grainId}_{reminderName}` | `(ServiceId, GrainId, ReminderName)` | — |
| ETag format | `Guid.NewGuid()` (string) | `Random.Shared.Next()` (int as string) | `Version` int (incremented) | SQL ETag is predictable (int sequence) |
| Range query mechanism | `ZRANGEBYLEX` on hex hash prefix | GSI `BETWEEN` expression | `WHERE GrainHash BETWEEN` | Redis approach is most elegant |
| Upsert atomicity | Lua script (remove+add) | Blind `PutItem` (replaces) | SQL `BEGIN TX` UPDATE+INSERT | DynamoDB is simplest |
| CAS delete mechanism | eTag in ZREMRANGEBYLEX bounds | `ConditionalCheckExpression` | `WHERE Version = @Version` | Redis approach is implicit/clever |
| Hash range wrap-around | Two `ZRANGEBYLEX` calls | Two `QueryAsync` calls | Single `WHERE ... OR ...` | SQL is most efficient |
| Cross-service isolation | Single sorted set key per `serviceId` | `ServiceId` attribute + GSI | `ServiceId` column | All approaches support multi-tenant |
| Consistent reads | Strong (single Redis command) | Eventually consistent (GSI) | Strong (default SQL isolation) | DynamoDB GSI reads are eventually consistent |

---

## 7. NATS-Specific Analysis for Reminders

Unlike the membership table, the reminder table's requirements are **much less demanding**:
- No multi-record atomicity requirement
- No table-level version counter
- Only CAS needed is on `RemoveRow`

### NATS KV Fit Assessment

**`UpsertRow`** — ✅ **Excellent fit**  
`kv.Put(key, value)` is unconditional. Maps perfectly. New ETag = NATS revision returned by Put, or a generated GUID embedded in the value.

**`ReadRow`** — ✅ **Excellent fit**  
`kv.Get(key)` where key = `{serviceId}/{grainIdHash}/{grainId}/{reminderName}`. Single round-trip.

**`ReadRows(GrainId)`** — 🟡 **Workable**  
NATS KV has no prefix-scan query, but `kv.Keys()` returns all keys in the bucket. Filter by prefix client-side. For large buckets this is expensive, but acceptable for typical reminder counts.

**`ReadRows(uint begin, uint end)`** — ❌ **Core challenge**  
This is the hardest operation. NATS KV has no range query. Two approaches:

1. **Client-side scan**: call `kv.Keys()` → parse grain hash from key → filter client-side. Works but O(N) over all reminders in the service. For 10,000 reminders this is 10K key reads + hash parsing per refresh cycle.

2. **Hash-embedded key structure**: Design keys as `{serviceId}/{grainHash8hex}/{grainId}/{reminderName}`. Then `kv.Keys()` can at least be filtered by string prefix `{serviceId}/`, and the hash prefix is lexicographically sortable. Still requires a full key scan but enables client-side binary search after sorting.

**`RemoveRow`** — 🟡 **Workable with retry**  
NATS KV `Delete` with revision guard: `kv.Delete(key, revision)`. The revision must match. Since `UpsertRow` changes the NATS revision, `RemoveRow` needs to:
1. `kv.Get(key)` → parse embedded ETag from value, extract NATS revision
2. Verify embedded ETag matches the provided `eTag`
3. `kv.Delete(key, revision)` using the NATS revision

This works but requires a read before the delete, unlike Redis (which encodes the ETag into the range bounds allowing a zero-read delete).

Alternatively, embed the NATS revision AS the ETag returned from `UpsertRow`. Then `RemoveRow` directly uses the stored revision.

**`TestOnlyClearTable`** — ✅  
Purge the bucket or delete all keys.

### NATS Key Design Recommendation

```
bucket: orleans-reminders-{serviceId}
key:    {grainHash8hex}/{grainId}/{reminderName}
value:  { "startAt": "...", "period": "...", "eTag": "<guid>" }
```

- **Grain hash in key prefix**: enables client-side lexicographic filtering for range queries
- **ETag as GUID in value**: makes ETag stable (not tied to NATS revision which changes on any Put)
- **NATS revision stored separately or ignored for CAS**: use embedded ETag in value for `RemoveRow` CAS:
  1. `kv.Get(key)` → get revision + deserialize value
  2. Check value.ETag == provided eTag
  3. `kv.Delete(key, revision)` — revision-guarded delete prevents races

### Hash Range Query Strategy

Sort all keys lexicographically client-side (NATS `kv.Keys()` returns keys in arbitrary order — sort them). After sorting by `{grainHash8hex}` prefix:
- Normal range `(begin, end]`: binary-search for `begin+1` and `end` in the sorted key list
- Wrap-around `(begin, max] ∪ [0, end]`: two slices from the sorted list

This requires loading all keys on each `ReadRows(uint, uint)` call. This may be cached locally between refresh cycles.

### Summary Assessment for NATS Reminders

| Requirement | NATS Fit | Notes |
|---|---|---|
| Unconditional upsert | 🟢 `kv.Put` | Simple and direct |
| Single-row read | 🟢 `kv.Get` | Single round-trip |
| Per-grain read | 🟡 Key prefix scan | Requires `kv.Keys()` + client-side filter |
| **Hash ring range read** | 🟡 Client-side after full key scan | No server-side range query; acceptable for moderate-scale clusters |
| Conditional delete (CAS) | 🟡 Get + revision-guarded Delete | 2 round-trips vs. 1 for SQL/DynamoDB |
| Wrap-around range | 🟡 Two client-side slices | Requires sorted key list |

**Verdict**: NATS is a viable backend for the reminder table, subject to the performance implications of client-side key scanning for range queries. For clusters with up to a few thousand reminders, the full key scan is acceptable (KV key enumeration in NATS is cheap). For clusters with tens of thousands of reminders, performance at refresh intervals (every `RefreshReminderListPeriod`) should be benchmarked.

---

## 8. Reminder Table vs. Membership Table: NATS Compatibility Comparison

| Dimension | Membership Table | Reminder Table |
|---|---|---|
| Multi-record atomic write required | ✅ Yes (silo entry + version row) | ❌ No |
| Table-level version counter | ✅ Yes | ❌ No |
| CAS on write | ✅ InsertRow/UpdateRow | ❌ UpsertRow is unconditional |
| CAS on delete | N/A (no explicit delete of rows) | ✅ RemoveRow |
| Range query required | ❌ No | ✅ Yes (hash ring) |
| NATS KV fit | 🔴 Poor (no multi-key TX) | 🟡 Good (only range queries need workaround) |
| Recommended NATS strategy | Single-blob workaround | Per-key + client-side range scan |

---

## 9. Gaps & Uncertainties

- 🟡 **NATS `kv.Keys()` performance at scale**: Not measured. Need to benchmark key enumeration for buckets with 10K+ entries.
- 🟡 **NATS revision vs embedded ETag**: If NATS revision is used as ETag, any `kv.Put` changes the revision, and the ETag becomes invalid even if the logical reminder data is the same. Embedding a GUID ETag in the value avoids this but requires a read before delete.
- 🔴 **NATS KV ordered key enumeration**: NATS KV `Keys()` does not guarantee lexicographic order. Client must sort the returned key list before applying binary search for range queries. This is not confirmed in all NATS .NET client versions.
- 🟡 **Redis `EntryExpiry` semantics**: The option applies to the entire sorted set key, not individual members. This means you cannot expire individual reminders — the entire reminder table for the service expires. This is explicitly noted as "for ephemeral environments (like in tests)" in the options documentation.
- 🔴 **DynamoDB `consistentRead: false`**: Range queries use eventually consistent reads. In theory, a newly registered reminder may not appear immediately in the next `ReadRows` call. The periodic refresh cycle (default every few seconds) mitigates this in practice, but it is a theoretical correctness gap.

---

## 10. References

| File | Purpose |
|---|---|
| `src/Orleans.Reminders/SystemTargetInterfaces/IReminderTable.cs` | Interface + `ReminderEntry` + `ReminderTableData` type definitions |
| `src/Orleans.Reminders/ReminderService/LocalReminderService.cs` | Runtime consumer — drives all table queries |
| `src/Orleans.Reminders/ReminderService/InMemoryReminderTable.cs` | In-process grain-based implementation (development) |
| `src/Redis/Orleans.Reminders.Redis/Storage/RedisReminderTable.cs` | Redis sorted set implementation |
| `src/Redis/Orleans.Reminders.Redis/Providers/RedisReminderTableOptions.cs` | Redis configuration options |
| `src/Redis/Orleans.Reminders.Redis/Hosting/SiloBuilderReminderExtensions.cs` | DI registration extension methods |
| `src/Redis/Orleans.Reminders.Redis/Hosting/RedisRemindersProviderBuilder.cs` | Provider builder for config-based setup |
| `src/AWS/Orleans.Reminders.DynamoDB/Reminders/DynamoDBReminderTable.cs` | DynamoDB implementation with GSIs |
| `src/AWS/Orleans.Reminders.DynamoDB/Reminders/DynamoDbReminderStorageOptions.cs` | DynamoDB configuration options |
| `src/AdoNet/Orleans.Reminders.AdoNet/ReminderService/AdoNetReminderTable.cs` | SQL thin-delegate implementation |
| `src/AdoNet/Orleans.Reminders.AdoNet/SQLServer-Reminders.sql` | SQL schema + query definitions |
| `src/Azure/Orleans.Reminders.AzureStorage/Storage/AzureBasedReminderTable.cs` | Azure Table Storage implementation |
