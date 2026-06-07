# MuxiMuxi — Subsystem Architecture Overview

**Status**: current. Describes the post-v3.1 three-subsystem shape.
**Date**: 2026-06-06.
**Companion docs**: `product-vision-v3.md`, `wedge/plan.md`, `capture/plan.md`, `memory-agent/plan.md`.

## Why three subsystems

MuxiMuxi is one product (the cockpit), but it benefits from three internal
subsystems that ship independently. The capture and memory subsystems are
designed to be detachable — they could be reused by a different host, replaced
by alternative implementations, or extracted as standalone projects later.

The boundaries:

1. **MuxiMuxi cockpit** owns the user-facing product: workstreams, signal inbox,
   approval gate, agent host, dock UI. It *emits* capture events; it does not
   own memory.
2. **MuxiMuxi.Capture** is the bus. Everything that becomes memory flows
   through it. Plugins (signal adapters) and cockpit code both emit capture
   events here. It normalizes and routes.
3. **MuxiMuxi.MemoryAgent** is the brain. It receives capture events,
   classifies them, distills them into useful synthesized context, persists
   them, and serves capability-checked queries back to workstream agents.

## Diagram

```
┌─────────────────────────┐        ┌──────────────────────────┐        ┌─────────────────────────┐
│  MuxiMuxi (cockpit)     │        │  MuxiMuxi.Capture        │        │  MuxiMuxi.MemoryAgent   │
│  - Wedge product        │ events │  - Capture bus           │ events │  - Distillation         │
│  - Signal inbox         ├───────►│  - Plugin signal agg     ├───────►│  - Classification       │
│  - Approval gate        │        │  - Normalization         │        │  - Persistence (events) │
│  - Workstreams          │        │  - Routes to MemoryAgent │        │  - Projections (graph)  │
│  - Agent host           │        │                          │        │  - Query API            │
│  - Plugins (signals)    │        │  ICaptureBus             │        │    (capability-checked) │
│                         │◄───────┤  ICaptureSource          │◄───────┤  IMemoryQueryAPI        │
└─────────────────────────┘ query  └──────────────────────────┘ query  └─────────────────────────┘
                                                                                   │
                                                                                   ▼
                                                                       Workstream agents get
                                                                       distilled, compressed
                                                                       context — not raw dumps
```

## Interface contracts

The subsystems talk through four interfaces. All live in a shared contracts
assembly (`src/MuxiMuxi.Memory.Contracts`) so any subsystem can be swapped
without touching the others.

### `ICaptureBus` (cockpit → capture)

The cockpit's lever for emitting events into memory. Used by approval gate,
signal inbox, draft pipeline, etc.

```csharp
public interface ICaptureBus
{
    Task EmitAsync(CaptureEvent evt, CancellationToken ct = default);
}

public record CaptureEvent(
    string EventId,           // ULID; globally unique; idempotent insertion key
    string WorkstreamId,
    CaptureEventType Type,    // signal.received, decision.approved, action.executed, ...
    CaptureSourceId Source,
    DateTimeOffset OccurredAt,
    DateTimeOffset CapturedAt,
    object Payload,
    CaptureProvenance Provenance
);
```

### `ICaptureSource` (plugins → capture)

What plugins implement to be a capture source: signal adapters, agent host
tool-call observers, etc.

```csharp
public interface ICaptureSource
{
    string SourceId { get; }
    Task<IAsyncEnumerable<CaptureEvent>> StreamAsync(CancellationToken ct);
}
```

### `IMemoryAgent` (capture → memory)

What capture uses to push to memory. Single subscriber by design — memory is
the canonical destination.

```csharp
public interface IMemoryAgent
{
    Task IngestAsync(CaptureEvent evt, CancellationToken ct = default);
}
```

### `IMemoryQueryAPI` (cockpit / agents → memory)

What workstream agents query through. Capability-checked. **No raw SQL escape
hatch.**

```csharp
public interface IMemoryQueryAPI
{
    Task<MemoryQueryResult> QueryAsync(
        MemoryQueryRequest req, CapabilityToken cap, CancellationToken ct = default);
    Task<DistillationResult> DistillAsync(
        DistillationRequest req, CapabilityToken cap, CancellationToken ct = default);
}
```

`CapabilityToken` encodes "this agent in this workstream can read this scope"
and is issued by the cockpit at workstream-agent startup. Cross-workstream
tokens require explicit human grant per request (not standing).

## Deployment shapes

The interfaces accommodate three deployment models. Pick at install time.

1. **Embedded** — capture + memory agent both run in the cockpit process.
   Simplest deployment; lowest latency. Default for single-user desktop install.
2. **Sidecar** — memory agent runs as a separate .NET process; capture stays
   in-cockpit and talks to memory over IPC (named pipes or gRPC). Useful when
   memory has heavy LLM workloads we don't want blocking UI.
3. **Service** — memory agent runs as a separate machine / cloud service.
   Useful for team deployments or memory shared across multiple cockpits.

All three supported because the interfaces are transport-agnostic. v1 ships
Embedded; Sidecar lands in v1.5; Service is post-v2.

## Independent build / release cadence

Each subsystem has its own `plan.md` and todo prefix:

| Subsystem | Plan file | Todo prefix | Repo project (proposed) |
|---|---|---|---|
| Wedge product | `wedge/plan.md` | `wedge-*` | `src/MuxiMuxi.*` (existing) |
| Capture | `capture/plan.md` | `cap-*` | `src/MuxiMuxi.Capture` |
| Memory agent | `memory-agent/plan.md` | `mem-*` | `src/MuxiMuxi.MemoryAgent` |
| Contracts | (shared by all) | `arch-*` | `src/MuxiMuxi.Memory.Contracts` |

## Build order

1. **Contracts first** — `ICaptureBus`, `ICaptureSource`, `IMemoryAgent`,
   `IMemoryQueryAPI`, `CaptureEvent` schema, `CapabilityToken`. Both subsystem
   projects depend on this. Wedge unblocks the moment contracts stabilize.
2. **Wedge starts immediately with capture stubs** — a null `ICaptureBus`
   implementation that drops everything is enough to develop signal adapters
   and workstreams without waiting for capture or memory.
3. **Capture subsystem** — bus, normalization, plugin host. Replaces the null
   stub when ready.
4. **Memory agent subsystem** — full v3+ scope per `memory-agent/plan.md`.
   Develops entirely in parallel with the wedge once contracts are stable.

Critical: **none of the three subsystems blocks any other's daily progress**
once contracts are defined. The wedge can ship with stubs; capture can land
before memory; memory can be developed against a recorded capture-event stream
without the cockpit running.

## Design principles (apply across all three subsystems)

1. **Capture is mandatory; memory is enrichment.** The wedge MUST work with
   degraded or absent memory. Approval Gate, signal flow, draft generation
   must not hard-depend on memory health. If memory is down, the cockpit logs
   but doesn't block.
2. **Memory agent compresses context proactively.** Its primary value is
   producing distilled, decision-useful context for workstream agents — not
   just storing raw events. Workstream agents query for synthesis ("what do I
   need to know about customer X right now?"), not for raw event dumps.
3. **No raw SQL path for agents.** All memory queries go through
   `IMemoryQueryAPI` with capability tokens. This is the access boundary,
   not a tag-based convention.
4. **Provenance everywhere.** Every captured event and every derived item
   records origin (source, model, prompt, timestamp). Replay must be possible.
5. **Append-only events; revocable content.** Event metadata is immutable;
   content blobs are separately addressable and can be revoked via tombstone.
   Satisfies retention-forever AND legal-revocation simultaneously.
6. **Each subsystem deployable independently.** A user could in principle run
   just capture + memory against a different host (e.g., a custom CLI agent).
   MuxiMuxi-the-cockpit is one consumer; the design admits others.

## Cross-references

- Product framing & scope: `product-vision-v3.md`
- Wedge product plan: `wedge/plan.md`
- Capture subsystem plan: `capture/plan.md`
- Memory agent plan: `memory-agent/plan.md`
- Memory agent build-vs-integrate research:
  `memory-agent/research-existing-systems.md` *(in progress — background research agent)*
