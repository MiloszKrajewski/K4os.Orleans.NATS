# K4os.Orleans.NATS - AI Coding Agent Instructions

## Project Architecture

This project provides **NATS-based Orleans providers** for clustering, persistence, reminders, streaming, and grain 
directories. It's a modular C# library where each component (`K4os.Orleans.*.NATS`) implements specific Orleans service 
providers using NATS as the backend.

## Core Components

- **`K4os.Orleans.NATS`** - Shared utilities and abstractions for NATS integration
- **`K4os.Orleans.Clustering.NATS`** - Clustering implementation
- **`K4os.Orleans.Persistence.NATS`** - Grain state persistence
- **`K4os.Orleans.Streaming.NATS`** - Stream processing
- **`K4os.Orleans.GrainDirectory.NATS`** - Grain location services
- **`K4os.Orleans.Reminders.NATS`** - Timer/reminder services
- **`PlaygroundSilo`** - Example silo setup for testing and demonstration trying to use all components together

## Key Patterns

* NATS KeyValue Store:
  - store can be read from, written to but also watched for changes
  - store has optimistic concurrency via revision numbers which built into NATS KeyValue API
* NATS ID:
  - NATS has limited character set for keys while Orleans does not have such limitations
  - We store Orleans IDs as Base64-encoded (actually Url64) strings compatible with NATS key requirements
  - Conversion is done via `ToNatsId()` and `FromNatsId()` extension methods
* All serialization should use either primitive serializer `NatsUtf8PrimitivesSerializer` or `NatsSystemJsonSerializer` 
  for complex types.

## Development Workflow

* Build: use `dotnet build` for full builds
* Test: use `dotnet test` to run all tests

