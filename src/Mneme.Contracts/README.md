# Mneme.Contracts

Interfaces and DTOs shared across all Mneme components. This is the
NuGet-shippable contract surface that external consumers (e.g., the
MuxiMuxi cockpit, third-party agent hosts) bind against.

## Status

**Phase 0 — not started.** This project intentionally has no source files
yet. See [`plans/plan.md`](../../plans/plan.md) Phase 0 for the contracts
that will land here:

- `CaptureEvent` — the wire envelope for events ingested by Mneme
- `CapabilityToken` — workstream-scoped query authorization
- `IMemoryAgent` — single-subscriber ingest API (capture → memory)
- `IMemoryQueryAPI` — capability-checked query API (consumers → memory)
- Supporting enums + records (epistemic categories, classification labels,
  query/distill specs)

## Design rules

- **No implementation code here.** Only interfaces, records, enums.
- **No dependencies beyond .NET 8 BCL.** This must be lightweight enough
  to pull into any agent host without dragging the world.
- **No raw SQL exposed.** Even at the contract level, queries are typed
  spec objects — never strings.
- **Backward compatibility from v0.1.0+.** Breaking changes only at major
  version bumps with migration notes.
