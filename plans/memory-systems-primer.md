# Agent Memory Systems — A Primer

*Last updated: 2026-06-07.*

A guided tour through the concepts you need in order to read
`research-existing-systems.md` and `research-design-lessons.md` (and
to argue with anyone shipping an agent memory product).

The goal is not to teach you any one system. It is to give you the
**vocabulary** and **mental model** to slice any memory product —
including Mneme — along the same axes, so that comparisons are
apples-to-apples instead of marketing-blurb-to-marketing-blurb.

---

## 0. The 60-second mental model

> An agent memory system is a thin layer over **three** things:
>
> 1. A **write path** that turns messy interaction events into
>    storable units.
> 2. A **storage substrate** that keeps those units (with some sense
>    of time and identity).
> 3. A **read path** that, given a question, returns a compact bundle
>    of context the LLM can actually use.
>
> Everything else — graph DBs, vector indexes, knowledge graphs,
> distillation prompts, capability tokens — is implementation detail
> in service of those three jobs.

Memory products differ in:
- **What units** they store (raw turns, facts, blocks, triples,
  embeddings, summaries).
- **How they index** those units (vector, full-text, graph,
  bi-temporal, hybrid).
- **When they distill** (synchronously at ingest, asynchronously in a
  worker, lazily at read).
- **Who they trust** (every consumer, namespace conventions, RBAC,
  capability tokens).

If you can categorize a system along those four axes, you understand
it well enough to compare.

---

## 1. Why memory is hard

A naive "memory" is *just chat history* — append every message to a
log, replay it on the next turn. This breaks immediately in production:

- **Context windows are finite.** Even at 200k+ tokens, multi-month
  workstreams overflow.
- **Repetition kills models.** Replaying every prior message dilutes
  signal, costs money, and confuses the model.
- **Facts go stale.** "I live in Berlin" said in January is no longer
  true after a May move. Naive replay surfaces both as equally true.
- **Decisions need rationale, not transcripts.** *"We chose
  PostgreSQL because X, Y, Z"* matters more than the 200-message
  thread that led to it.
- **Privacy.** API keys and PII appear in chat; raw replay is a
  compliance disaster.
- **Identity scope.** A team's shared memory must not leak between
  customers or workstreams.

Every concept below exists to solve at least one of these problems.
Keep them in mind as a checklist when you read a product's docs.

---

## 2. The taxonomy of memory units

What do you actually store? Six common answers, ordered roughly by
abstraction level (raw → curated):

### 2.1 Raw conversation turns
The lowest-level unit. Replayed verbatim. Example: pretty much every
"chat history" implementation, OpenAI Threads, MAF `ChatHistoryMemoryProvider`.

**Pros**: zero processing cost; perfect fidelity.
**Cons**: doesn't scale; doesn't generalize across sessions.

### 2.2 Facts (atomic propositions)
"User prefers TypeScript over JavaScript." "Customer ID 42 is in EU
GDPR scope." Extracted by an LLM from conversation. Examples: Mem0,
Letta core blocks, Zep, Cognee `DocumentSummary`.

**Pros**: dense, generalizable, queryable.
**Cons**: extraction quality is everything; can hallucinate; loses
nuance.

### 2.3 Episodic events
Time-stamped happenings: "User called API at 14:32 and got a 500."
Often paired with facts. Examples: Letta `Run`, Graphiti episodes,
Mneme's append-only `memory_events`.

**Pros**: preserves causality and ordering; supports temporal
queries.
**Cons**: storage grows unbounded; needs compaction.

### 2.4 Decisions, hypotheses, goals (epistemic categories)
Higher-level units that carry *why*, not just *what*. "Decision:
chose SQLite because Phase-1 substrate must be embeddable.
Hypothesis: needs to scale past 1M events." Examples: Mneme's
**7 epistemic categories** (Facts, Decisions, Hypotheses, Goals,
Actions, Evidence, Outcomes), partly LangGraph "memories of types
semantic/episodic/procedural."

**Pros**: matches how humans actually reason; rich provenance.
**Cons**: requires taxonomic discipline; LLM must classify reliably.

### 2.5 Blocks / files (markdown-ish documents)
Small editable text documents, typically named and versioned.
"`human.md` = 'Sara, ML researcher, prefers Python'." Examples:
Letta core blocks (5-10 markdown files per agent), Letta MemFS
(filesystem with git-worktree isolation).

**Pros**: human-readable; LLM-editable via tools; diff-able.
**Cons**: schema drift; merge conflicts; not query-shaped.

### 2.6 Graph nodes + edges (entities + relations)
"Sara —[works_for]→ Anthropic; Anthropic —[is_a]→ Company."
Examples: Graphiti, Cognee, Neo4j-backed systems, Zep (Graphiti
inside).

**Pros**: relationship queries are native; rich semantic structure.
**Cons**: extraction is expensive; resolution is hard; query
complexity grows.

### Cross-cutting: vectors
Most systems also index whatever unit type they chose into a
**vector embedding** (a 768- or 1536-dim numeric blob) for fuzzy
similarity search. This is orthogonal to the unit type — facts,
blocks, and nodes all get embeddings.

### Mneme's choice
Mneme stores **(2.3) episodic events** as the immutable substrate
(append-only `memory_events`), with **(2.4) epistemic categories**
as the typed projections derived from those events. Distilled bundles
look like **(2.5) blocks**. This is a deliberate "have your cake and
eat it" — temporal precision in storage, human-readable units at the
read API.

---

## 3. Memory tiers (working vs long-term)

Borrowed from cognitive psychology and absolutely standard:

| Tier | Lifetime | Typical implementation |
|---|---|---|
| **Working memory** | Within a single turn / call | The prompt itself, currently in-context |
| **Session memory** | Within one conversation/session | StateBag, in-process dict, ephemeral cache |
| **Short-term** | Hours to days | Hot table; full-replay tolerated |
| **Long-term** | Weeks to forever | Cold storage; distilled, summarized, indexed |

Most products handle multiple tiers explicitly:
- **Letta**: `human/persona` core blocks (long-term, always in
  context); archival passages (long-term, retrieved on demand);
  `messages` (working).
- **Mem0**: vector store (long-term semantic facts); session memory
  cache (short-term).
- **LangGraph**: `Checkpointer` (session state); `BaseStore`
  (long-term cross-session).
- **MAF**: `AgentSession.StateBag` (session); pluggable
  `AIContextProvider` for long-term.
- **Mneme**: `memory_events` log + projection tables (long-term);
  capability-token-scoped query (session is implicit, derived from
  workstream-id).

**Why it matters**: if a product can't articulate which tier its
memory operates at, it usually means it conflates them — leading to
either context bloat (long-term replayed every turn) or context loss
(short-term lost on restart).

---

## 4. The write path (ingest → storage)

The job: convert messy event input into stored, indexed, retrievable
units. Almost every system has the same backbone:

```
event in → validate → classify → redact → extract facts →
resolve entities → write to log → project to tables → index
```

But the **scheduling** of these stages is where systems diverge
sharply.

### 4.1 Synchronous vs asynchronous distillation

**Sync (everything on the ingest call)**: simple to reason about,
but ingest latency is bound by LLM call latency (often seconds).
Naive agents block while memory writes.

**Async (ingest commits the raw event; a worker does extraction)**:
ingest returns <50ms; extraction happens in the background.
Trade-off: between ingest and worker completion, the freshly-ingested
event is in the log but not yet in indexes — *read-after-write
staleness*.

**Hybrid**: ingest synchronously persists the WAL entry and runs
cheap stages (validate, classify, redact); a worker runs expensive
stages (extract, resolve, distill, index).

**Lesson from Mem0 v2 → v3**: dropping synchronous LLM-driven
invalidation (UPDATE/DELETE during ingest) and switching to
ADD-only single-pass extraction added **+20 points on LoCoMo**
benchmark (71 → 92.5). The single biggest distilled lesson of
2026.

**Lesson from Letta**: the "sleep-time compute" pattern
(arXiv 2504.13171) — the user-facing agent stays fast; a background
"sleep-time agent" handles memory writes. Now a near-standard
pattern (Mem0, Cognee, Graphiti, MAF, and Mneme's planned
`DistillationJob` worker all converge on this).

### 4.2 Stage details

- **Validate**: schema check, capability check, dedup-on-content-hash.
- **Classify**: assign category/type. Cheap LLM call (or rules-only
  if your taxonomy is small).
- **Redact**: regex-strip secrets *before* anything leaves the
  process. Cognee's regex set (`sk-…`, `bearer …`, `api[_-]?key…`)
  is the de-facto standard; ports trivially.
- **Extract facts**: LLM-driven. The classic prompt: *"Extract
  self-contained facts from the conversation. Return a JSON array
  of strings."*
- **Entity resolution / dedup**: see §6.
- **Write to log**: append-only is the safe default. Mutable updates
  cause history loss.
- **Project to tables**: derived views (fact tables, entity tables,
  bundles). These can be rebuilt from the log if corrupted.
- **Index**: vector embedding write, FTS5 index update, graph edge
  insert.

### 4.3 Idempotency and replay

Append-only logs need event-IDs that the writer can re-send safely
(e.g., on retry). Common patterns:
- **Content hash as ID** — same content always produces same ID;
  re-insertion is a no-op.
- **Client-generated ULID** — caller controls ID; replay-safe.
- **Server-generated sequence + caller-side idempotency key** —
  database-side dedup.

**Why it matters**: distributed agents will fail mid-write. Your
memory system must tolerate "the same event arriving twice."

---

## 5. The storage substrate

What database lives underneath. Five families:

### 5.1 Append-only / event-sourced (the "log" model)
Every operation is an immutable event. Read state is derived by
replaying events.
- **Examples**: KurrentDB (formerly EventStoreDB), Marten (Postgres-
  backed), Mneme's `memory_events`.
- **Pros**: complete history; trivial audit; rebuild any projection.
- **Cons**: read amplification (replay needed); requires snapshot
  + projection discipline.

### 5.2 Mutable document/KV stores
Each unit is a row/document you update in place.
- **Examples**: LangGraph `BaseStore`, Cognee SQLAlchemy ORM,
  MongoDB-style stores.
- **Pros**: simple; fast reads; familiar.
- **Cons**: history is lost on update unless you add a
  `history` table by hand.

### 5.3 Vector databases
Specialized for "find me the nearest k vectors to this query
vector."
- **Examples**: Pinecone, Weaviate, Chroma, Qdrant, Milvus;
  embedded: pgvector, sqlite-vec.
- **Pros**: fuzzy semantic recall; mature ANN algorithms (HNSW,
  IVF); horizontal scale.
- **Cons**: poor at structured queries; weak metadata filtering;
  requires separate truth-store.

### 5.4 Graph databases
Nodes + typed edges + properties on both. Queryable via Cypher,
Gremlin, or property-graph SPARQL variants.
- **Examples**: Neo4j (and `Neo4j.Driver` for .NET), Kuzu,
  FalkorDB, Memgraph.
- **Pros**: native multi-hop queries; relationship-shaped data is
  first-class.
- **Cons**: operational complexity; embedding extraction needed for
  fuzzy recall; not append-only without effort.

### 5.5 Hybrid (KG + vector + sometimes FTS)
The fashion choice of 2025+. A knowledge graph for structure plus
vector embeddings on nodes/edges/episodes for fuzzy recall.
- **Examples**: Graphiti, Cognee, Zep (= Graphiti + RBAC),
  Mem0 (KG mode).
- **Pros**: best of both worlds when done right.
- **Cons**: two indexes to keep in sync; cost; entity-resolution
  remains hard.

### Mneme's choice
**SQLite + sqlite-vec + FTS5** — a single-file, embeddable, hybrid
substrate. Event log + projection tables + vector index + full-text
index all in one DB file. Trade-off: scales to ~10M events
realistically (single-writer SQLite limit); not for multi-region
multi-writer scale. The bet: solo-developer and small-team agent
workstreams will live comfortably in this regime for years.

---

## 6. Entity resolution (the hardest unsolved problem)

> "Sara Smith," "S. Smith," "sara@anthropic.com," and "User #42"
> are the same person — or are they?

Entity resolution decides whether two extracted entities are the
same real-world thing. Gets it wrong → either you have 17 "Sara"
nodes that should be one, or you've merged two different Saras into
a Frankenstein. Both are bad.

### 6.1 Three-tier resolution strategy
Industry has converged on:

**Tier 1: deterministic key match (auto-merge, no LLM)**
- Define a canonical identity per entity type (email lowercased,
  user-ID as-is, name lowercased + whitespace-collapsed).
- Compute UUID5 from a fixed namespace + canonical key. Same key
  always produces same UUID. Auto-merges deterministically.
- **Cognee's implementation** is exemplary: `cognee/infrastructure/
  engine/models/DataPoint.py:72-110`; ~40 lines.

**Tier 2: embedding-similarity threshold (no LLM)**
- Compute cosine similarity of names/descriptions; merge above
  threshold (Mem0 uses 0.95 hard-coded).
- Fast, deterministic, but tuning the threshold per domain is hard.

**Tier 3: LLM-propose + human-confirm**
- LLM proposes merge candidates from the leftovers with rationale.
- Human (or another agent with elicitation) confirms.
- **Graphiti's `dedupe_nodes.py` prompt** is the canonical
  reference for the LLM-propose half.

### 6.2 Common gotchas
- **LLM ID hallucination**: when you pass existing IDs to the LLM
  for it to reason over, *don't* pass long ULIDs — pass sequential
  integers (`0, 1, 2`) and map back after. Mem0 nailed this at
  `main.py:718-722`.
- **Stale proposals**: if you propose a merge and the underlying
  entity changes before confirmation, the merge applies to the wrong
  thing. Letta's pattern: confirmation API re-cites the pre-merge
  values; mismatch → `StaleProposalError`.
- **Popularity bias**: a "John Smith" entity that's been mentioned
  500 times will dominate fuzzy matches forever. Mem0 applies a
  quadratic dampening: `weight = 1 / (1 + 0.001 * (n-1)²)`.

### Mneme's choice
All three tiers, with capability-token-scoped propose-then-confirm.
At the MCP edge, the confirmation step collapses to a single
`elicitation/create` call.

---

## 7. Bi-temporal modeling (the "when?" question)

Most memory systems have **one timestamp**: when the fact was
written. Mneme and Graphiti have **two**:

- **Valid time** (`valid_at`, `invalid_at`): when the fact was true
  in the world.
- **System time** (`recorded_at`, `superseded_at`): when the system
  learned about it.

These are independent. Examples:
- "Sara moved to Berlin in January (`valid_at=Jan`)." Recorded into
  the system in March (`recorded_at=Mar`).
- "Sara is now in Lisbon (`valid_at=May`, supersedes the Berlin
  fact)." Recorded in June.

### Why two timestamps matter
- **Single-timestamp systems can't answer "what did we believe was
  true on April 1?"** They only have "what we recorded by April 1,"
  which conflates "world truth" with "system state."
- **Audit & investigation**: when something went wrong, you need to
  reconstruct the agent's state of knowledge at the time, not the
  current state of knowledge.
- **Temporal reasoning**: "between Sara's move to Berlin and her
  move to Lisbon, what was her timezone?" requires bi-temporal
  scoping.

### Invalidation vs supersession
- **Edit-in-place** (most systems): UPDATE the fact; old value
  lost.
- **Append-with-supersedes** (Graphiti, Mneme): write a new fact;
  reference the old one as `supersedes_event_id`; old fact remains
  queryable with `invalid_at = new fact's recorded_at`.

### Mneme's choice
Append-only bi-temporal — the planned distinguishing feature. The
implementation cost is one extra column pair (`valid_at`,
`invalid_at`) and discipline in writers. The payoff: every other
memory system competes on accuracy; Mneme can compete on
**temporal accuracy** (a benchmark axis where its model is
architecturally superior).

---

## 8. The read path (retrieval & distillation)

This is where the value lives. Three sub-problems:

### 8.1 Recall — find the candidates

**Filter** (structured queries): "all decisions made by agent X in
workstream Y in the last 30 days." Boolean predicates over rows.

**Vector search** (semantic similarity): embed the query; find the
nearest k vectors. Good at fuzzy recall ("anything about my
preference for TypeScript") even when the user phrasing differs
from stored phrasing.

**Full-text search** (BM25 / FTS5): word-level matching with
inverse-document-frequency weighting. Good at exact phrases and
proper nouns (where embeddings underperform).

**Graph traversal**: "everything connected to entity Sara through
≤2 hops." Native to graph DBs.

**Hybrid**: combine all of the above. The 2026 standard.

### 8.2 Rank — order the candidates

Naive: sort by raw score. Fails because the score scales differ
(vector cosine is in [-1, 1]; BM25 is unbounded positive).

**Reciprocal Rank Fusion (RRF)**: `score = Σ 1/(k + rank_i)` across
sources. Simple, no normalization needed, surprisingly hard to
beat. Default in Weaviate, Vespa, OpenSearch.

**Additive scoring with gates** (Mem0 v3): `combined = (semantic +
bm25 + entity_boost) / max_possible`, with a *hard semantic
threshold gate* (default 0.1) that excludes candidates entirely if
below. BM25 cannot rescue a candidate with zero semantic match. This
is the +20 LoCoMo lesson.

**Reranking models** (cross-encoder, e.g. `bge-reranker-base`):
take the top 50 candidates from cheap recall, score each against
the query with a more expensive model; return top 5. Significantly
better quality at modest latency cost.

**MMR (Maximal Marginal Relevance)**: penalize candidates similar
to already-selected results; encourages diversity. Useful when the
top 5 are all near-duplicates.

### 8.3 Common scoring gotchas

- **Distance vs similarity confusion**: some vector libraries return
  *distance* (lower = better); some return *similarity* (higher =
  better). Mem0 PR #5391 fixed this across multiple backends. Always
  normalize to "[0,1] higher is better" before fusion.
- **Filter-first vs vector-first**: applying filters *after* vector
  top-k produces empty results if filters are restrictive. Apply
  filters *first*; vector-rank within the filtered set.
- **BM25 score range varies with query length**. Mem0's adaptive
  sigmoid normalization (`get_bm25_params`) maps raw BM25 to [0,1]
  with five parameter sets (1-3, 4-6, 7-9, 10-15, 15+ terms). Tiny
  function, big quality lift.

### 8.4 Distillation — turn candidates into a context bundle

Recall returns 50 candidates. Rank narrows to 10. **Distillation**
turns those 10 into a compact, LLM-ready bundle. Two strategies:

**Naive concatenation**: dump candidates as bullets with metadata.
Cheap; works for small candidate sets.

**LLM synthesis**: call an LLM with a prompt like *"Given these
facts, produce a single coherent context summary for an agent
answering question X."* Higher quality; costly; introduces a
synthesis-error surface.

**Hybrid (best practice)**: a `BundleIndex` (always-cheap thin
summary: what bundles exist, when they were generated, staleness
flag) plus on-demand **`BundleSection`s** (richer per-category
summaries the consumer requests by name).

### 8.5 Distillation prompt patterns worth knowing

- **Observation-date / Current-date dual anchor** (Mem0
  `prompts.py:528-536`): every distillation prompt is told *both*
  the date the fact was observed *and* the current date, with an
  explicit instruction to resolve relative references against the
  observation date only. Cheap; dramatically improves temporal
  accuracy.
- **"Capture transitions, not just states"** (Mem0): instruct the
  LLM to capture state transitions ("switched from X to Y after Z")
  rather than only the latest state.
- **Self-contained facts** (Cognee): "one leading sentence stating
  what the input is about, followed by bullets of self-contained
  facts." The self-contained constraint matters because each bullet
  must survive mid-context truncation.
- **Orientation summary**: prepend a one-paragraph "where are we"
  summary before the detailed bullets. Orients the consuming LLM
  before the data dump.
- **Lookup hints**: include short keyword pointers ("topic and key
  terms") to the original event log entries for facts that didn't
  fit. Consumers can re-query for detail.

### 8.6 Staleness — the under-discussed problem

If distillation is async, the bundle is by definition *not* the
freshest view. Most products hide this; the responsible ones expose:
- `generated_at` — when the bundle was synthesized.
- `events_covered_through` — the last event ID in the bundle.
- `force_refresh: true` — caller's escape hatch.

Without these, agents can't reason about whether their context is
current.

---

## 9. Identity, isolation, and capabilities

Memory belongs to *someone*. The "someone" is the
identity/isolation model. Four canonical approaches:

### 9.1 Three-key scope (Mem0)
Every operation takes `user_id`, `agent_id`, `run_id` (or some
subset). SQL `WHERE` clause includes whichever keys are set.

**Pros**: simple; familiar.
**Cons**: easy to forget a key and leak across scopes; no central
enforcement.

### 9.2 Namespace conventions (Graphiti `group_id`, LangGraph)
A single string key. Anyone with the key sees everything in the
namespace.

**Pros**: trivial to implement.
**Cons**: no actual access control; the namespace IS the
authentication.

### 9.3 RBAC (Cognee)
User → Tenant → Role → ACL → Dataset. Familiar enterprise pattern.

**Pros**: ergonomic; well-understood.
**Cons**: Cognee's enforcement is at the Python application layer,
NOT database-level Row Level Security. Direct DB access bypasses
ACLs.

### 9.4 Capability tokens (Mneme, sometimes Zep)
A token that *is* the authorization. The token's content
(workstream-id, scope, expiry, signature) determines what the
holder can do. There is no separate auth check — the token contents
are the contract.

**Pros**: hardest to bypass; principled cryptographic model.
**Cons**: heavier ceremony; consumers must construct tokens
correctly; ergonomics suffer if you don't ship a sugar layer.

### Ergonomic escape hatch
Even capability-token systems should ship a `AddMneme(opts =>
{ opts.WorkstreamId = "x"; })` ergonomic wrapper that constructs
the token internally. Force consumers to drop down to raw
`CapabilityToken` only when they need cross-workstream queries.

### Transport-level identity
- **Mem0 / OpenMemory**: identity in the URL path
  (`/api/memories/<user-id>/...`); never in tool args.
- **MCP HTTP**: JWT Bearer claim, validated by
  `AddJwtBearer + RequireAuthorization()`.
- **MCP stdio**: env var (`MNEME_CAPABILITY_TOKEN=...`) set by the
  launching consumer.

---

## 10. The MCP exposure layer

If your memory system exposes itself to other LLM agents, it almost
certainly does so via the **Model Context Protocol (MCP)**. Tool
naming conventions have converged in 2025-2026:

- `remember(content, workstream?)` — write a memory.
- `recall(query, limit?)` or `query(...)` — fuzzy-search memories.
- `forget(id_or_filter)` — explicit deletion.
- `list_recent(limit?)` — enumerate recent memories.
- *(differentiator)* `distill(workstream)` — synthesize bundle.

Variants: Mem0 uses `add_memories` / `search_memory` /
`delete_memories`; Basic Memory uses Obsidian-style `read_note` /
`write_note`. But the `remember/recall/forget` family is now the
gravitational center.

### MCP features beyond `tools`
- **Prompts** (slash commands): `/mneme_context` runs a structured
  prompt with arguments. Surfaces in Claude Desktop and VS Code.
- **Resources** (URI-addressable read-only content):
  `mneme://workstream/{id}/context` — subscribable so clients get
  push notifications when the resource updates.
- **Sampling**: server asks the client to run an LLM on its behalf
  (`sampling/createMessage`). Lets the server be model-agnostic.
- **Elicitation**: server asks the client to ask the user a
  question (`elicitation/create`). Perfect for entity-merge
  confirmation flows.

### Common MCP gotchas
- **C# SDK default annotations are wrong for memory tools.**
  `McpServerToolAttribute` defaults `DestructiveDefault=true` and
  `OpenWorldDefault=true`. For a `query` tool, both are wrong.
  Always set all four annotation properties explicitly.
- **Auto-injected types** — the SDK silently injects
  `CancellationToken`, `McpServer thisServer`, `IProgress<…>`,
  `IServiceProvider`, `[FromKeyedServices]`. Use these instead of
  manually wiring DI; less ceremony, fewer bugs.
- **Long-running tools** must use `TaskSupport=Optional` or
  `Required` (in MAF). Synchronous calls time out at ~30s in most
  clients.

---

## 11. Process / deployment patterns

How the memory service runs alongside the agent:

### 11.1 Embedded
Memory is a library called in-process. Simplest. Mneme Phase 1-7.
LangGraph default. Letta v3 default.

### 11.2 Sidecar
Memory runs as a local process; agent talks to it over Unix socket
or localhost HTTP. Mneme Phase 9. Lets multiple agents in the same
machine share memory.

### 11.3 Service (single-tenant)
Memory runs on a server; agent connects over network. Most "self-
hosted" deployments. Letta self-host. Graphiti self-host.

### 11.4 Managed multi-tenant
Memory runs as a hosted service with multi-tenant isolation. Mem0
Cloud, Letta Cloud, Zep Cloud, Pinecone. Capability-token
mechanics map cleanly here; namespace conventions do not.

### 11.5 The sleep-time compute pattern
Increasingly standard. The user-facing agent stays fast; a
**background "sleep-time agent"** handles memory writes during
quiet periods.
- **Letta** ships this in production (`sleeptime_multi_agent_v4.py`)
  with explicit `safe_create_task` fire-and-forget semantics.
- **Cognee** has equivalent `improve()` loop.
- **Mneme's `DistillationJob` worker** is the same pattern.
- **Reference**: arXiv 2504.13171 "Sleep-time Compute" from the
  Letta team.

### 11.6 Concurrency control
Multiple ingest paths writing the same workstream → races.
Approaches:
- **Optimistic locking** (Letta block writes with `version`
  column). Cheap; retries on conflict.
- **Pessimistic per-workstream lock** (Cognee `try_acquire_*_lock`,
  Mneme's planned SQLite-row-level mutex). Idle/quiet triggers and
  SessionEnd hooks both target the worker; lock deduplicates.
- **Git-worktree isolation** (Letta MemFS): each session writes to
  its own branch; explicit merge later. Niche; powerful when you
  need it.

---

## 12. Observability — measuring memory quality

Memory systems are notoriously hard to debug. The patterns worth
adopting:

### 12.1 OpenTelemetry with GenAI semantic conventions
- The 2026 standard is OTEL GenAI v1.37 conventions:
  `gen_ai.operation.name`, `gen_ai.agent.id`,
  `gen_ai.usage.input_tokens`, etc.
- MAF's `OpenTelemetryAgent` emits these natively. Memory spans
  should be **children** of `invoke_agent` spans using the same
  `ActivitySource` family — traces then show memory as part of
  the agent execution, not a parallel mystery.

### 12.2 Span-time secret redaction
Apply the redactor *at span-attribute write time*, not at log
emission. Cognee's `tracing.py:redact_secrets()` is the reference
implementation.

### 12.3 The `Explain` flag
Critical for debugging retrieval. Mem0 ships `explain=True`; the
result includes per-signal contributions. Without it, you cannot
diagnose "why did this irrelevant memory bubble up?" or "why
didn't the obvious one return?"

### 12.4 In-memory trace buffer
Cognee's `CogneeSpanExporter` keeps a circular buffer of the last
50 traces in-process. Letting users call `getLastTrace()` in the
debugger is a huge developer-experience win for an embedded library.

### 12.5 Per-fact provenance
Every derived item records its origin: `(source_event_id, agent_id,
model, prompt_hash)`. When a wrong fact bubbles up, you can trace
back to the exact prompt + event that created it.

### 12.6 Benchmarks
- **LoCoMo** (Long Conversation Memory) — multi-session memory
  recall. Industry-standard.
- **LongMemEval** — fact-tracking across long contexts.
- Mem0 publishes 92.5 LoCoMo / 94.4 LongMemEval. Mneme should run
  both — and is architecturally positioned to win the temporal
  subcategory of LoCoMo specifically (no competitor has bi-temporal
  modeling).

---

## 13. Integration seams (how memory plugs into agent frameworks)

You will inevitably integrate with an agent framework. The
integration *shape* matters as much as the storage:

### 13.1 MAF (Microsoft Agent Framework)
- Seam: `MessageAIContextProvider` (abstract class, not interface).
- Two methods: `ProvideMessagesAsync` (pre-LLM hook, inject
  context) and `StoreAIContextAsync` (post-LLM hook, write back).
- Session state: `AgentSession.StateBag` (thread-safe dict; JSON
  round-trip survives serialization).
- Workflow checkpoints: implement `ICheckpointStore<JsonElement>` —
  3 methods.
- LLM abstraction: **`Microsoft.Extensions.AI.IChatClient`**
  (Semantic Kernel is superseded).
- Mneme delivers via `Mneme.Agents.AI` NuGet package implementing
  these.

### 13.2 LangGraph (Python, fashion-leading)
- Seam: `Checkpointer` (session state) + `BaseStore` (long-term).
- Concepts to know: nodes, edges, checkpointers, interrupts (the
  "time-travel" pattern).
- Adoption of memory: every node call can read/write to the store.

### 13.3 OpenAI Assistants API
- Seam: `threads` API. Memory is per-thread by design — no built-in
  cross-thread persistence.
- For long-term memory, build on top with your own store, mapping
  `thread_id ↔ workstream_id`.

### 13.4 Google ADK
- Seam: `MemoryService` interface — simple `add_memory` /
  `search_memory` / `clear`.
- Smallest surface area of any framework. Worth studying for
  minimalism.

### 13.5 MCP (Model Context Protocol)
- Cross-framework. Any MCP-aware client (Claude Desktop, VS Code
  Copilot, Cursor, Continue) can consume an MCP memory server.
- The 2026 universal integration story for memory products.

---

## 14. The taxonomy as a comparison matrix

When you read the next memory product's docs, fill in this table:

| Axis | Concrete options |
|---|---|
| **Unit type** | turns / facts / events / blocks / KG nodes / hybrid |
| **Storage substrate** | append-only / mutable doc / vector / graph / hybrid |
| **Tiering** | working only / +session / +short-term / +long-term |
| **Temporal model** | single timestamp / bi-temporal / version-on-write |
| **Ingest scheduling** | sync / async / hybrid sync+async |
| **Distillation timing** | at ingest / async worker / lazy on read |
| **Retrieval** | vector / FTS / graph / filter / hybrid |
| **Score fusion** | RRF / additive with gate / reranker / none |
| **Entity resolution** | none / deterministic / +embedding / +LLM |
| **Identity model** | namespace / three-key scope / RBAC / capability token |
| **Concurrency** | last-writer-wins / optimistic / pessimistic / branched |
| **Deployment** | embedded / sidecar / service / managed multi-tenant |
| **MCP exposure** | none / community-standard / custom / both |
| **Observability** | logs only / OTEL / OTEL+explain / +in-mem buffer |
| **Benchmark** | none published / LoCoMo / LongMemEval / both |

Mneme's filled-in row:

| Axis | Mneme's choice |
|---|---|
| Unit type | episodic events → epistemic-category projections → block-shaped bundles |
| Storage | append-only SQLite event log + projection tables + sqlite-vec + FTS5 |
| Tiering | session (StateBag/derived) + long-term (event log) |
| Temporal | **bi-temporal (valid + system)** — primary differentiator |
| Ingest | hybrid: sync WAL commit, async distillation worker |
| Distillation | async `DistillationJob`; per-stage swappable; bundle index + sections |
| Retrieval | filter → vector (Phase 11) + FTS5 + graph projections |
| Fusion | additive-with-threshold-gate (port Mem0) + adaptive BM25 sigmoid |
| Entity resolution | three-tier: deterministic UUID5 → embedding → LLM-propose+confirm |
| Identity | **capability tokens** — strongest of the surveyed models |
| Concurrency | pessimistic per-workstream SQLite lock for distillation worker |
| Deployment | embedded (Phase 1-7) → sidecar (Phase 9) → cloud snapshot (Phase 10) |
| MCP exposure | full: tools + prompt + subscribable resource + elicitation + sampling |
| Observability | OTEL from Phase 1 + `Explain` flag + per-fact provenance |
| Benchmark | planned: LoCoMo + LongMemEval (Phase 4.5); expected temporal win |

That row is the elevator pitch for Mneme. Anywhere it says "Mneme"
in the right column, you can swap in another product to compare.

---

## 15. The reading order that maximizes your understanding

If you want to internalize the field, read in this order:

1. **This primer** — vocabulary.
2. **`plans/research-existing-systems.md`** — fit/no-fit summary of
   all 19 systems. Skim. Identifies which systems are worth a deep
   look.
3. **`plans/research-zep-sqlite-deepdive.md`** — narrow but deep:
   shows how to evaluate a single substrate (SQLite) for a memory
   product end-to-end, and analyzes Graphiti's source code.
4. **`plans/research-design-lessons.md`** — the meat. §2 has
   per-framework deep dives; §3 is the cross-cutting synthesis;
   §4 stress-tests Mneme against the field; §5 lists concrete
   backlog candidates.
5. **`AGENTS.md`** — Mneme's locked decisions; the "rules of the
   road" for contributors.
6. **`plans/plan.md`** + **`plans/backlog.md`** — concrete phases
   and task list.

After step 4 you should be able to argue for or against any design
choice in any of the 19 systems on technical grounds, not vibes.

---

## 16. The four questions to ask of any memory system

When evaluating a new memory product — yours or someone else's —
the four questions that cut through marketing:

1. **What's the unit of memory and how is it derived from
   conversation?** (Facts? Triples? Files? Raw turns? How does
   extraction work?)
2. **What's the time model and how do you query "as of"
   yesterday?** (Single timestamp? Bi-temporal? Version-on-write?
   Or are you stuck with "now"?)
3. **What's the identity model and how is access enforced?**
   (Namespace? Capability token? Where in the stack is the check?)
4. **What's the read shape — what does the agent actually receive,
   and at what cost?** (Raw rows? An LLM-synthesized bundle?
   Tokens? Latency? Staleness?)

If a product's docs don't make all four answers obvious in 10
minutes, the design is probably under-thought. If they make them
*all* obvious, the product is probably worth a deeper look.

---

*End of primer.*
