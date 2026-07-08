# Mneme Backlog — dependency-ordered task list

> Derived from [`plan.md`](plan.md). Each task maps to a single PR-sized
> unit of work. When picking up work, find the first task with all
> dependencies ✅ done and no other agent claiming it. Update this file
> in the same PR that lands the work — mark `[ ]` → `[x]` and add a
> link to the merge commit.
>
> **Phase numbering matches** [`../README.md`](../README.md) roadmap and
> the section structure of [`plan.md`](plan.md). Keep them in sync.

## Status legend

- `[ ]` — not started
- `[~]` — in progress (mention agent / branch / PR in the line)
- `[x]` — done (link the merge commit)
- `[!]` — blocked (note the blocker)

---

## Phase 0 — Contracts (interfaces + DTOs)

Goal: ship `Mneme.Contracts` v0.1.0 to NuGet. Pure .NET 8 BCL.
No implementation, no SQLite, no MCP. **Required before anything else.**

- [x] **contracts-assembly** — Stand up `Mneme.Contracts` project. *Done at scaffold time; project exists with packaging metadata.*
- [~] **arch-capture-event-schema** *(shipped to working tree, pending commit — see `src/Mneme.Contracts/CaptureEvent.cs`)* — Define `CaptureEvent` envelope:
  `EventId` (ULID), `WorkstreamId`, `Source`, `Type`, `Timestamp`,
  `SchemaVersion`, `Payload` (typed union), `Provenance`. Document
  versioning policy.
- [~] **arch-capability-token** *(shipped to working tree, pending commit — see `src/Mneme.Contracts/CapabilityToken.cs`)* — Define `CapabilityToken`:
  workstream-scoped, permitted query categories, cross-workstream grant
  flags, `IncludeTechnical` flag (see `contracts-event-channels`).
  Every `IMemoryQueryAPI` call requires one.
- [~] **arch-imemoryagent** *(shipped to working tree, pending commit — see `src/Mneme.Contracts/IMemoryAgent.cs`)* — Define `IMemoryAgent`:
  `IngestAsync(CaptureEvent, CancellationToken)`. Backpressure
  semantics documented in XML doc + interface comment. **Sync stage
  contract**: returns after WAL commit (<50ms); distillation runs
  asynchronously (see `mem-distillation-job-abstraction`).
- [~] **arch-imemoryquery** *(shipped to working tree, pending commit — see `src/Mneme.Contracts/IMemoryQueryAPI.cs`)* — Define `IMemoryQueryAPI`:
  `QueryAsync(spec, token, ct)`, `DistillAsync(workstream, token, ct)`,
  and `ListRecentAsync(workstream, limit, token, ct)`. No raw-SQL
  escape. Capability-checked at every call. `QueryRequest` includes
  `Explain: bool` flag (see `mem-query-api-explain-flag`).
- [~] **contracts-event-categories** *(shipped to working tree, pending commit — `EpistemicCategory` in `src/Mneme.Contracts/Enums.cs`; 7 payload records in `src/Mneme.Contracts/EventPayloads.cs`)* — Enum + base records for the 7
  epistemic categories (Evidence, Facts, Decisions, Hypotheses, Goals,
  Actions, Outcomes). See `plan.md` "Seven epistemic categories".
- [~] **contracts-event-channels** *(shipped to working tree, pending commit — `EventChannel` in `src/Mneme.Contracts/Enums.cs`; default in `QuerySpec.Channel`)* — Add `EventChannel` enum
  (`Epistemic | Technical`) so workflow-checkpoints and similar
  non-epistemic events (e.g., from `MnemeCheckpointStore`) don't
  pollute the 7 epistemic categories. Default queries filter to
  `Epistemic`. See `research-design-lessons.md` §2.15.
- [~] **contracts-query-spec** *(shipped to working tree, pending commit — see `src/Mneme.Contracts/QuerySpec.cs` + `QueryResult.cs`)* — `QuerySpec` DTO: filters by
  workstream, category, channel, time range, free-text, entity.
  Designed so capability check is unambiguous from inputs.
- [~] **contracts-distillation-bundle-reshape** *(shipped to working tree, pending commit — see `src/Mneme.Contracts/ContextBundle.cs`)* — `ContextBundle`
  shape: `BundleIndex` (always-loadable, 500-1000 tokens) +
  `BundleSection[]` (on-demand, 2-4k tokens each) +
  `OrientationSummary` (single paragraph prepend) + `LookupHints`
  (keyword pointers to original events). Per-section staleness:
  `GeneratedAt`, `EventsCoveredThrough`, `IsStale`, `Distiller`,
  `TokenBudget`, `TokenCount`, `Provenance`. See
  `research-design-lessons.md` §3.1.
- [~] **arch-imemorycurator** *(shipped to working tree, pending commit — see `src/Mneme.Contracts/IMemoryCurator.cs`)* — Define `IMemoryCurator` interface:
  `AmendFactAsync`, `AnnotateAsync`, `PinAsync`, `DemoteAsync`,
  `SplitFactAsync`, `MergeFactsAsync`, `RevertCurationAsync`. Every
  mutation takes a `CurationCapability` token. Mutating operations
  (`amend`, `split`, `merge`) require a `preStateHash` parameter for
  the stale-state guard. See `plan.md` "Human-in-the-loop curation".
- [~] **arch-curationcapability** *(shipped to working tree, pending commit — see `src/Mneme.Contracts/CurationCapability.cs`)* — Define `CurationCapability` token
  type: principal, workstream scope, time window, per-operation
  permission flags (`CanAmend`, `CanRevoke`, `CanPin`, `CanSplit`,
  `CanMerge`, `CanReview`). Separate token type from
  `CapabilityToken` (read/ingest); a principal needs both for full
  curation rights. See `plan.md` "Human-in-the-loop curation".
- [~] **arch-curation-event-types** *(shipped to working tree, pending commit — `CurationType` enum in `src/Mneme.Contracts/Enums.cs`; payload records in `src/Mneme.Contracts/Curation.cs`)* — Define event records for the
  curation event family: `fact.amended`, `fact.annotated`,
  `fact.pinned`, `fact.demoted`, `fact.split`, `fact.merged`,
  `curation.reverted`. All flagged `IsCurationAction = true` so the
  `CurationLog` projection can filter them efficiently. All carry
  `Curator` (principal id), `Rationale` (free text), and (where
  applicable) `PreStateHash`. See `plan.md` "Human-in-the-loop
  curation".
- [~] **arch-icurationlog** *(shipped to working tree, pending commit — see `src/Mneme.Contracts/ICurationLog.cs`)* — Define `ICurationLog` query interface:
  `GetCurationHistoryAsync(workstream, since, token, ct)` and
  `GetCurationsByPrincipalAsync(principal, since, token, ct)`.
  Returns `CurationEntry` records with curator, rationale, target
  event id, pre/post state. Read-only (no mutation). Capability-
  checked. See `plan.md` "Human-in-the-loop curation".
- [~] **arch-ireviewqueue** *(shipped to working tree, pending commit — see `src/Mneme.Contracts/IReviewQueue.cs`)* — Define `IReviewQueue` interface for
  opt-in pre-distillation review: `GetPendingAsync`, `ApproveAsync`,
  `RejectAsync`, `DeferAsync`. A `WorkstreamMode` enum
  (`AutoDistill | ReviewBeforeDistill`) lives on the workstream
  metadata. Default is `AutoDistill`. See `plan.md` "Human-in-the-
  loop curation".
- [~] **contracts-tests** *(shipped to working tree, pending commit — 136 tests in `tests/Mneme.Contracts.Tests/` covering identifiers, enums, polymorphic payload round-trips, capability/curation defaults, exception subtyping, bundle/query round-trips, and a reflection-based surface invariant. `dotnet test` passes locally.)* — One test per public type in
  `tests/Mneme.Contracts.Tests/`. Minimum: type exists, properties
  round-trip via `System.Text.Json`. Proves the build+test pipeline.
- [ ] **agents-md-revisit-sk-decision** — Update `AGENTS.md`
  locked-decisions to replace `Semantic Kernel as planned
  indirection` with `Microsoft.Extensions.AI.IChatClient`. Cite
  `research-design-lessons.md` §2.15 + §4.7. **Note: completed at
  the same time as this backlog update; verify changes shipped.**
- [ ] **contracts-release-v0.1.0** — Tag, pack, push to NuGet. Update
  `CHANGELOG.md`. **DO NOT** publish until contracts have stabilized
  through at least one consumer integration; v0.1.0-alpha to NuGet is
  fine.

---

## Phase 1 — Event log + SQLite schema

Goal: persistent, idempotent, append-only ingest. No reads yet.

Dependencies: Phase 0 contracts.

- [ ] **mem-event-schema** — Materialize 7-category schema in C#
  (typed payloads, validation). Implements the `CaptureEvent.Payload`
  union from contracts. See `plan.md` "Per-event fields" and
  `research-zep-sqlite-deepdive.md §3.1`.
- [ ] **mem-store-tables** — Build `memory_events`, `memory_artifacts`,
  `memory_edges` SQLite tables. Bi-temporal: 4 timestamps
  (`valid_at`, `invalid_at`, `created_at`, `expired_at`). Translate
  Graphiti DDL — see `research-zep-sqlite-deepdive.md §3.1`. Use
  `Microsoft.Data.Sqlite`; **WAL mode**; foreign keys on. Add
  `event_channel` column (`Epistemic | Technical`); index on it.
- [ ] **mem-secret-redactor** — Regex-based redactor for API keys,
  tokens, passwords, AWS/Azure keys, GitHub PATs. Replaces with
  structure-preserving markers (e.g. `<REDACTED:aws-key>`). Pluggable
  rule set. Runs **inline at ingest** — non-bypassable. **Port
  Cognee `tracing.py:redact_secrets()` regex set verbatim**
  (`sk-…`, `bearer …`, `api[_-]?key…`, etc.); see
  `research-design-lessons.md` §3.2.
- [ ] **mem-ingest-async-split** — Confirm and document the sync
  ingest / async distillation split. `IMemoryAgent.IngestAsync`
  returns after WAL commit (<50ms target); validation, redaction,
  classification stub, and persist are synchronous; everything else
  is async. Add invariant test: ingest latency p99 < 50ms across
  a representative workload. See `research-design-lessons.md` §3.2
  + §4.2.
- [ ] **mem-ingest-path** — Implement `IMemoryAgent.IngestAsync`:
  validate → redact → classify (Phase 2 stub for now) → persist to
  event log. Idempotent on `event_id` (re-ingest = no-op). No
  distillation yet. Returns immediately after WAL commit; emits
  the event to the distillation queue.
- [ ] **mem-content-shapes** — Two storage strategies:
  `RedactedContent` (full body minus secrets) or
  `ReferenceWithSynopsis` (source pointer + sanitized synopsis).
  Decided at ingest time based on a quality envelope.
- [ ] **mem-workstream-id-validation** — Regex-validate every
  workstream-id parameter at the public API boundary (and at the
  MCP boundary in Phase 8) before any storage operation. Path-
  traversal guard pattern from Basic Memory
  `validate_project_path()`. See `research-design-lessons.md` §3.5.
- [ ] **obs-otel-baseline** — OpenTelemetry from Phase 1 with GenAI
  Semantic Conventions v1.37 attributes. Span names:
  `mneme.ingest.event`, `mneme.classify.run`, `mneme.redactor.run`,
  `mneme.entity.resolve`, `mneme.distill.run`,
  `mneme.projection.rebuild`, `mneme.query.execute`. Default off
  via `MnemeNullSpan` no-op (zero overhead in default builds).
  Apply `IRedactor` to span attribute values at write time (not log
  emission). See `plan.md` "Observability" + `research-design-
  lessons.md` §3.8.

---

## Phase 2 — Classification + revocation

Goal: every event gets a sensitivity label; artifacts are revocable.

Dependencies: Phase 1.

- [ ] **mem-llm-classifier** — Async (non-blocking ingest) classifier
  that labels content with one of: `secret` / `pii` /
  `customer_confidential` / `internal_confidential` / `public`.
  Uses **`Microsoft.Extensions.AI.IChatClient`** as the LLM
  abstraction (replaces earlier Semantic Kernel choice — see
  `research-design-lessons.md` §2.15 + §4.7). Labels are
  **metadata-only** — they never gate capture.
- [ ] **mem-revocation** — Revoke API zeroes the
  `memory_artifacts` blob and leaves `memory_events` metadata intact.
  Audit trail preserved. Satisfies "keep forever metadata" + legal /
  privacy revocation simultaneously.
- [ ] **mem-capability-token-ergonomic-api** — Ship `AddMneme(opts =>
  { opts.WorkstreamId = "..."; opts.SqlitePath = "..."; opts.UserId
  = "..." })` developer ergonomic that constructs the
  `CapabilityToken` internally for the single-workstream 90% case.
  Full `CapabilityToken` API remains available for cross-workstream
  scenarios. See `research-design-lessons.md` §3.5 + §4.9.

---

## Phase 3 — Projections (current-state views)

Goal: derived read-models that are rebuildable from the event log.

Dependencies: Phase 1 (storage), Phase 2 ok if classification stub).

- [ ] **mem-projections** — Read-models: `facts`, `goals`,
  `decisions`, `hypotheses`, `entity_index`, `decision_chains`
  (supersession links). Rebuildable from scratch; updated
  incrementally on new events.
- [ ] **mem-pipeline-status-table** — `event_processing_log(event_id,
  projection_name, status, processed_at)` for per-projection status
  tracking; enables selective re-projection of a single projection
  without full rebuild. Pattern from Cognee. See
  `research-design-lessons.md` §3.3.
- [ ] **mem-projection-snapshots** — Periodic full-row snapshots of
  projection tables (every N events or T hours) so rebuilds are
  O(1) from nearest snapshot instead of O(N) from genesis. Pattern
  from Letta `BlockHistory`. See `research-design-lessons.md` §3.6.
- [ ] **mem-text-index** — SQLite FTS5 over event content
  (post-redaction). Recency-weighted. Workstream-scoped. Bridges
  queries until vector search arrives in v2.
- [ ] **mem-text-index-adaptive-bm25** — Query-length-adaptive sigmoid
  normalization mapping raw FTS5 BM25 to [0,1]. Five parameter sets
  (query length 1-3, 4-6, 7-9, 10-15, 15+). Port Mem0
  `get_bm25_params` formula. See `research-design-lessons.md` §3.3.

---

## Phase 4 — Temporal graph + capability-checked query API

Goal: point-in-time queries with strict workstream isolation.

Dependencies: Phase 3.

- [ ] **mem-graph-projection** — Temporal graph: nodes + typed edges
  with `valid_from` / `valid_until`. Point-in-time queries via WHERE
  clauses on the 4 timestamp cols. Recursive CTE for BFS up to 5 hops.
  See `research-zep-sqlite-deepdive.md §3.2-3.3`.
- [ ] **mem-query-api-impl** — Implement `IMemoryQueryAPI` on top of
  projections. Every call validates `CapabilityToken` against requested
  workstream + categories + channel. Workstream-scoped by default;
  cross-workstream requires explicit grant. Technical-channel events
  excluded unless `IncludeTechnical = true` on the token. **No raw
  SQL path exposed.**
- [ ] **mem-query-api-explain-flag** — Implement `Explain: bool` on
  `QueryRequest`. When set, `QueryResult` includes `ScoreDetails`:
  per-signal contributions (semantic, BM25, entity-boost, filter,
  capability resolution), gate decisions, and final fused score.
  Critical for diagnosing workstream-isolation bugs and temporal-
  window mistakes. Pattern from Mem0 `explain=True`. See
  `research-design-lessons.md` §3.8.
- [ ] **mem-query-api-dispatcher** — Rule-based query router (regex
  → strategy). Quoted phrases → lexical FTS5 path; year ranges →
  temporal range query; "summarize…" → bundle synthesis;
  relationship questions → graph traversal. Pattern from Cognee
  `query_router.py`. Tests pin negation-window detection. See
  `research-design-lessons.md` §3.3.
- [ ] **mem-list-recent** — Implement `IMemoryQueryAPI.ListRecentAsync
  (workstream, limit, token, ct)`. Every memory MCP server has a
  way to dump/enumerate stored memories; needed so agents can
  avoid re-ingesting what they've already stored. See
  `research-design-lessons.md` §4.10.
- [ ] **mem-query-curation-weight-hook** — Retrieval scoring picks up
  pin/demote multipliers from the `entity_curation_weights`
  projection. Apply multiplier **after** the additive-with-gate
  fusion but **before** the threshold check (so a demoted fact
  must still pass the semantic threshold to be returned). Stub
  multiplier = 1.0 until Phase 7.5 lands curation events; this
  task wires the hook so the integration is mechanical when
  curation goes live. See `plan.md` "Human-in-the-loop curation".
- [ ] **mem-degraded-modes** — Memory agent failures must not block
  consumers. Approval gate persists locally + async-emits. Distillation
  falls back to "no synthesis available". Spool drains on recovery.

---

## Phase 4.5 — Benchmarks

Goal: publish credible LoCoMo + LongMemEval numbers, especially on the
temporal subcategory.

Dependencies: Phase 4 (queryable API).

- [ ] **mem-benchmark-locomo-longmemeval** — Run LoCoMo (long
  conversation memory) and LongMemEval (long-context fact tracking).
  Publish leaderboard numbers + harness scripts under
  `benchmarks/`. **Expected outcome**: Mneme wins the temporal
  subcategory of LoCoMo (bi-temporal model should architecturally
  beat single-timestamp competitors). If we don't beat Mem0 on
  temporal LoCoMo specifically, something is wrong with the
  implementation. See `research-design-lessons.md` §4.8.

---

## Phase 5 — Distillation pipeline (the primary value)

Goal: produce 2-4k-token decision-useful bundles instead of dumping 50k
tokens of raw events into consumer prompts.

Dependencies: Phase 4.

- [ ] **mem-distillation-job-abstraction** — `DistillationJob` record
  with status tracking (`created → running → completed | failed`),
  per-workstream attribution. Pattern from Letta `Run / RunStatus`.
  Surfaces via `IMemoryQueryAPI.GetDistillationJobAsync(jobId)`. See
  `research-design-lessons.md` §3.6.
- [ ] **mem-distillation-lock** — Per-workstream pessimistic SQLite
  lock for the distillation worker. Table
  `distillation_locks(workstream_id PRIMARY KEY, holder_id,
  acquired_at)` with `ON CONFLICT DO NOTHING`. Idle/quiet triggers
  and SessionEnd hooks both target the worker; lock deduplicates.
  Pattern from Cognee `try_acquire_improve_lock`. See
  `research-design-lessons.md` §3.6.
- [ ] **mem-distillation-pipeline-stages** — Explicit named stages
  (`classify → entity-resolve → category-bundle → synthesize →
  write-projection`) with per-stage `BatchSize` config and per-stage
  swappable implementations. Not a monolithic function. Enables
  per-stage prompt iteration. Pattern from Cognee
  `get_default_tasks()`. See `research-design-lessons.md` §3.3.
- [ ] **mem-distillation-extract** — LLM-driven extraction of
  structured facts/decisions/hypotheses from raw evidence.
  **ADD-only single-pass** — no simultaneous invalidation
  (reconciliation is a separate stage). **Port Graphiti prompts**
  `extract_nodes.py` + `extract_edges.py` with Apache 2.0
  attribution. **Update `NOTICE`** in the same commit. Record
  provenance per extracted node (source events + prompt hash). Uses
  `IChatClient` LLM abstraction.
- [ ] **mem-distillation-prompt-observation-date** — Port Mem0
  Observation-Date / Current-Date dual-anchor prompting verbatim
  (`prompts.py:528-536`). Every extraction/distillation prompt is
  told both dates with explicit instruction to resolve relative
  references against `ObservationDate` only. Improves `valid_at`
  accuracy at zero runtime cost. See `research-design-lessons.md`
  §3.2.
- [ ] **mem-distillation-prompt-transitions** — Port Mem0 "capture
  transitions, not just states" prompt instruction
  (`prompts.py:611-622`). Distillation prompts instruct the model
  to capture state transitions ("switched from X to Y after Z")
  rather than only the latest state. Maps onto Mneme's
  Decisions / Hypotheses / Outcomes arc.
- [ ] **mem-distillation-integer-id-anti-hallucination** — When
  extraction/distillation prompts embed existing event/fact IDs,
  pass sequential integers (`0, 1, 2, ...`) as handles; map back to
  ULIDs after the LLM call. Prevents LLM ID hallucination. Pattern
  from Mem0 `main.py:715-722`. Wrap as `PromptIdMapper<T>` helper
  in `Mneme/Llm/Prompting/`. See `research-design-lessons.md` §3.2.
- [ ] **mem-distillation-bundle** — `DistillAsync(workstream)` returns
  a workstream `ContextBundle` per the two-tier shape: `BundleIndex`
  + `BundleSection[]` + `OrientationSummary` + `LookupHints`.
  Target ~2-4k tokens per section; bundle total enforced post-
  composition using the LLM tokenizer. See
  `contracts-distillation-bundle-reshape`.
- [ ] **mem-distillation-orientation** — `WorkstreamOrientationSummary`
  single-paragraph "where are we" prepend generated atop the
  `BundleIndex`. Orients the consuming LLM before detailed bullets.
  Pattern from Cognee `GlobalContextSummary`. See
  `research-design-lessons.md` §3.1.
- [ ] **mem-distillation-lookup-hints** — `LookupHints` section in
  bundle: short keyword pointers ("topic and key terms") to original
  event-log entries for facts that didn't fit. Consumers re-query
  for detail. Pattern from Letta compaction prompt. See
  `research-design-lessons.md` §3.1.
- [ ] **mem-distillation-staleness-indicator** — Every `ContextBundle`
  carries `GeneratedAt`, `EventsCoveredThrough`, `IsStale`;
  `DistillAsync` accepts `ForceRefresh: bool` to bypass cache and
  synthesize fresh. Without these consumers cannot reason about
  context freshness across the sync/async split. See
  `research-design-lessons.md` §4.2.
- [ ] **mem-distillation-rationale** — For any Decision event,
  synthesize "why was this approved/rejected" from supporting evidence
  + chat history. Surfaces in decision-history view.
- [ ] **mem-distillation-cross-loop** — Detect patterns across
  workstreams (with consent) that compress to higher-level facts.
  Conservative — surfaces as suggestions, never auto-promoted.
- [ ] **mem-reconciliation-worker** — Async reconciliation pass that
  proposes invalidations of superseded facts through the propose-
  then-confirm pipeline. **Never** synchronous in ingest's LLM call
  (this is the Mem0 v2→v3 lesson; +20 LoCoMo points from dropping
  sync invalidation). See `research-design-lessons.md` §3.2 + §4.2.
- [ ] **mem-distillation-honor-curation** — Distillation worker MUST
  honor `fact.pinned` (always include in next bundle, regardless of
  recency or score) and `fact.demoted` (route to `LookupHints`
  rather than main `BundleSection[]` unless directly queried). Read
  from the `entity_curation_weights` projection. See `plan.md`
  "Human-in-the-loop curation".
- [ ] **mem-distillation-review-queue-gate** — When a workstream is
  in `WorkstreamMode.ReviewBeforeDistill`, the distillation worker
  skips events flagged pending review until `IReviewQueue
  .ApproveAsync` is called. Default mode is `AutoDistill` (no
  behavior change). See `plan.md` "Human-in-the-loop curation".

---

## Phase 6 — Conservative entity resolution (three-tier)

Goal: identity correctness without the Graphiti failure mode of LLMs
silently merging unrelated entities.

Dependencies: Phase 3 (projections). Tier 2 additionally requires Phase 11.

- [ ] **mem-entity-resolution-deterministic** — Tier 1: auto-merge
  via UUID5 from a fixed namespace + per-identity-type
  canonicalization spec (emails: lowercase + dot-strip for gmail;
  GitHub login: as-is; Stripe/Linear/Slack IDs: as-is; names:
  lowercase + whitespace-collapse, but names alone NEVER auto-merge).
  Port Cognee `cognee/infrastructure/engine/models/DataPoint.py
  :_generate_identity_id` mechanism. SQL pattern in
  `research-zep-sqlite-deepdive.md §3.4`. **Stricter than Graphiti**
  (which auto-merges on LLM judgment alone). See
  `research-design-lessons.md` §3.4.
- [ ] **mem-entity-resolution-embedding** — Tier 2: embedding
  similarity ≥0.95 cosine for candidates without a deterministic
  key. Threshold matches Mem0 `main.py:919`. **Blocked on Phase 11
  (vector search)** — no-op stub in v1. See
  `research-design-lessons.md` §3.4.
- [ ] **mem-entity-resolution-llm-propose** — Tier 3: port Graphiti
  `dedupe_nodes.py` prompt (Apache 2.0 — update `NOTICE`). LLM
  scores possible merges; high-confidence proposals surface to a
  human for confirm via Mneme's propose-then-confirm pipeline
  (or MCP elicitation in Phase 8). **Never auto-merges on LLM
  judgment alone.** All merges recorded as events.
- [ ] **mem-entity-resolution-stale-proposal-guard** — Confirmation
  API re-cites pre-merge canonical names exactly; mismatch returns
  `StaleProposalError`. Prevents confirmations against stale
  proposals when the underlying entity has changed. Pattern from
  Letta `core_memory_replace:base.py:262-280`. See
  `research-design-lessons.md` §3.4.
- [ ] **mem-entity-popularity-dampening** — Apply quadratic weight
  `1 / (1 + 0.001 * (n-1)^2)` where n is the mention count.
  Prevents widely-shared entities ("john.smith") dominating fuzzy
  matches forever. Pattern from Mem0 `main.py:1515-1517`. Cite
  Mem0 in implementation comment. See `research-design-lessons.md`
  §3.3.

---

## Phase 7 — Outcome closure

Goal: close the Action → Decision → Outcome loop so learning compounds.

Dependencies: Phase 3 (projections).

- [ ] **mem-outcome-closure** — Per-source watchers (PR merged, ticket
  closed, email replied) auto-emit Outcome events. Human can mark
  manually too. Outcomes link back to Action → Decision.
- [ ] **mem-outcome-closure-feedback-weights** — When an Outcome
  closes a Decision, update `feedback_weight` on linked Evidence
  records: `feedback_weight += alpha * (score - 0.5)` where score
  reflects positive/negative outcome. Cognee's `improve()` pattern
  adapted to Mneme. Surfaces learning over time in retrieval
  ranking. See `research-design-lessons.md` §3.3.

---

## Phase 7.5 — HITL Curation surface (Mneme differentiator)

Goal: turn Mneme from "you can correct memory" (true today via revoke
+ re-ingest) into "Mneme is *designed* for curation as a first-class
workflow" (genuine differentiator vs. Mem0/Letta/Cognee/Zep).

Dependencies: Phase 0 (curation contracts), Phase 1 (event log handles
the new event types natively — no special case needed), Phase 3
(projections), Phase 7 (outcome closure proves the pattern).

See `plan.md` "Human-in-the-loop curation (Phase 6.5 — Mneme
differentiator)" for the full design.

- [ ] **mem-curator-amend** — Implement
  `IMemoryCurator.AmendFactAsync`. Appends a `fact.amended` event
  carrying the new content, the old fact's content hash
  (`PreStateHash`), and the curator's rationale. Old fact is
  superseded but remains queryable bi-temporally (`as_of` queries
  return the old content for pre-amend timestamps). Returns
  `StaleProposalError` if the cited `PreStateHash` doesn't match
  current state. Pattern from Letta `core_memory_replace`
  (`base.py:262-280`). See `research-design-lessons.md` §3.4.
- [ ] **mem-curator-annotate** — Implement
  `IMemoryCurator.AnnotateAsync`. Appends a `fact.annotated` event
  attaching human commentary to a target event. Annotations are
  surfaced in `QueryResult.Annotations` alongside the target. Pure
  metadata — does not change the target's content or score.
- [ ] **mem-curator-pin** — Implement `IMemoryCurator.PinAsync`.
  Appends a `fact.pinned` event with a weight multiplier (default
  2.0) and a `PinScope` (`Workstream | Global`). Projector writes
  the multiplier to `entity_curation_weights(event_id,
  workstream_id, multiplier, source_event_id)`.
- [ ] **mem-curator-demote** — Implement `IMemoryCurator.DemoteAsync`.
  Appends a `fact.demoted` event with a weight multiplier (default
  0.3). Same projection as pin. The two events compose: a later
  `fact.demoted` overrides an earlier `fact.pinned` (and vice
  versa). Revert via `RevertCurationAsync` to restore the prior
  multiplier.
- [ ] **mem-curator-split** — Implement
  `IMemoryCurator.SplitFactAsync`. Appends a `fact.split` event
  declaring N replacement facts for one source fact. Projector
  marks source as `superseded_by_split` and creates N new fact
  rows. All N new facts share the source's `valid_at` (bi-temporal
  honesty: we now know the source was actually N separate claims as
  of that observation date). Requires `PreStateHash`.
- [ ] **mem-curator-merge** — Implement
  `IMemoryCurator.MergeFactsAsync`. Appends a `fact.merged` event
  declaring one target fact for N source facts. Projector marks
  sources as `superseded_by_merge` and creates the merged fact.
  Merged fact's `valid_at` = earliest source `valid_at`.
  Distinct from `entity.merged` (Phase 6 — that's identity-level;
  this is claim-level). Requires `PreStateHash` (hash spans all N
  source facts).
- [ ] **mem-curator-revert** — Implement
  `IMemoryCurator.RevertCurationAsync`. Appends a
  `curation.reverted` event recording the curation-event-id being
  reverted and the curator's rationale. Projector inverts the
  effect: a reverted `fact.amended` restores the prior content; a
  reverted `fact.pinned` restores the prior multiplier (which may
  itself be a prior `fact.demoted` value); a reverted `fact.split`
  reinstates the source and removes the split children. **No
  further `RevertCurationAsync` on a `curation.reverted` event** —
  to re-curate, issue a fresh curation event instead. Prevents
  pathological revert chains.
- [ ] **mem-curation-stale-state-guard** — Cross-cutting helper:
  `ComputePreStateHash(EventId)` computes the canonical hash of an
  event's current content + applicable curation overrides. Every
  curator API that takes `PreStateHash` validates via this helper
  before appending the curation event. Pattern from Letta
  `core_memory_replace`; see `research-design-lessons.md` §3.4 +
  §4.4. Test: concurrent amend on the same fact produces exactly
  one success + one `StaleProposalError`.
- [ ] **mem-curation-log-projection** — Build the `curation_log`
  projection table: `(event_id PK, curator_id, target_event_id,
  curation_type, rationale, occurred_at, pre_state_hash,
  workstream_id)`. Populated by projector from all
  `IsCurationAction = true` events. Implements `ICurationLog`
  (`arch-icurationlog`).
- [ ] **mem-curation-bi-temporal-amend** — When `fact.amended` lands,
  the projector creates a new `facts` row with
  `recorded_at = NOW()` but `valid_at = source.valid_at`. The
  prior row's `valid_until` stays open in `as_of` queries
  (bi-temporal honesty: we still know what we believed before).
  Test fixture: query at `as_of < amend_time` returns the original;
  query at `as_of >= amend_time` returns the amended. See
  `memory-systems-primer.md` §7.
- [ ] **mem-review-queue-table** — Schema:
  `review_queue(event_id PK, workstream_id, captured_at, status
  TEXT CHECK(status IN ('pending', 'approved', 'rejected',
  'deferred')), reviewer_id, reviewed_at, defer_until, rationale)`.
  Workstreams in `ReviewBeforeDistill` mode insert here; the
  distillation worker filters them out until status = approved.
- [ ] **mem-review-queue-api** — Implement `IReviewQueue`
  (`arch-ireviewqueue`). Approve appends an `event.review_approved`
  technical event so audit shows the human's go-ahead. Reject
  appends `event.review_rejected` and tombstones the source
  (calls into `mem-revocation`).
- [ ] **mem-curation-capability-tests** — One test per
  `CanAmend / CanRevoke / CanPin / CanSplit / CanMerge / CanReview`
  flag: a `CurationCapability` with the flag unset → operation
  returns `CapabilityDeniedError`. Critical because this is the
  privilege boundary for memory-mutating operations.
- [ ] **mem-curation-otel-spans** — Span taxonomy under
  `mneme.curate.*`: `mneme.curate.amend`, `mneme.curate.annotate`,
  `mneme.curate.pin`, `mneme.curate.demote`, `mneme.curate.split`,
  `mneme.curate.merge`, `mneme.curate.revert`. Tags: `curator.id`,
  `target.event_id`, `workstream.id`, `pre_state.matched`.
  Extends Phase 1 `obs-otel-baseline`.
- [ ] **mem-curation-bulk-operations** — *(v2; defer)* Bulk amend /
  demote / forget with a single `CurationCapability` check.
  Patterns: "demote everything classified `low-confidence` from
  session X", "merge these 10 entities". Required for serious
  cleanup workflows but not for v1.

---

## Phase 8 — MCP server interface

Goal: expose memory to any ACP / Copilot / Claude / Cursor client via
standard MCP tools.

Dependencies: Phase 4 (query API).

- [ ] **mem-mcp-server** — `Mneme.Mcp` ships an MCP server alongside
  the .NET `IMemoryQueryAPI`. Uses the
  [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).
  **Tool surface (community-vocabulary aligned)**: `remember` (→
  Ingest), `query` (kept; alias `recall` in description), `distill`
  (kept; differentiator), `improve` (→ dispatches to
  `IMemoryCurator` based on `operation` parameter — see
  `mem-mcp-improve-curator-dispatch`), `forget` (→ Revoke),
  `list_recent` (new).
  **Annotations explicit on every tool** — the C# SDK's
  `McpServerToolAttribute` defaults (`DestructiveDefault=true`,
  `OpenWorldDefault=true`) are wrong for `query`. Set all four
  annotation properties explicitly. See `research-design-lessons.md`
  §2.8 + §3.7 + §4.5.
- [ ] **mem-mcp-tool-descriptions** — Tool descriptions written as
  implicit system-prompt guidance to the calling LLM about when to
  call (prompt-injection-strength text, e.g.
  `query: "Call before responding to questions that may benefit
  from prior context. Use `since` parameter to scope to recent
  decisions."`). Pattern from Mem0 `search_memory` description. See
  `research-design-lessons.md` §3.1.
- [ ] **mem-mcp-prompts** — `mneme_context` MCP prompt (Claude Desktop
  `/mneme_context` slash command, VS Code Copilot). Accepts
  workstream + scope arguments; returns a pre-distilled bundle as
  structured prompt content. See `research-design-lessons.md` §2.8.
- [ ] **mem-mcp-resources** — `mneme://workstream/{id}/context`
  subscribable MCP resource. Push `notifications/resources/updated`
  after background distillation completes. None of the surveyed
  servers ship subscribable evolving context — this is a Mneme
  differentiator. **Also expose**
  `mneme://workstream/{id}/curation-log` and
  `mneme://workstream/{id}/review-queue` as subscribable resources
  so a curation UI (cockpit) can render live updates.
  See `research-design-lessons.md` §2.8.
- [ ] **mem-mcp-improve-curator-dispatch** — Wire the `improve` MCP
  tool to dispatch to the relevant `IMemoryCurator` operation based
  on an `operation` parameter (`amend`, `annotate`, `pin`,
  `demote`, `split`, `merge`, `revert`). Pull the
  `CurationCapability` from the request context (env var in stdio
  mode; JWT claim in HTTP mode — see `mem-mcp-auth-token-flow`).
  Tool description steers the calling LLM: *"Use `improve` to
  correct, annotate, or reweight memories the user has flagged as
  wrong or stale. Always elicit user confirmation before destructive
  changes (`amend`, `split`, `merge`)."*
- [ ] **mem-mcp-elicitation-curation** — When an agent wants to
  curate but cannot confirm a destructive change unilaterally,
  trigger `elicitation/create` with the proposed
  amend/split/merge; on user confirmation, call the relevant
  `IMemoryCurator` op with the confirmed payload. Extends
  `mem-mcp-elicitation` (entity-merge) to cover the full curation
  surface. See `plan.md` "Human-in-the-loop curation".
- [ ] **mem-mcp-elicitation** — Propose-then-confirm via
  `elicitation/create` when stateful HTTP transport is available.
  Collapses entity-merge confirmation into a single round-trip.
  Falls back to local propose-queue in stdio mode. See
  `research-design-lessons.md` §2.8 + §3.4.
- [ ] **mem-mcp-sampling-mode** — Distillation via
  `sampling/createMessage` when `thisServer.ClientCapabilities?
  .Sampling != null` — sends structured fact bundle as
  `systemPrompt`; client's LLM synthesizes. Falls back to local
  `IChatClient` if absent. Makes Mneme model-agnostic by design.
  See `research-design-lessons.md` §2.8 + §3.7.
- [ ] **mem-mcp-tool-task-support** — Mark `distill` with
  `TaskSupport=Optional` once the SDK's experimental flag
  stabilizes. Distillation is multi-second; sync calls will time
  out at ~30s in most MCP clients. Emit
  `IProgress<ProgressNotificationValue>` progress notifications via
  the SDK's auto-injected `IProgress<>`. See
  `research-design-lessons.md` §2.15.
- [ ] **mem-mcp-skills-source** — Expose `skill://index.json` for
  MCP SEP-2640 agent skill discovery. Declared skills like
  `recall-decision-rationale`, `summarize-workstream`. Pattern from
  MAF `AgentMcpSkillsSource`. See `research-design-lessons.md`
  §2.15.
- [ ] **mem-mcp-auth-token-flow** — Two deployment modes split:
  `Mneme.Mcp.Stdio` reads capability token from
  `MNEME_CAPABILITY_TOKEN` env var (Claude Desktop, no elicitation,
  no sampling); `Mneme.Mcp.Http` validates JWT Bearer claim via
  `AddJwtBearer + RequireAuthorization()` (multi-client, stateful,
  full elicitation + sampling). See `research-design-lessons.md`
  §2.8 + §3.5.

---

## Phase 8.5 — `Mneme.Agents.AI` MAF integration package

Goal: drop-in integration with Microsoft Agent Framework so MAF
agents can use Mneme as their memory context provider with five
lines of setup.

Dependencies: Phase 4 (query API).

- [ ] **mneme-maf-integration** — Ship `Mneme.Agents.AI` NuGet
  package implementing `MnemeContextProvider :
  MessageAIContextProvider` (from
  `Microsoft.Agents.AI.Abstractions`). `ProvideMessagesAsync`
  returns Mneme's distillation bundle as a single
  `ChatMessage(ChatRole.System, bundle.ToMarkdown())`.
  `StoreAIContextAsync` ingests `RequestMessages` +
  `ResponseMessages` back into Mneme. State surviving session
  serialization lives in
  `AgentSession.StateBag.GetValue<MnemeState>("MnemeContextProvider")`.
  Capability token read from `AIAgent.CurrentRunContext?.RunOptions?
  .AdditionalProperties["mneme:capability-token"]`. See
  `research-design-lessons.md` §2.15.
- [ ] **mneme-maf-checkpoint-store** — `MnemeCheckpointStore :
  ICheckpointStore<JsonElement>` shipping in the same package.
  Backs MAF workflow checkpoints with Mneme's append-only
  `memory_events` log using `EventChannel = Technical`. Three-method
  interface (`RetrieveIndexAsync`, `CreateCheckpointAsync`,
  `RetrieveCheckpointAsync`). See `research-design-lessons.md` §2.15.
- [ ] **mneme-maf-demo** — Demo project under `samples/MAF.Demo/`
  showing five-line setup; README comparing against
  `Microsoft.Agents.AI.Mem0` package. See
  `research-design-lessons.md` §2.15.
- [ ] **mneme-maf-purview-integration** — Investigate
  `Microsoft.Agents.AI.Purview` for governance/data-classification
  hooks relevant to Mneme's capability-token enforcement. May
  defer to v2. See `research-design-lessons.md` §2.15.

---

## Phase 9 — Sidecar deployment

Goal: option to run Mneme in its own process (gRPC) instead of in-proc.

Dependencies: Phase 4.

- [ ] **mem-sidecar-host** — Separate-process host with gRPC contract
  over `IMemoryAgent` + `IMemoryQueryAPI`. Same SQLite file shared
  (WAL mode permits this). Adds the option without removing the
  in-process embedding mode.

---

## Phase 10 — Cloud snapshot sync

Goal: optional local-first cloud sync; merge correctness via idempotency.

Dependencies: Phase 1 (event log).

- [ ] **mem-sync-snapshot** — Idempotent append-only merge via ULID
  `event_id` (**NOT** last-write-wins). S3-compatible target with
  user-provided credentials. Snapshot upload + delta sync. Default
  off; user-opted-in per workstream.

---

## Phase 11 — v2 features (deferred)

Don't start until v1 (Phases 0-10) has at least one shipping consumer.

- [ ] **mem-vector-search** — Embedding pipeline + `sqlite-vec`
  extension. Same SQLite file. Schema designed to allow embedding
  columns post-hoc without migration. Defer until `sqlite-vec` is ≥
  v1.0. **Scoring**: implement Mem0 v3 additive-with-threshold-gate
  fusion (`(semantic + bm25 + entity_boost) / max_possible` with
  hard semantic threshold ≥0.1). See `research-design-lessons.md`
  §3.3.
- [ ] **mem-vector-search-normalization-tests** — Pin the [0,1]
  higher-is-better normalization invariant via test fixtures.
  Multiple Mem0 backends shipped returning distance (lower = better)
  instead of similarity — PR #5391 fixed across all. Mneme's vector
  backend must pass these tests before merge. See
  `research-design-lessons.md` §3.3.
- [ ] **mem-vector-benchmark** — Benchmark sqlite-vec at 1M, 5M, 10M
  embeddings to establish the empirical upper bound. Required
  before committing to Mneme's vector substrate at scale. Without
  this, "v2 vector search" is a hand-wave. See
  `research-design-lessons.md` §4.3.
- [ ] **mem-autonomous-capture** — Heuristic capture beyond explicit
  emission set (decision-adjacency + novelty + human-anchors). Review
  queue surfaces uncertain captures for human approval.

---

## Phase 12 — Subject-attributed knowledge graph

Motivation: a controlled LoCoMo diagnosis (see
`benchmarks/Mneme.Benchmarks.LoCoMo/ANALYSIS.md`, Experiments 1–7)
showed the adversarial/multi-hop gap is **not** primarily a recall
problem — ~50% of misses have the gold fact already in the top-25
context and the model still abstains, because the fact is
**attributed to the wrong entity** (a distractor about another
person). Statement-level facts name every speaker after pronoun
resolution, so "facts mentioning X" ≠ "facts about X". The fix is a
subject-attributed index: `(subject, predicate, object)` triples where
the subject is the specific entity the fact is about.

Shipped (infrastructure, all tested):

- [x] **kg-contracts** — `FactTriple` DTO + optional
  `FactPayload.Triples` (BCL-only, null-default, append-only-safe;
  redactor scrubs triple subject/object inline). Query surface:
  `QueryRequest.SupplementSubjectTriples` → `QueryResult.SubjectTriples`
  (`SubjectTripleHit`).
- [x] **kg-storage** — `projection_fact_triples`
  (`subject_text`, `subject_key`, `subject_entity_id` (nullable),
  `predicate`, `object`, `valid_at`, `revoked_at`); schema v9→v10.
  Derived + rebuildable from `memory_events`.
- [x] **kg-projector** — `FactTriplesProjector` runs alongside
  `FactsProjector`; normalizes the subject surface form to a stable
  `SubjectKey`. `subject_entity_id` left null (names are Tier-1
  ineligible — full resolution is a later pass).
- [x] **kg-query** — subject-scoped retrieval in `MemoryQueryApi`:
  a `MnemeOptions.SubjectAttributionBoost` (default **off**) additive
  boost, and the append-only `SubjectTriples` answer-context supplement.

Findings (the levers, and what makes them work):

- Retrieval-side boost (Exp 6) **regressed** −3.8pp: within a fixed
  top-k window, promoting/injecting subject facts displaces semantic
  ones (the losing "replacement" shape). Kept behind
  `SubjectAttributionBoost` (default off).
- Answer-context supplement fed by *combined-distiller* triples
  (Exp 7) landed at the noise floor (net −2 / 186).
- Answer-context supplement fed by a **separate** triple-extraction
  pass (Exp 8) **won: +3.2pp overall, +3.5pp adversarial** (net +6 /
  186, gains concentrated in the target category). Triple *source
  quality* was the missing variable — a dedicated triple prompt yields
  more, and more precisely attributed, triples than a combined
  statement+triple prompt.

Done:

- [x] **kg-separate-extraction** — `LlmSessionDistiller` runs statement
  and triple extraction as two dedicated LLM calls (`--kg-triples`),
  attaching triples to the fact they most overlap by supporting entry.
  Validated the supplement win (Exp 8). The recommended host recipe:
  distill statements + triples in separate passes, then query with
  `QueryRequest.SupplementSubjectTriples: true`.
- [x] **kg-subject-entity-resolution** — `SubjectTripleResolver`
  post-projection pass binds distinct triple subjects to canonical
  entity ids via the Phase-6 `EntityResolver` (idempotent), stamping
  `subject_entity_id` so aliases/re-mentions unify when an embedding
  provider is wired.

Open (further, not blocking):

- [ ] **kg-entity-scoped-retrieval** — Once `subject_entity_id` is
  populated, match the supplement/boost on the resolved entity id
  (unifying aliases) rather than the surface-key `LIKE`, and resolve
  the *query's* subject to the same id space.

---

## Cross-cutting / nice-to-have (not blocking any phase)

- [x] **ci-github-actions** — Workflow that runs `dotnet build` +
  `dotnet test` on push to main and on PRs. Should fail on warnings.
  *(Shipped: `.github/workflows/ci.yml` — Release build with
  warnings-as-errors + test on push/PR to `main`.)*
- [ ] **release-automation** — Workflow that packs + pushes NuGet on
  tag. Skip until at least one Phase has a tagged release.
- [x] **docs-adr-index** — Set up `docs/adr/` with the
  [MADR template](https://adr.github.io/madr/). First ADR: "Why
  SQLite as the only embedded backend" (consolidate
  `research-zep-sqlite-deepdive.md §3` and `plan.md` notes). Second
  ADR: "`IChatClient` instead of Semantic Kernel" (consolidate
  `research-design-lessons.md` §2.15 + §4.7).
  *(Shipped: `docs/adr/` with template + ADR-0001 (SQLite), ADR-0002
  (`IChatClient`), ADR-0003 (host owns chat log).)*
- [ ] **plans-rebrand** — Sweep `plans/*.md` for any remaining
  MuxiMuxi-specific framing that should be re-cast as substrate-general.
- [x] **plans-renumber-alignment** — Reconcile `plan.md` "Sequencing"
  section's Phase 1-11 numbering with `backlog.md` Phase 0-11
  numbering (they're currently off-by-one — plan's Phase 1 = backlog's
  Phase 0). `AGENTS.md` says they must stay in sync. Pick one scheme
  and update README.md, plan.md, backlog.md together.
  *(Done 2026-06-24: `plan.md` "Sequencing" rewritten to the canonical
  Phase 0–11 scheme; stale Phase 6.5/Phase 11-MCP references fixed.)*
- [x] **benchmarks** — `BenchmarkDotNet` project under `tests/` for
  ingest throughput + query latency. Useful once Phase 1 lands.
  *(Shipped: `benchmarks/Mneme.Benchmarks.Perf/` — ingest + query-latency
  microbenchmarks. First run surfaced a ~150× single-category query
  regression, fixed in `MemoryQueryApi` via an equality predicate.)*
- [ ] **cross-cutting-workstream-export** — Single-file workstream
  export (event log + projections + artifacts for one workstream)
  for support cases, GDPR Article 20 data-portability, and migration
  between Mneme installations. Pattern from Marten / KurrentDB
  export tooling + Letta `.af` agent-file. See
  `research-design-lessons.md` §3.6.
- [ ] **cross-cutting-doctor-command** — `mneme bundle health
  --workstream X` CLI command reporting token usage per bundle
  section, stale bundles (where `EventsCoveredThrough` lags the
  workstream head significantly), projection drift, and entity-
  merge backlog. Developer-experience tool. See
  `research-design-lessons.md` §3.1.
- [ ] **cross-cutting-trace-buffer** — In-memory circular buffer of
  the last N (e.g., 50) distillation traces, exposed via
  `IMneme.GetLastDistillationTrace()` and `IMneme.GetAllTraces()`.
  Returns `{operation, total_duration_ms, span_count,
  breakdown_by_span_name, errors}`. Pattern from Cognee
  `CogneeSpanExporter`. Major developer ergonomics win for an
  embedded library. See `research-design-lessons.md` §3.8.

---

## Out of scope for this repo

These belong to **consumers** (e.g., MuxiMuxi cockpit), not Mneme:

- `ICaptureBus` / `ICaptureSource` (in-process plumbing on the
  cockpit side).
- Capture-event sources (file watchers, ACP listeners, MCP audit
  hooks). The cockpit emits `CaptureEvent`s; Mneme just ingests them.
- Null/stub implementations of contracts for cockpit-only development.

If you find yourself wanting to add any of those here, that's a sign
the design boundary needs revisiting — open an issue first.
