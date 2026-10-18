# NATS Grain Storage Provider Tasks

Based on the Redis implementation analysis, here are the key tasks for implementing the NATS-based Orleans grain storage provider.

Use general rules from tasks/CodingStyle.md

## Core Implementation Tasks

### 1. Basic Storage Provider Structure

- [ ] Create `NatsGrainStorage` class implementing `IGrainStorage` and `ILifecycleParticipant<ISiloLifecycle>`
- [ ] Add constructor with dependencies: `INatsKVContext`, `IOptions<NatsStorageOptions>`, `IGrainStorageSerializer`, `IOptions<ClusterOptions>`, etc.
- [ ] Implement lifecycle management (`Participate`, `Init`, `Close` methods)

### 2. Configuration and Options

- [ ] Create `NatsStorageOptions` class with properties:
  - [ ] `NatsContext` key/value context factory callback
  - [ ] `NatsOpts` nats connection options
  - [ ] `BucketName` for explicit bucket naming (required)
- [ ] Create extension methods for DI registration in `Extensions.cs`

NOTE: `NatsGrainStorage` constructor should parse/cache/simplify values passed as options so rest of the implementation does not need to do it anymore.

### 3. Core CRUD Operations

- [ ] **ReadStateAsync**: 
  - [ ] Generate NATS key using grain type and ID or configured callback
  - [ ] Retrieve versioned state from NATS KV store
  - [ ] Handle missing entries gracefully (there is TryXXX family of methods)
  - [ ] Map NATS revision to Orleans ETag
- [ ] **WriteStateAsync**:
  - [ ] Implement optimistic concurrency using NATS revisions
  - [ ] Handle `NatsKVWrongLastRevisionException` → `InconsistentStateException`
  - [ ] Update grain state ETag after successful write
- [ ] **ClearStateAsync**:
  - [ ] Support both delete and clear-data behaviors
  - [ ] Maintain concurrency control

### 4. Key Management and Serialization

- [ ] Implement default storage key generation: `{grainType.ToNatsId()}/{grainId.ToNatsId()}`
- [ ] Use existing `ToNatsId()/FromNatsId()` utilities for key encoding
- [ ] Use `IGrainStorageSerializer` for grain state serialization (user-configurable)
- [ ] Store serialized data as binary in NATS KV store using `NatsRawSerializer<byte[]>`

### 5. NATS-Specific Features

- [ ] Create dedicated KV bucket per storage instance: `{options.BucketName}`
- [ ] Configure bucket with appropriate settings:
  - [ ] Storage type (File for persistence)
  - [ ] History settings (1 for latest only)
  - [ ] No TTL (grain state should persist indefinitely)
- [ ] Implement bucket lifecycle management (create on init, optional cleanup)

## Advanced Features

### 6. Performance and Resilience

- [ ] Implement retry logic for transient NATS failures
- [ ] Add connection health monitoring
- [ ] Optimize for batch operations if needed
- [ ] Consider read-through caching strategies

### 7. Monitoring and Diagnostics
- [ ] Add structured logging using `ILogger<NatsGrainStorage>`
- [ ] Create log messages for:
  - [ ] Initialization/shutdown events
  - [ ] Read/write operation failures
  - [ ] Concurrency conflicts
  - [ ] Performance metrics
- [ ] Add performance counters/metrics

### 8. Testing Infrastructure
- [ ] Create `NatsGrainStorageTests` with `NatsConnectionFixture`
- [ ] Test scenarios:
  - [ ] Basic CRUD operations
  - [ ] Concurrency conflict handling
  - [ ] Missing state scenarios
  - [ ] Error conditions and recovery
- [ ] Integration tests with actual Orleans grains

## Implementation Notes

### NATS KV Store Patterns
- Use `ValueStream<T>` pattern for state management if beneficial
- Leverage NATS KV revision numbers for ETag implementation
- Consider bucket-per-grain-type vs single bucket strategies

### Error Handling
- Map NATS exceptions to Orleans storage exceptions:
  - `NatsKVWrongLastRevisionException` → `InconsistentStateException`
  - Other failures → `NatsStorageException` (create custom exception type)

### Serialization Strategy
- Primary: `IGrainStorageSerializer` (Orleans standard, user-configurable)
- NATS Storage: Use `NatsRawSerializer<byte[]>` to store serialized binary data
- Handle null/empty state scenarios
- Support any serialization format Orleans provides (JSON, Binary, etc.)

### Key Design Decisions
- **Bucket Strategy**: Explicit bucket naming via configuration (supports multiple storage instances)
- **Persistence**: No TTL - grain state persists indefinitely until explicitly cleared/deleted
- **Revision Mapping**: NATS revision → Orleans ETag conversion
- **Cleanup Policy**: Automatic vs manual cleanup of old/unused buckets

## Dependencies and Prerequisites
- [ ] NATS server with JetStream enabled
- [ ] `NATS.Client.KeyValueStore` package
- [ ] Existing `K4os.Orleans.NATS` utilities (ID encoding, etc.)
- [ ] Orleans dependencies (`Microsoft.Orleans.Server`)

## Future Enhancements
- [ ] Support for grain state partitioning across multiple buckets
- [ ] Backup and restore functionality
- [ ] Multi-region/cluster replication support
- [ ] State migration tools for Orleans upgrades
- [ ] Performance benchmarking against Redis provider