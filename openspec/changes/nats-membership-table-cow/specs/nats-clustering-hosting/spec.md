## ADDED Requirements

### Requirement: Primary registration method
The library SHALL expose `AddNatsClustering(IServiceCollection services, string name, Action<OptionsBuilder<NatsClusteringOptions>>? configurationBuilder = null)` as the primary method for registering the NATS clustering provider. It SHALL register `NatsMembershipTable` as `IMembershipTable`, apply options validation, and configure named options logging.

#### Scenario: Service registered with name
- **WHEN** `AddNatsClustering(services, "my-cluster", builder => builder.Configure(...))` is called
- **THEN** `IMembershipTable` is resolvable from the container and uses the named options

#### Scenario: Registration without configuration builder
- **WHEN** `AddNatsClustering(services, "default")` is called with no configuration builder
- **THEN** `IMembershipTable` is registered with default `NatsClusteringOptions` values

---

### Requirement: Method overloads
The library SHALL provide 7 additional overloads following the same pattern as `NatsGrainStorage` hosting extensions, all in a separate `HostingExtensions.overloads.cs` file:
1. `Action<NatsClusteringOptions>` variant (configures via `.Configure(action)`)
2. `AsDefault` variant omitting `name` (uses `ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME`)
3. `AsDefault` + `Action<OptionsBuilder<NatsClusteringOptions>>`
4. `AsDefault` + `Action<NatsClusteringOptions>`
5. `ISiloBuilder` variant of the primary method
6. `ISiloBuilder` + `Action<NatsClusteringOptions>`
7. `ISiloBuilder` + `AsDefault` variants (2)

All `ISiloBuilder` overloads SHALL delegate to `builder.ConfigureServices(...)`.

#### Scenario: ISiloBuilder overload delegates correctly
- **WHEN** `siloBuilder.AddNatsClustering(options => ...)` is called
- **THEN** the clustering provider is registered in the silo's service collection

#### Scenario: AsDefault uses provider constant
- **WHEN** an `AsDefault` overload is called
- **THEN** the provider is registered under `ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME`

---

### Requirement: Namespace
All hosting extension classes SHALL be in the `Orleans.Hosting` namespace (not the library's own namespace) so they appear alongside Orleans built-in extensions in IDE auto-complete.

#### Scenario: Extensions discoverable in Orleans.Hosting namespace
- **WHEN** a developer adds `using Orleans.Hosting`
- **THEN** `AddNatsClustering` appears in IDE completion alongside `AddMemoryGrainStorage` and similar extensions

---

### Requirement: NatsClusteringOptions
`NatsClusteringOptions` SHALL expose the following configurable properties with the indicated defaults:
- `Uri? NatsUrl` — NATS server URL (default: null, uses NATS default)
- `string? BucketName` — override for the computed bucket name (default: null)
- `Func<INatsKVContext>? Connector` — custom KV context factory (default: null)
- `TimeSpan SiloEntryTtl` — silo blob TTL (default: 7 days)
- `TimeSpan SiloEntryRefreshInterval` — silo blob refresh interval (default: 1 hour)
- `TimeSpan AliveKeyTtl` — alive key TTL (default: 2 hours)

`ClusterId` and `ServiceId` SHALL NOT be properties of `NatsClusteringOptions`.

#### Scenario: Default options produce valid configuration
- **WHEN** `NatsClusteringOptions` is constructed with no configuration
- **THEN** `SiloEntryTtl` is 7 days, `SiloEntryRefreshInterval` is 1 hour, `AliveKeyTtl` is 2 hours

#### Scenario: Connector overrides NATS connection
- **WHEN** `Connector` is set to a factory function
- **THEN** the returned `INatsKVContext` is used instead of creating a new connection from `NatsUrl`
