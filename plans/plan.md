# Mneme — Build Plan

**Project**: Mneme — a chronological memory substrate for AI agents.
**Status**: **Phases 0–10 + 8.5 shipped (2026-06-12)**; only Phase 11
(sqlite-vec) deferred upstream. See "Implementation status" below.

## Implementation status (2026-06-12)

| Phase | State | Key artefact |
|---|---|---|
| 0 — Contracts | ✅ | `src/Mneme.Contracts/` (BCL-only, 5 host-pluggable interfaces) |
| 1 — Event log + ingest | ✅ | `src/Mneme/Storage/SqliteSchema.cs` (v7), `Ingest/` (<50ms p99) |
| 2 — Classification + revocation | ✅ | `src/Mneme/Classification/`, `Revocation/` |
| 3 — Projections + FTS5 | ✅ | `src/Mneme/Projections/`, `Search/` (adaptive-BM25) |
| 4 — Capability-checked query API | ✅ | `src/Mneme/Query/MemoryQueryApi.cs` (+ Explain, AsOf) |
| 4.5 — Benchmarks | ✅ | `benchmarks/Mneme.Benchmarks/` (LoCoMo harness; baseline 1/6 recall) |
| 5 — Distillation | ✅ | `IDistiller` (read-side bundle) + `ISessionDistiller` (ingest-side chat→events) + `DistillationCache` + heuristic fallback |
| 6 — Entity resolution | ✅ | 3-tier: UUID5 / embedding ≥0.95 / LLM-propose-confirm |
| 7 — Outcome closure | ✅ | `DecisionChainsProjector` (D→A→O) + `FeedbackLearner` |
| 7.5 — HITL curation | ✅ | `IMemoryCurator` (amend/annotate/pin/demote/revert + stale-state guard) |
| 8 — MCP server | ✅ | `src/Mneme.Mcp/` stdio (remember/query/list_recent/distill/forget/improve) |
| 8.5 — MAF integration | ✅ | `src/Mneme.Agents.AI/MnemeContextProvider : AIContextProvider` |
| 9 — Sidecar | ✅ | `src/Mneme.Sidecar/` HTTP + bearer auth + Dockerfile |
| 10 — Cloud sync | ✅ | `ISyncStore` + `SyncEngine` + `FileSystemSyncStore` |
| UI scaffold | ✅ | `Mneme.Studio` (Blazor), `.Desktop` (Photino), `.Electron` (pure desktop) |
| Semantic retrieval | ✅ | `VectorIndex` — brute-force cosine KNN over float32-BLOB embeddings; hybrid (semantic+BM25+recency) query fusion. Unblocks LoCoMo. |
| LoCoMo harness | ✅ | `benchmarks/Mneme.Benchmarks.LoCoMo` — ingest→embed→retrieve→answer→judge→score; OpenAI-compatible (turnkey) + offline dry-run. |
| 11 — sqlite-vec @ scale | ⏸ partial | Brute-force vectors ship now (sufficient to LoCoMo scale). sqlite-vec still deferred for million-vector corpora; autonomous capture still deferred. |

**Verification**: `dotnet test Mneme.slnx` → 321/321 (136 contracts + 182 Mneme + 3 MAF).

### Benchmarks
- **`Mneme.Benchmarks.Perf`** — storage-layer latency (BenchmarkDotNet). Ingest ~1.4ms/event; hybrid/category/list queries sub-ms–low-ms.
- **`Mneme.Benchmarks.LoCoMo`** — accuracy benchmark comparable to Mem0/Zep. Needs a real chat+embedding model for a comparable score (env-wired); runs offline in dry-run mode to verify the pipeline.

### Pending follow-ups (smaller-scope, deferred inside completed phases)
- Run the real LoCoMo set with a production model and publish the number vs Mem0 (92.5) / Zep.
- `mem-projection-snapshots` — Letta BlockHistory pattern for projection time-travel.
- Phase 7.5: `split` / `merge` curator ops, bi-temporal amend carry-over, `IReviewQueue` impl, curation OTel spans.
- Phase 8: MCP prompts/resources/elicitation/sampling; HTTP transport (split Stdio + Http hosts).
- Phase 8.5: `MnemeCheckpointStore` (workflow checkpoints), MAF demo sample under `samples/MAF.Demo/`.
- sqlite-vec for million-vector corpora; vector-score normalization tests + scale benchmark.
- Bump `Microsoft.Extensions.AI.Abstractions` 9.7.0 → 10.0.0 across non-MAF projects (already pulled transitively).

### Architectural invariant reinforced throughout
**SDK ships interfaces; host owns the model/LLM/policy.** Five symmetric
seams live in `Mneme.Contracts` (BCL-only): `ISessionDistiller`,
`IDistiller`, `IEmbeddingProvider`, `IEntityProposer`, `ISyncStore`.
Mneme has zero LLM/embedding/cloud SDK dependencies anywhere.

---

## Original plan (design rationale below)

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
**distills**. The pipeline is split into **synchronous** and
**asynchronous** stages (see `research-design-lessons.md` §3.2 + §4.2;
Mem0 v2→v3 dropped synchronous LLM invalidation, gaining +20 LoCoMo
points):

**Synchronous (on the `Ingest` call, target <50ms):**

1. **Validate** — schema, capability token, dedup-on-content-hash.
2. **Redact** — regex-based secret scrub (inline, non-bypassable).
3. **Classify** — sensitivity label (cheap LLM call OR rules-only;
   non-blocking variant logged for the worker).
4. **Persist** — append to `memory_events` with WAL commit. Return.

**Asynchronous (background `DistillationJob` worker — "sleep-time
compute" pattern per Letta `sleeptime_multi_agent_v4.py`,
arXiv 2504.13171):**

5. **Extract** structured facts / entities / hypotheses / goals from
   event payload (LLM-assisted, **ADD-only single-pass** — no
   simultaneous invalidation; reconciliation is a separate stage).
6. **Resolve** entities against `entity_index` (three-tier:
   deterministic UUID5 → embedding similarity ≥0.95 →
   LLM-propose+human-confirm — see Conservative Entity Resolution).
7. **Update** projections (current_facts, decision_chains, etc.).
8. **Reconcile** — propose invalidations of superseded facts through
   the propose-then-confirm pipeline. **Never** edit in place in
   ingest's LLM call.
9. **Synthesize** distillation bundle if criteria met.
10. **Index** for retrieval (text index in v1; vector index in v2).

**Per-workstream pessimistic lock**: the worker holds a SQLite
row-level mutex on `distillation_locks(workstream_id PRIMARY KEY)`
during a run. Idle/quiet triggers and SessionEnd hooks both target
the worker; the lock deduplicates concurrent triggers (pattern from
Cognee `try_acquire_improve_lock`).

**Read-after-write staleness**: between WAL commit and worker
completion, the freshly-ingested event is in the log but not yet in
projections / bundles. Consumers handle this via the **staleness
contract** on bundle responses (see next section).

Distillation outputs:

- **Workstream context bundle** — what an agent needs to know about
  this workstream right now (current goals, recent decisions, open
  hypotheses, key facts about referenced entities, recent outcomes)
- **Decision rationale** — why X was decided, with citations
- **Cross-loop insight** — when a new signal matches patterns from
  past workstreams (informs but doesn't auto-act)

**Compressed context goal**: an agent should query "distill what I
need to know about this workstream" and get back ~2–4k tokens of
useful synthesis, NOT a 50k-token raw event dump.

### Bundle staleness contract (rubber-duck finding L)

Because distillation is async, every `ContextBundle` response carries
explicit staleness metadata so consumers can reason about whether
their context is current:

- `GeneratedAt` — when the bundle was synthesized.
- `EventsCoveredThrough` — the last `event_id` (ULID) included in
  the bundle. Compare against the workstream's current head to
  detect drift.
- `IsStale` — convenience flag (true if drift exceeds a per-bundle
  threshold).
- `ForceRefresh` parameter on `DistillAsync` — caller's escape hatch
  to bypass cache and synthesize a fresh bundle synchronously.

Without these fields a consuming agent cannot tell whether the
bundle reflects the latest writes. See `research-design-lessons.md`
§4.2 for the analysis.

### Bundle shape — two-tier (`BundleIndex` + `BundleSection`)

The distillation result is composed of:

- **`BundleIndex`** (always-loadable, ~500–1000 tokens) — names the
  available sections, their categories, staleness flags, and short
  descriptions. Cheap to ship every turn.
- **`BundleSection[]`** (on-demand, 2–4k tokens each) — per-category
  rich content. Consumers fetch sections by name only when needed.
- **`OrientationSummary`** — single paragraph "where are we" prepend
  generated atop the index. Orients the consuming LLM before the
  data dump. Pattern from Cognee `GlobalContextSummary`.
- **`LookupHints`** — short keyword pointers ("topic and key terms")
  to original event-log entries for facts that didn't fit. Consumers
  re-query for detail. Pattern from Letta compaction prompt.

Each section records:

- `Distiller` — the prompt+model that produced it.
- `GeneratedAt`, `EventsCoveredThrough` — per-section staleness.
- `Provenance` — source events the section synthesizes from.
- `TokenBudget` / `TokenCount` — actual size vs target (per-section
  budgets; bundle total enforced post-composition using the LLM
  tokenizer; pattern from LlamaIndex).

### Distillation prompt patterns (ported)

Several prompt-design conventions ported from leading systems. See
`research-design-lessons.md` §3.2 + §3.3 for source citations.

- **Observation-Date + Current-Date dual anchor** (Mem0
  `prompts.py:528-536`). Every distillation prompt is told both the
  observation date and the current date with an explicit instruction
  to resolve relative references against the observation date only.
- **"Capture transitions, not just states"** (Mem0
  `prompts.py:611-622`). Prompts instruct the model to capture state
  transitions ("switched from X to Y after Z") rather than only the
  latest state.
- **Self-contained-fact constraint** (Cognee). One leading sentence
  stating what the input is about, followed by bullets of
  self-contained facts — each bullet must survive mid-context
  truncation.
- **Integer-ID anti-hallucination** (Mem0 `main.py:715-722`). When
  prompts embed existing event/fact IDs, pass sequential integers
  (`0, 1, 2, ...`) as handles; map back to ULIDs after the call.
  Prevents LLM ID hallucination.

### Retrieval scoring (Phase 4 + Phase 11)

When Phase 4 (FTS5 + structured) and Phase 11 (sqlite-vec) land,
score fusion follows the Mem0 v3 pattern (ported from
`mem0/utils/scoring.py`):

- **All signals normalized to [0,1] higher-is-better BEFORE fusion**.
  Pin this in test fixtures (Mem0 PR #5391 fixed multiple backends
  shipping distance instead of similarity).
- **Additive scoring with hard semantic threshold gate**:
  `combined = (semantic + bm25 + entity_boost) / max_possible`,
  excluding candidates with semantic below threshold (default 0.1).
  BM25 cannot rescue a candidate with zero semantic match — this
  was Mem0's +20 LoCoMo lesson.
- **Query-length-adaptive BM25 sigmoid normalization** (Mem0
  `get_bm25_params`). Five parameter sets for query lengths 1-3,
  4-6, 7-9, 10-15, 15+. Tiny function, big quality lift.
- **Entity-count popularity dampening** (Mem0
  `main.py:1515-1517`): `weight = 1 / (1 + 0.001 * (n-1)^2)`
  prevents widely-shared entities from dominating every query.
- **Filter-first, vector-rank-second** (Pinecone). Apply
  category/workstream/temporal/capability filters *before* the
  vector search; otherwise top-k followed by filtering produces
  empty results when filters are restrictive.

### `Explain` flag for retrieval debugging

`IMemoryQueryAPI.QueryAsync` accepts an `Explain: bool` parameter.
When set, the result includes a `ScoreDetails` block per item:
per-signal contributions (semantic, BM25, entity-boost, filter,
capability resolution), gate decisions, and final fused score.
Critical for diagnosing workstream-isolation bugs and temporal-window
mistakes. Pattern from Mem0 `explain=True`.

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

### Conservative entity resolution (three-tier)

Updated from the original two-tier design per
`research-design-lessons.md` §3.4 (three-tier strategy convergent
across Mem0, Cognee, Graphiti).

**Tier 1: deterministic-key auto-merge (no LLM).** Compute UUID5
from a fixed namespace + canonical key per entity type.
Canonicalization spec per identity type:

- Emails: lowercase + strip dots in localpart for `@gmail.com`.
- GitHub login: as-is (case-sensitive per GitHub policy).
- Stripe customer ID, Linear ID, Slack user ID: as-is.
- Names: lowercase + collapse whitespace + strip leading/trailing
  punctuation. **Names alone never auto-merge** — they're only used
  in Tier 2/3 candidate generation.

Pattern from Cognee `cognee/infrastructure/engine/models/DataPoint.py
:72-110` (`_generate_identity_id`). ~40 lines of code; trivial port
to C#.

**Tier 2: embedding-similarity threshold (no LLM).** When no
deterministic key is available, compute cosine similarity of
candidate name+description embeddings; merge if ≥0.95 cosine.
Threshold matches Mem0 `main.py:919`. **This tier requires Phase 11
(vector search) to be live**; it's a no-op in v1.

**Tier 3: LLM-propose + human-confirm.** LLM scores possible merges
not handled by Tier 1/2; high-confidence proposals surface to a
human (or MCP elicitation) for confirmation. Port Graphiti
`dedupe_nodes.py` prompt. Confirmation API re-cites pre-merge
canonical names exactly; mismatch returns `StaleProposalError`
(pattern from Letta `core_memory_replace`).

**Popularity dampening** (all tiers): per Mem0 pattern, apply
quadratic weight = `1 / (1 + 0.001 * (n-1)^2)` where n is the
mention count. Prevents widely-shared entities ("john.smith")
dominating fuzzy matches forever.

Proposed merges go to `EntityMergeProposal` table; surfaced via the
cockpit (or MCP elicitation when stateful HTTP). Confirmed merges
become events (`entity.merged`) with audit trail. Split events also
supported (`entity.split`) for unwinding bad merges.

Why this matters: bad merges poison memory. Once you've conflated
two customers, your facts about them are permanently wrong;
untangling requires event replay.

### Outcome closure (rubber-duck finding 7)

When the wedge emits `action.executed` (e.g., PR opened), memory agent:

1. Records the action
2. Subscribes to outcome watchers per source (GitHub PR watcher polls PR
   state; Email watcher polls thread replies; etc.)
3. When state change detected, emits `outcome.observed` and links to the
   originating action + decision

Manual marking: cockpit UI lets human mark outcomes for actions where
automated watching isn't possible.

### Human-in-the-loop curation (Phase 7.5 — Mneme differentiator)

Most memory systems treat curation as destructive (`UPDATE` / `DELETE`).
Mem0, Letta, Cognee, and Zep all support confirm/revoke at best, with no
audit trail of who edited what. **Mneme treats curation as a first-class
workflow** built on the same append-only event log everything else uses.

**Principles:**

1. **Every curation is an append-only event.** Never mutate projections
   or artifacts in place. The projector applies the curation event the
   next time it runs.
2. **Stale-state guard everywhere.** Every curation API requires the
   caller to cite the pre-curation state (hash of the canonical form).
   If another curator advanced the state in the meantime, the call
   fails with `StaleProposalError`. Pattern from Letta
   `core_memory_replace` (`base.py:262-280`).
3. **Bi-temporal preserving.** `fact.amended` carries `valid_at` so
   point-in-time queries can still answer "what did we believe on date
   X" vs. "what do we now know about date X." Curation does not
   rewrite history; it adds a new chapter.
4. **Capability-scoped.** `CurationCapability` is a separate token type
   from `IngestCapability` / `QueryCapability`. An agent with ingest
   rights cannot curate by default.
5. **Audit by default.** A `CurationLog` projection answers "who
   curated what, when, with what rationale" — GDPR Article 30 falls
   out for free.

**`IMemoryCurator` interface (Phase 0 contract, Phase 7.5 impl):**

```csharp
public interface IMemoryCurator
{
    // Correct a fact's content; carries pre-state hash for stale guard.
    Task<CurationResult> AmendFactAsync(
        FactId target,
        string preStateHash,
        FactAmendment amendment,
        CurationCapability cap,
        CancellationToken ct = default);

    // Attach human commentary without changing content.
    Task<CurationResult> AnnotateAsync(
        EventId target,
        string commentary,
        CurationCapability cap,
        CancellationToken ct = default);

    // Boost retrieval weight (default multiplier 2.0).
    Task<CurationResult> PinAsync(
        EventId target,
        PinScope scope,
        float weightMultiplier,
        CurationCapability cap,
        CancellationToken ct = default);

    // Suppress retrieval weight (default multiplier 0.3).
    Task<CurationResult> DemoteAsync(
        EventId target,
        float weightMultiplier,
        CurationCapability cap,
        CancellationToken ct = default);

    // Distillation aggregated wrong — break a fact into parts.
    Task<CurationResult> SplitFactAsync(
        FactId source,
        IReadOnlyList<FactSplitPart> parts,
        string preStateHash,
        CurationCapability cap,
        CancellationToken ct = default);

    // Two facts say the same thing — combine them.
    Task<CurationResult> MergeFactsAsync(
        IReadOnlyList<FactId> sources,
        FactMerged target,
        string preStateHash,
        CurationCapability cap,
        CancellationToken ct = default);

    // Reverse a prior curation (appends an inverse event).
    Task<CurationResult> RevertCurationAsync(
        EventId curationEventId,
        string reason,
        CurationCapability cap,
        CancellationToken ct = default);
}
```

**New event types** (added to `EventChannel.Epistemic`, flagged
`IsCurationAction = true` for `CurationLog` projection):

| Event type | Meaning | Reversible via |
|---|---|---|
| `fact.amended` | Content correction; old fact superseded but still queryable bi-temporally | `RevertCurationAsync` |
| `fact.annotated` | Human commentary attached to event | `RevertCurationAsync` |
| `fact.pinned` | Retrieval weight multiplied (default 2.0) | `fact.demoted` or `RevertCurationAsync` |
| `fact.demoted` | Retrieval weight multiplied (default 0.3) | `fact.pinned` or `RevertCurationAsync` |
| `fact.split` | One fact declared as N facts; original marked superseded | `fact.merged` (manual) or `RevertCurationAsync` |
| `fact.merged` | N facts declared as one; sources marked superseded | `fact.split` (manual) or `RevertCurationAsync` |
| `curation.reverted` | Inverse of a prior curation; records the reverted event-id | (no further inverse) |

**Pre-distillation review queue** (opt-in per workstream): for
sensitive workstreams, set `WorkstreamMode = ReviewBeforeDistill`.
Distillation worker skips events in those workstreams until they pass
through `IReviewQueue.ApproveAsync`. Default mode is `AutoDistill`
(today's behavior).

**Scoring integration** (Phase 4): retrieval scoring picks up pin /
demote multipliers from the `entity_curation_weights` projection.
Multiplier applied **after** the additive-with-gate fusion but
**before** the threshold check — so a demoted fact still has to pass
the semantic threshold to be returned at all (preventing demotion from
silently zeroing-out the index).

**Distiller integration** (Phase 5): the distillation worker MUST
honor `fact.pinned` (always include in the next bundle) and
`fact.demoted` (place in `LookupHints`, not the main sections, unless
the user explicitly queries for it).

**MCP exposure** (Phase 8): `improve` MCP tool dispatches to the
relevant `IMemoryCurator` operation based on an `operation` parameter
(`amend`, `annotate`, `pin`, `demote`, `split`, `merge`, `revert`).
`mneme://workstream/{id}/curation-log` and
`mneme://workstream/{id}/review-queue` are subscribable resources so
a curation UI updates live. Elicitation flow: an agent that wants to
curate triggers `elicitation/create` with the proposed change; the
user confirms in the cockpit; the curator API is called with the
confirmed proposal.

**Why this is a differentiator:**

| System | Amend | Annotate | Pin | Demote | Split | Merge | Audit log |
|---|---|---|---|---|---|---|---|
| Mem0 | UPDATE (destructive) | — | — | — | — | — | — |
| Letta | exact-string replace | — | — | — | — | — | `BlockHistory` (block-only) |
| Cognee | — | — | — | — | — | — | — |
| Zep / Graphiti | — | — | — | — | — | LLM auto | — |
| **Mneme** | ✓ event | ✓ event | ✓ event | ✓ event | ✓ event | ✓ event + propose-confirm | ✓ `CurationLog` projection |

See `backlog.md` Phase 7.5 for the full task breakdown.

### Memory agent process model

- **Embedded** *(v1 default)*: runs in cockpit process; same SQLite file;
  in-process method calls
- **Sidecar** *(v1.5)*: separate .NET process; gRPC over named-pipe / TCP;
  shared SQLite or separate
- **Service** *(v2+)*: hosted; shared by multiple cockpit users; mTLS auth

**Pluggable LLM provider**: classifier + distillation LLM is
configured per-deployment via **`Microsoft.Extensions.AI.IChatClient`**
(supersedes earlier Semantic Kernel plan — see
`research-design-lessons.md` §2.15 + §4.7). Local llama, OpenAI,
Anthropic, Azure OpenAI, etc. — register any
`IChatClient` implementation. Defaults to a small local model in
embedded mode (no network egress); cloud in sidecar / service.
`Mneme/Llm/` depends only on
`Microsoft.Extensions.AI.Abstractions ≥ 10.4.0`.

### Observability (Phase 1+)

Mneme ships with OpenTelemetry from Phase 1 using GenAI Semantic
Conventions v1.37 (aligning with MAF's `OpenTelemetryAgent`). Pattern
from Cognee `tracing.py`; see `research-design-lessons.md` §3.8.

**Activation guard**: when tracing is disabled, all span creation
goes through `MnemeNullSpan` — zero allocation, zero overhead. Default
builds pay nothing.

**Span name taxonomy**:

- `mneme.ingest.event` — sync ingest path (validate → redact → WAL).
- `mneme.classify.run` — classifier LLM call.
- `mneme.redactor.run` — secret-redaction pass.
- `mneme.entity.resolve` — entity resolution (tag
  `method = deterministic | embedding | llm-proposed | human-confirmed`).
- `mneme.distill.run` — distillation worker run (tags: input/output
  tokens, bundle size, workstream).
- `mneme.projection.rebuild` — projection rebuild from event log.
- `mneme.query.execute` — query API call (tags: signal count,
  gated_count, capability check).

**MCP spans** (Phase 8): set `mcp.method.name`, `gen_ai.tool.name`,
`mcp.session.id`, `network.transport` per MAF Python PR `dcc218d`
(2026-06-05).

**Secret-redaction at span-attribute write time** (not at log emission).
Apply `IRedactor` to all attribute values inline at emission — port
Cognee's regex set verbatim.

**Per-fact provenance** (already in plan): every derived item records
`{source_event_ids, agent_id, model, prompt_hash}`. Surfaced in
`QueryResult.Provenance` and in OTEL span attributes.

**In-memory trace buffer** (developer ergonomic): `IMneme.GetLastDistillationTrace()`
returns a structured summary of the most recent distillation —
`{operation, total_duration_ms, span_count, breakdown_by_span_name,
errors}`. Highly valuable when developing consumer agents against
an embedded library. Pattern from Cognee `CogneeSpanExporter`
circular buffer.

### MAF integration — `Mneme.Agents.AI` package (Phase 8.5)

The MAF integration seam is `MessageAIContextProvider` (abstract
class in `Microsoft.Agents.AI.Abstractions`) — **not** a custom
`IMemoryStore` interface. See `research-design-lessons.md` §2.15
for the source-level analysis.

`Mneme.Agents.AI` (Phase 8.5 NuGet package) ships:

- **`MnemeContextProvider : MessageAIContextProvider`** — drop-in
  context injection. `ProvideMessagesAsync` returns Mneme's
  distillation bundle as a single
  `ChatMessage(ChatRole.System, bundle.ToMarkdown())`.
  `StoreAIContextAsync` ingests `RequestMessages` + `ResponseMessages`
  back into Mneme.
- **`MnemeCheckpointStore : ICheckpointStore<JsonElement>`** —
  durable MAF workflow checkpoints backed by `memory_events` (event
  channel = `Technical`, see `EventChannel` below). Three-method
  interface; clean integration.
- **`AddMneme(opts => { opts.WorkstreamId = "..."; opts.SqlitePath
  = "..."; })`** — developer-ergonomic registration that hides
  capability-token mechanics for the 90% case. Full `CapabilityToken`
  API remains available for multi-workstream scenarios.

State surviving session serialization lives in
`AgentSession.StateBag.GetValue<MnemeState>("MnemeContextProvider")`
— the StateBag is the persistence carrier across service restarts.

**Capability token via `AdditionalProperties`**: MAF has no native
auth slot; the sanctioned propagation channel is
`AgentRunOptions.AdditionalProperties["mneme:capability-token"]`,
read inside `ProvideMessagesAsync` via
`AIAgent.CurrentRunContext?.RunOptions?.AdditionalProperties`.

### `EventChannel` — Epistemic vs Technical events

To keep the 7 epistemic categories pure, the event schema gains an
`EventChannel` discriminator:

- **`Epistemic`** — Evidence, Facts, Decisions, Hypotheses, Goals,
  Actions, Outcomes. The substance of agent memory; included in
  queries by default.
- **`Technical`** — workflow checkpoints (MAF), distillation job
  records, projection-rebuild markers, internal bookkeeping.
  Excluded from `IMemoryQueryAPI` results unless the capability
  token explicitly grants `IncludeTechnical = true`.

Without this distinction, MAF's `MnemeCheckpointStore` writes
pollute epistemic queries with workflow JSON blobs.

### Benchmarks — LoCoMo + LongMemEval (Phase 4.5)

Mneme has no published benchmarks yet; competitor credibility (Mem0
92.5 LoCoMo / 94.4 LongMemEval) comes from numbers. Phase 4.5 (after
queryable Phase 4 lands) runs both benchmarks against Mneme.

**Expected outcome**: Mneme should **win the temporal subcategory of
LoCoMo** specifically — bi-temporal modeling is the architectural
advantage. If Mneme doesn't beat single-timestamp competitors on
temporal queries, something is wrong with the implementation.

Results published in the same form as Mem0's
`github.com/mem0ai/memory-benchmarks` — both leaderboard numbers and
the harness scripts. See `research-design-lessons.md` §4.8.

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

## Sequencing

> **Numbering note.** This section uses the **canonical Phase 0–11
> scheme** shared by [`backlog.md`](backlog.md), [`README.md`](../README.md),
> and [`AGENTS.md`](../AGENTS.md). `AGENTS.md` requires these four
> documents to stay in sync; if you reorder phases, update all of them
> together. (An earlier revision of this section used an off-by-one
> Phase 1–11 scheme; it was reconciled on 2026-06-24.)

Status reflects what has shipped as of 2026-06; see the
"Implementation status" table at the top of this file and `backlog.md`
for per-task detail.

**Phase 0 — Contracts** ✅
- `IMemoryAgent`, `IMemoryQueryAPI`, `IMemoryCurator`, `CapabilityToken`,
  `CurationCapability` (BCL-only `Mneme.Contracts`)
- Event schema (7 categories, fields, provenance, classification)
- `QuerySpec` / `QueryResult` / `ContextBundle` DTOs
- One test per public type

**Phase 1 — Event log + SQLite schema** ✅
- `memory_events`, `memory_artifacts`, `memory_edges` tables
- Append-only, idempotent ingest (sync stages, <50ms p99)
- Secret regex redaction inline at ingest
- ULID event ids; OpenTelemetry from day one

**Phase 2 — Classification + revocation** ✅
- Pluggable classifier; per-source defaults
- `RedactedContent` / reference-with-synopsis shapes
- Content tombstone + `memory.revoke`; per-classification retention hooks
- `AddMneme(opts => ...)` developer-ergonomic registration

**Phase 3 — Projections (current-state views)** ✅
- `projection_facts`, `projection_decisions`, `projection_goals`,
  `projection_hypotheses`
- Entity index (deterministic resolution only)
- Rebuild-from-events tooling
- FTS5 text index + adaptive BM25

**Phase 4 — Temporal graph + capability-checked query API** ✅
- Point-in-time (`AsOf`) bi-temporal queries; workstream isolation
- `Explain` flag returning per-signal score decomposition
- No raw-SQL escape hatch

**Phase 4.5 — Benchmarks: LoCoMo + LongMemEval** ✅
- Harness + fixtures against the queryable stack
- Baseline numbers published honestly (vector search is the next lever)

**Phase 5 — Distillation pipeline (the primary value)** ✅
- Read side: `IDistiller` → two-tier `BundleIndex` + `BundleSection`
  synthesis; distillation cache + invalidation; staleness contract
- Ingest side: `ISessionDistiller` (session entries → epistemic events;
  see ADR-0003)
- **Sync/async split** locked (sync `Ingest` <50ms; distillation async)
- Heuristic fallback when no distiller registered

**Phase 6 — Conservative entity resolution (three-tier)** ✅
- Tier 1: deterministic UUID5 auto-merge
- Tier 2: embedding similarity ≥0.95 (no-op until Phase 11 vectors)
- Tier 3: LLM-propose + human/elicitation confirm (stale-proposal guard)
- `entity.merged` / `entity.split` events with audit

**Phase 7 — Outcome closure** ✅
- `DecisionChainsProjector` (Decision → Action → Outcome, out-of-order
  backfill)
- `FeedbackLearner` (outcome → per-event weight updates)
- Manual outcome marking API

**Phase 7.5 — HITL curation surface** ✅ *(Mneme differentiator)*
- `IMemoryCurator`: `amend`, `annotate`, `pin`, `demote`, `revert`
  (+ planned `split` / `merge`) — all append-only events
- `CurationCapability` token (separate from ingest/query)
- Stale-state guard on every mutation (Letta `core_memory_replace`
  pattern: caller cites pre-state hash, fail-on-mismatch)
- `CurationLog` projection (GDPR Article 30 falls out for free)
- Pin/demote multipliers wired into Phase 4 retrieval scoring
- `IReviewQueue` for opt-in pre-distillation review *(interface defined;
  impl is a follow-up)*

**Phase 8 — MCP server interface** ✅
- Stdio host exposing community-vocab tools (`remember`, `query`,
  `list_recent`, `distill`, `distill_session`, `get_watermark`,
  `forget`, `improve`)
- Explicit tool annotations (Destructive / ReadOnly / OpenWorld /
  Idempotent)
- *Follow-ups:* MCP prompts/resources/elicitation/sampling; HTTP
  transport split

**Phase 8.5 — `Mneme.Agents.AI` MAF integration package** ✅
- `MnemeContextProvider : AIContextProvider` (drop-in MAF context
  injection; read-only by design — see ADR-0003)
- *Follow-ups:* `MnemeCheckpointStore`, a real MAF demo sample

**Phase 9 — Sidecar deployment** ✅
- HTTP host + bearer auth + healthz/readyz + Dockerfile
- Same SQLite file; coordination via WAL

**Phase 10 — Cloud snapshot sync** ✅
- `ISyncStore` + `SyncEngine` + `FileSystemSyncStore`
- Gzipped JSONL snapshots; idempotent `INSERT OR IGNORE` merge
- Conflict-free (no last-write-wins)

**Phase 11 — v2 features (deferred / blocked)** ⏸
- Vector search: embedding pipeline + `sqlite-vec` semantic query
  (blocked on `sqlite-vec` v1; FTS5 + structured queries suffice for v1)
- Vector scoring normalization tests + scale benchmarks
- Autonomous capture heuristics — note: must be re-thought against the
  Phase 5 `DistillSessionAsync` model rather than the deleted per-turn
  capture pipeline (ADR-0003)

## Risks (memory-specific, inherited + amended from vision-v3.1 §12)

7. **Memory agent classifier accuracy** — over-capture / under-capture.
   Mitigation: deterministic capture in v1 (driven by cockpit, no autonomy);
   autonomous adds in v2 via review queue (human confirms before persist);
   manual "memorize this" / "forget this" always available; **richer
   curation via `IMemoryCurator` from Phase 7.5 (amend / pin / demote /
   split / merge / revert) when over/under-capture is detected after the
   fact**.
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
| `Microsoft.Extensions.AI.Abstractions` (≥10.4.0) | MIT | LLM provider abstraction (`IChatClient`). **Supersedes Semantic Kernel choice** — see `research-design-lessons.md` §2.15 + §4.7. |
| `Microsoft.Agents.AI` + `.Abstractions` *(Phase 8.5 only)* | MIT | `MessageAIContextProvider` + `ICheckpointStore<JsonElement>` base types for `Mneme.Agents.AI` integration package. |
| `ModelContextProtocol` (C# SDK) | Apache 2.0 | Memory agent exposed as MCP server |
| `sqlite-vec` *(v2 only)* | Apache 2.0 | Vector search extension |
| OpenTelemetry .NET *(observability)* | Apache 2.0 | GenAI Semantic Conventions v1.37 spans |

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
- **LLM provider**: `Microsoft.Extensions.AI.IChatClient`
  (`Microsoft.Extensions.AI.Abstractions ≥ 10.4.0`) is the LLM
  abstraction. Aligns with MAF (`Microsoft.Agents.AI`) which uses
  the same; **supersedes the earlier Semantic Kernel choice**
  (`research-design-lessons.md` §2.15 + §4.7). Local model
  (llama / ollama via `IChatClient`) is the v1 default for embedded
  deployment; user can configure cloud (OpenAI / Anthropic / Azure)
  via any `IChatClient` provider.
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
- Phase 8 (MCP exposure) elevated — was implicit, now explicit: memory
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
