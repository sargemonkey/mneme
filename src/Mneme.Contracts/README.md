# Mneme.Contracts

Interfaces and DTOs shared across all Mneme components. This is the
NuGet-shippable contract surface that external consumers (e.g., the
MuxiMuxi cockpit, third-party agent hosts) bind against.

## Status

**Phase 0 — shipped.** Pure-BCL contract surface for ingest, query, capability
checks, distillation bundles, and human-in-the-loop curation. All public types
are records, interfaces, enums, or exceptions; **no implementation code lives
here**. Verified by [`ContractSurfaceTests`](../../tests/Mneme.Contracts.Tests/ContractSurfaceTests.cs).

### Shipped types

**Capture / ingest**
- [`CaptureEvent`](CaptureEvent.cs), [`CaptureProvenance`](CaptureEvent.cs),
  [`CaptureSourceId`](CaptureEvent.cs), [`IngestResult`](CaptureEvent.cs)
- [`EventPayload`](EventPayloads.cs) (abstract, STJ-polymorphic) with seven
  sealed derived records: `EvidencePayload`, `FactPayload`, `DecisionPayload`,
  `HypothesisPayload`, `GoalPayload`, `ActionPayload`, `OutcomePayload`.
- [`IMemoryAgent.IngestAsync`](IMemoryAgent.cs) — single-subscriber ingest;
  contract documents the <50 ms post-WAL latency target.

**Query / distillation**
- [`IMemoryQueryAPI`](IMemoryQueryAPI.cs), [`QuerySpec`](QuerySpec.cs),
  [`QueryRequest`](QuerySpec.cs), [`DistillOptions`](QuerySpec.cs)
- [`QueryResult`](QueryResult.cs), [`QueryResultItem`](QueryResult.cs),
  [`ScoreDetails`](QueryResult.cs) (fused vs. final separation),
  [`QueryExplain`](QueryResult.cs)
- [`ContextBundle`](ContextBundle.cs) two-tier shape: `BundleIndex` +
  `BundleSection` + `OrientationSummary` + `LookupHints` + `LookupHint`,
  each carrying staleness metadata.

**Identity / authorization**
- Strong-typed IDs as `readonly record struct`: [`EventId`](Identifiers.cs)
  (with `None` sentinel), `WorkstreamId`, `FactId`, `EntityId`, `PrincipalId`.
- [`CapabilityToken`](CapabilityToken.cs) — read/query authorization with
  `IsValidAt`, `Allows`, `CrossWorkstream`, `IncludeTechnical`.
- [`CurationCapability`](CurationCapability.cs) — write authorization;
  least-privilege defaults (all eight `CanX` flags default to `false`).
- [`CapabilityDeniedError`](Exceptions.cs) (subclass of
  `UnauthorizedAccessException`), [`StaleProposalError`](Exceptions.cs)
  (subclass of `InvalidOperationException`).

**Human-in-the-loop curation (Mneme differentiator)**
- [`IMemoryCurator`](IMemoryCurator.cs) — all seven curation operations
  (amend, annotate, pin, demote, split, merge, revert).
- [`ICurationLog`](ICurationLog.cs) — append-only audit history.
- [`IReviewQueue`](IReviewQueue.cs) — pre-distillation approve / reject /
  defer flow for `WorkstreamMode.ReviewBeforeDistill`.
- [`CurationResult`](Curation.cs), [`CurationEntry`](Curation.cs),
  [`FactAmendment`](Curation.cs), [`FactSplitPart`](Curation.cs),
  [`FactMerged`](Curation.cs), [`PendingReviewItem`](Curation.cs).

**Enums** (in [`Enums.cs`](Enums.cs) and [`EventPayloads.cs`](EventPayloads.cs))
- `EpistemicCategory` (7), `EventChannel`, `Classification`, `CurationType` (7),
  `WorkstreamMode`, `PinScope`, `HypothesisState`, `GoalState`, `OutcomePolarity`.

### Verification

Run from the repository root:

```pwsh
dotnet build src/Mneme.Contracts/Mneme.Contracts.csproj
dotnet test  tests/Mneme.Contracts.Tests/Mneme.Contracts.Tests.csproj
```

136 tests cover identifier equality, enum stability, payload polymorphism
(JSON round-trip), capability/curation defaults, exception subtyping,
bundle/query round-trips, and a reflection-based surface invariant that
fails the build if a non-record/non-interface/non-enum/non-exception type
is ever added to this assembly.

## Design rules

- **No implementation code here.** Only interfaces, records, enums.
- **No dependencies beyond .NET 8 BCL.** This must be lightweight enough
  to pull into any agent host without dragging the world.
- **No raw SQL exposed.** Even at the contract level, queries are typed
  spec objects — never strings.
- **Backward compatibility from v0.1.0+.** Breaking changes only at major
  version bumps with migration notes.
