# Changelog

All notable changes to Mneme are recorded here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
once a stable release is cut. Pre-1.0 the public API may change between
minor versions; breaking changes will be called out in the relevant
release notes.

## [Unreleased]

_No unreleased changes yet._

## [0.1.0-alpha] - 2026-08-06

First public preview release. Packages: `Mneme`, `Mneme.Contracts` (folded
into `Mneme`), `Mneme.Agents.AI`, `Mneme.Mcp`.

### Security
- **Pre-publish security/architecture/SDL review — fixed 6 findings.** A
  parallel multi-agent review (security, architecture, SDL/privacy, correctness)
  of the shippable surface produced these fixes:
  - **Cross-principal Private disclosure via `SupplementSubjectTriples` (HIGH,
    the one publish blocker).** The subject-triple answer-context supplement
    (`MemoryQueryApi.LoadSubjectTripleSupplement`) filtered only by workstream +
    projection revocation — no visibility/principal/category/channel gate, unlike
    every primary read path. In a shared multi-agent workstream, a co-member who
    set the public `QueryRequest.SupplementSubjectTriples = true` flag could read
    another principal's **Private** (Confidential/PII) fact triples. Now joins the
    event + visibility sidecars and applies the same author-only gate + category/
    channel scope + live-revocation check as the ranked paths. Regression test:
    `SubjectScopedQueryTests.Subject_triple_supplement_enforces_author_only_private`.
  - **Dreamer/fleet load paths bypassed visibility gating.** The offline
    consolidation loaders (`DreamCoordinator.LoadEvents`/`LoadPriorSkills`/
    `LoadOpenContradictions`, `FleetConsolidator.LoadSkillEvents`, and
    `DerivedCitationResolver`) read raw events without the author-only Private
    filter, so another principal's Private events/skills could be fed to the
    dreamer LLM and re-emitted under the caller's principal (or, for fleet,
    laundered into the global library). All now apply the visibility+principal
    gate; the fleet miner refuses to mine non-shareable (Private) skills across
    the isolation boundary.
  - **Dream visibility override could raise a PII output above its ingest
    default.** `SetVisibility` unconditionally overwrote the ingest-time default,
    so a derived output whose *own* text contained PII (stamped Private at
    ingest) could be published Shared/Global when its sources were benign. The
    `ON CONFLICT` now keeps a Private ingest-default (`CASE WHEN existing = 0`),
    never raising an output above what its own classification earned — while
    still allowing benign→Global fleet promotion.
  - **Curation `RevertCuration` missing workstream-scope check.** A revert-capable
    token scoped to workstream A could revert curations in workstream B; it now
    applies the same `cap.Workstream` guard as every other curation op.
    Regression test: `SqliteMemoryCuratorTests.Revert_denies_a_capability_scoped_to_a_different_workstream`.
  - **MCP `remember` idempotency/ULID accuracy.** Blank `event_id` now generates a
    real (time-sortable) ULID instead of a GUID, and the tool description states
    that idempotency holds only for a caller-supplied id. New `Mneme.Util.Ulid`
    with tests.
  - **Fleet audit `started_at` recorded completion time** instead of start time;
    now captured before the dreamer runs.
- **Fixed: ingest secret redaction was a silent no-op in DI-wired hosts.**
  Registering the redactor as `TryAddSingleton<IRedactor, RegexRedactor>()` let
  the DI container pick `RegexRedactor`'s greedy `IEnumerable<RedactionRule>`
  constructor and resolve it to an **empty** rule set — so the inline secret
  redactor (locked decision #11) stripped nothing at ingest. Now registered via a
  factory (`_ => new RegexRedactor()`) so the default rule set is used. Affected
  `AddMneme`, `Mneme.Studio`, and `Mneme.Studio.Desktop`; verified with an
  end-to-end ingest-redaction regression test. Direct `new RegexRedactor()` usage
  (benchmarks, unit tests) was always correct, which is why this went unnoticed.

### Added
- **Cross-workstream "fleet" consolidation (Phase 14, ADR-0004):** a
  `FleetConsolidator` that mines the skills of every **opted-in** workstream for
  recurring patterns and promotes eligible results into a shared
  `Visibility.Global` skill library (default workstream `mneme-global-skills`).
  The one job that crosses the isolation boundary, so it carries every guardrail:
  opt-in only (`WorkstreamConfigStore.ListParticipatingWorkstreams`), a required
  cross-workstream token, a hard **classification floor** (a global skill is
  promoted only when every source event is Public/Internal — sensitive sources
  are skipped, never written as a sensitive global skill), plus re-redaction and
  `dream_runs` audit. Completes the Phase 14 dreaming roadmap. Tested
  (`FleetConsolidatorTests`).
- **Cross-session fact de-duplication (Phase 14, ADR-0004):** a
  `DuplicateFactsProjector` that records two non-revoked facts sharing a
  normalized statement (`LOWER(TRIM(...))`) in a new `memory_duplicates` review
  table — the signature of concurrent sessions/agents asserting the same thing.
  Propose-only (never auto-revokes or merges, honouring the conservative-
  resolution locked decision); the earlier fact is canonical. Entity-level merges
  continue to flow through the existing `entity_merge_proposals` pipeline. Schema
  v17→v18. Tested (`DuplicateFactsTests`).
- **Dreamer privacy guardrails (Phase 14, ADR-0004):** the operational
  guardrails around the consolidation worker. A per-workstream opt-in flag
  `participates_in_cross_workstream_consolidation` (default **false**, on
  `workstream_config`, via `WorkstreamConfigStore`) so a workstream is never
  cross-workstream-mined unless it explicitly opts in; a `DreamGuardrails` helper
  with the reusable **classification floor** (an output may reach shared/global
  visibility only when every source event is Public/Internal — unknown sources
  are ineligible); a capability-gated `DerivedCitationResolver` that filters a
  derived event's `Citation.Derived` sources to only those the caller's token may
  read (closing the cross-workstream citation back-channel); and a
  `DreamCoordinator.GetAuditTrail` surface over the `dream_runs` log. Schema
  v16→v17. Tested (`DreamGuardrailsTests`).
- **Offline consolidation / "dreaming" worker (Phase 14, ADR-0004):** a third
  host LLM seam `IDreamer` (events → derived events, symmetric to
  `ISessionDistiller` and `IDistiller`) plus a `DreamCoordinator` that loads a
  workstream's events, prior skills, and open contradiction candidates, runs the
  dreamer, and **direct-ingests** each output as a `Citation.Derived` event.
  Guardrails enforced by the coordinator: outputs are re-run through the ingest
  redactor, and each output's requested `Visibility` is **capped by the
  sensitivity of its source events** (any Confidential/Secret/Pii source forces
  the derived event to `Private`; only all-Public/Internal sources may reach
  `Global`). Every run is audited in a new `dream_runs` table. The coordinator is
  invoked by the host (Mneme owns the consolidation logic, not the scheduler);
  it's inert until an `IDreamer` is wired. Schema v15→v16. Tested
  (`DreamCoordinatorTests`).
- **Cross-agent contradiction detection (Phase 13, ADR-0004):** two currently-
  valid structured triples with the same `subject_key` + `predicate` but a
  different `object` are recorded as open candidates in a new
  `memory_contradictions` projection by a `ContradictionsProjector`, instead of a
  silent bi-temporal supersession (which assumes sequential observation). Narrow
  and deterministic (structured triples only, trim/case-insensitive object
  compare); candidates surface for human review and are never auto-resolved.
  Schema v14→v15. Tested (`ContradictionDetectionTests`).
- **Procedural memory / skills (Phase 14, ADR-0004):** a new `SkillPayload`
  ("how we reliably do X") with its own `projection_skills` read model and
  `SkillsProjector`. Skills ride in the append-only log under the `Evidence`
  category (so the seven epistemic categories stay locked) and the projector
  recognises them by payload type; typically ingested with a `Citation.Derived`
  provenance. Schema v13→v14. Tested (`SkillProjectionTests`).
- **Agent/role scope + data-subject primitive (Phase 13, ADR-0004):** an indexed
  `memory_events.principal_id` column (mirrors provenance author identity) plus a
  nullable `QuerySpec.Principal` filter. Scopes reads to a single agent/user
  within a shared workstream and makes "everything principal X authored" an
  O(index) query — the basis for data-subject access and erasure. Enforced across
  all read paths (structured, free-text, list-recent). Schema v11→v12.
- **Read-side visibility tier (Phase 13, ADR-0004):** a `Visibility`
  (`Private`/`Shared`/`Global`) dimension in a mutable `memory_visibility` sidecar
  (keyed by event id, so promotion never mutates the append-only log). Sensitive
  classes (`Pii`/`Confidential`/`Secret`) default to `Private` (author-only);
  everything else to `Shared`. `Private` events are readable only by their
  authoring principal — the PII-containment boundary in a shared workstream.
  Enforced across all read paths. Schema v12→v13.
- **`Citation.Derived(From[], ConsolidatorId)`** — a new provenance shape on
  the polymorphic `Citation` set for events derived from *other Mneme events*
  (the offline consolidation / "dreaming" pass), as opposed to a source signal.
  Keeps the audit chain intact and projections rebuildable, and lets
  consolidation operate over Mneme's own event log rather than raw transcripts
  (preserves the host-owns-the-chat-log invariant). First increment of Phase 14.
- **ADR-0004** (`docs/adr/0004-multi-agent-and-dreaming.md`, Proposed) —
  records the design for Phase 13 (multi-agent shared workstreams) and Phase 14
  (offline dreaming/consolidation), resolves the three locked-decision tensions
  (conservative entity resolution, seven epistemic categories, "never store raw
  turns"), and adds a **binding privacy & compliance section** for the
  single-user → multi-user shift (indexed subject access/erasure, PII-private-
  by-default visibility, and five consolidator guardrails).
- **`Mneme.Studio.Agent`** reference consumer (not shipped as a package): a
  Photino + Blazor desktop app that is an ACP client (`LibAcp`) driving GitHub
  Copilot over `copilot --acp`, using Mneme's own distillers (LLM = Copilot) to
  distill a turn-based conversation into epistemic memory. Includes LoCoMo-shaped
  corpus replay, per-memory reject (revocation), and a sleep/consolidate view.

- **Phases 0–12 implemented** across `Mneme.Contracts`, `Mneme`,
  `Mneme.Agents.AI`, and `Mneme.Mcp`: append-only bi-temporal SQLite event
  log, classification + revocation, projections + FTS5, capability-checked
  query API (Explain + AsOf), LLM distillation (`IDistiller` +
  `ISessionDistiller`), three-tier entity resolution, outcome closure, HITL
  curation, MCP server, MAF `MnemeContextProvider`, HTTP sidecar, and cloud
  snapshot sync.
- Semantic retrieval (`VectorIndex`, brute-force cosine) + hybrid
  semantic/BM25/recency query fusion; pluggable `IReranker` (6th host seam).
- **Subject-attributed knowledge graph (Phase 12):** `FactTriple` +
  `projection_fact_triples` + `FactTriplesProjector` + subject-scoped query
  boost + append-only `QueryResult.SubjectTriples` answer-context supplement +
  `SubjectTripleResolver`.
- LoCoMo benchmark harness with Mem0-aligned methodology; published results in
  `benchmarks/Mneme.Benchmarks.LoCoMo/RESULTS.md` (**89.6% Mem0-comparable at
  parity; 80.3% at ~9× less context**).
- CI workflow (`ci.yml`, build + test, warnings-as-errors) and a tag-driven
  **release workflow** (`release.yml`) that packs + pushes to nuget.org.
- ADR index (`docs/adr/`) and publishing runbook (`docs/PUBLISHING.md`).

### Performance
- Query hot paths batch candidate/subject-key lookups into single queries
  (in-memory gating unchanged) and the fact-triple projector reuses a prepared
  insert command — fewer allocations, better scaling. See
  `docs/pre-release-review-2026-07.md`.

### Documentation
- README rewritten as a product-facing readme (install, quickstart, packages,
  benchmark headline) with the phase-status board removed.
- New `docs/INTEGRATION.md` one-shot integration recipe (agent-friendly) and
  `docs/pre-release-review-2026-07.md` (perf + security review record).

### Security
- Sidecar bearer-token auth now uses a constant-time comparison
  (`CryptographicOperations.FixedTimeEquals`) instead of `string.Equals`,
  closing a timing side-channel on the auth path (pre-release review).
- Override the transitive `SQLitePCLRaw` stack (pulled by `Microsoft.Data.Sqlite`)
  to `bundle_e_sqlite3` **3.0.3**, which ships patched SQLite **3.50.4** and is
  out of the vulnerable range of **CVE-2025-6965** / GHSA-2m69-gcr7-jv3q
  (`SQLitePCLRaw.lib.e_sqlite3 <= 2.1.11`, high severity). Clears the NU1903
  audit warning for downstream consumers and fixes the underlying native SQLite
  memory-corruption risk while staying on the net8-friendly 8.x line of
  `Microsoft.Data.Sqlite`.

### Packaging
- Publishable packages: `Mneme` (library) and `Mneme.Agents.AI` (library), plus
  `Mneme.Mcp` (a `dotnet tool`, command `mneme-mcp`).
- `Mneme.Contracts` stays a separate csproj (BCL-only allowlist boundary) but its
  assembly is **folded into the `Mneme` package** — customers install one
  package for both contracts and implementation.
- `Mneme.Cli`, `Mneme.Sidecar`, and the `Mneme.Studio*` apps are not packable.
- Package version is centralized in `Directory.Build.props` and overridable
  from the release tag; every package embeds README, LICENSE, NOTICE, XML docs,
  a symbol package, and Source Link.

### Known limitations (v0.x)
These were surfaced by the pre-publish review and consciously deferred for the
alpha (tracked in `plans/backlog.md`); none affect the default single-agent
surface, and the multi-agent isolation invariants are enforced.
- **HITL `SplitFactAsync` / `MergeFactsAsync` are not yet implemented** — they
  throw on call. The other curation ops (amend, annotate, pin, demote, revert)
  are complete. Split/merge land in a Phase 7.5 follow-up.
- **Curation history is a rebuildable projection, not a main-log payload.**
  Curation ops are recorded in a `curation_events` table (with a `reverted_by`
  update on revert) rather than as append-only `memory_events` payloads. The
  main event log stays append-only and untouched by curation; a follow-up will
  move curation onto the main log per locked decision #13.
- **Erasure is content-tombstoning, not full purge.** `RevokeAsync` nulls the
  artifact body; distilled derivatives in `payload_json`, `projection_*`, and FTS
  are not yet cascaded. Full per-principal erasure (GDPR Art. 17) and per-
  workstream export (Art. 20) are planned.
- **Deep bi-temporal / sync items deferred:** superseded facts don't yet close
  `invalid_at`/`expired_at` intervals; storage timestamps are ISO-8601 text (not
  Unix-ms); snapshot sync doesn't carry principal/visibility or re-run
  projections. These are v0.x internal-format items and will change pre-1.0.

### Notes
- Pre-1.0 the public API may change between minor versions.
- First public release of `Mneme` / `Mneme.Agents.AI` / `Mneme.Mcp` at
  `0.1.0-alpha`. Published by pushing the `v0.1.0-alpha` tag (the release
  workflow derives the package version from the tag).
