## 1. Configuration and Options

- [x] 1.1 Replace `NatsClusteringOptions` stub with full options class (`NatsUrl`, `BucketName`, `Connector`, `SiloEntryTtl`, `SiloEntryRefreshInterval`, `AliveKeyTtl` with defaults)
- [x] 1.2 Add `NatsClusteringOptionsValidator` validating TTL values are positive and non-conflicting

## 2. Internal DTOs and Key Helpers

- [x] 2.1 Create `Internal/MembershipDirectory.cs` — `MembershipDirectory` record (`int Version`, `List<DirectoryEntry> Silos`) and `DirectoryEntry` record (`string SiloAddress`, `string EntryKey`)
- [x] 2.2 Create `Internal/MembershipSiloData.cs` — `MembershipSiloData` record (`string SiloAddress`, `string HostName`, `string SiloName`, `int Status`, `int ProxyPort`, `DateTime StartTime`, `List<SiloSuspectTime>? SuspectTimes`) and `SiloSuspectTime` record; no `SiloEtag` — the GUID in the directory entry serves as the per-silo etag
- [x] 2.3 Create `Internal/MembershipKeys.cs` — static helpers: `BucketName(serviceId)`, `ClusterPrefix(clusterId)`, `DirectoryKey(cId)`, `SiloBlobKey(cId, encodedAddr, guid)`, `SiloKeyPrefix(cId, encodedAddr)`, `AliveKey(cId, encodedAddr)`, `EncodeAddr(SiloAddress)`, `DecodeAddr(string)`
- [x] 2.4 Create `Internal/MembershipConverter.cs` — `ToMembershipEntry(MembershipSiloData, DateTime? aliveTime)` and `ToSiloData(MembershipEntry, int siloEtag)`

## 3. NatsMembershipTable — Core Class

- [x] 3.1 Create `NatsMembershipTable.cs` — class declaration, constructor (inject `INatsKVContext`, `IOptions<ClusterOptions>`, `NatsClusteringOptions`, `ILocalSiloDetails`), field declarations including local-silo cached state (`_myGuid`, `_myData`, `_myGuidRevision`, `_myAliveRevision`), CancellationTokenSource, serializer fields
- [x] 3.2 Implement `ILifecycleParticipant<ISiloLifecycle>` — register `Init`/`Close` at `ApplicationServices` stage
- [x] 3.3 Implement `Init` — call `CreateOrUpdateStore(bucketName, config)` (History=1, no MaxAge, File storage); start silo blob refresh loop via `Task.Run`
- [x] 3.4 Implement silo blob refresh loop — every `SiloEntryRefreshInterval`, call `TryUpdateAsync(_myGuid, _myData, _myGuidRevision, SiloEntryTtl)` if `_myGuid` is set; update `_myGuidRevision` on success
- [x] 3.5 Implement `Close` / `Dispose` — cancel CTS, await background task

## 4. Read Operations

- [x] 4.1 Implement `InitializeMembershipTable(bool tryInitTableVersion)` — ensure bucket; if `tryInitTableVersion`, `TryCreate` directory with `Version=0, Silos=[]`; ignore `NatsKVCreateException`
- [x] 4.2 Implement private `ReadDirectory()` — `TryGetEntryAsync` on directory key; return `(MembershipDirectory, revision)` or throw if key not found and table not initialized
- [x] 4.3 Implement `ReadAll()` — read directory; parallel-fetch all silo blobs + alive keys; handle 404 silo blob with one retry (re-read directory); merge entries; return `MembershipTableData`
- [x] 4.4 Implement `ReadRow(SiloAddress)` — read directory; find silo; fetch its blob + alive key; merge entry; return `MembershipTableData` with 0 or 1 entries

## 5. Write Operations

- [x] 5.1 Implement `InsertRow(entry, tableVersion)` — generate newGuid; `TryCreate` silo blob (`SiloEtag=1`, TTL); read+validate directory (revision match, silo absent); `TryUpdate` directory with `Version+1`; on success update `_myGuid` caches and start TTL refresh; on failure delete orphaned blob; return bool
- [x] 5.2 Implement `UpdateRow(entry, etag, tableVersion)` — read directory; validate revision matches `tableVersion.VersionEtag`; find silo in directory; validate `directoryEntry.EntryKey == etag` (GUID is the per-silo etag — no silo blob read needed for validation); generate newGuid; `TryCreate` new silo blob with TTL; `TryUpdate` directory with `Version+1`; on success fire-and-forget delete old blob key, update `_myGuid` caches if local silo; on failure delete orphaned blob; return bool
- [x] 5.3 Implement `UpdateIAmAlive(entry)` — if `_myAliveRevision == 0`: `TryCreate(aliveKey, utcNow, AliveKeyTtl)`; else `TryUpdate(aliveKey, utcNow, _myAliveRevision, AliveKeyTtl)`; update `_myAliveRevision` on success

## 6. Cleanup Operations

- [x] 6.1 Implement `CleanupDefunctSiloEntries(DateTimeOffset beforeDate)` — retry loop: read directory + parallel-fetch all blobs + alive keys; identify defunct silos (effective IAmAliveTime < beforeDate AND status not Active/Joining); if none found return; `TryUpdate` directory without defunct silos; on success sweep `{cId}.struct.silo.{encodedAddr}.>` keys + delete alive key for each defunct silo; retry on CAS failure (up to 5 attempts)
- [x] 6.2 Implement `DeleteMembershipTableEntries(string clusterId)` — delete directory key; `GetKeysAsync("{cId}.>")` to list all cluster keys; purge each one

## 7. Hosting Extensions

- [x] 7.1 Create `HostingExtensions.cs` — `AddNatsClustering(IServiceCollection, string name, Action<OptionsBuilder<NatsClusteringOptions>>?)` primary method; register `IMembershipTable` as `NatsMembershipTable` via `ActivatorUtilities`; register validator; configure named options logging
- [x] 7.2 Create `HostingExtensions.overloads.cs` — all 7 overloads: `Action<NatsClusteringOptions>` variant, 2× `AsDefault` variants (both option builder forms), 4× `ISiloBuilder` variants (all four above delegating to `ConfigureServices`)

## 8. Integration Tests

Tests run against real NATS (Testcontainers, `nats:2.11`) but without an Orleans runtime. `NatsMembershipTable` is constructed directly. TTL behaviour is not tested. No mocking framework — pure xUnit.

**Test infrastructure (add once):**
- [x] 8.1 Add `Internal/TestLocalSiloDetails.cs` — simple `ILocalSiloDetails` implementation accepting `SiloAddress` and optional `silo name` in constructor; used to inject local silo identity into `NatsMembershipTable`
- [x] 8.2 Add `MembershipTableShould.cs` — `IClassFixture<NatsConnectionFixture>`; private `CreateTable(SiloAddress localSilo)` helper that constructs `NatsMembershipTable` with a fresh random `serviceId` (isolation via unique bucket), fixed `clusterId = "test"`, default `NatsClusteringOptions`, and `TestLocalSiloDetails(localSilo)`; calls `InitializeMembershipTable(true)` before returning; private `MakeSilo(int port)` helper returning `SiloAddress`; private `MakeEntry(SiloAddress, SiloStatus)` helper returning populated `MembershipEntry`

**InitializeMembershipTable:**
- [x] 8.3 `CreatesBucketAndDirectory` — after init, `ReadAll()` returns `Version = 0` and empty entries
- [x] 8.4 `InitializeIsIdempotent` — calling `InitializeMembershipTable(true)` twice on same table does not throw

**InsertRow:**
- [x] 8.5 `InsertRowSucceeds` — `InsertRow(entry, version0)` returns true; subsequent `ReadAll()` contains the silo
- [x] 8.6 `InsertRowReturnsFalseForDuplicateSilo` — inserting same silo address twice returns false on second call
- [x] 8.7 `InsertRowReturnsFalseOnStaleVersion` — insert with version whose etag doesn't match current directory revision returns false

**ReadAll / ReadRow:**
- [x] 8.8 `ReadAllReturnsAllInsertedSilos` — insert two silos; `ReadAll()` returns both with correct addresses and status
- [x] 8.9 `ReadRowReturnsEntryForKnownSilo` — insert silo; `ReadRow(addr)` returns that entry
- [x] 8.10 `ReadRowReturnsEmptyForUnknownSilo` — `ReadRow(unknownAddr)` returns empty entries list with current version

**UpdateRow:**
- [x] 8.11 `UpdateRowSucceedsWithCorrectEtag` — insert silo; read etag from `ReadAll`; `UpdateRow` with new status returns true; `ReadAll` reflects updated status
- [x] 8.12 `UpdateRowReturnsFalseOnWrongEtag` — insert silo; `UpdateRow` with fabricated etag returns false
- [x] 8.13 `UpdateRowReturnsFalseOnStaleTableVersion` — insert two silos; take version snapshot; insert third silo (bumps version); `UpdateRow` with stale version returns false
- [x] 8.14 `UpdateRowReturnsFalseForAbsentSilo` — `UpdateRow` for an address not in directory returns false

**UpdateIAmAlive:**
- [x] 8.15 `UpdateIAmAliveReflectsInReadAll` — insert silo; call `UpdateIAmAlive` with a specific timestamp; `ReadAll()` returns entry with that `IAmAliveTime`
- [x] 8.16 `ReadAllDefaultsIAmAliveToStartTimeWhenNoAliveKey` — insert silo without calling `UpdateIAmAlive`; `ReadAll()` returns `IAmAliveTime == StartTime`

**CleanupDefunctSiloEntries:**
- [x] 8.17 `CleanupRemovesDefunctSilosFromDirectory` — insert two silos with old `IAmAliveTime` (set via `UpdateIAmAlive`) and `Dead` status; `CleanupDefunctSiloEntries(DateTimeOffset.UtcNow)` removes them; `ReadAll()` returns empty
- [x] 8.18 `CleanupKeepsActiveSilos` — insert active silo with recent heartbeat alongside defunct silo; after cleanup only active silo remains
- [x] 8.19 `CleanupDeletesGuidBlobsForDefunctSilo` — after cleanup, `GetKeysAsync` on the defunct silo's blob prefix returns no keys

**DeleteMembershipTableEntries:**
- [x] 8.20 `DeleteEntriesRemovesAllClusterKeys` — insert silos; call `DeleteMembershipTableEntries`; `ReadAll()` fails or returns empty; no keys with cluster prefix remain in bucket
