## ADDED Requirements

### Requirement: Bucket naming
The membership table SHALL use a NATS KV bucket named `orleans-{serviceId.ToNatsId()}`, where `serviceId` is taken from `ClusterOptions.ServiceId`. Multiple clusters within the same service MAY coexist in this bucket, distinguished by their key prefix.

#### Scenario: Bucket created on initialization
- **WHEN** `InitializeMembershipTable` is called
- **THEN** a NATS KV bucket named `orleans-{serviceId.ToNatsId()}` is created or confirmed to exist

#### Scenario: Custom bucket name override
- **WHEN** `NatsClusteringOptions.BucketName` is set to a non-empty value
- **THEN** that value is used as the bucket name instead of the default

---

### Requirement: Key layout
All NATS KV keys SHALL use `.` as the hierarchy separator. All user-supplied string components (serviceId, clusterId, siloAddress) SHALL be Url64-encoded via `ToNatsId()` before use as key segments, ensuring no `.` appears within a segment.

Key patterns within the bucket:
- `{cId}.struct.membership` — directory blob (permanent, no TTL)
- `{cId}.struct.silo.{encodedAddr}.{guidEncoded}` — silo entry blob (7-day TTL + hourly refresh)
- `{cId}.alive.{encodedAddr}` — heartbeat timestamp (2-hour TTL, reset on each write)

where `cId = clusterId.ToNatsId()`, `encodedAddr = siloAddr.ToParsableString().ToNatsId()`, `guidEncoded = Url64.EncodeGuid(guid)`.

#### Scenario: SiloAddress with special characters encodes without dots
- **WHEN** a silo address such as `10.0.0.1:11111@12345` is encoded for use in a key
- **THEN** the encoded form contains only `[A-Za-z0-9_-]` characters with no `.`

#### Scenario: Encoded address is reversible
- **WHEN** `encodedAddr.FromNatsId()` is called on the encoded form
- **THEN** `SiloAddress.FromParsableString()` on the result reconstructs the original address

---

### Requirement: Directory blob schema
The directory blob SHALL be a JSON object with the following structure, serialized via `NatsSystemJsonSerializer<MembershipDirectory>` (PascalCase property names):
```json
{ "Version": 5, "Silos": [{ "SiloAddress": "10.0.0.1:11111@12345", "EntryKey": "Url64Guid" }] }
```
- `Version`: integer matching `TableVersion.Version`
- `Silos`: array of `{ SiloAddress: string (ToParsableString), EntryKey: string (Url64-encoded GUID) }`

The NATS KV revision of the directory key serves as `TableVersion.VersionEtag`. `VersionEtag` is NOT stored in the blob.

#### Scenario: Directory round-trips through serialization
- **WHEN** a `MembershipDirectory` is serialized and deserialized
- **THEN** `Version` and all `SiloAddress`/`EntryKey` pairs are preserved exactly

---

### Requirement: Silo blob schema
Each silo entry blob SHALL be a JSON object serialized via `NatsSystemJsonSerializer<MembershipSiloData>` (PascalCase):
```json
{ "SiloAddress": "...", "HostName": "...", "SiloName": "...", "Status": 3,
  "ProxyPort": 30000, "StartTime": "2026-04-30T10:00:00Z",
  "SuspectTimes": [{"SiloAddress":"...","Time":"..."}] }
```
- `IAmAliveTime` SHALL NOT be stored in the silo blob (sourced from alive key)
- `Status` SHALL be stored as the integer value of `SiloStatus`
- `SuspectTimes` MAY be null or omitted when empty
- The GUID segment of the blob key (`DirectoryEntry.EntryKey`) serves as the per-silo etag; no etag field is stored in the blob

#### Scenario: IAmAliveTime is not in the silo blob
- **WHEN** a silo blob is written by InsertRow or UpdateRow
- **THEN** the JSON does not contain an `IAmAliveTime` field

#### Scenario: GUID changes on each successful UpdateRow
- **WHEN** `UpdateRow` succeeds
- **THEN** the directory entry references a new GUID key; the previous GUID key is deleted

---

### Requirement: InitializeMembershipTable
`InitializeMembershipTable(bool tryInitTableVersion)` SHALL create the KV bucket. When `tryInitTableVersion` is `true`, it SHALL attempt to create the directory key with `Version = 0` and empty `Silos` via `TryCreate`. A failure indicating the key already exists SHALL be silently ignored.

#### Scenario: First silo initializes the directory
- **WHEN** `InitializeMembershipTable(true)` is called on a new cluster
- **THEN** the directory key is created with `Version = 0` and `Silos = []`

#### Scenario: Concurrent initialization is idempotent
- **WHEN** multiple silos call `InitializeMembershipTable(true)` simultaneously
- **THEN** only one directory is created; all calls succeed without error

#### Scenario: Non-primary silo skips directory creation
- **WHEN** `InitializeMembershipTable(false)` is called
- **THEN** the bucket is ensured but the directory key is not written

---

### Requirement: ReadAll
`ReadAll()` SHALL return a `MembershipTableData` containing all silos in the directory and the current `TableVersion`. For each silo, `IAmAliveTime` SHALL be merged from the alive key; if the alive key is absent, `IAmAliveTime` SHALL default to `StartTime` from the silo blob. Silo blob Gets and alive key Gets SHALL be issued in parallel.

#### Scenario: Returns all silos in directory
- **WHEN** the directory contains N silos
- **THEN** `ReadAll()` returns exactly N entries

#### Scenario: Missing alive key defaults to StartTime
- **WHEN** a silo's alive key does not exist
- **THEN** the returned entry has `IAmAliveTime = StartTime`

#### Scenario: TOCTOU retry on missing silo blob
- **WHEN** a silo blob referenced by the directory returns 404 (deleted by concurrent UpdateRow)
- **THEN** the directory is re-read and the blob fetch is retried once before skipping

---

### Requirement: ReadRow
`ReadRow(SiloAddress)` SHALL return a `MembershipTableData` with 0 or 1 entries and the current `TableVersion`. The silo entry, if present, SHALL include `IAmAliveTime` merged from the alive key (defaulting to `StartTime` if absent).

#### Scenario: Returns single entry for known silo
- **WHEN** the silo is in the directory
- **THEN** `ReadRow` returns exactly one entry with correct etag and IAmAliveTime

#### Scenario: Returns empty for unknown silo
- **WHEN** the silo is not in the directory
- **THEN** `ReadRow` returns an empty entry list with the current TableVersion

---

### Requirement: InsertRow
`InsertRow(entry, tableVersion)` SHALL atomically add a new silo to the directory using the CoW protocol. It SHALL return `false` if the directory revision does not match `tableVersion.VersionEtag`, if the silo already exists in the directory, or if the CAS on the directory fails. A new `SiloEtag = 1` SHALL be set on the new silo blob.

#### Scenario: Successful insert returns true
- **WHEN** no concurrent modification and silo is not in directory
- **THEN** `InsertRow` returns `true`, directory version is incremented by 1, new silo blob exists

#### Scenario: Duplicate insert returns false
- **WHEN** the silo is already present in the directory
- **THEN** `InsertRow` returns `false`

#### Scenario: Stale tableVersion returns false
- **WHEN** another silo has modified the directory since tableVersion was read
- **THEN** `InsertRow` returns `false`

#### Scenario: Orphaned GUID is cleaned up on CAS failure
- **WHEN** the directory CAS fails
- **THEN** the newly-written silo blob is deleted before returning false

---

### Requirement: UpdateRow
`UpdateRow(entry, etag, tableVersion)` SHALL atomically update an existing silo's data using the CoW protocol. It SHALL return `false` if: the directory revision doesn't match `tableVersion.VersionEtag`; the silo is absent from the directory; `DirectoryEntry.EntryKey` (the current GUID) does not match the provided `etag`; or the directory CAS fails. Etag validation is performed against the directory only — no silo blob read is required.

#### Scenario: Successful update returns true
- **WHEN** all validations pass and CAS succeeds
- **THEN** `UpdateRow` returns `true`, old silo blob is deleted (fire-and-forget), SiloEtag incremented

#### Scenario: Etag mismatch returns false
- **WHEN** the provided etag doesn't match the current SiloEtag in the silo blob
- **THEN** `UpdateRow` returns `false`

#### Scenario: Orphaned new GUID cleaned up on CAS failure
- **WHEN** the new silo blob is written but the directory CAS fails
- **THEN** the new blob is deleted before returning false

---

### Requirement: UpdateIAmAlive
`UpdateIAmAlive(entry)` SHALL write the current UTC timestamp to the alive key for the local silo. It SHALL use `TryCreate` on first call and `TryUpdate` with the cached revision on subsequent calls, both with the configured `AliveKeyTtl`. It SHALL NOT modify the directory or any silo blob. This operation SHALL always succeed (per Orleans interface contract).

#### Scenario: Alive key created on first heartbeat
- **WHEN** `UpdateIAmAlive` is called for the first time for this silo
- **THEN** the alive key is created with the current timestamp and configured TTL

#### Scenario: Alive key TTL is reset on each call
- **WHEN** `UpdateIAmAlive` is called repeatedly
- **THEN** the alive key TTL is reset to `AliveKeyTtl` from the moment of each call

#### Scenario: Directory and silo blob are not touched
- **WHEN** `UpdateIAmAlive` is called
- **THEN** neither the directory nor the silo blob revision changes

---

### Requirement: Silo blob TTL and refresh
Silo blobs SHALL be written with a TTL equal to `NatsClusteringOptions.SiloEntryTtl` (default: 7 days). The local silo's `NatsMembershipTable` SHALL maintain a background refresh loop that re-writes the local silo's current blob via `TryUpdateAsync` with the same TTL at the configured `SiloEntryRefreshInterval` (default: 1 hour), resetting the expiry clock. Orphaned blobs (from failed CoW CAS) will expire after `SiloEntryTtl` with no explicit cleanup required.

#### Scenario: Silo blob written with TTL
- **WHEN** `InsertRow` or `UpdateRow` succeeds
- **THEN** the new silo blob has a TTL equal to `SiloEntryTtl`

#### Scenario: Refresh loop resets TTL
- **WHEN** `SiloEntryRefreshInterval` elapses
- **THEN** the local silo's blob is re-written via TryUpdate with a fresh `SiloEntryTtl`

#### Scenario: Orphaned blob expires automatically
- **WHEN** a silo blob is written but the directory CAS fails and the orphan delete also fails
- **THEN** the blob expires after `SiloEntryTtl` with no further action

---

### Requirement: CleanupDefunctSiloEntries
`CleanupDefunctSiloEntries(beforeDate)` SHALL remove from the directory all silos whose effective `IAmAliveTime` (max of alive key value and StartTime) is before `beforeDate` AND whose status is not `Active` or `Joining`. The directory update SHALL be a CAS operation; on conflict it SHALL retry. After a successful CAS, the implementation SHALL delete the defunct silo's alive key and sweep all their GUID blobs via `GetKeysAsync("{cId}.struct.silo.{encodedAddr}.>")`.

#### Scenario: Defunct silos removed from directory
- **WHEN** silos with stale IAmAliveTime and non-active status are present
- **THEN** they are removed from the directory in a single CAS operation

#### Scenario: All GUID blobs swept for defunct silo
- **WHEN** a defunct silo is removed from the directory
- **THEN** all keys matching `{cId}.struct.silo.{encodedAddr}.>` are deleted, including any orphaned blobs

---

### Requirement: DeleteMembershipTableEntries
`DeleteMembershipTableEntries(clusterId)` SHALL delete the directory key and all keys with the cluster prefix `{cId}.` from the bucket. This operation is not required to be atomic.

#### Scenario: Directory key is deleted
- **WHEN** `DeleteMembershipTableEntries` is called
- **THEN** the `{cId}.struct.membership` key no longer exists

#### Scenario: All cluster keys are removed
- **WHEN** `DeleteMembershipTableEntries` is called
- **THEN** no keys with the prefix `{cId}.` remain in the bucket
