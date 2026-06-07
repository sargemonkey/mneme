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
- [ ] **arch-capture-event-schema** — Define `CaptureEvent` envelope:
  `EventId` (ULID), `WorkstreamId`, `Source`, `Type`, `Timestamp`,
  `SchemaVersion`, `Payload` (typed union), `Provenance`. Document
  versioning policy.
- [ ] **arch-capability-token** — Define `CapabilityToken`:
  workstream-scoped, permitted query categories, cross-workstream grant
  flags. Every `IMemoryQueryAPI` call requires one.
- [ ] **arch-imemoryagent** — Define `IMemoryAgent`:
  `IngestAsync(CaptureEvent, CancellationToken)`. Backpressure
  semantics documented in XML doc + interface comment.
- [ ] **arch-imemoryquery** — Define `IMemoryQueryAPI`:
  `QueryAsync(spec, token, ct)` and `DistillAsync(workstream, token, ct)`.
  No raw-SQL escape. Capability-checked at every call.
- [ ] **contracts-event-categories** — Enum + base records for the 7
  epistemic categories (Evidence, Facts, Decisions, Hypotheses, Goals,
  Actions, Outcomes). See `plan.md` "Seven epistemic categories".
- [ ] **contracts-query-spec** — `QuerySpec` DTO: filters by
  workstream, category, time range, free-text, entity. Designed so
  capability check is unambiguous from inputs.
- [ ] **contracts-distillation-bundle** — `ContextBundle` DTO returned
  by `DistillAsync`: summary, supporting evidence refs, decision
  citations, confidence. ~2-4k token target documented.
- [ ] **contracts-tests** — One test per public type in
  `tests/Mneme.Contracts.Tests/`. Minimum: type exists, properties
  round-trip via `System.Text.Json`. Proves the build+test pipeline.
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
  `Microsoft.Data.Sqlite`; **WAL mode**; foreign keys on.
- [ ] **mem-secret-redactor** — Regex-based redactor for API keys,
  tokens, passwords, AWS/Azure keys, GitHub PATs. Replaces with
  structure-preserving markers (e.g. `<REDACTED:aws-key>`). Pluggable
  rule set. Runs **inline at ingest** — non-bypassable.
- [ ] **mem-ingest-path** — Implement `IMemoryAgent.IngestAsync`:
  validate → redact → classify (Phase 2 stub for now) → persist to
  event log. Idempotent on `event_id` (re-ingest = no-op). No
  distillation yet.
- [ ] **mem-content-shapes** — Two storage strategies:
  `RedactedContent` (full body minus secrets) or
  `ReferenceWithSynopsis` (source pointer + sanitized synopsis).
  Decided at ingest time based on a quality envelope.

---

## Phase 2 — Classification + revocation

Goal: every event gets a sensitivity label; artifacts are revocable.

Dependencies: Phase 1.

- [ ] **mem-llm-classifier** — Async (non-blocking ingest) classifier
  that labels content with one of: `secret` / `pii` /
  `customer_confidential` / `internal_confidential` / `public`.
  Pluggable LLM provider (separate from action model). Labels are
  **metadata-only** — they never gate capture.
- [ ] **mem-revocation** — Revoke API zeroes the
  `memory_artifacts` blob and leaves `memory_events` metadata intact.
  Audit trail preserved. Satisfies "keep forever metadata" + legal /
  privacy revocation simultaneously.

---

## Phase 3 — Projections (current-state views)

Goal: derived read-models that are rebuildable from the event log.

Dependencies: Phase 1 (storage), Phase 2 ok if classification stub).

- [ ] **mem-projections** — Read-models: `facts`, `goals`,
  `decisions`, `hypotheses`, `entity_index`, `decision_chains`
  (supersession links). Rebuildable from scratch; updated
  incrementally on new events.
- [ ] **mem-text-index** — SQLite FTS5 over event content
  (post-redaction). Recency-weighted. Workstream-scoped. Bridges
  queries until vector search arrives in v2.

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
  workstream + categories. Workstream-scoped by default;
  cross-workstream requires explicit grant. **No raw SQL path
  exposed.**
- [ ] **mem-degraded-modes** — Memory agent failures must not block
  consumers. Approval gate persists locally + async-emits. Distillation
  falls back to "no synthesis available". Spool drains on recovery.

---

## Phase 5 — Distillation pipeline (the primary value)

Goal: produce 2-4k-token decision-useful bundles instead of dumping 50k
tokens of raw events into consumer prompts.

Dependencies: Phase 4.

- [ ] **mem-distillation-extract** — LLM-driven extraction of
  structured facts/decisions/hypotheses from raw evidence. **Port
  Graphiti prompts** `extract_nodes.py` + `extract_edges.py` with
  Apache 2.0 attribution. **Update `NOTICE`** in the same commit.
  Record provenance per extracted node (source events + prompt hash).
- [ ] **mem-distillation-bundle** — `DistillAsync(workstream)` returns
  a workstream context bundle: summary + supporting evidence refs +
  decision citations + confidence. Target ~2-4k tokens.
- [ ] **mem-distillation-rationale** — For any Decision event,
  synthesize "why was this approved/rejected" from supporting evidence
  + chat history. Surfaces in decision-history view.
- [ ] **mem-distillation-cross-loop** — Detect patterns across
  workstreams (with consent) that compress to higher-level facts.
  Conservative — surfaces as suggestions, never auto-promoted.

---

## Phase 6 — Conservative entity resolution

Goal: identity correctness without the Graphiti failure mode of LLMs
silently merging unrelated entities.

Dependencies: Phase 3 (projections).

- [ ] **mem-entity-resolution-deterministic** — Auto-merge **only** on
  deterministic keys (email, GitHub ID, Linear ID, etc.). No
  surface-similarity auto-merge. SQL pattern in
  `research-zep-sqlite-deepdive.md §3.4`. **Stricter than Graphiti**
  (which auto-merges on LLM judgment alone).
- [ ] **mem-entity-resolution-llm-propose** — Port Graphiti
  `dedupe_nodes.py` prompt (Apache 2.0 — update `NOTICE`). LLM scores
  possible merges; high-confidence proposals surface to a human for
  confirm. **Never auto-merges on LLM judgment alone.** All merges
  recorded as events.

---

## Phase 7 — Outcome closure

Goal: close the Action → Decision → Outcome loop so learning compounds.

Dependencies: Phase 3 (projections).

- [ ] **mem-outcome-closure** — Per-source watchers (PR merged, ticket
  closed, email replied) auto-emit Outcome events. Human can mark
  manually too. Outcomes link back to Action → Decision.

---

## Phase 8 — MCP server interface

Goal: expose memory to any ACP / Copilot / Claude / Cursor client via
standard MCP tools.

Dependencies: Phase 4 (query API).

- [ ] **mem-mcp-server** — `Mneme.Mcp` ships an MCP server alongside
  the .NET `IMemoryQueryAPI`. Uses the
  [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).
  Tools: `query`, `distill`, `ingest`, `revoke`. Capability tokens
  passed via tool args.

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
  v1.0.
- [ ] **mem-autonomous-capture** — Heuristic capture beyond explicit
  emission set (decision-adjacency + novelty + human-anchors). Review
  queue surfaces uncertain captures for human approval.

---

## Cross-cutting / nice-to-have (not blocking any phase)

- [ ] **ci-github-actions** — Workflow that runs `dotnet build` +
  `dotnet test` on push to main and on PRs. Should fail on warnings.
- [ ] **release-automation** — Workflow that packs + pushes NuGet on
  tag. Skip until at least one Phase has a tagged release.
- [ ] **docs-adr-index** — Set up `docs/adr/` with the
  [MADR template](https://adr.github.io/madr/). First ADR: "Why
  SQLite as the only embedded backend" (consolidate
  `research-zep-sqlite-deepdive.md §3` and `plan.md` notes).
- [ ] **plans-rebrand** — Sweep `plans/*.md` for any remaining
  MuxiMuxi-specific framing that should be re-cast as substrate-general.
- [ ] **benchmarks** — `BenchmarkDotNet` project under `tests/` for
  ingest throughput + query latency. Useful once Phase 1 lands.

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
