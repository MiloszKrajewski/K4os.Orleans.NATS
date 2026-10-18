## Why

The `K4os.Orleans.Clustering.NATS` project has no working membership table implementation — three previous attempts (vPrev1/2/3) were abandoned. Orleans needs a correct, scalable `IMembershipTable` backed by NATS KV to complete the clustering provider.

## What Changes

- **Replace** the stub `NatsClusteringOptions` with a full options class (NATS URL, bucket override, TTL settings)
- **Add** `NatsMembershipTable` implementing `IMembershipTable` using a GUID copy-on-write (CoW) strategy
- **Add** internal DTO types for the directory blob and silo entry blob
- **Add** key construction helpers for the NATS KV key layout
- **Add** `AddNatsClustering` hosting extensions (primary + 7 overloads) in `Orleans.Hosting` namespace
- **Remove** the existing `NatsClusteringOptions.ClusterId` property (sourced from `ClusterOptions` instead)

## Capabilities

### New Capabilities

- `nats-membership-table`: Orleans `IMembershipTable` backed by NATS KV using the GUID copy-on-write strategy — directory blob as atomic CAS point, per-silo blobs with 7-day TTL and hourly refresh, separate alive keys with 2-hour TTL for heartbeats
- `nats-clustering-hosting`: `AddNatsClustering` service collection and silo builder extensions for registering the provider

### Modified Capabilities

## Impact

- **`K4os.Orleans.Clustering.NATS`**: primary target — new files, `NatsClusteringOptions` replaced
- **`K4os.Orleans.NATS.Tests`**: integration tests for the membership table needed
- **No breaking changes to other providers** (Persistence, Streaming, GrainDirectory, Reminders)
- **NATS Server 2.10+** required for per-key TTL support
- **Orleans 9.x** `IMembershipTable`, `ClusterOptions`, `ILocalSiloDetails` APIs consumed
