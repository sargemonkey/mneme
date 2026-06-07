# Mneme — Build Plan

**Project**: Mneme — a chronological memory substrate for AI agents.
**Status**: planned (not started).
**Scope**: full v3+ design. **No "minimum viable memory" rescope** — design
the system as a whole.
**Companion docs**:
`research-existing-systems.md` (survey: 19 systems evaluated, build-vs-integrate),
`research-zep-sqlite-deepdive.md` (deep dive: Graphiti source code + SQLite
capability proof; confirms bespoke-SQLite + provides DDL blueprint + Apache 2.0
prompt provenance),
`consumer-architecture-reference.md` (reference architecture from Mneme's
first consumer, [MuxiMuxi](https://github.com/sargeMonkey/muximuxi); illustrates
how a cockpit binds against the contracts).

## Goal

Be the chronological organizational memory for AI agents.

**Primary value**: proactive context compression. Produce distilled,
decision-useful synthesis that consuming agents couldn't derive from raw
signals on their own.

**Secondary value**: audit, decision-history, cross-loop learning.

## Why a standalone project

- **Detachable**: any agent host (CLI agent, headless service, full cockpit)
  can use Mneme as its memory layer.
- **Replaceable**: an org could swap in a different memory implementation
  matching the same `Mneme.Contracts` surface.
- **Independently developable**: full v3+ scope is substantial work; isolating
  it lets it ship on its own timeline without blocking any single consumer.
- **Pluggable LLM provider**: Mneme runs its own model (could be local
  llama, smaller cloud model, etc.) — separate from the action model used by
  the consuming agent.

## Functional surface area (full v3+)

### Seven epistemic categories

| Category | Examples | Edit semantics |
|---|---|---|
| **Evidence** | signals, drafts, replies, artifacts, screenshots, CI runs | Immutable, append-only |
| **Facts** | "customer X on plan Y", "auth uses JWT" | Versioned (`valid_from` / `valid_until`); new value = new version |
| **Decisions** | approvals, rejections + reasoning + citations | Immutable; supersedable via new linked decision |
| **Hypotheses** | "is regression caused by auth change?" | State machine: `open → confirmed \| refuted \| abandoned` |
| **Goals** | "ship auth fix by Friday" | Versioned like facts |
| **Actions** *(experimental)* | executed spoke-writes — PR opened, email sent | Immutable |
| **Outcomes** *(experimental)* | "PR merged on date X", "customer responded with Y" | Immutable; links back to Action → Decision |

Experimental categories: try Actions + Outcomes as first-class for a sprint;
if they materially improve cross-loop learning queries AND let the distillation
layer compress better, keep. Otherwise collapse into Evidence with strict
event-type tagging.

Utility layers:
- **Entities** — stable nodes (customers, repos, services, people) referenced
  by all categories
- **Annotations** — live-editable human marginalia on any node

### Per-event fields (richer typing — rubber-duck finding I)

Every memorable item carries:

- `EventId` (ULID — globally unique, idempotent insertion key)
- `WorkstreamId` (access boundary)
- `Category` (one of the seven)
- `RecordedAt` (when memory captured it)
- `ValidTime` (when the thing happened / applies — optional for facts / goals)
- `Provenance` (source, agent, model, prompt hash, upstream event id)
- `Classification` (sensitivity label — see Classification section)
- `ClaimStatus` (asserted / verified / disputed / stale) — for facts / hypotheses
- `Confidence` (0–1) — for derived items
- `SourceReliability` (a-priori weight per source)
- `Owner` (workstream, agent, or human who created it)

### Storage architecture (rubber-duck finding E — event log vs graph)

The **event log is the source of truth.** Three core tables:

```sql
memory_events     -- append-only; the canonical store
memory_artifacts  -- separate blob store for content (revocable)
memory_edges      -- derived; rebuildable; typed relationships
```

Projections (derived; rebuildable from events any time):

- `current_facts` — latest version per fact key, with validity window
- `current_goals` — latest version per goal key
- `entity_index` — resolved entities + alias graph + provisional flags
- `decision_chains` — decision → action → outcome chain projections
- `hypothesis_states` — current state per hypothesis
- `relationships` (graph view) — typed edges between entities
- `vector_index` *(v2)* — embeddings table for semantic search

The graph is just one projection. Adding a projection requires no migration;
dropping a projection loses no data.

### Content + revocation model (rubber-duck finding 3; founder direction: classify but always store)

Event metadata is immutable. **Content blobs are separately addressable and
revocable.**

```
memory_events { event_id, content_ref, classification, ... }    // immutable
   │
   │ content_ref ─►
   ▼
memory_artifacts { content_id, body, classification, tombstone? }  // body revocable
```

- Default `body` = `RedactedContent` (secret-redacted content body,
  classification-labeled)
- For heavy artifacts: `body` = `ReferenceWithSynopsis` (source pointer +
  quality envelope — see below)
- Revocation: set `tombstone` to `{reason, revoked_by, revoked_at}`; null out
  `body`. The event row still exists; the body is unrecoverable.
- This satisfies "retention forever for metadata + audit" AND "revocable when
  needed for legal / privacy / oops" simultaneously.

**Classification labels** (stored, never gate capture per founder direction):

- `secret` — credentials, tokens
- `pii` — personal identifiable info
- `customer_confidential` — customer business data, conversation contents
- `internal_confidential` — internal roadmap, hire decisions, finances
- `public` — anything safe to surface broadly

Per-source defaults:
- Email → `customer_confidential`
- GitHub PR → `internal_confidential`
- Slack DM → `customer_confidential`
- Public RSS / docs → `public`

All configurable.

### `ReferenceWithSynopsis` quality envelope (rubber-duck finding J)

For heavy artifacts where we store a pointer + synopsis instead of full content:

- `SourceUri`
- `SourceExtId` — stable external ID (gmail thread, github issue number)
- `FetchedAt` — when we last reached the source
- `SourceRevisionHash` — if source provides one
- `SynopsisModelId` + `SynopsisModelVersion`
- `Confidence` — model's self-reported
- `Coverage` — how much of the source the synopsis covers ("first 80% of thread")
- `LastRetrievalState` — `ok` / `source-deleted` / `auth-expired` / `network-error`

Agents querying these see the envelope; they know when they're reasoning over
a synopsis vs original.

### Classification engine

At capture, every event runs through:

1. **Regex pass** for known secret formats (AWS keys, GitHub tokens, JWTs,
   common API key patterns) → tags `secret`
2. **LLM classifier** (small model) for PII + confidentiality categorization →
   tags `pii` / `customer_confidential` / `internal_confidential` / `public`
3. **Source-default override** (per-source policy overrides classifier if more
   restrictive)

Classifier is pluggable; user can swap models.

### Distillation pipeline (the primary value)

When a capture event arrives, memory agent doesn't just persist — it
**distills**. Pipeline:

1. **Persist** raw event to `memory_events`
2. **Classify** (sensitivity tagging)
3. **Extract** structured facts / entities / hypotheses / goals from event
   payload (LLM-assisted)
4. **Resolve** entities against `entity_index` (deterministic match → auto-link;
   ambiguous → provisional entity + suggest merge — see Conservative Entity
   Resolution)
5. **Update** projections (current_facts, decision_chains, etc.)
6. **Synthesize** a distillation if criteria met (e.g., decision approved →
   produce "context bundle for this workstream's next agent invocation")
7. **Index** for retrieval (text index in v1; vector index in v2)

Distillation outputs:

- **Workstream context bundle** — what an agent needs to know about this
  workstream right now (current goals, recent decisions, open hypotheses, key
  facts about referenced entities, recent outcomes)
- **Decision rationale** — why X was decided, with citations
- **Cross-loop insight** — when a new signal matches patterns from past
  workstreams (informs but doesn't auto-act)

**Compressed context goal**: an agent should query "distill what I need to
know about this workstream" and get back ~2–4k tokens of useful synthesis,
NOT a 50k-token raw event dump.

### Capability-based query API (rubber-duck finding H)

No raw SQL path for agents. All access goes through:

```csharp
public interface IMemoryQueryAPI
{
    Task<MemoryQueryResult> QueryAsync(
        MemoryQueryRequest req, CapabilityToken cap, CancellationToken ct = default);

    Task<DistillationResult> DistillAsync(
        DistillationRequest req, CapabilityToken cap, CancellationToken ct = default);

    Task<EntityMergeProposal[]> ProposeEntityMergesAsync(
        CapabilityToken cap, CancellationToken ct = default);

    Task ConfirmEntityMergeAsync(
        string mergeProposalId, CapabilityToken cap, CancellationToken ct = default);

    // ... other capability-checked operations
}
```

`CapabilityToken` encodes:

- Which workstreams can be read
- Which categories can be read
- Whether cross-workstream queries are allowed *(default: no)*
- Token expiration

The cockpit issues tokens at workstream-agent startup, scoped to that
workstream only. **Cross-workstream tokens require explicit human grant via a
UI prompt and are single-request** (not standing).

Query types:

- Structured (by entity, by time range, by category, by classification)
- Free text (text index)
- Semantic *(v2 — vector)*
- Distillation (synthesize a bundle for X)

### Conservative entity resolution (rubber-duck finding F)

**Default: do not merge entities on surface similarity.**

**Auto-merge** only when a deterministic key matches:

- Same UUID
- Same stable external ID (Stripe customer ID, GitHub user login, Slack user ID)
- Same canonical email domain on company-domain match (with explicit
  per-domain whitelist)

**Propose-merge** when:

- LLM extractor finds two mentions plausibly the same
- Two entities have overlapping name + matching workstream context

Proposed merges go to `EntityMergeProposal` table; surfaced to human via the
cockpit. Confirmed merges become events (`entity.merged`) with audit trail.
Split events also supported (`entity.split`) for unwinding bad merges.

Why this matters: bad merges poison memory. Once you've conflated two
customers, your facts about them are permanently wrong; untangling requires
event replay.

### Outcome closure (rubber-duck finding 7)

When the wedge emits `action.executed` (e.g., PR opened), memory agent:

1. Records the action
2. Subscribes to outcome watchers per source (GitHub PR watcher polls PR
   state; Email watcher polls thread replies; etc.)
3. When state change detected, emits `outcome.observed` and links to the
   originating action + decision

Manual marking: cockpit UI lets human mark outcomes for actions where
automated watching isn't possible.

### Memory agent process model

- **Embedded** *(v1 default)*: runs in cockpit process; same SQLite file;
  in-process method calls
- **Sidecar** *(v1.5)*: separate .NET process; gRPC over named-pipe / TCP;
  shared SQLite or separate
- **Service** *(v2+)*: hosted; shared by multiple cockpit users; mTLS auth

**Pluggable LLM provider**: classifier + distillation LLM is configured
per-deployment. Local llama, OpenAI, Anthropic, Azure OpenAI, etc. Defaults
to a small local model in embedded mode (no network egress); cloud in
sidecar / service.

### Sync model (rubber-duck finding G — idempotent append-only, NOT LWW)

Local-first; cloud sync optional.

**Sync semantics**: idempotent append-only merge with globally unique event
IDs. Each device's `memory_events` is independently writable; sync merges by
`EventId` (ULID — naturally sortable + unique).

- **Conflict resolution at event layer**: NONE — events are immutable;
  insertion is idempotent
- **Conflict resolution at projection layer**: projections are derived;
  rebuild after merge
- **Conflict resolution at annotations**: LWW with timestamp (annotations are
  mutable; conflicts rare in practice)

Sync transports:

- v1: snapshot upload to cloud storage (S3-compatible); periodic; deltas only
  (since last sync sequence)
- v2: CRDT-based real-time if multi-device pain emerges

### Failure / degraded behavior (rubber-duck finding K)

The wedge must work with degraded memory. Memory agent provides modes:

- **Sound** (normal): all features available
- **Degraded** (storage backend down): in-memory event buffer; queries return
  "memory unavailable" with cached recent items; ingest accepts events but
  warns
- **Read-only** (during sync conflict resolution): ingest queued; queries
  served from existing projections
- **Catastrophic** (data loss): tombstone-style "memory was reset" event; new
  events resume; cockpit warns user; backups (if cloud sync enabled) can be
  restored

Approval Gate explicitly does **NOT** call into memory at decision time; it
persists locally first, then asynchronously emits to capture bus. If capture
bus / memory agent is down, the approval still completes; the audit event sits
in spool until memory recovers.

## Sequencing (~12 weeks, full v3+ scope, parallel with wedge)

**Phase 1 — Contracts + foundation** *(~1.5 wks)*
- `IMemoryAgent`, `IMemoryQueryAPI`, `CapabilityToken` (in shared contracts
  assembly)
- Event schema (7 categories, fields, provenance, classification)
- `memory_events`, `memory_artifacts`, `memory_edges` tables
- Append-only ingest path (no distillation yet)
- Basic query (by event id, by workstream + category + time range)

**Phase 2 — Classification + redaction** *(~1 wk)*
- Secret regex pass
- LLM classifier integration (pluggable provider)
- Per-source defaults
- `RedactedContent` / `ReferenceWithSynopsis` shapes
- Synopsis quality envelope

**Phase 3 — Projections** *(~2 wks)*
- `current_facts`, `current_goals`, `decision_chains`, `hypothesis_states`
- Entity index (deterministic resolution only)
- Rebuild-from-events tooling
- Text index for free-text query

**Phase 4 — Distillation pipeline** *(~2 wks)*
- Extraction (events → facts / entities / hypotheses / goals candidates)
- Workstream context bundle synthesis
- Decision rationale synthesis
- Distillation cache + invalidation

**Phase 5 — Conservative entity resolution** *(~1 wk)*
- LLM-proposed merges with quality scoring
- Merge proposal table + confirm / reject API
- `entity.merged` / `entity.split` events with audit

**Phase 6 — Outcome closure** *(~1 wk)*
- Per-source outcome watchers (GitHub PR, Email thread, Linear ticket, etc.)
- Outcome → action → decision linking
- Manual outcome marking API

**Phase 7 — Revocation + retention** *(~3 days)*
- Content tombstone mechanism
- `memory.revoke` API
- Per-classification retention policy hooks (default: keep forever)

**Phase 8 — Sync v1 (snapshot)** *(~1 wk)*
- ULID event IDs (already in Phase 1 — confirm)
- Cloud snapshot upload (S3-compatible)
- Delta sync (since last sequence)
- Conflict handling (idempotent insert; projection rebuild)

**Phase 9 — Process separation (Sidecar)** *(~1 wk)*
- gRPC contract
- Named-pipe transport
- Health / restart supervision
- Same SQLite file; coordination via WAL

**Phase 10 — Autonomous capture + heuristics** *(v2, ~2 wks)*
- Memory agent listens to capture stream beyond deterministic set
- Heuristic policies (decision-adjacency, novelty, citation density, etc.)
- Review queue for proposed-not-yet-confirmed captures
- Human confirm / promote
- Configurable per-workstream heuristic mix

**Phase 11 — Vector search** *(v2, ~1 wk)*
- Embedding pipeline (per-event)
- Vector index (sqlite-vec or similar)
- Semantic query in `IMemoryQueryAPI`

(Total ~12–14 weeks of memory-agent work; parallelizable with wedge after
Phase 1 contracts land.)

## Risks (memory-specific, inherited + amended from vision-v3.1 §12)

7. **Memory agent classifier accuracy** — over-capture / under-capture.
   Mitigation: deterministic capture in v1 (driven by cockpit, no autonomy);
   autonomous adds in v2 via review queue (human confirms before persist);
   manual "memorize this" / "forget this" always available.
8. **Secret redaction false negatives** — novel formats slip through.
   Mitigation: redactor runs at capture AND on retrieval; pluggable regex
   sets; "scrub" command for retro-redaction.
9. **Storage growth** — keep-forever + multi-year. Mitigation: classifier
   prefers `ReferenceWithSynopsis` for heavy artifacts; compression on cold
   events; sync deltas only.
10. **Dangling reference pointers** — source disappears. Mitigation: synopsis
    rich enough to standalone; periodic source-presence check; promote to
    `RedactedContent` if source stale.
11. **Distillation hallucination** — synthesized context bundles include
    claims not in source events. Mitigation: every distillation outputs
    `Provenance` with cited event IDs; agents see the citation chain; cockpit
    UI surfaces "view source events" for any distillation.
12. **Entity merge poisoning** — bad merge corrupts memory. Mitigation:
    conservative defaults; LLM proposes, human confirms; split events for
    unwinding.
13. **Sync drift / event loss** — multi-device, idempotency depends on
    globally unique IDs. Mitigation: ULID generation everywhere; idempotent
    inserts; sync sequence numbers; periodic integrity check.

## Dependencies

- **Upstream**: receives `CaptureEvent`s from `MuxiMuxi.Capture` via
  `IMemoryAgent.IngestAsync`. Requires capture contracts stable.
- **Downstream**: cockpit (and other consumers) call `IMemoryQueryAPI`.
  Requires query contracts stable.
- **External**: LLM provider (pluggable — OpenAI / Anthropic / local model /
  Azure OpenAI). Local model is the v1 default for embedded deployment.
- **Cloud sync** *(v1.5)*: S3-compatible storage (user-provided credentials).

## Build vs integrate

Research at `research-existing-systems.md` (47 KB report, 27 sources) surveyed
Mem0, Letta, Zep, Graphiti, Cognee, LangGraph, LlamaIndex, OpenAI Assistants,
Anthropic, Google ADK, MCP memory servers, Pinecone, Weaviate, Chroma, MS Agent
Framework, MS Kernel Memory, KurrentDB, Marten, Neo4j .NET driver.

**Decision: Hybrid H2 — build bespoke on .NET-native SQLite substrate.**

### Why not integrate

- **No system satisfies more than 3–4 of our 10 functional requirements.**
  Graphiti (closest, Apache 2.0, used by Zep) covers temporal facts +
  provenance but is Python-only, requires Neo4j or FalkorDB, has no epistemic
  categories, no workstream isolation, no classification, no revocation, no
  distillation engine, and uses LLM auto-merge for entity resolution (violates
  our conservative policy).
- **The entire agent-memory ecosystem is Python/TypeScript-first.** Zero
  frameworks provide a native .NET 8 embedded library for episodic/temporal
  memory. Every integration would require a Python or Node.js sidecar —
  viable for server products, toxic UX for a local-first desktop app shipping
  to solo developers (300 MB+ runtime, 2–5 s cold start, separate update
  channel, two debugging surfaces).
- **The novel work is small in code surface but high in value.** Epistemic
  schema + distillation pipeline + workstream capability model = MuxiMuxi's
  core differentiator. Cannot be outsourced; would be reimplemented anyway
  on top of any integrated substrate.

### What we build vs reuse

**Native .NET libraries we use (no sidecar, no Python):**

| Library | License | Purpose |
|---|---|---|
| `Microsoft.Data.Sqlite` | MIT | Event log + temporal graph storage |
| `Microsoft.SemanticKernel` or MAF | MIT | Pluggable LLM provider abstraction |
| `ModelContextProtocol` (C# SDK) | Apache 2.0 | Memory agent exposed as MCP server |
| `sqlite-vec` *(v2 only)* | Apache 2.0 | Vector search extension |

**Substrate decisions:**

- **Storage**: SQLite (v1) for both event log and temporal graph. Single
  file, zero install, .NET-native. Schema designed to migrate to PostgreSQL
  + pgvector or Neo4j as optional v2+ upgrade path.
- **Event log**: hand-rolled append-only table on SQLite (not Marten in v1 —
  avoids PostgreSQL dependency for desktop install). Marten on PostgreSQL
  remains a viable v2 option for cloud-sync tier.
- **Temporal graph**: SQL tables with `valid_from` / `valid_until` columns.
  Point-in-time queries are standard WHERE clauses. Neo4j .NET driver is
  available as a v2+ upgrade if graph query complexity outgrows SQL.
- **LLM provider**: Semantic Kernel / MAF for abstraction. Local model
  (llama / ollama) is the v1 default for embedded deployment; user can
  configure cloud (OpenAI / Anthropic / Azure) via standard SK provider
  registration.
- **MCP exposure**: memory agent runs an MCP server so any ACP agent (and
  external Copilot / Claude / Cursor) can query it via standard MCP tool
  calls — no MuxiMuxi-specific client needed.

### What this changes in the phasing

No restructure required. The 11-phase plan above already targets SQLite +
local-first + pluggable LLM. The research validates the architecture and
removes the uncertainty that was blocking commitment.

**Net adjustments:**

- Phase 1 (event log + storage) reaffirmed as SQLite-only; remove "consider
  Marten" optionality from Phase 1 decision.
- Phase 11 (MCP exposure) elevated — was implicit, now explicit: memory
  agent ships an MCP server interface alongside the .NET `IMemoryQueryAPI`.
- v2+ optional substrate upgrades documented (Marten/PG, Neo4j) for cloud
  sync tier; not needed for desktop v1.

### Research effort estimate vs. our plan

The research report estimates ~13 weeks solo for a "bespoke on SQLite"
functional v1. Our 11-phase plan above is more ambitious (full v3+ scope
including distillation pipeline, conservative entity resolution, outcome
closure, classification + revocation, sync) — closer to 16–20 weeks solo
in realistic terms. The wedge (~11 weeks) ships with null-stubbed memory
and gets real memory as it lands phase-by-phase.

## Todo prefix

`mem-*` — see SQL `todos` table.
