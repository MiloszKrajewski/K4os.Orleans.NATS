# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test

```shell
# Full build (uses Nuke build system, output in .output/)
./build.cmd

# Development builds
dotnet build src/K4os.Orleans.NATS.sln
dotnet test src/K4os.Orleans.NATS.sln

# Single test class
dotnet test src/K4os.Orleans.NATS.Tests --filter "FullyQualifiedName~DistributedLockShould"
```

Tests spin up a real NATS server via **Testcontainers** (Docker required). No mocking of the NATS connection.

Package management uses **Paket**, not NuGet central package management. Add or update packages with `paket add` / `paket update`.

## Project Structure

Each project is an independent Orleans provider backed by NATS:

| Project | Status | Purpose |
|---------|--------|---------|
| `K4os.Orleans.NATS` | Core | Shared utilities: `Url64`, `ValueStream<T>`, `Extensions` |
| `K4os.Orleans.Clustering.NATS` | Active (WIP) | Membership table implementation |
| `K4os.Orleans.Persistence.NATS` | Active | `IGrainStorage` via NATS KeyValue store |
| `K4os.Orleans.Streaming.NATS` | Active | NATS JetStream-based streaming adapter |
| `K4os.Orleans.GrainDirectory.NATS` | Stub | Grain location service (not implemented) |
| `K4os.Orleans.Reminders.NATS` | Stub | Reminder service (not implemented) |
| `K4os.Orleans.NATS.Tests` | Tests | xUnit integration tests |
| `PlaygroundSilo` | Demo | Example silo using all components together |

`vPrev1`, `vPrev2`, `vPrev3` subdirectories in Clustering are previous iterations kept for reference.

## Key Architectural Patterns

### NATS ID encoding
Orleans IDs can contain any characters; NATS keys have a restricted charset. All Orleans identifiers are encoded via `Url64` (URL-safe Base64, no padding) before use as NATS keys. Use `str.ToNatsId()` / `str.FromNatsId()` extension methods from `K4os.Orleans.NATS`.

### NATS KeyValue store creation (two-step pattern)
Always create or update KV objects using the two-step pattern to handle race conditions:
1. Call `CreateOrUpdateStoreAsync` (or equivalent `TryCreate`)
2. On `NatsJSApiException` with `ApiError` 500, fall back to a `Get` call

### Optimistic concurrency
NATS KV uses revision numbers natively. These map directly to Orleans ETags (stored as `ulong.ToString()`). `NatsKVWrongLastRevisionException` → `InconsistentStateException`.

### `ValueStream<T>`
A thread-safe cache for a single NATS KV entry that is being watched. Holds the latest `Value`, `Revision`, and `Timestamp`. Entries older than 15 seconds are treated as stale. Used by Clustering to cache the membership table and per-silo heartbeats without re-reading NATS on every Orleans call.

### `StoreLock` (Clustering)
Implements a distributed lock on top of NATS KV TTL entries. Acquires by `TryCreate`, waits by watching the key, renews via `TryUpdate` at 1/3 of TTL interval.

### Serialization
- Primitives: `NatsUtf8PrimitivesSerializer<T>` (NATS built-in)
- Complex types: `NatsSystemJsonSerializer<T>` (wraps `System.Text.Json`)
- Raw binary (grain state payloads): `NatsRawSerializer<ReadOnlyMemory<byte>>`

Do **not** use other serializers — keep it to these three.

### Hosting extensions
Every provider exposes `AddNats*` extension methods on `IServiceCollection`. Each primary method (`IServiceCollection, string name, Action<OptionsBuilder<TOptions>>? configurationBuilder`) must be accompanied by 7 overloads in a separate `HostingExtensions.overloads.cs` file:
- `Action<TOptions>` variant (configures via `.Configure`)
- Versions omitting `name` (use `ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME`, method name gains `AsDefault` suffix)
- `ISiloBuilder` variants of all four above (delegate to `builder.ConfigureServices(...)`)

## Coding Style

- Methods should stay under ~20 lines; extract named helpers for identifiable sub-tasks.
- Encapsulate all NATS client calls in small private helper methods, exposing only the parameters that vary in the given class context.
- Use `TryXxx` NATS APIs (returning `NatsResult<T>`) when the absent/failed case is expected and not exceptional.
- Global usings for `System`, `System.Linq`, `System.IO`, `System.Collections.Generic`, `System.Threading`, `System.Threading.Tasks` are active project-wide — do not repeat them.
- Nullable reference types are enabled everywhere (`<Nullable>enable</Nullable>`).
- Hosting extension classes live in `namespace Orleans.Hosting` (not the library's own namespace) so they appear alongside Orleans built-ins in IDE auto-complete.
