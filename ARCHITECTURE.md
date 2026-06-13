# Mneme — Architecture

This document walks through Mneme's runtime architecture, the data
model, and the five host-pluggable seams the SDK exposes. Read it after
[README.md](README.md); it complements [USAGE.md](USAGE.md), which
shows how to wire Mneme into a host.

The design rationale and locked decisions live in [AGENTS.md](AGENTS.md)
and [plans/](plans/). This document focuses on *how the system is built*,
not why each choice was made.

---

## 1. The one big idea

> **The host owns the chat log. Mneme owns the interpretation.**

Mneme never stores raw conversational turns. The host (your agent
runtime, your CLI, your Copilot front-end, whatever) is the source of
truth for the chat history of every session. Periodically the host calls

```
IMemoryAgent.DistillSessionAsync(session, entries, capability)
```

passing the entries that have accumulated since the *watermark* Mneme
persisted for that session. Mneme:

1. Reads the watermark from `distillation_watermarks`.
2. Filters the entries to the strict un-distilled tail.
3. Short-circuits if `(session, from-id, to-id)` has been distilled
   before (idempotency guard via `distillation_runs`).
4. Hands the tail to the host-supplied `ISessionDistiller`.
5. Ingests each epistemic event the distiller produces with a
   `Citation.SessionRange(session, fromEntryId, toEntryId)` stamp.
6. Advances the watermark + records the distillation run, atomically.

Re-distillation = lower the watermark (or just pass a wider entry list)
and call again. The new events sit alongside the old in the append-only
log; nothing gets overwritten.

Why this is right:

- **No data duplication.** Chat logs are already append-only and
  timestamped in the host. Mneme storing a second copy would be waste.
- **Citations stay honest.** Every distilled event names a session-id
  + entry-id range. "Why does memory say X?" is answered by re-fetching
  those entries from the host's log.
- **Re-distillation is trivial.** Run a better distiller next month —
  new events with the same citation range appear next to the old.
- **Bi-temporality is preserved.** `valid_at` comes from the entry's
  timestamp; `recorded_at` is when distillation noticed it.

This is locked in [AGENTS.md](AGENTS.md). Per-turn capture interfaces
(`ICapturePolicy`, `CaptureSession`, etc.) were deleted in the
2026-06 refactor; do not reintroduce them.

---

## 2. Process model

Mneme runs **in-process** as a NuGet library inside whatever host you
bring. There are three optional shells around it:

| Shell | Purpose | Project |
|---|---|---|
| **MCP server (stdio)** | Expose Mneme to any MCP-speaking client (Claude Desktop, VS Code Copilot, Cursor) over the standard `mcp-config.json` mechanism. | `src/Mneme.Mcp` |
| **HTTP sidecar** | Run Mneme as its own process, talk to it over bearer-authenticated HTTP. Has a Dockerfile. | `src/Mneme.Sidecar` |
| **MAF context provider** | Drop into a Microsoft Agent Framework agent as an `AIContextProvider`. | `src/Mneme.Agents.AI` |

There is no Mneme daemon and no "Mneme cluster." A SQLite file under
the host's data directory is the entire deployed state.

---

## 3. The seven epistemic categories

Every event in Mneme is one of seven typed payloads:

| Category | Payload | Lifecycle |
|---|---|---|
| **Evidence** | `EvidencePayload(content, source, classification)` | Raw observation — chat fragment, document excerpt. |
| **Fact** | `FactPayload(statement, supportingEvents)` | Synthesized atomic claim. |
| **Decision** | `DecisionPayload(statement, rationale, supportingEvents, approver)` | A choice with rationale. |
| **Hypothesis** | `HypothesisPayload(statement, state)` | open → confirmed \| refuted \| abandoned |
| **Goal** | `GoalPayload(statement, state)` | active → achieved \| abandoned |
| **Action** | `ActionPayload(statement, decisionEvent, externalReference)` | An executed step linked to its deciding event. |
| **Outcome** | `OutcomePayload(statement, actionEvent, polarity)` | An observation that closes an Action → Decision loop. |

Categories are deliberate. They mirror the structure of how engineering
teams actually reason: "we *decided* X because of Y, we *did* Z,
*outcome* W." Storing memory as a flat blob of text loses this
structure; storing it as typed events makes Decision-chain queries
(`Decision → Action → Outcome`) trivial.

---

## 4. The bi-temporal model

Every event carries four timestamps:

| Stamp | Meaning |
|---|---|
| `valid_at` | When the claim became true in the world. (Event time.) |
| `invalid_at` | When the claim stopped being true. (Nullable.) |
| `created_at` | When Mneme's WAL committed the row. (Transaction time.) |
| `expired_at` | When Mneme superseded the row. (Nullable; never deleted.) |

A query can be either:

- "What did Mneme know about X **as of yesterday afternoon**?"
  (`AsOf(2026-06-11T16:00:00Z)` — uses `created_at` / `expired_at`.)
- "What was true about X **as of yesterday afternoon**?"
  (uses `valid_at` / `invalid_at`.)

These differ when a decision is recorded after the fact, or when memory
is amended to correct a stale claim. Mneme is one of very few systems
that gets this distinction right — see the LoCoMo / LongMemEval
discussion in `plans/research-design-lessons.md` §4.8.

---

## 5. Storage layer (SQLite, schema v8)

One SQLite file per workstream-tenant. WAL mode, busy-timeout, foreign
keys enforced. Schema is **append-only** for the source-of-truth table
(`memory_events`); projections are derived and rebuildable.

```
memory_events            (append-only source of truth)
memory_artifacts         (revocable blob bodies separate from metadata)
memory_revocations       (tombstones; metadata stays, body zeroed)
memory_edges             (graph placeholder)
distillation_queue       (outbox for async processing)
distillation_cache       (read-side bundle cache)
distillation_watermarks  (per-session "distilled through entry X")  ← v8
distillation_runs        (idempotency guard for DistillSessionAsync) ← v8
projection_facts         (current-state synth from Fact events)
projection_decisions     (current-state synth from Decision events)
projection_goals
projection_hypotheses
decision_chains          (Decision → Action → Outcome rollup)
event_feedback           (per-event weight learned from outcomes)
curation_events          (amend / annotate / pin / demote / revert)
entity_index             (canonical entity rows)
entity_mentions          (event → entity mentions)
entity_merges            (auto-merge audit)
entity_merge_proposals   (Tier-3 LLM proposals pending review)
event_processing_log     (per-projector idempotency tracking)
schema_meta              (version stamp)
```

Schema invariants enforced in code:

- **No `UPDATE` or `DELETE`** on `memory_events`. Revocation tombstones
  the artifact blob; metadata stays.
- **`event_id` is the idempotency key.** Re-ingest with the same id is a
  no-op (`ON CONFLICT(event_id) DO NOTHING`).
- **`(session, from, to)` is the idempotency key** for
  `DistillSessionAsync`. Re-call is a no-op.
- **Projection tables can be dropped and rebuilt** by replaying
  `memory_events` end-to-end through the projector pipeline.

Schema lives in `src/Mneme/Storage/SqliteSchema.cs` as one constant
DDL string. The `Initialize` method is idempotent — safe to call on
every startup. Version bumps are tracked in `schema_meta`.

---

## 6. The ingest pipeline

```
IMemoryAgent.IngestAsync(envelope)
        │
        │  sync stages, target <50ms p99:
        ▼
┌──────────────────┐
│ validate         │  workstream + event id + payload non-null
│ redact           │  IRedactor strips PII / secrets inline
│ classify         │  IClassifier labels Public / Internal / Confidential / Secret / Pii
│ select shape     │  IContentShapeSelector chooses Redacted / FullText / Tombstone
│ WAL commit       │  single SQLite transaction
└────────┬─────────┘
         │  observers fire post-commit (sync, in-process):
         ▼
   ┌──────────────────────────────────────────────────────┐
   │ ProjectorIngestObserver   → projection tables        │
   │ TextSearchIngestObserver  → FTS5                     │
   │ FeedbackIngestObserver    → outcome → weight updates │
   │ (and any observers the host registers)               │
   └──────────────────────────────────────────────────────┘
```

The host can hook the ingest path two ways:

1. **Directly** — for events that are already in epistemic shape (a
   workflow run, a webhook, a manual `remember` call). Just call
   `IngestAsync` with a `CaptureEvent` envelope.
2. **Through `DistillSessionAsync`** — for ambiguous unstructured input
   (conversation slices). The coordinator runs the host's
   `ISessionDistiller`, then funnels each produced event through
   `IngestAsync` so the same redact / classify / WAL path applies.

Async distillation (the heavy LLM work) is decoupled. Mem0 went from
synchronous to asynchronous invalidation between v2 and v3 and gained
+20 points on LoCoMo. This split is locked.

---

## 7. Session distillation in detail

```
host gathers ContextEntry[] from its session                      [host]
     │
     │ DistillSessionAsync(session, entries, capability)
     ▼
SessionDistillationCoordinator                                    [Mneme]
     │
     ▼  ReadWatermark(session)
     │     └─► row in distillation_watermarks or null
     ▼  TailAfter(entries, watermark)
     │     └─► strict tail; if empty → no-op result
     ▼  TryGetExistingRun(session, from, to)
     │     └─► row in distillation_runs ⇒ no-op result
     ▼  ReadPriorFacts(workstream, cap=50)
     │     └─► small set of prior facts for distiller context
     ▼
     ▼  ISessionDistiller.DistillAsync(request, ct)               [host's LLM]
     │     └─► SessionDistillationResult { events, dropped }
     ▼  for each event:
     │     build Citation.SessionRange from min/max supporting entry ids
     │     build CaptureEvent envelope with Citation in provenance
     │     IMemoryAgent.IngestAsync(envelope, ct)
     ▼  WriteWatermarkAndRun(advanced, from, to, count)
     │     └─► upsert distillation_watermarks
     │         insert distillation_runs (idempotency)
     │         single SQL transaction
     ▼
DistillSessionResult { newEvents, newWatermark, dropped, wasNoOp }
```

Failure modes the coordinator handles:

- **No distiller registered** → `InvalidOperationException` with a clear
  hint.
- **Cancelled mid-call** → events already ingested stay, watermark not
  advanced; the next call retries (idempotent inserts handle dupes,
  `distillation_runs` short-circuits if the same range was already
  fully processed).
- **Distiller throws** → exception propagates; nothing ingested,
  watermark untouched.

---

## 8. Read-side: query + distill

Two complementary read APIs:

```csharp
// Granular: ranked hit list with capability check, optional AsOf, optional Explain.
api.QueryAsync(new QueryRequest(new QuerySpec(...)), token, ct)

// Synthesized: one ContextBundle for the workstream (orientation +
// section index + bullets + lookup hints), suitable for injecting as
// a system message into the next agent invocation.
api.DistillAsync(workstream, new DistillOptions(), token, ct)
```

`DistillAsync` is the read-side counterpart to `DistillSessionAsync`:

| | `ISessionDistiller` (ingest) | `IDistiller` (read) |
|---|---|---|
| **Input** | Slice of session chat entries | Workstream events + active curations |
| **Output** | Epistemic events to ingest | Synthesized `ContextBundle` |
| **Frequency** | Every N minutes / at session-end | Per agent invocation |
| **Model** | Cheap / fast LLM often fine | Tends to want broader-context model |
| **Citation** | `Citation.SessionRange` per event | Per-section event-id provenance |

Retrieval uses **adaptive BM25** (FTS5 with custom k1/b weights tuned
to short events) combined with **recency** (30-day half-life) and
**curation multipliers** (pinned events boosted, demoted suppressed).
Capability tokens filter by workstream + epistemic-category allowlist
+ classification gate.

---

## 9. The five host-pluggable seams

| Interface | Where it lives | What the host plugs in |
|---|---|---|
| **`ISessionDistiller`** | `Mneme.Contracts` | LLM (any provider) that converts session entries → epistemic events. |
| **`IDistiller`** | `Mneme.Contracts` | LLM that converts a workstream's events → a `ContextBundle`. Falls back to a heuristic if not registered. |
| **`IEmbeddingProvider`** | `Mneme.Contracts` | Embedding model used by Tier-2 entity resolution (cosine ≥ 0.95 auto-merge). Optional. |
| **`IEntityProposer`** | `Mneme.Contracts` | LLM that proposes Tier-3 entity merges for human review. Optional. |
| **`ISyncStore`** | `Mneme.Contracts` | Cloud snapshot backend (S3, Azure Blob, local FS). Optional. |

All five live in `Mneme.Contracts`, which has **zero dependencies
outside the .NET 8 BCL**. Mneme itself depends on `Microsoft.Data.Sqlite`
and nothing related to LLMs, embeddings, or cloud SDKs.

This is the load-bearing decision of the project. It's the reason
Mneme can ship on any .NET allowlist, and the reason a host can swap
OpenAI for a local Llama tomorrow without touching Mneme.

---

## 10. HITL curation

Memory is not write-once-read-many. Users need to amend wrong claims,
pin authoritative ones, suppress noisy ones, and audit changes. Mneme
treats curation as a first-class surface:

```csharp
public interface IMemoryCurator
{
    Task<CurationResult> AmendFactAsync(FactId, preStateHash, FactAmendment, CurationCapability, ct);
    Task<CurationResult> AnnotateAsync(EventId, text, CurationCapability, ct);
    Task<CurationResult> PinAsync(EventId, PinScope, multiplier, CurationCapability, ct);
    Task<CurationResult> DemoteAsync(EventId, multiplier, CurationCapability, ct);
    Task<CurationResult> RevertCurationAsync(curationEventId, rationale, CurationCapability, ct);
}
```

Properties:

- **Append-only**: curations are themselves events in `curation_events`.
  Revert = inverse curation, not deletion.
- **Stale-state guarded**: `AmendFactAsync` requires a
  `preStateHash` so concurrent curators don't trample each other
  (Letta's `core_memory_replace` pattern, adapted).
- **Separate capability**: `CurationCapability` is distinct from
  `CapabilityToken` (read) and from ingest authorization.
- **Auditable**: every curation has a curator principal + rationale +
  timestamp, all in the log.

This is one of Mneme's intentional differentiators against Mem0 /
Letta / Cognee / Zep, which only support point-curation
(confirm / revoke).

---

## 11. Entity resolution

Three tiers, deliberately conservative:

| Tier | Trigger | Action |
|---|---|---|
| 1. Deterministic | UUID5 from canonical keys (email, GitHub login, etc.) collide | Auto-merge |
| 2. Embedding | `IEmbeddingProvider` cosine ≥ 0.95 | Auto-merge |
| 3. LLM-judgment | `IEntityProposer` returns a candidate match | **Propose only** — written to `entity_merge_proposals`; a human approves via curation |

Most agent-memory libraries auto-merge much more aggressively. The
LoCoMo evaluations show that aggressive merging is a primary source of
recall failures (entities collapse incorrectly, claims attach to the
wrong identity). Mneme refuses to do this without a human signal.

---

## 12. MCP server tools

The MCP server (`src/Mneme.Mcp`) exposes Mneme through community-vocab
tools so it's immediately discoverable by LLM clients trained on the
ecosystem:

| Tool | Purpose |
|---|---|
| `remember` | Direct-ingest an Evidence event for a single content snippet. |
| `query` | Ranked retrieval over the workstream, with optional `AsOf` and `Explain`. |
| `list_recent` | Last-N events in the workstream. |
| `distill` | Get the current `ContextBundle` (read-side `IDistiller`). |
| `distill_session` | Hand Mneme a JSON array of `ContextEntry` items + a session id; run the host distiller; advance the watermark. |
| `get_watermark` | Read the last-distilled entry id for a session. |
| `forget` | Revoke an event (tombstone). |
| `improve` | Run a curation operation (`amend` / `annotate` / `pin` / `demote` / `revert`). |

Annotations (`Destructive`, `ReadOnly`, `OpenWorld`, `Idempotent`)
are set explicitly on every tool; the MCP SDK's defaults are wrong
for read-only paths.

---

## 13. Capability tokens

Every read goes through a `CapabilityToken`:

```csharp
public sealed record CapabilityToken(
    PrincipalId Principal,
    WorkstreamId? Workstream,                 // null only for cross-workstream tokens
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    IReadOnlyList<EpistemicCategory>? AllowedCategories,  // empty = all
    bool CrossWorkstream = false,
    bool IncludeTechnical = false);
```

The query API checks the token against every event before returning it.
There is no raw-SQL escape hatch. `CurationCapability` is a separate
type with per-operation flags (`CanAmend`, `CanPin`, etc.) so curation
authorization is independent of read authorization.

---

## 14. The MAF integration (`Mneme.Agents.AI`)

`MnemeContextProvider : AIContextProvider` is intentionally **read-only**:

- `InvokingAsync` — pulls the latest `ContextBundle` and surfaces it as
  a single `ChatRole.System` `ChatMessage` rendered as Markdown.
- `InvokedAsync` — not implemented. Capture flows through
  `DistillSessionAsync` on the host's own schedule. Putting capture in
  the MAF hook would undermine the "host owns the chat log" invariant.

Five-line wiring:

```csharp
services.AddMneme(o => { o.WorkstreamId = "..."; o.SqlitePath = "..."; o.UserId = "..."; });
services.AddMnemeContextProvider(new WorkstreamId("..."));
services.AddSingleton<ISessionDistiller>(_ => new MyDistiller(...));   // optional, ingest side
services.AddSingleton<IDistiller>(_ => new MyBundleSynth(...));        // optional, read side
agent.ContextProviders.Add(sp.GetRequiredService<MnemeContextProvider>());
```

---

## 15. Sync (optional)

`ISyncStore` is the cloud back-end shape. The default `FileSystemSyncStore`
writes gzipped JSONL snapshot batches to a local directory; an S3 or
Azure Blob implementation slots in identically.

`SyncEngine` push / pull is conflict-free by design: snapshot batches
are append-only and merged via `INSERT OR IGNORE` on `event_id`. There
is no last-write-wins, no vector clocks, no conflict resolution UI.

---

## 16. Testing & verification

| Test project | Count | Scope |
|---|---|---|
| `Mneme.Contracts.Tests` | 136 | Pure contract shape tests (records, enums, validators). |
| `Mneme.Tests` | 177 | Storage, ingest, projections, query, curation, entity resolution, sessions, sync. |
| `Mneme.Agents.AI.Tests` | 3 | MAF context provider rendering + DI registration. |

`benchmarks/Mneme.Benchmarks/` is a LoCoMo / LongMemEval-style harness
(currently a baseline; documented honestly at 1/6 recall — see
`AGENTS.md`). Phase 11 (sqlite-vec) is the natural next lever.

---

## 17. What is intentionally not built

- **Real-time push of bundle updates** to MCP clients. Bundles are
  pulled on each `distill` call; the cache invalidates on ingest.
- **Multi-process write coordination.** WAL handles concurrent readers,
  but writers are expected to be a single process per database file.
  Sidecar mode is the supported way to share an instance.
- **Distillation auto-trigger.** Mneme does not run a clock that decides
  to distill on its own. The host decides when to call
  `DistillSessionAsync`. This keeps the SDK from owning policy that
  ought to live in the host.
- **Vector search** (Phase 11). FTS5 + structured queries are enough
  for v1 per locked decision; sqlite-vec is the v2 lever.

---

## 18. Reading order for going deeper

1. [`plans/plan.md`](plans/plan.md) — long-form design.
2. [`plans/research-zep-sqlite-deepdive.md`](plans/research-zep-sqlite-deepdive.md)
   — SQL patterns + DDL blueprint, why SQLite is enough.
3. [`plans/research-design-lessons.md`](plans/research-design-lessons.md)
   — Mem0 / Letta / Cognee / MAF deep dives, design patterns adopted.
4. [`plans/memory-systems-primer.md`](plans/memory-systems-primer.md)
   — vocabulary / mental model for agent memory.
5. [`AGENTS.md`](AGENTS.md) — locked decisions + "don't do this" list.
6. Source: start at `src/Mneme.Contracts/` (small, no deps) and
   `src/Mneme/Hosting/MnemeServiceCollectionExtensions.cs` (single
   wiring point for the entire SDK).
