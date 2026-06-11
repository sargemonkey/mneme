# Mneme — Design Lessons from Existing Agent Memory Systems

**Prepared:** June 2026
**Scope:** Deep-dive comparison of 19 agent-memory systems against the Mneme
plan, with two angles per system: (a) borrowable design ideas mapped to Mneme
phases, and (b) honest stress-test of where the Mneme plan looks weaker than
the system in question.
**Audience:** Mneme maintainers and any agent contributing to Mneme.

## 0. Purpose — how this differs from `research-existing-systems.md`

The earlier [`research-existing-systems.md`](research-existing-systems.md) is a
**fit/no-fit checklist** that scored each of 19 systems against the consuming
cockpit's (MuxiMuxi) functional requirements and concluded "build bespoke." It
is the right answer to *"should we adopt one of these wholesale?"* — and the
answer is no.

This document asks a different question: *"given that we're building Mneme,
which specific design ideas from each of these systems should we steal, and
where does our plan look weaker than what's already shipping?"*

The two docs are complementary:

| `research-existing-systems.md` | `research-design-lessons.md` (this doc) |
|---|---|
| Per-system **fit** to MuxiMuxi requirements | Per-system **strengths / weaknesses / borrowable ideas** against the Mneme plan |
| Conclusion: build bespoke | Conclusion: build bespoke, but borrow these specific patterns |
| Comparison matrix (✓/⚠/✗ per requirement) | Cross-cutting "patterns to adopt" + "decisions to revisit" sections |
| Audience: the original build-vs-integrate decision | Audience: agents shipping the 11 phases now |

Where this doc duplicates an architecture summary from the earlier doc, it
does so only as a short snapshot before getting to the design-lesson analysis.
Always read both docs in sequence; this one assumes you've seen the matrix.

## 1. The lens — Mneme design surfaces

Every borrowable-idea recommendation in this doc maps to one of Mneme's
design surfaces. This table is the index — when a per-system section says
"borrow X for surface Y," look up Y here.

| Surface | What it is | Owning artifact |
|---|---|---|
| **S1 — Event log schema** | 7-category typed `memory_events`, bi-temporal timestamps, ULID idempotency | `plan.md` §"Per-event fields"; backlog `mem-store-tables` |
| **S2 — Artifact / revocation** | Separate `memory_artifacts` blob store with tombstone field | `plan.md` §"Content + revocation model"; backlog `mem-revocation` |
| **S3 — Classification + redaction** | Inline regex + LLM classifier; per-source defaults; never gates capture | `plan.md` §"Classification engine"; backlog `mem-secret-redactor`, `mem-llm-classifier` |
| **S4 — Projections** | Derived read-models rebuildable from the event log (facts, decisions, hypotheses, goals, entities, decision_chains, graph) | `plan.md` §"Storage architecture"; backlog `mem-projections`, `mem-text-index`, `mem-graph-projection` |
| **S5 — Distillation pipeline** | Multi-stage extract → resolve → synthesize → bundle producing 2-4k-token context | `plan.md` §"Distillation pipeline"; backlog `mem-distillation-extract`, `-bundle`, `-rationale`, `-cross-loop` |
| **S6 — Query API** | `IMemoryQueryAPI` with `CapabilityToken`; no raw-SQL escape | `plan.md` §"Capability-based query API"; backlog `arch-imemoryquery`, `mem-query-api-impl` |
| **S7 — Entity resolution** | Deterministic-key auto-merge only; LLM proposes; human confirms | `plan.md` §"Conservative entity resolution"; backlog `mem-entity-resolution-deterministic`, `-llm-propose` |
| **S8 — Outcome closure** | Action → Decision → Outcome chain with per-source watchers | `plan.md` §"Outcome closure"; backlog `mem-outcome-closure` |
| **S9 — Sync** | Idempotent append-only via ULID; snapshot upload to S3-compatible storage | `plan.md` §"Sync model"; backlog `mem-sync-snapshot` |
| **S10 — Process / deployment model** | Embedded → Sidecar → Service progression | `plan.md` §"Memory agent process model"; backlog `mem-sidecar-host` |
| **S11 — MCP server interface** | `Mneme.Mcp` exposing `query` / `distill` / `ingest` / `revoke` via MCP C# SDK | backlog `mem-mcp-server` |
| **S12 — Degraded modes** | Sound / Degraded / Read-only / Catastrophic; consumers must work without memory | `plan.md` §"Failure / degraded behavior"; backlog `mem-degraded-modes` |
| **S13 — LLM provider abstraction** | Pluggable; default Semantic Kernel; classifier ≠ action model | `plan.md` §"Memory agent process model"; locked-decision table |
| **S14 — Contracts surface** | `Mneme.Contracts` — pure .NET 8 BCL only; no SQLite, MCP, or LLM SDK leakage | `AGENTS.md` architectural rule #1; backlog Phase 0 |

These map to the 11 backlog phases as: Phase 0 = S14, Phase 1 = S1+S2+S3,
Phase 2 = S3 (LLM half) + S2 (revoke), Phase 3 = S4, Phase 4 = S6, Phase 5 =
S5, Phase 6 = S7, Phase 7 = S8, Phase 8 = S11, Phase 9 = S10, Phase 10 = S9,
Phase 11 = v2 vector + autonomous capture.

---

## 2. Per-framework deep dives

Format per framework:

- **Snapshot** — what it is, who maintains, license, runtime
- **Strengths** — what it does well, with specifics
- **Weaknesses** — what it doesn't do, or does poorly
- **Borrowable design ideas for Mneme** — concrete, mapped to a surface (S1–S14)
- **Stress-test for Mneme** — where this system's approach challenges a Mneme assumption; what we should answer

### 2.1 Mem0

**Snapshot.** Apache 2.0 Python (+ TypeScript SDK) memory layer. Source HEAD
`366945965df43aa7084be98d1b5073b62a20b431` (June 2026). OSS v3 (April 2026)
collapsed the prior ADD/UPDATE/DELETE algorithm into **single-pass ADD-only
extraction**: one LLM call per ingestion, memories accumulate, no
invalidation. **Graph store support was entirely removed in v3** (Neo4j
backend gone; legacy Cypher utils are dead code). SQLite is used in OSS but
only as an audit log and a per-session message cache, never as source of
truth — vector store (Qdrant default) is canonical.

**Strengths.**

- **Single-pass extraction wins on benchmarks.** v3 ADD-only scores 92.5 on
  LoCoMo / 94.4 on LongMemEval. The previous ADD/UPDATE/DELETE version
  scored 71.4 on LoCoMo. **+20 points** from dropping LLM-driven
  invalidation. Direct citation: `mem0.ai/research` (June 5, 2026).
  Mechanism: LLMs asked to decide "should this UPDATE/DELETE existing
  memory X?" frequently hallucinate, corrupting the store.
- **Integer-ID anti-hallucination.** When the dedup prompt receives existing
  memories, it gets sequential integers (`0, 1, 2, ...`) instead of UUIDs.
  Mapped back after the response.
  Source: `mem0/memory/main.py:718-722`.
- **Hash dedup (MD5 of text) at insert.** Exact-text duplicate skipped
  pre-embed. Cheap and fast; semantically-similar but differently-worded
  memories both stored (an honest design choice).
- **Adaptive BM25 normalization.** Sigmoid with query-length-adaptive
  midpoint/steepness (`{1-3 terms: midpoint=5.0, steepness=0.7}` …
  `{15+ terms: midpoint=12.0, steepness=0.5}`).
  Source: `mem0/utils/scoring.py`.
- **Additive hybrid scoring with semantic-threshold gate** (NOT RRF). Final
  score is `(semantic + bm25 + entity_boost) / max_possible`. Semantic
  score below `0.1` excludes the candidate from the pool entirely — BM25
  and entity hits cannot rescue a semantically-irrelevant memory.
- **Entity memory-count penalty.** Entities linked to hundreds of memories
  get dampened during boost: `weight = 1.0 / (1.0 + 0.001 * ((n-1)^2))`.
  Avoids "the CEO" boosting every memory.
- **Procedural memory as a single dense blob.** A new memory type in v3
  (`MemoryType.PROCEDURAL`). Single LLM call summarizes agent execution
  trace verbatim (every output unmodified); stored as one embedded record;
  bypasses entity linking and dedup. Purpose: resumable agent execution
  state.
- **`explain=True` returns per-signal score decomposition.** Operational
  gold — semantic vs. bm25 vs. entity contributions surfaced for any
  ranked result. `mem0/utils/scoring.py:99-108`.
- **Reproducible benchmark suite** at
  `github.com/mem0ai/memory-benchmarks`. Memory product credibility comes
  from numbers, and Mem0 publishes them.

**Weaknesses.**

- **Entity resolution is purely embedding-similarity at 0.95 threshold.** No
  deterministic-key merge, no LLM judgment, no human confirmation. Below
  0.95 → new entity. Bad merges happen; no recovery path.
- **No bi-temporal semantics.** Only `created_at` / `updated_at` per
  memory. Cannot answer "what did we believe was true on date X?"
- **No epistemic categories.** Three generic types (`SEMANTIC`,
  `EPISODIC`, `PROCEDURAL`) is the entire taxonomy.
- **Graph layer removed.** v3 migration guide: *"Graph store support has
  been removed entirely."* Cypher utils in `utils.py` are unreferenced.
- **SQLite is not authoritative.** If the vector store is lost, the SQLite
  history is orphaned (no rebuilt-from-events recovery).
- **No content classification or secret redaction at ingest.** Custom
  categories (15 defaults: `personal_details`, `family`, `professional_…`,
  …) are *platform-only* tagging.
- **No revocation / tombstone mechanism.**
- **Temporal reasoning + memory decay + custom categories are platform-only**
  — OSS users get none of it.

**Borrowable design ideas for Mneme.**

- **S5 — Distillation prompt design (high value, near-zero cost).** Pass
  *both* an `ObservationDate` (when the event happened) and a `CurrentDate`
  (now) into LLM extraction; instruct the model to ground all relative
  references (`"last week"`, `"yesterday"`) against `ObservationDate`
  *only*. Quote from `mem0/configs/prompts.py:526-536`: *"'User went to
  Paris last week' is useless 6 months later. 'User went to Paris the week
  of May 15, 2023' is meaningful forever."* This directly improves Mneme's
  `valid_at` accuracy at no architectural cost. Backlog:
  `mem-distillation-extract`.
- **S5 / S7 — Integer ID handles in LLM prompts.** When Mneme's
  distillation or entity-merge prompts include existing facts/events for
  the model to reason over, pass sequential integers as handles, not
  ULIDs. Map back after the call. Prevents the LLM from hallucinating IDs.
  Single source: `mem0/memory/main.py:718-722`. Backlog: every prompt
  that embeds an event-id list.
- **S5 — Capture transitions, not just states.** Mem0's prompt explicitly
  instructs: *"When the user describes changing, switching, replacing,
  stopping, or trying something new in place of something else, the memory
  MUST capture the transition — what the new state is AND what it
  replaces."* `mem0/configs/prompts.py:611-622`. Maps directly onto
  Mneme's Decisions / Hypotheses arc — "we tried X, it failed, switching
  to Y" is far more useful than just "we use Y." Backlog:
  `mem-distillation-extract` prompt design.
- **S4 — `explain=true` score decomposition on every query.** When Phase 4
  ships `IMemoryQueryAPI.QueryAsync`, support a request flag
  (`Explain: bool`) that returns per-signal contributions. Critical for
  diagnosing workstream-isolation bugs and temporal-window bugs.
  Cite Mem0 in the doc string. Backlog: `arch-imemoryquery`,
  `mem-query-api-impl`.
- **S4 — Adaptive BM25 sigmoid normalization (v1 immediate).** SQLite
  FTS5 returns raw BM25 scores. When Mneme's text-index ranker fuses
  multiple results, apply Mem0's query-length-adaptive sigmoid. Pure
  function, ~10 lines, ports cleanly to C#. Backlog: `mem-text-index`.
- **S4 — Additive hybrid score with semantic-threshold gate (Phase 11).**
  When sqlite-vec lands, do *not* implement pure RRF. Adopt Mem0's
  formula with the threshold gate. Strong evidence (the v2→v3 jump) that
  this beats rank-only fusion. Backlog: `mem-vector-search`.
- **S4 — Entity memory-count penalty (Phase 11).** When boosting evidence
  for queried entities, dampen entities that appear in hundreds of
  events. Quadratic penalty formula is tunable. Backlog: `mem-vector-search`
  retrieval scoring.
- **S1 — Procedural memory as an event sub-type.** Mneme's `Actions` and
  `Outcomes` categories are close but expect events to be broken into
  typed facts. Add an `ExecutionLog` ingest mode (sub-type of `Actions`)
  that bypasses fact extraction and stores the entire verbatim agent
  execution trace as a single blob. Restart-safe state for long-running
  tasks. Aligns with Mneme's distinction between metadata-immutable and
  artifact-revocable: the verbatim blob lives in `memory_artifacts`,
  revocable; the metadata event survives.
- **S9 — Periodic reconciliation, not synchronous invalidation.** Mem0's
  evidence (v2→v3, +20 LoCoMo) suggests Mneme's bi-temporal `invalid_at`
  writes should *not* be decided synchronously by the ingest LLM call.
  Instead: accumulate everything (write events; let projections show all
  versions); run a periodic reconciliation pass that *proposes*
  invalidations for human/LLM confirmation through Mneme's existing
  propose-then-confirm pipeline. Backlog: re-scope `mem-ingest-path` to
  not include synchronous invalidation; add a new
  `mem-reconciliation-worker` task in Phase 3 or Phase 5.

**Stress-test for Mneme.**

- **Single-pass extraction is faster *and* more accurate.** Mem0 scores
  92.5 LoCoMo with one ~6.8k-token LLM call. Mneme's plan describes a
  multi-stage extract/classify/validate pipeline that risks worse
  accuracy at higher cost. Lesson: pile complexity onto *distillation*
  (bundle assembly), keep *ingest* close to single-pass. Already
  documented as "split sync ingest / async distillation" pattern in §3.2.
- **Bi-temporal invalidation may be more theoretically right than
  operationally useful.** Mem0's accumulate-without-reconciling wins on
  every published benchmark. Mneme's auditability argument is real — but
  most consumer agents don't query "what was true on date X?" Honest
  assessment: bi-temporal is correctness-preserving but adds prompt
  complexity (every ingest LLM call must decide "does this invalidate
  existing fact Y?") that Mem0's evidence says is unreliable. **Mitigation
  already in plan:** the propose-then-confirm pipeline. Make sure
  invalidation flows through it, not through the synchronous ingest call.
- **Three scoping IDs (`user_id` / `agent_id` / `run_id`) is simpler than
  capability tokens for the common case.** Most agent developers want
  "memories for this user × this agent × this run." Mneme's capability
  token machinery is correct for multi-tenant security but heavy for
  single-user agents. Recommendation: ship a `simple mode` developer
  ergonomic that maps `(user_id, agent_id, run_id)` → workstream
  automatically; capability tokens stay available behind the same API.
  Otherwise Mneme loses adoption to Mem0's three-parameter call.
- **Mneme has no published benchmark; this is a credibility gap.** The
  bi-temporal model claim is unverified. Run LoCoMo on Mneme as soon as
  Phase 4 is queryable; expect the temporal subcategory (the dimension
  where Mneme should architecturally win) to be the proof point. If
  Mneme doesn't beat Mem0 on temporal LoCoMo, something is wrong.

**Sources.** `github.com/mem0ai/mem0` HEAD
`366945965df43aa7084be98d1b5073b62a20b431`; `mem0/memory/main.py`,
`mem0/configs/prompts.py`, `mem0/utils/scoring.py`,
`mem0/utils/entity_extraction.py`, `mem0/memory/storage.py`;
`mem0.ai/research` (June 5 2026 benchmark publication);
`docs.mem0.ai/migration/oss-v2-to-v3`,
`docs.mem0.ai/platform/features/temporal-reasoning`,
`docs.mem0.ai/platform/features/memory-decay`,
`docs.mem0.ai/platform/platform-vs-oss`.

**Source-code deep dive (Mem0).** *Citations against commit
`366945965df43aa7084be98d1b5073b62a20b431`.*

- **Extraction prompt (verbatim porting target).**
  `mem0/configs/prompts.py:468-944` is `ADDITIVE_EXTRACTION_PROMPT` —
  ~480 lines including 12 named few-shot examples. Critical sections:
  - **Role declaration** (`prompts.py:468-480`): *"You are a Memory
    Extractor — a precise, evidence-bound processor… Your sole operation
    is ADD."* Extracts from **both** user and assistant turns.
  - **Six named inputs** (`prompts.py:481-545`): `## New Messages`,
    `## Summary`, `## Recently Extracted Memories`, `## Existing Memories`,
    `## Last k Messages`, `## Observation Date`, `## Current Date` (+
    optional `## includes`, `## excludes`, `## custom_instructions`,
    `## feedback_str`).
  - **Observation-date rule** (`prompts.py:528-536`): *"This is your
    ONLY temporal anchor for resolving time references. Do NOT use
    [Current Date] to resolve temporal references in messages."* Mneme
    should port this verbatim — it's the single highest-leverage
    prompt-design idea in the codebase.
  - **Transition-capture rule** (`prompts.py:611-622`): explicit
    instruction to emit "switched from X to Y" facts when changes are
    described.
  - **No-detail-contamination rule** (`prompts.py:689`): prevents the
    LLM from importing details from existing memories into new
    extractions unless the new message references them.
  - **Output schema** (`prompts.py:918-943`): JSON with `{id (string
    integer), text, attributed_to: "user"|"assistant", linked_memory_ids:
    [UUID]}`. **`id` is sequential integer**; the existing-memory mapping
    is built in `main.py:715-722` (`uuid_mapping = {str(idx): mem.id
    for idx, mem in enum(existing_results)}`) so the model never sees
    UUIDs in its input or output. This is the anti-hallucination trick.
  - **Builder**: `prompts.py:1016-1062`
    `generate_additive_extraction_prompt()`; past messages truncated
    at `PAST_MESSAGE_TRUNCATION_LIMIT = 300` chars (`prompts.py:965`).

- **Write path (8-phase pipeline).** Entry: `main.py:574-661`
  `Memory.add()`. Dispatch on `memory_type`:
  - `procedural_memory` branch → `_create_procedural_memory()`
    (`main.py:1646-1683`); single LLM call with
    `PROCEDURAL_MEMORY_SYSTEM_PROMPT`; `remove_code_blocks()`; single
    `_create_memory()`. **Bypasses all 8 phases** — no hash dedup, no
    entity linking, no session message save.
  - Default → `_add_to_vector_store()` (`main.py:663-972`):
    - Phase 0 (`main.py:318-325`): build session scope, fetch last 10
      messages from SQLite cache.
    - Phase 1 (`main.py:710-715`): query-embed parsed messages; vector
      search `top_k=10` for existing memories.
    - Phase 2 (`main.py:732-748`): single LLM call with
      `response_format={"type": "json_object"}`; parse JSON.
    - Phase 3 (`main.py:772-774`): batch-embed all extracted texts
      (one `embed_batch` call).
    - Phase 4/5 (`main.py:787-806`): MD5 hash of text; skip if already
      in `existing_hashes`; lemmatize for BM25.
    - Phase 6 (`main.py:831,857`): batch insert into vector store +
      batch history into SQLite.
    - Phase 7 (`main.py:866-956`): batch entity extraction → batch
      embed → entity-store search → if score ≥ 0.95 merge linked_ids
      else insert new entity. **All entity work is batched.**
    - Phase 8 (`main.py:959`): save messages to SQLite session cache.

- **Pinning test for write path** (`tests/test_memory.py:518-556`):
  asserts `embed.call_count == 1` and `embed_batch.call_count == 1`,
  proving each ingest costs exactly 1 query embedding + 1 batch
  embedding of extracted memories regardless of input message count.

- **LLM provider abstraction** (`mem0/llms/`). Factory pattern:
  `LlmFactory.create(provider, config)` (`factory.py:50-104`); 16
  providers registered (`factory.py:36-66`): `openai`, `anthropic`,
  `azure_openai`, `gemini`, `groq`, `together`, `aws_bedrock`,
  `litellm`, `deepseek`, `ollama`, `lmstudio`, `vllm`, `langchain`,
  `xai`, `sarvam`, `minimax`. `_is_reasoning_model()`
  (`base.py:65-99`) strips temperature/top_p/max_tokens for GPT-5 family
  reasoning models. **`gpt-5-mini` is the default**
  (`mem0/llms/openai.py:34`) but is NOT classified as reasoning.

- **Idempotency**: **none at the event level.** No `event_id` dedup,
  no client-id correlation. Only protection is MD5(text) hash check
  (`main.py:800-803`). Submit same conversation twice → LLM emits
  similarly-worded-but-different facts → both stored. Mneme's
  ULID-based event IDs are a clear improvement here.

- **Scoring** (`mem0/utils/scoring.py`, complete 113-line file, SHA
  `2076c42`). The `score_and_rank()` function is the entire fusion
  logic. Verbatim:
  ```python
  ENTITY_BOOST_WEIGHT = 0.5
  # max_possible = 1.0 + (1.0 if has_bm25) + (0.5 if has_entity)
  if semantic_score < threshold: continue   # HARD GATE
  raw = semantic + bm25 + entity
  combined = min(raw / max_possible, 1.0)
  ```
  All weights/sigmoid params/threshold are **hard-coded constants**;
  only `threshold` is caller-configurable (default `0.1`). BM25 source
  is `vector_store.keyword_search()` — Qdrant returns native sparse
  BM25; stores without it silently drop the signal.

- **Entity merge** (`main.py:414-455` sync, `main.py:866-956` batch,
  `main.py:1898` async). Threshold `0.95` is **hard-coded in all three
  paths**. Merge action: union `linked_memory_ids`, update payload.
  No LLM. **No deterministic-key path** (no email-normalization, no
  GitHub-ID lookup).

- **Entity boost dampening** (`main.py:1515-1517`):
  `weight = 1.0 / (1.0 + 0.001 * ((n-1)**2))`. `0.001` hard-coded.

- **Graph remnants** are dead code. `mem0/memory/utils.py` still
  contains `sanitize_relationship_for_cypher()` and
  `remove_spaces_from_entities()` but they're not imported anywhere in
  v3 `main.py`. Regression fence: `tests/test_memory.py:838-855`
  manually sets `memory.graph = None` and asserts it stays `None` after
  `reset()`. **`Memory.__init__` does not initialize `self.graph`** —
  the attribute doesn't exist on a fresh `Memory` instance.

- **Pinning tests for scoring/explain** (`tests/utils/test_scoring.py`):
  - `test_threshold_gates_on_semantic`: BM25=0.99 cannot rescue a
    candidate with semantic=0.05 under threshold=0.1.
  - `test_all_three_signals`: exact formula `(0.8 + 0.6 + 0.3) / 2.5`
    pinned to `pytest.approx`.
  - `test_weight_value`: `ENTITY_BOOST_WEIGHT == 0.5` pinned as a
    constant.
  - `test_search_explain_includes_score_details`
    (`test_memory.py:151-179`): `explain=True` returns
    `{semantic_score, bm25_score, entity_boost, final_score, threshold}`.

- **Managed-vs-OSS gap (code evidence).** There is **no `if managed:`
  branch** in OSS. The gap is structural:
  - `MemoryClient` (`mem0/client/main.py`) is a thin HTTP wrapper to
    `https://api.mem0.ai/`. Zero local processing.
  - `MemoryConfig` (`mem0/configs/base.py`) **has no fields** for
    `decay`, `custom_categories`, `temporal_reasoning`,
    `reference_date`, or `structured_attributes`. Managed-only features
    don't even have OSS config-schema stubs.
  - Comments at `prompts.py:463` and `:960` explicitly say *"Ported
    from platform/backend/shared/core/..."* — confirming v3 OSS is a
    direct port of the managed extraction logic. The gap is on
    **retrieval** (temporal reasoning, decay) and **storage**
    (structured attributes, categories), not extraction.
  - `BaseLlmConfig.response_callback: Optional[Callable]`
    (`mem0/llms/configs.py`) is the only managed hook in OSS — no-op
    by default.

- **Direction of project (commit log).** GitHub API
  `since=2026-04-01` shows ~20 commits, all dated June 5-6 2026 (the
  v3 release batch). Architecturally significant:
  - `a44855af` (Jun 5): `feat(memory): add search score explanations
    (#5102)` — `explain=True` shipped at v3 launch, not bolted on.
  - `d817aa9c` (Jun 5): `fix(oss): parallelize entity boost searches
    (#5377)` — `ThreadPoolExecutor(max_workers=4)`. Latency was the
    constraint at launch.
  - `7ac8ab15` (Jun 5): `fix(vector_stores): normalize scores to
    similarity (higher = better) across all backends (#5391)` —
    **direct lesson for Mneme: normalize all signals to [0,1]
    higher-is-better before any fusion**. Several backends shipped
    returning distance not similarity.
  - `069ea088` (Jun 5): `is_reasoning_model` explicit config override
    for Azure deployments with versioned model names.
  - `ae7f4062` (Jun 5): self-hosted server hardening (pgvector
    upgrade, mandatory admin auth, endpoint security).
  - Conspicuous absence of incremental April-May commits means v3 was
    developed on a private branch, squash-merged at launch.

### 2.2 Letta (formerly MemGPT)

**Snapshot.** Apache 2.0 Python server + TypeScript/Python SDKs + desktop
app + CLI ("Letta Code"). **Major architectural shift since June 2026**:
memory blocks (`human`, `persona`, custom labeled JSON) are now explicitly
**legacy**. The default for all new agents is **MemFS (Context
Repositories)** — a **git-backed filesystem** of markdown files. Blog:
`letta.com/blog/context-repositories`. The codebase still ships memory
blocks (`letta/schemas/block.py`) and `SleeptimeMultiAgentV4` for
backward compatibility.

**Strengths.**

- **Block versioning via SQLAlchemy optimistic locking + BlockHistory snapshots.**
  Every block edit bumps `version`; concurrent writers get `StaleDataError`
  (DB-level hard guard). Full-row snapshot written to `block_history`
  table with `sequence_number` + `actor_type` (agent vs. user). Not a
  diff — full text snapshot per version. Source:
  `letta/orm/block.py`, `letta/orm/block_history.py`.
- **Self-edit tools require exact-string citation.** `core_memory_replace(label,
  old_content, new_content)` fails if `old_content` is not present exactly.
  Forces the agent to read before writing — primary anti-corruption
  mechanism. Source: `letta/functions/function_sets/base.py`.
- **Sleep-time compute pattern (production-validated).** When
  `enable_sleeptime=True`, Letta creates two agents: a primary
  (foreground, restricted to *no* memory writes) and a sleep-time
  (background, full memory tools). Primary responds to user
  synchronously; sleep-time fires-and-forgets to digest the transcript
  into updated blocks. Frequency: every turn or every N turns
  (`sleeptime_agent_frequency`). Run tracked by `Run/RunStatus`. Backed
  by research paper arXiv 2504.13171 (Pareto improvements vs. inference-time
  compute on AIME / GSM benchmarks).
- **MemFS = git-backed markdown filesystem.** Per-agent directory
  `~/.letta/agents/<id>/memory/` with YAML-frontmatter markdown files.
  `system/` subdirectory is always loaded; everything else is
  description-visible / content-on-demand. Edits are git commits;
  push/pull for cloud sync; `letta memory backup/restore/diff/pull`
  CLI for ops. Concurrent memory subagents work in separate git worktrees
  and merge on completion.
- **`/init` does parallel ingest swarm.** Multiple concurrent subagents,
  each in its own git worktree, process codebase + prior Claude Code /
  Codex history in parallel. Merge via git. This is genuinely novel as a
  "many-agent same-memory" pattern.
- **`/doctor` audits memory layout.** Reports redundancy, token usage,
  flags bloat. `letta memory tokens` reports `system/` portion size.
  Operational tooling missing from every other memory framework.
- **AgentType enum specializes prompts.** `letta_v1_agent`,
  `memgpt_v2_agent`, `sleeptime_agent`, `voice_convo_agent` each get
  distinct system-prompt templates. Source:
  `letta/services/helpers/agent_manager_helper.py`.
- **`message_buffer_autoclear`** for stateless / voice use cases — the
  agent forgets between messages. A graceful escape hatch for use cases
  that don't want persistent memory.
- **`block.read_only` flag.** Block is visible to the agent but the agent
  cannot edit it. Used for deployment / template blocks.
- **`FileBlock` subtype** — block linked to a file source with
  `is_open` / `last_accessed_at`, enabling file open/close idioms over
  documents.
- **Agent file (`.af`) format** — full agent state export/import
  (memory + tools + config) as a checkpoint.

**Weaknesses.**

- **No bi-temporal semantics, no validity windows.** Archival passages
  have `created_at` only. Cannot answer "what was true on date X?"
- **No epistemic categories.** Block labels are arbitrary; `human` and
  `persona` are conventions, not enforced.
- **No event log; no rebuildable projections.** `BlockHistory` is full-row
  snapshots per write, not a canonical event stream. If the projection
  layer concept doesn't exist, rebuild-from-events tooling can't either.
- **Archival memory has no native vector index on SQLite.** Uses
  `pgvector` on Postgres but only `CommonVector` BLOB column on SQLite
  — likely brute-force search at any scale.
- **No human-confirmation workflow for memory edits.** Agent edits;
  user sees result.
- **No classification engine, no secret redaction.**
- **No capability-token enforcement.** Shared-block coordination is
  optimistic-lock + per-worktree branch isolation; no auth model on
  reads.

**Borrowable design ideas for Mneme.**

- **S5 — MemFS-style progressive disclosure for distillation output (high value).**
  Mneme's `ContextBundle` should adopt MemFS's two-tier shape: an
  always-in-context **index/TOC block** (~500–1000 tokens — what bundles
  exist, their labels, their staleness) + the **on-demand full bundle**
  (the 2-4k tokens). Maps to MemFS frontmatter's `description` field:
  agent always sees the description, fetches the content only when needed.
  Backlog: re-shape `contracts-distillation-bundle` with `BundleIndex`
  + `BundleSection` records.
- **S6 — Sleep-time pattern for distillation scheduling (high value).** Mneme
  already says distillation is async. Adopt Letta's `Run/RunStatus`
  pattern explicitly: a `DistillationJob` entity with status
  (`created → running → completed → failed`), surfaced via
  `IMemoryQueryAPI` so consumers can poll or subscribe. Don't bury this
  in implementation. Letta's frequency policy (every turn, every N turns,
  compaction-event) is a useful set of triggers; Mneme's equivalents are
  *N-event batch*, *workstream-quiet for T*, *consumer-query-encountered-
  stale-bundle*. Backlog: split `mem-distillation-bundle` into a job
  abstraction + a worker.
- **S5 — "Capture transitions" via `/doctor` analogue.** Mneme should
  ship a CLI / API endpoint `mneme bundle health --workstream X` that
  reports per-workstream token usage, stale bundles (`last_distilled_at`
  vs. newest event), and projection drift. Mirror Letta's `/doctor`
  + `letta memory tokens`. Cheap to build; high ops value during
  development of consumer agents. Backlog: `cross-cutting` new item.
- **S6 — Self-edit pattern (exact-string citation) for entity-merge confirmation.**
  When Mneme's LLM-propose entity merge surfaces to a human, require the
  confirmation API to cite the exact pre-merge entity records (by their
  current canonical name + identifier). Mirrors `core_memory_replace`'s
  fail-on-mismatch pattern: prevents stale confirmations from going
  through. Backlog: `mem-entity-resolution-llm-propose` API design.
- **S11 — Block label standardization (huge MCP impact).** Letta's
  `human` and `persona` are de-facto standard names every consumer
  reaches for. Mneme's MCP server should expose distillation output as
  *labeled blocks* with stable canonical names (`mneme:facts`,
  `mneme:decisions`, `mneme:goals`, `mneme:hypotheses`,
  `mneme:open-questions`, `mneme:recent-outcomes`,
  `mneme:workstream-summary`). Consumer agents using Letta-shaped MCP
  surfaces immediately recognize the convention. Makes Mneme's output
  portable across frameworks at zero cost. Backlog: `mem-mcp-server`
  tool/resource naming.
- **S2 — `block.read_only` analogue for bundle outputs.** Distillation
  bundles served to consumer agents should carry an `IsReadOnly` flag
  (true by default); the MCP server should refuse writes to them.
  Writes go through the event-log path, not back through the
  derived-projection path. Backlog: `contracts-distillation-bundle`.
- **S4 — Snapshot checkpoints for projection rebuild.** Letta's full-row
  `BlockHistory` snapshots make point-in-time rebuilds O(1) from
  nearest snapshot rather than O(N) from the beginning. Mneme should
  snapshot projection state periodically (every N events or every T
  hours); rebuilds replay only events since the last snapshot.
  Operationally critical at scale. Backlog: `mem-projections` should
  include snapshot/checkpoint sub-task.
- **S1 — `FileBlock` analogue (`ReferenceWithSynopsis` enhancement).**
  Mneme's `ReferenceWithSynopsis` quality envelope already covers this,
  but Letta's `is_open` / `last_accessed_at` fields add useful
  per-source state for caching and prefetch hints. Worth adding to the
  envelope.
- **S5 — Sub-pipeline parallelism for distillation by category.**
  Letta's `/init` runs concurrent subagents per worktree. Mneme could
  parallelize distillation across the 7 epistemic categories: 7
  category-specific workers run in parallel against the event log, each
  producing its bundle section; bundle assembly is a join. Mirrors
  Letta's memory swarm. Worth prototyping when distillation latency
  becomes a bottleneck. Backlog: `mem-distillation-bundle` future work.
- **S10 — Agent file (`.af`) analogue for workstream export.** Letta's
  `.af` is a full-agent snapshot. Mneme should support workstream
  export (event log + projections + artifacts for one workstream) as a
  single file — useful for support cases, regulatory data-portability
  requests (GDPR Article 20), and migrating workstreams between
  installations. Backlog: cross-cutting new item; analogue exists in
  Marten / KurrentDB for streams.

**Stress-test for Mneme.**

- **Agent-edits-own-memory vs. external-distillation-pipeline.** Letta
  itself acknowledges the failure mode (*"memories may become messy and
  disorganized over time"*); the sleep-time agent is the workaround.
  Mneme's external pipeline is the principled answer — but the trade
  is **distillation lag**: if an agent queries before distillation
  completes, it gets stale bundles. Mitigate with a staleness indicator
  in every bundle response (`generated_at`, `events_covered_through`)
  and a `force_refresh` parameter for query-triggered re-distillation.
- **Append-only event log overhead vs. mutable blocks.** Letta's
  blocks-only model is simpler and probably faster for interactive chat
  agents. Mneme's event-log is correct for the multi-session,
  multi-agent, auditable use case but adds write amplification (event
  row + projection update for every fact). Mitigation already in plan
  (snapshot checkpoints per backlog `mem-projections`); add it now,
  don't defer.
- **Seven epistemic categories — Letta succeeds with 2-block defaults.**
  Letta's `human` + `persona` cover the bulk of agent use cases; MemFS
  `/init` produces ~5-10 files in practice. Mneme's 7 might be too many
  for the *consumer interface* but reasonable for the *storage model*.
  Mitigation: surface all 7 as MCP blocks but ensure the "always-in-
  context" index block is small enough that consumers can ignore the
  rest. (Aligned with the MemFS borrow above.)
- **Vector-only archival memory undermines bi-temporal differentiator?**
  No — Letta users in practice hit the limits of vector-only archival
  (cannot ask "what goals were active last month?"). Mneme's bi-temporal
  + structured projections is a real differentiator for audit/governance
  agents. *But* Mneme should still expose a semantic-search path over
  event content as a v2 complement (`mem-vector-search`). Absence of any
  fuzzy-recall path would push fuzzy-recall users to Letta even when
  they need Mneme's strengths.
- **No "memory swarm" concurrency story in Mneme.** Letta's git-worktree
  parallel-subagent model is one of the most innovative patterns in the
  space. Mneme's sidecar process model (Phase 9) addresses sidecar-vs-
  embedded but not "many agents writing the same memory simultaneously."
  At Mneme's design-target scale (single user, embedded), this is
  acceptable; at the v3+ multi-user / service scale, this becomes a
  real gap. Document the concurrency contract in Phase 9.

**MemGPT paper insights worth lifting** (arXiv 2310.08560):

- **OS metaphor.** LLM context = RAM, external storage = disk, paging =
  tool calls. Mneme's distillation output to consumer agents *is*
  paging. Frame the API this way in docs; it's the right mental model.
- **Tiered memory is universal.** Main context (in-context) / recall
  storage (searchable history) / archival (structured external) — Mneme
  maps naturally: event log + projections = archival; distillation
  bundle = main-context summary. Mneme doesn't have a per-session
  recall layer (consumer agents have their own); that's fine.
- **Bundle size is parameter, not constant.** `max_tokens` should be a
  query parameter on `DistillAsync`; a 128k context can afford a 4k
  bundle, a 32k context cannot. The "~2-4k tokens" in `plan.md` should
  read "~2-4k default, configurable per request."

**Sources.** `github.com/letta-ai/letta` (commit references throughout —
`45f584b`, `d484cbc`, `9819e44`, `fb59aff`, `1dd563d`, `f02fe15`,
`c79eac7`, `56e7918`); `docs.letta.com/letta-code/memfs`,
`docs.letta.com/letta-code/subagents`,
`docs.letta.com/letta-code/skills`,
`docs.letta.com/guides/ade/overview`;
`letta.com/blog/context-repositories`,
`letta.com/blog/sleep-time-compute`; arXiv 2504.13171 (sleep-time
compute), arXiv 2310.08560 (MemGPT).

**Source-code deep dive (Letta).** *Citations against HEAD
`1131535716e8a31c9a437f8695e25ac98f203a24` (2026-05-14) of
`letta-ai/letta`. MemFS lives in the separate `letta-ai/letta-code`
repo, TypeScript, HEAD `9e514fdf`.*

- **Block schema** (`letta/schemas/block.py:11-75`, SHA `45f584b`):
  Pydantic `Block(BaseBlock)` with `value: str`, `limit: int`
  (default `CORE_MEMORY_BLOCK_CHAR_LIMIT = 100_000` chars in
  `letta/constants.py`), `label`, `read_only`, `description`,
  `metadata`, `tags`, `created_by_id`, `last_updated_by_id`. Null-
  byte sanitization in `BaseBlock.sanitize_value_null_bytes()`
  prevents PostgreSQL encoding errors — Mneme's text storage should
  apply the same pre-write sanitization.
- **Optimistic locking** (`letta/orm/block.py:46-53`, SHA `d484cbc`):
  ```python
  version: Mapped[int] = mapped_column(Integer, nullable=False,
      default=1, server_default="1",
      doc="Optimistic locking version counter, incremented on each state change.")
  __mapper_args__: ClassVar[dict] = {"version_id_col": version}
  ```
  Uses SQLAlchemy's **built-in** `version_id_col` — concurrent writers
  detect stale version and raise `StaleDataError`. **Mneme analogue
  for projection tables**: if Phase 4 projections support direct UPDATE
  (e.g., entity merge), use the same SQLAlchemy-style optimistic
  locking pattern (a `version` column + check on UPDATE). Backlog:
  `mem-projections` concurrency design.
- **Full-row history snapshot** (`letta/orm/block_history.py:13-44`,
  SHA `9819e44`). `BlockHistory` table:
  `{id, label, value, limit, metadata_, actor_type (agent|user),
  actor_id, block_id (FK CASCADE), sequence_number}` with unique
  index on `(block_id, sequence_number)`. **Full prior `value`
  stored, not a diff.** Storage cost is high, but rebuilds are O(1)
  from the nearest snapshot. Mneme should adopt periodic full-row
  snapshots for projection rebuild without O(N) event replay (cite
  this pattern in `mem-projection-snapshots` task).
- **Self-edit tool with exact-string guard**
  (`letta/functions/function_sets/base.py:262-280`, SHA `56e7918`):
  ```python
  def core_memory_replace(agent_state, label, old_content, new_content):
      current_value = str(agent_state.memory.get_block(label).value)
      if old_content not in current_value:
          raise ValueError(f"Old content '{old_content}' not found in memory block '{label}'")
      new_value = current_value.replace(str(old_content), str(new_content))
      agent_state.memory.update_block_value(label=label, value=new_value)
      return new_value
  ```
  **Anti-hallucination mechanism**: failed lookup → ValueError →
  tool error returned to LLM → LLM must re-read block and retry.
  Mneme's entity-merge confirmation API should adopt the same shape:
  the confirmation call must cite the pre-merge canonical names
  exactly; mismatch returns error.
- **Stricter v2** (`base.py:310-379`, `memory_replace`): adds two
  guards beyond `core_memory_replace`:
  - Rejects `old_string` that contains `\nLine \d+: ` line-number
    prefixes (the block display includes line numbers; LLM might
    accidentally include them in the replace pattern).
  - Rejects non-unique matches — if `old_string` occurs > 1 times,
    raise with line numbers to help LLM disambiguate.
  Mneme's structured-edit APIs should adopt similar uniqueness
  guards.
- **Compaction triggers** (`letta/agents/letta_agent_v3.py`, SHA
  `8dabe57`). **Two paths**, both inside `_step`:
  - **Hard trigger** (lines 1217-1294): catches
    `ContextWindowExceededError`, retries up to
    `summarizer_settings.max_summarizer_retries` after compaction.
  - **Soft trigger** (lines 1438-1498): post-step check
    `self.context_token_estimate > compaction_trigger_threshold`.
  Threshold = `context_window * 0.9`
  (`SUMMARIZATION_TRIGGER_MULTIPLIER = 0.9` in `constants.py`).
  Earlier proactive compaction logic at `step()` lines 366-410 is
  **entirely commented out**. Mneme's distillation should adopt a
  similar event-count threshold + on-demand `force_refresh` path.
- **Compaction dispatch** (`letta/services/summarizer/compact.py:134-380`,
  SHA `025f624`). Four modes with graceful fallback chain:
  ```
  self_compact_all → on error → self_compact_sliding_window → all
  self_compact_sliding_window → on error → all
  sliding_window (default) → on error → all
  all
  ```
  Default summarizer models: Anthropic `claude-haiku-4-5`, OpenAI
  `gpt-5-mini`, Google `gemini-2.5-flash`. The `self_*` modes use
  the **agent's own model** for compaction (cheaper, lower latency,
  but agent must respect "do not call tools, output summary only"
  instruction). Mneme's distillation worker should similarly support
  pluggable model selection via `IChatClient`.
- **Compaction prompts** (`letta/prompts/summarizer_prompt.py`, SHA
  `8edd65b`). `SLIDING_PROMPT` (default): *"The following messages
  are being evicted from the BEGINNING of your context window. Write
  a detailed summary that captures what happened in these messages
  to appear BEFORE the remaining recent messages in context… 5.
  Lookup hints: For any detailed content that couldn't fit, note
  topic and key terms that could be used to find it in message
  history later. Keep your summary under 300 words. Only output the
  summary."* **The "lookup hints" instruction is a borrowable
  pattern for Mneme's `ContextBundle`**: include short key terms
  that point to the original event log entries for facts that didn't
  fit. Backlog: add a `LookupHints` section to bundle schema.
- **Archival memory backend** (`letta/orm/passage.py:18-55`, SHA
  `fb59aff`). Dual backend:
  ```python
  if settings.database_engine is DatabaseChoice.POSTGRES:
      from pgvector.sqlalchemy import Vector
      embedding = mapped_column(Vector(MAX_EMBEDDING_DIM), nullable=True)
  else:
      embedding = Column(CommonVector, nullable=True)  # SQLite fallback
  ```
  **`CommonVector` on SQLite has no native index** — brute-force
  search at any scale. ORM declares no HNSW/IVFFlat indices even for
  Postgres (must be added separately as DDL). Mneme's `sqlite-vec`
  integration (Phase 11) sidesteps this gap by using a real vector
  index extension.
- **Sleep-time compute production path**
  (`letta/groups/sleeptime_multi_agent_v4.py`, SHA `1dd563d`):
  - Subclasses `LettaAgentV3`; overrides `step()` and `stream()`.
  - After primary agent response, calls `run_sleeptime_agents()` →
    enqueues background task per sleep-time agent via
    `safe_create_task` (= `asyncio.create_task`). Fire-and-forget.
  - Run tracked by `Run/RunStatus` (`created → running →
    completed/failed`). Caller polls via `response.usage.run_ids`.
  - Frequency guard: `bump_turns_counter_async()` + check `counter %
    sleeptime_agent_frequency == 0`.
  - **System reminder injected to sleep-time agent**:
    *"You are a sleeptime agent - a background agent that
    asynchronously processes conversations after they occur…
    Messages labeled 'assistant' are from the primary agent (not
    you)… Your primary role is memory management."* Critical for
    role clarity. Mneme's distillation worker prompt should
    similarly clarify *who* the worker is and *what* the conversation
    being processed represents.
- **Tools available to sleep-time agent only**: `memory_rethink`,
  `memory_replace`, `memory_insert`, `memory_finish_edits`
  (exit-loop sentinel — once called, agent loop terminates).
  Primary agent in sleep-time mode **lacks** `core_memory_append`,
  `core_memory_replace`, `archival_memory_insert`. Test pinning:
  `tests/integration_test_sleeptime_agent.py:65-78` (SHA `f02fe15`).
- **Shared block coordination across agents**
  (`letta/orm/block.py:80-100`):
  ```python
  groups: Mapped[List["Group"]] = relationship("Group",
      secondary="groups_blocks", lazy="raise",
      back_populates="shared_blocks", passive_deletes=True)
  ```
  Test pin (`integration_test_sleeptime_agent.py:60-72`): both
  primary and sleep-time agent see the **same `block.id`** via
  `blocks_agents` junction. Updates by either are visible to the
  other on next read (subject to optimistic-lock retry under
  contention).
- **DEPRECATED_LETTA_TOOLS** (`letta/constants.py`):
  `["archival_memory_insert", "archival_memory_search"]`. The agent-
  directed paging tools from MemGPT (2310.08560) are **deprecated**.
  Modern path: system-directed compaction via `ContextWindowExceededError`
  + `compact_messages()`. The agent is no longer consulted on what to
  page. **Mneme's distillation is similarly system-directed** —
  consumer agents request a bundle, but they don't control what's in
  it. This is the correct direction.
- **Test fixtures (sleep-time semantics)**
  (`tests/integration_test_sleeptime_agent.py`, SHA `f02fe15`):
  - `:65-78` `test_sleeptime_group_chat` — primary lacks memory-write
    tools; sleep-time has them.
  - `:89-96` — sleep-time fires every N=2 turns: `len(usage.run_ids)
    == (i+1) % 2`.
  - `:140-162` `test_sleeptime_edit` — sleep-time updates
    `fact_block` with "Inter Miami" after user says Messi moved
    there. Pins that actual writes happen.
  - `:60-72` — both agents share same block ID via `blocks_agents`
    junction.
  - `:200-210` `test_sleeptime_agent_new_block_attachment` — new
    blocks attached to main agent must auto-propagate to sleep-time
    agent.
- **Project direction (commit log).** Only **4 commits in 60 days**
  on `letta-ai/letta`, all maintenance:
  - `11315357` (2026-05-14): `fix(security): use JSON instead of
    pickle for sandbox→server tool result transport (#3343)` —
    closes pickle-deserialization RCE in multi-tenant. Letta is
    hardening for production multi-tenant.
  - `bb52a890` (2026-04-08), `f1800c83` / `c71353f9` (2026-04-07):
    CI / issue-bot maintenance.
  **`letta-ai/letta` is in maintenance mode.** Active development
  has moved to `letta-ai/letta-code` (TypeScript, HEAD `9e514fdf`)
  + private Letta Cloud. MemFS is entirely client-side; server has
  no MemFS-specific code beyond `letta/services/memory_repo/` +
  `letta/services/block_manager_git.py` (SHA `d7a7049`) bridge.
  **Mneme should not depend on `letta-ai/letta` having active
  upstream features — the OSS substrate is stable and frozen.**

### 2.3 Zep (managed platform on top of Graphiti)

**Snapshot.** Commercial managed SaaS built on Graphiti (§2.4). Adds
user/thread management, sub-200ms retrieval SLA, dashboard with graph
viz, SOC 2 / HIPAA, audit logs. Python / TypeScript / Go SDKs; no .NET
SDK. Tiered pricing from $125/mo (Flex) to enterprise. Apache 2.0 engine,
proprietary platform.

**Strengths.**

- The first commercial product to expose temporal-graph memory with a polished
  developer API. Sub-200ms p50 retrieval at scale is real.
- "Threads" and "users" as first-class scope concepts; graph visualization
  helps debugging.
- Compliance posture (SOC 2, HIPAA, audit logs, SLAs) is the differentiator
  vs. self-hosted Graphiti.

**Weaknesses.**

- Cloud-only. Incompatible with local-first.
- No .NET SDK.
- Inherits every Graphiti gap that matters to Mneme: no epistemic categories,
  no workstream isolation, no classification engine, no event-log replay
  semantics.
- Vendor lock — pricing tied to per-episode byte cost ("credits").

**Borrowable design ideas for Mneme.**

- **S6 — Query API:** Zep's threads-as-scope concept is conceptually parallel
  to Mneme's workstream-as-scope. Their docs are worth reading for *how to
  describe scope* to users — naming, mental model, "which thread does this
  go to?" UX patterns translate.
- **S4 — Projections:** their graph visualization is a useful diagnostic;
  Mneme should ship a `dotnet mneme graph dump --workstream X --as-of T` CLI
  to dump a workstream's projection state to GraphViz / Mermaid early, for
  the same debugging value. Cheap to add; high value when distillation
  outputs disagree with expectations.

**Stress-test for Mneme.**

- Zep's success suggests a managed offering *is* the eventual commercial
  shape for memory substrate. Mneme's licensing (Apache 2.0) and contracts
  surface (`Mneme.Contracts` shippable as NuGet) are already aligned, but the
  plan has no Phase for "managed deployment of Mneme as a service." Should
  there be a Phase 12 (post-v1) for a hosted offering? Not urgent, but worth
  not painting ourselves into a corner: the capability token model and
  workstream-scoped storage already accommodate it.

### 2.4 Graphiti (Zep's open-source engine)

**Snapshot.** Python library on top of Neo4j / FalkorDB / Amazon Neptune
(Kuzu deprecated). Bi-temporal facts, episode-derived entity/edge
extraction, pluggable LLM, MCP server, Apache 2.0. The most
architecturally relevant existing system — Mneme's bi-temporal schema is
a direct DDL translation from `graphiti_core/edges.py`. See
[`research-zep-sqlite-deepdive.md`](research-zep-sqlite-deepdive.md) §2
for source-level architecture and §3 for the translated SQLite schema.

**Strengths.**

- Bi-temporal model implemented in production, with paper-cited theoretical
  grounding (arXiv 2501.13956, "transaction time vs valid time" pattern).
- Three-tier model (Episodes → Entities → Edges) cleanly separates raw,
  resolved, and relational layers — Mneme's seven-category schema is a strict
  superset (Evidence ≈ Episodes; Facts ≈ Edges; the other five categories are
  additional epistemic types layered on the same edge table; see
  `research-zep-sqlite-deepdive.md` §2.2 for the mapping).
- Prompt assets (`extract_nodes.py`, `extract_edges.py`, `dedupe_nodes.py`,
  invalidation check, summary generation, community labeling) are Apache 2.0
  and battle-tested. **These are the actual asset.** Mneme has already
  committed to porting them (backlog `mem-distillation-extract`,
  `mem-entity-resolution-llm-propose`).
- Hybrid retrieval (BM25 + vector + 3-hop BFS) with RRF / MMR / cross-encoder
  rerankers. Search recipe is well-documented in
  `graphiti_core/search/search_config.py`.
- Incremental ingest (no batch recomputation) is the right shape for an
  always-on memory agent.

**Weaknesses.**

- Python-only. Requires Python sidecar for .NET integration — non-starter for
  Mneme's local-first .NET embedded shape.
- Requires an external graph DB process (Neo4j or FalkorDB). KuzuDB
  (the only embeddable option) is archived, removing "embed Graphiti" as a
  future-proofing argument. See `research-zep-sqlite-deepdive.md` §2.7.
- Entity resolution is **LLM-judgment-only** auto-merge — exactly the failure
  mode Mneme's conservative policy is designed to avoid. Bad merges poison
  memory permanently.
- No epistemic categories; no workstream isolation; no classification engine;
  no capability tokens.
- Episodes are raw data but not an event log with replay semantics — the
  graph is not rebuildable from episodes alone the way Mneme's projections
  are rebuildable from `memory_events`.
- LLM call cost per episode is ~3 minimum, ~12 typical, ~25+ for complex
  episodes. The graph DB is doing trivial work (~5% of ingest latency); the
  LLM dominates. (`research-zep-sqlite-deepdive.md` §2.3.)

**Borrowable design ideas for Mneme.**

- **S1 — Event log schema:** the 4-timestamp bi-temporal pattern is already
  adopted. Confirm column types `INTEGER` (Unix-ms) match `valid_at`,
  `invalid_at`, `created_at`, `expired_at` per `research-zep-sqlite-deepdive.md`
  §3.1. Backlog: `mem-store-tables`.
- **S5 — Distillation:** port `extract_nodes.py`, `extract_edges.py`, the
  invalidation-check prompt, and the summary-generation prompt. Each port
  requires a `NOTICE` update in the same commit per `AGENTS.md` rule. Backlog:
  `mem-distillation-extract`.
- **S7 — Entity resolution:** port `dedupe_nodes.py` for the LLM-propose
  half of Mneme's pipeline, but gate it behind the deterministic-key step
  (we always run deterministic first; LLM propose only fires on ambiguous
  cases). Backlog: `mem-entity-resolution-llm-propose`.
- **S4 — Projections:** Graphiti's `cosine_similarity` / `bm25` / `bfs` /
  RRF / MMR / cross-encoder pattern is the right *search recipe* for Mneme
  to mirror once vector search lands in v2 (Phase 11). For v1, FTS5 + BFS
  (CTE) covers two of three; the recipe documentation is the value.
- **S4 — Graph projection:** 3-hop BFS via `WITH RECURSIVE` SQLite CTE
  matches Graphiti's `MATCH (n)-[*1..3]-(m)` Cypher (worked example in
  `research-zep-sqlite-deepdive.md` §3.5). Backlog: `mem-graph-projection`.

**Stress-test for Mneme.**

- Graphiti's LLM-cost dominance (~95% of ingest latency) means the substrate
  choice (SQLite vs. Neo4j) is mostly irrelevant to throughput. Mneme's
  Phase 4 query API and Phase 5 distillation prompts will live or die by
  prompt quality and LLM call efficiency, **not** by SQLite schema tuning.
  Ensure benchmarking from `cross-cutting/benchmarks` measures end-to-end
  LLM-included ingest latency, not just storage write latency, or we'll
  optimize the wrong thing.
- Graphiti's three-tier (Episodes/Entities/Edges) is simpler than Mneme's
  seven categories. Is the additional epistemic typing (Decisions /
  Hypotheses / Goals / Actions / Outcomes vs. just "Fact edges with type
  labels") worth the schema complexity? **Locked decision** — yes, because
  the distillation prompts can target category-specific synthesis (e.g.,
  "decision rationale" vs. "hypothesis status update") that generic edges
  cannot. But we should validate this on the first real workstream;
  collapse to typed edges if the prompts can't distinguish them in practice.

### 2.5 Cognee

**Snapshot.** Apache 2.0 Python "memory control plane." Source HEAD
`cfb0aa4d0b3ae0154cf9f24e5908263d565341f4`. Three-store architecture:
**Relational** (SQLite + SQLAlchemy + Alembic) for documents/chunks/
provenance/ACL, **Vector** (LanceDB default, local file) for embeddings,
**Graph** (Kuzu default, local file) for nodes/edges/triplets. All three
share a UUID key per `DataPoint`. New v2 API verbs:
`remember`, `recall`, `improve`, `forget`, `serve`, `disconnect`,
`visualize` (legacy v1 verbs `add`, `cognify`, `memify`, `search`,
`delete`, `prune` still exported). Multi-user RBAC default-on
(`ENABLE_BACKEND_ACCESS_CONTROL` env var).

**Strengths.**

- **DataPoint = single generic Pydantic schema.** Anyone defines a
  Pydantic subclass with `metadata={"index_fields": [...]}`, and it
  becomes a graph node type, a vector-searchable entity, and a
  relational record simultaneously. No core-code changes. Extensible.
  Source: `cognee/infrastructure/engine/`.
- **Deterministic UUID5 dedup via `Annotated[str, Dedup()]`.** When
  identity fields are declared, Cognee generates UUID5 from
  `class_name + sorted_field_values` instead of random UUID4. Same
  inputs → same UUID → upsert-on-same-node. This is **exactly Mneme's
  planned "conservative entity resolution: deterministic-key auto-merge"
  with a concrete mechanism**.
- **5-task `cognify` pipeline.** `classify_documents → extract_chunks →
  extract_graph_and_summarize → add_data_points → extract_dlt_fk_edges`.
  The third step produces both a structured `KnowledgeGraph` (nodes +
  edges with descriptions) AND a `SummarizedContent` ("one leading
  sentence + bulleted self-contained facts") — two outputs from one LLM
  call. Source:
  `cognee/api/v1/cognify/cognify.py:get_default_tasks()`.
- **Four extraction-prompt variants shipped, swappable by env var
  (`GRAPH_PROMPT_PATH`).** `generate_graph_prompt.txt` (default balanced),
  `_simple.txt` (compact), `_strict.txt` (no inference / explicit
  categories), `_guided.txt` (allows logically implied facts). Custom
  prompt also supported per call.
- **Temporal mode.** `extract_events_and_timestamps` produces
  `Event(name, at: Timestamp, during: Interval)` nodes; `SearchType.TEMPORAL`
  filters by date range. Native (not via Graphiti). A second `cognee[graphiti]`
  extra integrates Graphiti episodes if Neo4j is present.
- **`pipeline_status` per record + `forget(memory_only=True)`** lets you
  clear *derived* knowledge (vectors, graph) and re-process from raw
  records with new settings, without re-ingest. Operationally
  invaluable.
- **`improve()` is a feedback-weight loop, not retraining.** Session Q&A
  feedback updates `feedback_weight` on the graph nodes/edges that
  contributed to the answer. Tunable `feedback_alpha`. Light, additive,
  non-destructive memory "learning."
- **`GlobalContextSummary` clustering.** `improve(build_global_context_index=True)`
  builds semantic-cluster buckets over `TextSummary` nodes plus a
  dataset root summary — prepend-able to retrievals for orientation.
- **OpenTelemetry baked-in with Cognee-specific semantic attributes**
  (`COGNEE_PIPELINE_TASK_NAME`, `COGNEE_LLM_MODEL`, `COGNEE_RECALL_SCOPE`,
  …). Built-in `redact_secrets()` applied to span attributes. Custom
  in-memory `CogneeSpanExporter` (circular buffer, last 50 traces)
  with `summary()` and `tree()` for debugging.
- **Claude Code plugin auto-captures via lifecycle hooks.** Hook manifest
  in `cognee-integrations/integrations/claude-code/hooks/hooks.json`:
  `SessionStart`, `UserPromptSubmit` (sync inject + async stage),
  `PostToolUse` (async write tool trace as `TraceEntry`), `Stop` (write
  `QAEntry`), `PreCompact` (memory anchor before context reset),
  `SessionEnd` (detached worker syncs to graph). Marker-file dedup
  prevents double-sync. **This is a working blueprint for an agent-host
  capture integration**.
- **Two-tier session cache + permanent graph with explicit `improve()`
  bridging.** Fast write to cache, async distill to graph. Decouples
  latency.
- **`SkillRun` / `SkillImprovementProposal` first-class** — agents
  track what patterns worked / failed with success scores, latency,
  tool traces.

**Weaknesses.**

- **No bi-temporal model.** `DataPoint` has `created_at` / `updated_at`
  / `version` only; no `valid_at` / `invalid_at`. The temporal mode
  adds `Event.at` / `Event.during` but those are *event time on a
  specific Event entity*, not validity windows on facts.
- **No append-only event log as source of truth.** Updates overwrite
  (call `update_version()` then re-add). No replay from a canonical
  event stream; if the graph is corrupted, rebuilding from raw records
  is the recovery (cognify pipeline supports it but it's
  re-extraction-from-scratch, not event replay).
- **Kuzu is not concurrent-safe for multi-agent writes** (file-locking;
  docs explicitly recommend Neo4j for production multi-agent).
- **No capability tokens.** RBAC (User → Tenant → Role → ACL → Dataset)
  is enforced at the API layer but has no fine-grained capability/scope
  concept; cross-dataset queries are role-controlled, not
  capability-checked.
- **No epistemic categories.** Generic `Node(id, name, type, description)`
  tuples; node "type" is whatever the LLM decides.
- **`forget` is hard delete** (no tombstone). `everything=True` also
  prunes session cache. Raw files preserved as a sole safety net.
- **No conflict resolution model for session→graph bridging** documented;
  presumably additive merge.
- **Cognitive-science marketing claim has no codebase grounding.**
  No DIKW, no ACT-R, no Ebbinghaus, no spaced-repetition cited or
  implemented. The cited paper (arXiv 2505.24478) is about
  KG+LLM retrieval optimization, not cognitive science. Worth noting
  because Cognee's positioning leans heavily on the claim.

**Borrowable design ideas for Mneme.**

- **S7 — Deterministic UUID5 dedup (high value, near-zero cost).**
  Cognee's `Annotated[str, Dedup()]` mechanism is the concrete
  implementation of Mneme's "deterministic-key auto-merge." Adopt: for
  any entity field set declared as identity (email + github_id + stripe_id),
  generate `event_id` or `entity_id` as `UUID5(category_name + sorted(identity_fields))`.
  Idempotent inserts become structural, not LLM-driven. Backlog:
  `mem-entity-resolution-deterministic` should reference this pattern.
- **S4 — `pipeline_status` field for selective re-projection (high value).**
  Mneme's plan describes "rebuild projections from events" but doesn't
  detail the *selective* rebuild path. Borrow Cognee's pattern: maintain
  a `event_processing_log(event_id, projection_name, status, processed_at)`
  table. Re-project = WHERE `status != COMPLETED`. Re-extract with new
  prompt logic = mark status `PENDING` for affected events; worker
  picks them up. Enables prompt iteration without full rebuilds.
  Backlog: `mem-projections` should include this table.
- **S5 — Steal Cognee's `SummarizedContent` prompt shape.** "One
  leading sentence stating what the input is about, followed by a
  bulleted list of self-contained facts." Cognee's lesson: the
  *self-contained facts* constraint is critical — each bullet stands
  alone, survives mid-context truncation. Adapt for Mneme's
  `ContextBundle` section format. Backlog: `mem-distillation-bundle`
  prompt design.
- **S5 — `GlobalContextSummary` analogue → `WorkstreamOrientationSummary`.**
  After distillation, generate a single-paragraph "where are we"
  summary for the workstream that prepends every bundle. Gives the
  consuming LLM orientation before the detailed bullets. Backlog:
  new `mem-distillation-orientation` subtask in Phase 5.
- **S5 — Auto-routing in `recall()`.** Cognee dispatches between
  retrieval strategies via rule-based query classification (no ML):
  summary queries → `GRAPH_SUMMARY_COMPLETION`; relationship queries
  → `GRAPH_COMPLETION_CONTEXT_EXTENSION`; time queries → `TEMPORAL`;
  code queries → coding-rules retriever; quoted phrases → lexical.
  Mneme's `IMemoryQueryAPI` could dispatch similarly between epistemic
  categories, temporal windows, semantic similarity, and structured
  lookup. Cheap to implement (regex + keywords); cuts consumer-agent
  cognitive load. Backlog: `mem-query-api-impl` dispatcher.
- **S3 — OpenTelemetry from Phase 1 with Mneme-specific semantic attributes.**
  Don't bolt on telemetry later. Define `mneme.event.ingest`,
  `mneme.classify.run`, `mneme.entity.resolve` (with `method:
  deterministic|proposed`), `mneme.distill.run` (input/output tokens,
  bundle size), `mneme.projection.rebuild` (projection_name,
  duration_ms, row_count), `mneme.secret.redact` (fields_redacted_count).
  Borrow Cognee's `redact_secrets()` for span attributes — apply
  inline at emission, just like the ingest-time content redactor.
  Backlog: new cross-cutting item `obs-otel-baseline`.
- **S8 — `SkillRun` shape for the Outcomes category.** Cognee's
  `SkillRunEntry(selected_skill_id, task_text, result_summary,
  success_score, feedback, error_type, tool_trace, latency_ms)`
  maps cleanly to Mneme's `Outcomes`. Ensure Outcomes can capture all
  these fields. Closes the action→outcome loop with quantifiable
  feedback. Backlog: `contracts-event-categories` Outcomes record
  shape.
- **S11 — Adopt `remember` / `recall` / `improve` / `forget` as MCP
  tool names (high adoption impact).** These verbs have won
  mindshare across Mem0, Cognee, several MCP memory servers. Mneme's
  planned `query` / `distill` / `ingest` / `revoke` is technically
  cleaner but doesn't match consumer-agent muscle memory. **Recommended
  MCP tool set**:
  - `remember` → wraps `Ingest`
  - `recall` → wraps `Query` + `Distill`
  - `improve` → triggers re-distillation / merge confirmation
  - `forget` → wraps `Revoke`
  - `distill` retained as an explicit "give me the workstream context
    bundle" tool — Mneme's differentiator
  Doesn't change `IMemoryQueryAPI` (the .NET surface). Just renames at
  the MCP edge. Backlog: `mem-mcp-server` tool naming.
- **S6 — `DataItem`-style wrapper for `Ingest`.** Cognee's
  `DataItem(data, label, external_metadata, data_id)` lets callers
  attach metadata without the core path needing to understand it.
  Mneme's `IngestAsync(CaptureEvent)` already has this shape via
  `Provenance`, but consider an even thinner wrapper for the MCP
  `remember` tool: `MemoryItem(content, label?, external_id?, workstream?)`.
- **S2 — Workstream isolation by working directory (Claude Code
  pattern).** Cognee's plugin defaults to `per-directory` session
  scoping — each cwd gets its own session ID. Mneme should support
  the same for the eventual capture-side defaults: in addition to
  explicit workstream IDs, allow consumers to derive workstream from
  cwd, git branch, or process group. Backlog: capture-side defaults
  in consumer integration docs (`consumer-architecture-reference.md`).
- **S1 — Claude Code lifecycle-hook taxonomy for the capture spec.**
  Cognee's hook manifest shows which lifecycle events are worth
  capturing: `SessionStart`, `UserPromptSubmit` (sync inject + async
  capture), `PostToolUse` (tool trace), `Stop` (QA pair), `PreCompact`
  (memory anchor before context reset), `SessionEnd` (final sync).
  This is a ready-made taxonomy for what consuming agent hosts should
  emit to Mneme. Document it as a "recommended capture set" in
  `consumer-architecture-reference.md`.

**Stress-test for Mneme.**

- **Generic DataPoints vs. fixed 7-category schema (the real challenge).**
  Cognee's DataPoint lets anyone add a typed entity that becomes a
  graph node + vector record + relational row with no core changes.
  Mneme's 7-category schema is rigid by design — but if a consumer
  needs "Constraint" or "Risk" or "Stakeholder" as a first-class type,
  they fork. **Mitigation:** make epistemic categories runtime-extensible
  in a later phase. The 7 stay as defaults; consumers register custom
  categories via `CapabilityToken`-scoped registration with their own
  classifier hints. Backlog: new phase task `arch-category-extensibility`
  (post-v1).
- **Synchronous pipeline latency.** Cognee's `remember(session_id=…)`
  returns immediately (cache write); KG processing happens in the
  background. Mneme's plan describes a 7-step pipeline on the ingest
  path. **Already covered by §3.2 (split sync/async)** — make sure
  Phase 1's `mem-ingest-path` task explicitly documents the split.
- **No `memory_only=True` equivalent in Mneme's revocation model.**
  Cognee's `forget(dataset, memory_only=True)` clears *derived* state
  while preserving raw — Mneme's append-only log naturally supports
  this but no API exposes it. Add `RebuildProjectionAsync(name,
  filter?)` to `IMemoryQueryAPI` (capability-checked: admin only) or
  to a separate `IMnemeAdmin` interface. Without it, prompt iteration
  requires full event replay.
- **No feedback-weight learning loop.** Cognee's `feedback_weight` on
  graph nodes is additive, non-destructive, no model retraining.
  Mneme has `Confidence` and `SourceReliability` but no path for
  Outcomes to update them. Worth adding: when an Outcome links back
  to a Decision that cited specific Evidence and Facts, route the
  outcome's success_score to update an `influence_weight` on those
  records. Closes the loop without retraining. Could ship in Phase 7
  alongside outcome closure. Backlog: extend `mem-outcome-closure`.
- **No `recall()`-style synthesized answer.** Cognee's default `recall`
  returns an LLM-synthesized answer from graph context;
  `only_context=True` returns raw context. Mneme returns bundles, not
  answers. Consumers will expect an "answer this from memory" surface.
  Two options: (a) keep Mneme as substrate (bundles only) and let
  consumers do the synthesis themselves — defensible, matches plan;
  (b) ship `AnswerAsync(question, workstream, token)` at the MCP edge
  that pipes bundle + question through one LLM call. **Recommended:**
  (a) in v1; (b) as Phase 12+ post-v1 if users keep asking.
- **Capability tokens vs. RBAC.** Cognee's RBAC is simpler to explain
  and ships default-on. Mneme's capability tokens are more powerful
  but more ceremony. (Same Mneme weakness as identified by Mem0 §2.1.)
  Recommendation: capability tokens as the underlying mechanism;
  expose an RBAC-style developer ergonomic over it
  (`AddMnemeMemory(opts => opts.User = "alice"; opts.Workstream = "X")`
  internally creates a workstream-scoped token).

**Sources.** `github.com/topoteretes/cognee` HEAD
`cfb0aa4d0b3ae0154cf9f24e5908263d565341f4` (**v1.1.2, 2026-05-30**);
`cognee/__init__.py`, `cognee/api/v1/cognify/cognify.py`,
`cognee/infrastructure/engine/`, `cognee/memory/entries.py`,
`cognee/modules/engine/models/Event.py`,
`cognee/modules/observability/tracing.py`,
`cognee/shared/data_models.py`;
`docs.cognee.ai/core-concepts/main-operations/{remember,recall,improve,forget}.md`,
`docs.cognee.ai/core-concepts/building-blocks/datapoints.md`,
`docs.cognee.ai/setup-configuration/graph-stores.md`,
`docs.cognee.ai/core-concepts/multi-user-mode/`;
`github.com/topoteretes/cognee-integrations` (Claude Code plugin
hooks); arXiv 2505.24478 (cited paper).

**Source-code deep dive (Cognee).** *Citations against commit
`cfb0aa4d0b3ae0154cf9f24e5908263d565341f4` (= v1.1.2).*

- **DataPoint schema** (`cognee/infrastructure/engine/models/DataPoint.py:1-110`).
  Beyond the base fields previously documented, the schema includes:
  `ontology_valid: bool`, `version: int`, `topological_rank: int|None`,
  `source_pipeline`, `source_task`, `source_node_set`, `source_user`,
  `source_content_hash` (provenance), `feedback_weight: float = 0.5`
  (the `improve()` target), `importance_weight: float = 0.5`. **Every
  DataPoint carries full pipeline provenance.** Mneme's `Provenance`
  envelope already covers most of this, but `source_pipeline +
  source_task` is a cleaner schema for "which pipeline produced this
  artifact" than free-form `producer` strings.
- **Deterministic UUID5 logic** (`DataPoint.py:72-110`). The
  `__init__` override checks for identity fields; if present and `id`
  was not explicitly provided, generates `uuid5(NAMESPACE_OID,
  f"{class_name}:{'|'.join(normalized_field_values)}")`. **Normalization:
  `value.lower().replace(" ", "_").replace("'", "")`** — exactly the
  kind of canonicalization Mneme's entity-resolution plan needs to
  specify. Mneme should pick a similar canonical-form spec per
  identity-field type (emails: lowercase + strip dots in localpart;
  IDs: as-is; names: lowercase + collapse whitespace) and document it
  alongside `mem-entity-resolution-deterministic`.
- **Annotation-based markers**
  (`cognee/infrastructure/engine/models/FieldAnnotations.py`):
  `_Embeddable`, `_LLMContext`, `_Dedup` are Pydantic annotation
  markers consumed by `__pydantic_init_subclass__` to auto-populate
  `metadata["index_fields"]`. **Mneme analogue**: use `[Embed]`,
  `[Identity]`, `[LlmContext]` attributes on `record` properties so
  the ingest pipeline can derive vector targets, identity keys, and
  LLM-context fields declaratively. Removes boilerplate. Reflection
  cost is one-time per type.
- **cognify orchestration**
  (`cognee/api/v1/cognify/cognify.py:get_default_tasks()`, ~230-275).
  Five `Task` objects in order:
  ```python
  Task(classify_documents),
  Task(extract_chunks_from_documents, max_chunk_size=..., chunker=...),
  Task(extract_graph_and_summarize, graph_model=KnowledgeGraph,
       config=..., custom_prompt=..., task_config={"batch_size": 100}),
  Task(add_data_points, embed_triplets=..., task_config={"batch_size": 100}),
  Task(extract_dlt_fk_edges),
  ```
  Stages 3-4 batched at 100 by default. **Temporal variant**
  (`get_temporal_tasks()`) swaps stages 3-4 for
  `extract_events_and_timestamps` →
  `extract_knowledge_graph_from_events`. This is direct evidence that
  Mneme should split its distillation pipeline into named, swappable,
  batched task stages, not a monolithic function. Map to
  `mem-distillation-pipeline-stages` (new backlog item).
- **Extraction prompt** (verbatim from
  `cognee/infrastructure/llm/prompts/generate_graph_prompt.txt`):
  *"You are a top-tier algorithm designed for extracting information
  in structured formats to build a knowledge graph. **Nodes** represent
  entities and concepts… **Edges** represent relationships… Every edge
  should include a description when the text supports relevant
  information about the endpoints. The description must use the
  endpoint names, stay dry and efficient… Do not add outside
  knowledge."* Plus rules: never use integers as node IDs; dates in
  `YYYY-MM-DD` format; properties snake_case; *"Non-compliance will
  result in termination."* Three siblings: `_simple.txt`, `_strict.txt`,
  `_guided.txt` — swap via `GRAPH_PROMPT_PATH` env var. Worth
  studying as a porting baseline for Mneme's Facts/Entities extractor.
- **`recall` dispatcher** (`cognee/api/v1/recall/recall.py:215-320`).
  Signature includes `scope: "auto"|"graph"|"session"|"trace"|
  "graph_context"|"all"` and `feedback_influence: float = 0.0`.
  Session search is **token-set-intersection over `(question, context,
  answer)`** — no embeddings (`recall.py:70-130`):
  `scored = [(overlap_count, entry) for each session entry]`. Cheap,
  good enough for short session caches. Mneme should expose a similar
  cheap text-overlap path for `mem-text-index` Phase 4 work before
  vector search lands. Routes are decided by a regex-based query
  router (`query_router.py`) — see test fixtures below.
- **`forget` cascade** (`cognee/api/v1/forget/forget.py`). Dispatch
  table (lines ~65-90): `everything` → `_forget_everything`;
  `memory_only + dataset_ref` → `_forget_dataset_memory`; `dataset_ref
  + data_id` → `_forget_data_item`; `dataset_ref` →
  `_forget_dataset`. All paths call
  `delete_dataset_nodes_and_edges()` + `datasets.empty_dataset()` —
  **hard deletes, no tombstones**. The `memory_only=True` path is the
  only soft path: it clears KG + vectors but preserves the
  `Data` rows, mutating the JSON `pipeline_status` column to remove
  `dataset_id` keys (uses SQLAlchemy `flag_modified()` for mutable-
  JSON dirty marking). Mneme keeps tombstoning as a differentiator —
  but **the `pipeline_status` JSON dirty-mark pattern is a clean
  trick** for the Mneme equivalent (re-projection markers).
- **`improve` operations**. Five concrete steps with no retraining:
  - `apply_feedback_weights`: `feedback_weight += alpha * (score -
    0.5)`, default `alpha=0.1`. **Direct match for Mneme's
    outcome→evidence weight propagation in §3.4 above.**
  - `persist_sessions_in_knowledge_graph`: runs full cognify on
    session Q&A text; nodes tagged `node_set="user_sessions_from_cache"`.
  - `persist_agent_trace_feedbacks`: cognifies tool-trace content
    into `node_set="agent_trace_feedbacks"`. Recent addition (v1.1.x).
  - `memify()`: extracts triplet embeddings from existing graph nodes;
    runs custom enrichment tasks.
  - `sync_graph_to_session()`: writes new graph edges since last
    checkpoint as JSON lines into `graph_knowledge:{user}:{session}`
    cache key, fetched by `recall(scope="graph_context")`.
  - **Single-session mutex** via `try_acquire_improve_lock()` —
    prevents duplicate concurrent runs from `SessionEnd` hook +
    idle-watcher. Mneme's `DistillationJob` needs the same locking
    discipline.
- **Multi-tenancy enforcement (critical caveat)**
  (`cognee/modules/users/permissions/methods/get_all_user_permission_datasets.py`).
  **Application-level Python filter, NOT database-level RLS.** The
  function fetches ACL entries via SQLAlchemy, then filters in Python:
  `[d for d in unique.values() if d.tenant_id == user.tenant_id]`.
  No PostgreSQL `ROW SECURITY POLICY`, no Kuzu-level access control.
  Forget pre-checks via `get_authorized_dataset(user, dataset_ref,
  "delete")` before mutations. **Mneme's capability-token model is
  meaningfully stronger here** (the token *is* the authorization, not
  an out-of-band check that a developer might forget to call) — keep
  this as a Mneme strength claim. Kuzu isolation IS filesystem-level
  (`context_global_variables.py` switches active DB directory per
  user via async context vars) — useful pattern if Mneme ever supports
  per-workstream SQLite file split.
- **Storage backend defaults**. Set entirely by env vars (not
  hard-coded): `GRAPH_DATABASE_PROVIDER=kuzu`,
  `VECTOR_DB_PROVIDER=lancedb`, `DB_PROVIDER=sqlite`,
  `EMBEDDING_PROVIDER=openai`,
  `EMBEDDING_MODEL=text-embedding-3-small`. **`fastembed` recommended
  for local-only.** Mneme's local-first stance maps: ship with
  `fastembed`-equivalent (ONNX MiniLM via `Microsoft.ML.OnnxRuntime`)
  as the default; allow swap to OpenAI via `IChatClient`-style
  abstraction. Backlog candidate: `mem-local-embeddings`.
- **OpenTelemetry pattern** (`cognee/modules/observability/`).
  - **Activation guard** (`__init__.py:new_span()`): `@contextmanager`
    that yields a `_NullSpan` no-op when `COGNEE_TRACING_ENABLED=false`.
    **Zero overhead in default config.** Mneme should mirror this — a
    `MnemeNullSpan` no-op in default builds, real span on opt-in.
  - **Span name + attribute taxonomy** for direct port:
    `cognee.api.{cognify,forget,improve,recall,remember}` plus
    pipeline-task and LLM-call spans. Standard attributes:
    `COGNEE_PIPELINE_NAME`, `COGNEE_DATASET_NAME`,
    `COGNEE_LLM_PROVIDER`, `COGNEE_LLM_MODEL`, `COGNEE_RESULT_COUNT`,
    `COGNEE_DB_SYSTEM`, `COGNEE_VECTOR_COLLECTION`. **Mneme should
    define `Mneme.Diagnostics.MnemeActivitySource`** with span names
    `mneme.ingest.event`, `mneme.classify.run`, `mneme.entity.resolve`,
    `mneme.distill.run`, `mneme.projection.rebuild`,
    `mneme.query.execute` and a Mneme-specific tag prefix.
  - **Secret redaction at attribute-write time**
    (`tracing.py:redact_secrets()`): four regexes for OpenAI keys
    (`sk-…`), `api_key=…`, `Bearer …`, `password=…`; replaces with
    `prefix[:6] + "***REDACTED***"`. **Port the regex set verbatim**
    into Mneme's content redactor — they cover the common cases and
    are battle-tested.
  - **In-memory trace buffer** (`tracing.py:CogneeSpanExporter`):
    circular buffer of last 50 traces accessible via
    `cognee.get_last_trace()` without any external backend.
    `CogneeTrace.summary()` returns
    `{operation, total_duration_ms, span_count, breakdown_by_span_name,
    errors}`. **Highly useful for developer ergonomics** — Mneme could
    ship `IMneme.GetLastDistillationTrace()` for the same effect.
- **Claude Code plugin** (`integrations/claude-code/scripts/`). Five
  capture scripts mapping to lifecycle hooks. From
  `store-to-session.py` (the `PostToolUse` handler):
  ```python
  entry = {"type": "trace", "origin_function": tool_name,
           "status": status, "method_params": params,  # ≤4000 bytes per field
           "method_return_value": return_value,        # ≤8000 bytes
           "error_message": error_message,
           "generate_feedback_with_llm": False}
  ```
  Self-reference guard: if `tool_name == "Bash"` and `"cognee" in cmd`,
  skip. Calls `cognee.remember(TraceEntry(...), session_id=...,
  self_improvement=False)` so the heavy `improve()` run happens
  separately on `SessionEnd`. `sync-session-to-graph.py` dedup via
  marker files at `~/.cognee-plugin/final-sync-once/*.done` with 1-hour
  TTL. **Direct lesson for Mneme's planned Claude Code / Codex
  integration**: separate fast capture (write-only) from slow
  reconciliation (read+merge), with marker-file dedup.
- **Pinning tests for query router**
  (`cognee/tests/unit/api/v2/test_query_router.py`):
  - `:12` `route_query("Who won Nobel Prizes?")` →
    `SearchType.GRAPH_COMPLETION`
  - `:40` quoted `'"polonium and radium"'` →
    `SearchType.CHUNKS_LEXICAL` (quotes route to lexical)
  - `:56` `"Summarize everything about Marie Curie"` →
    `SearchType.GRAPH_SUMMARY_COMPLETION`
  - `:77` `"What happened between 1910 and 1920?"` →
    `SearchType.TEMPORAL` (temporal beats relationship for year ranges)
  - `:99,110` negation suppression has a **20-character window** — a
    pragmatic regex-based heuristic. **Mneme's query dispatcher
    backlog item should adopt the same shape**: a thin rule-based
    router that maps query patterns to retrieval strategies.
- **Direction (`cfb0aa4` v1.1.2, 2026-05-30).** UI rebrand
  ("Brains"), conversation-driven search with per-message dataset
  tracking, KG customization UI, agent permission modeling. Postgres
  graph adapter is production-used (recent `e21fc48` cast-fix for
  asyncpg). KG-extraction prompt is now user-editable via UI — a
  product-level admission that one prompt cannot fit all domains.
  Mneme should ship per-workstream prompt overrides (env var
  `MNEME_DISTILLATION_PROMPT_PATH` analogous to
  `GRAPH_PROMPT_PATH`).

### 2.6 LangChain / LangGraph

**Snapshot.** LangGraph is the stateful agent orchestration framework (MIT,
Python + TypeScript). Memory model splits short-term (thread-scoped
checkpointed state) from long-term (namespaced `BaseStore` with semantic /
episodic / procedural cognitive types). Pluggable backends — Postgres,
Redis, Mem0, custom vector stores. No .NET SDK.

**Strengths.**

- The short-term / long-term split is conceptually clean and matches
  cognitive-science memory taxonomies (Tulving's episodic/semantic +
  Anderson's procedural). The taxonomy reads naturally to engineers from a
  cogsci background.
- Pluggable everything: checkpointer backends, store backends, embedding
  models. Backend interfaces are well-documented.
- Thread-scoped checkpointing supports "time travel" — replay agent execution
  from any prior checkpoint. Used heavily by LangGraph users debugging
  multi-agent runs.
- `interrupt` primitive for human-in-the-loop is well-designed and
  serializable across checkpoints.

**Weaknesses.**

- "Cognitive types" (semantic / episodic / procedural) are a generic
  three-category taxonomy. Insufficient for Mneme's 7 epistemic categories;
  a Mneme `Decision` doesn't naturally fit any of the three.
- Checkpointing ≠ event sourcing. Checkpoints are *snapshots* of agent
  state at a point; they're not the canonical append-only event log Mneme
  uses as source of truth. Rebuilding a checkpoint from "earlier checkpoint +
  events since" is not the LangGraph model.
- Namespace scoping is name-based convention, not enforced. No capability
  token model.
- Python / TypeScript only.

**Borrowable design ideas for Mneme.**

- **S4 — Projections:** LangGraph's "time travel" UX is the right pitch for
  Mneme's bi-temporal point-in-time queries. The user-facing story should
  borrow LangGraph's framing ("show me the state of this workstream as of
  Monday at 3pm") — concrete and persuasive — rather than the technical
  "bi-temporal point-in-time query" framing that requires database theory to
  understand. Bake the time-travel framing into the `IMemoryQueryAPI` docs
  and into the MCP tool descriptions in Phase 8.
- **S6 — Query API:** the `interrupt` pattern for human-in-the-loop is a
  good fit for Mneme's entity-merge confirmation flow. When LLM-propose
  flags a merge candidate, the API call should return an `interrupt`-style
  pending-decision token that the consumer surfaces to a human; on confirm,
  the consumer resumes the call with the resolution. This is a cleaner
  cancellation/resumption semantic than the planned
  `ProposeEntityMergesAsync` + separate `ConfirmEntityMergeAsync`. Worth
  prototyping in Phase 6.
- **S5 — Distillation:** LangGraph distinguishes *short-term* (per-thread
  scratch) from *long-term* (cross-session) memory. Mneme's "workstream
  context bundle" is long-term; should there also be a per-agent-invocation
  short-term scratch that Mneme stores but doesn't promote to events? This
  could be a Phase 5 sub-feature: distillation output is consumed within an
  agent run and either promoted to a persisted bundle or discarded. Reduces
  storage pressure for exploratory queries.

**Stress-test for Mneme.**

- LangGraph users are happy with three cognitive types. Is Mneme's seven
  categories an over-design that adds friction for consumer agents? Same
  question as Graphiti (§2.4 stress-test), with a separate data point: even
  LangGraph's taxonomy of 3 is widely considered confusing. Watch the
  consumer experience carefully on the first integration; if consumers
  routinely misclassify events between categories, collapse.
- LangGraph's checkpointer abstraction is *very* generic — works with
  Postgres, Redis, in-memory, etc. Mneme picks SQLite-only. Should
  `Mneme.Contracts` allow alternative storage backends behind the same
  contracts, the way LangGraph does? Per `AGENTS.md` rule #1, Contracts has
  no storage dependency anyway — but the *implementation* `Mneme` package
  is SQLite-only. Worth keeping the `IEventStore` internal abstraction
  pluggable enough that a Postgres-backed `Mneme.PostgreSql` could ship
  later without a breaking refactor. Not a Phase 1 commitment; just don't
  hard-code `Microsoft.Data.Sqlite` types into the projection layer.

### 2.7 LlamaIndex Memory Modules

**Snapshot.** `ChatMemoryBuffer` (windowed message history), `VectorMemory`
(vector store-backed semantic search over conversation),
`SimpleComposableMemory` (combining backends). Primarily a retrieval-
augmented chat history library. MIT, Python + TypeScript, no .NET SDK.

**Strengths.**

- `SimpleComposableMemory` composes multiple memory backends into a single
  query interface. Useful pattern: combine windowed recent-history + vector
  search + custom backend, return merged results.
- `ChatMemoryBuffer` with token-aware truncation is the most-used component;
  ships sensible defaults (LLM-aware token counting, FIFO eviction).
- Extensive backend zoo: Pinecone, Weaviate, Chroma, Qdrant, custom.

**Weaknesses.**

- Not an episodic memory system. No temporal graph, no event log, no
  categories. RAG-over-chat-history library.
- No entity resolution; no distillation beyond windowed summarization.

**Borrowable design ideas for Mneme.**

- **S5 — Distillation:** `SimpleComposableMemory`-style composition is a
  good pattern for `DistillAsync`. A `ContextBundle` is a *composition* of
  outputs from multiple distillers (recent-decisions, current-facts, open-
  hypotheses, entity-summary, recent-outcomes), each producing a token-
  budgeted subsection. The bundle's top-level type should reflect this:
  `record ContextBundle(IReadOnlyList<BundleSection> Sections, ...)` where
  each section names the contributing distiller. This makes the bundle
  inspectable and lets consumers drop sections they don't need (rather than
  receiving a monolithic markdown blob). Backlog: `contracts-distillation-bundle`.
- **S5 — Distillation:** token-aware truncation in `ChatMemoryBuffer` is
  the right defensive pattern for Mneme. Every distiller should target a
  token budget passed in via `DistillationRequest`; total bundle size is
  enforced after composition. Use the LLM provider's tokenizer (via
  Semantic Kernel) not a character count.

**Stress-test for Mneme.**

- None significant. LlamaIndex is a different category of tool (RAG library
  vs. memory substrate); the comparison isn't apples-to-apples.

### 2.8 MCP memory server ecosystem

**Snapshot.** As of the **2025-06-18 MCP spec**, the memory-server
ecosystem has converged on a small set of patterns despite no formal
RFC. Five reference implementations dominate:

| Server | Lang | Backend | Tool count | Notable |
|---|---|---|---|---|
| `@modelcontextprotocol/server-memory` | TS (Node) | JSONL file | 9 | Official reference; toy storage; full overwrite per write |
| Mem0 `openmemory` | Python (FastMCP) | Qdrant + SQLite | 5 | URL-path identity injection; SSE + Streamable HTTP; per-call ACL |
| Graphiti (Zep) `mcp_server` | Python (FastMCP) | Neo4j or FalkorDB | 9 | Async queue ingest; bi-temporal; renamed `add_episode → add_memory` |
| Basic Memory | Python (FastMCP 3.0) | Markdown + SQLite | 22+ | Uses **all four** MCP primitives (tools + resources + prompts + subscriptions); path-traversal guard |
| Cognee | Python | Graph + Vector + SQL | 4 (`remember/recall/forget/improve`) | Cleanest vocabulary in the field |

**Plus**: Microsoft `Microsoft.Agents.AI.Mem0` already ships a
`Mem0Provider` for MAF — Mneme will compete with this directly.

**Strengths (ecosystem-wide).**

- **The vocabulary has converged on `remember` / `recall` / `forget`**
  (Cognee leads; Mem0 uses `add_memories`/`search_memory`/`delete_memories`
  which are recognizable variants). No deployed server uses `query`,
  `distill`, `ingest`, or `revoke`.
- **All 9 reference-server tools now ship `readOnlyHint / destructiveHint
  / idempotentHint / openWorldHint` annotations** (added 2026-05-30, PR
  #3874, commit `64b1cb0`). Annotations are now considered table-stakes
  for well-behaved MCP tools.
- **Mem0's URL-path identity injection** is the cleanest auth pattern in
  the field: identity travels in URL path
  (`/mcp/{client}/sse/{user_id}`), populates Python `ContextVar` at
  mount time, tools never see raw tokens. Decouples auth from
  authorization.
- **Mem0's ACL-before-vector-search pattern**: SQL ACL check returns
  `accessible_memory_ids`; vector search results post-filtered by ID set.
  Correct order for capability-token enforcement.
- **Graphiti's async-queue ingest**: `add_memory` returns immediately
  after enqueueing (`add_episode` → background processing). MCP call
  doesn't block on LLM pipelines.
- **Basic Memory uses MCP prompts**: `/basic_memory` slash command
  appears in Claude Desktop. The only memory server in the survey
  exploiting MCP prompts for discoverability.
- **C# SDK is production-grade.** Three packages (`.Core`, main,
  `.AspNetCore`); attribute-driven tool/prompt/resource registration;
  automatic injection of `CancellationToken`, `McpServer`,
  `IProgress<>`, `IServiceProvider`, `[FromKeyedServices]`; JWT Bearer
  auth via `AddMcp()` + `RequireAuthorization()`; structured-content
  support via `UseStructuredContent = true`; SSE + Streamable HTTP +
  stdio transports; `DistributedCacheEventStreamStore` for stateful
  cross-instance sessions via `IDistributedCache` (horizontal scaling
  even with sampling/elicitation).
- **MCP 2025-06-18 spec adds three primitives Mneme should exploit**:
  - **Resources + subscriptions**: server exposes
    `mneme://workstream/{id}/context`; client subscribes; server emits
    `notifications/resources/updated` after background distillation
    completes. **Zero polling.**
  - **Sampling** (`sampling/createMessage`): server asks client's LLM
    to synthesize — Mneme can be model-agnostic by design, no OpenAI
    key needed for distillation.
  - **Elicitation** (`elicitation/create`): server pops a flat-schema
    form on the client (accept/decline/cancel). **Perfect for
    entity-merge confirmation** — exactly the propose-then-confirm
    flow Mneme's plan describes.

**Weaknesses (ecosystem-wide).**

- **None of the reference servers has bi-temporal validity windows.**
  Graphiti is the closest (bi-temporal edges) but the MCP surface
  doesn't expose `valid_at` / `invalid_at` query parameters. Mneme can
  uniquely answer "what was true on date X?" through the MCP tool
  surface.
- **Graphiti's `group_id` scoping has no access control.** `group_id`
  is namespace-only; any caller can write to any group. Mem0's URL-path
  injection is far stronger. Mneme's capability tokens are the
  strongest of all three.
- **Reference server stores by full-file overwrite** — race conditions
  under concurrent writes; no transactions; no append-only. Toy.
- **No server in the survey ships rich epistemic categories.** Mem0's
  three types (semantic/episodic/procedural) and Graphiti's entity/edge
  taxonomy are the closest. Mneme's 7 categories are the richest
  exposure.
- **Most servers (Mem0, Cognee, Basic Memory) have a per-call
  authorization model that's easy to forget to call** (application-level
  Python filter, not DB-level RLS). Mneme's capability-token-on-token
  model is more secure but requires careful onboarding ergonomics.
- **The C# SDK's `McpServerToolAttribute` defaults are dangerous**:
  `Destructive = true`, `OpenWorld = true` are the default. Tools that
  fail to set these explicitly broadcast scary annotations to clients.
- **Elicitation is "may evolve" in spec.** Claude Desktop and VS Code
  Copilot support not fully documented. Test before depending on it.

**Borrowable design ideas for Mneme.**

- **S11 — Final tool surface for `Mneme.Mcp` (evidence-based).** Adopt
  the community vocabulary. Recommended **5 tools, 1 prompt, 1
  resource**:

  | Tool | Replaces | Annotations | Why |
  |---|---|---|---|
  | `remember` | `ingest` | `ReadOnly=false, Destructive=false, Idempotent=false, OpenWorld=false` | Cognee + Mem0 convention; muscle memory |
  | `query` | (keep) | `ReadOnly=true, Destructive=false, Idempotent=true, OpenWorld=false` | Distinctive; bi-temporal is the differentiator; alias `recall` in description |
  | `distill` | (keep) | `ReadOnly=true, Destructive=false, Idempotent=false, OpenWorld=false`, `UseStructuredContent=true`, `TaskSupport=Optional` | Semantically precise; the differentiator; returns `resource_link` |
  | `forget` | `revoke` | `ReadOnly=false, Destructive=true, Idempotent=true, OpenWorld=false` | `revoke` is non-standard; agents trained on other servers never reach for it |
  | `list_recent` | **NEW** | `ReadOnly=true, Destructive=false, Idempotent=true, OpenWorld=false` | Every competitor has this; agents need to check what's already stored before re-ingest |
  | `mneme_context` (prompt) | **NEW** | — | Surfaces `/mneme_context` slash command in Claude Desktop / VS Code Copilot |
  | `mneme://workstream/{id}/context` (resource) | **NEW** | `audience=["assistant"]`, `priority=0.9` | Subscribable; push updates after background distillation |

  Backlog: rewrite `mem-mcp-server` task with this exact surface;
  add `mem-mcp-prompts`, `mem-mcp-resources`, `mem-mcp-list-recent`.

- **S11 — Async-queue `remember` (high value).** Mneme's `Ingest`
  already writes to the event log first; the MCP `remember` tool should
  return immediately with the `event_id` after the WAL commit, before
  classification / entity resolution / distillation. Mirror Graphiti's
  `add_memory`: *"Episode 'X' queued for processing in workstream Y"*.
  Background workers handle the heavy work. Backlog: confirm
  `mem-ingest-path` returns within ≤50ms after WAL commit.

- **S11 — Subscribable distill resource (high value, differentiator).**
  `distill` returns a `resource_link` to
  `mneme://workstream/{id}/context`. Client subscribes. Background
  distillation completes → Mneme emits
  `notifications/resources/updated`. Client re-reads. **No competitor
  ships this.** Plays directly to Mneme's projection-rebuild
  architecture: the event log is always the truth; resources are
  derived projections; pushing updates is natural. Backlog:
  `mem-distillation-resource-push` (new Phase 8 sub-task).

- **S11 — Sampling-based distillation (graceful fallback).** When
  `thisServer.ClientCapabilities?.Sampling != null`, send
  `sampling/createMessage` to the client's LLM with structured fact
  bundle as `systemPrompt`. Client's model does the synthesis. Mneme
  doesn't hold any API key. Falls back to local LLM (via
  `IChatClient`) if client doesn't advertise sampling. **Mneme becomes
  model-agnostic by design.** Backlog: `mem-distillation-sampling-mode`.

- **S7 — Elicitation for entity-merge confirmation (high value).**
  Mneme's plan calls for propose-then-confirm. MCP elicitation is
  the right transport: server sends `elicitation/create` with flat
  schema `{confirm: bool, canonical_name: string}`. Client shows UI;
  user accepts/declines/cancels. No out-of-band confirmation UI
  needed. Constraint: requires stateful HTTP
  (`options.Stateless = false`), so the Mneme MCP server has two
  deployment modes — stdio (no elicitation, propose-only via Mneme's
  own UI) and stateful HTTP (full propose-then-confirm via
  elicitation). Backlog: `mem-mcp-elicitation`.

- **S11 — All four tool annotations on every Mneme tool.** Verbatim
  defaults table above. Annotations were backfilled across the
  reference server (PR #3874) — Mneme must ship them from day one.
  Defaults of `McpServerToolAttribute` are wrong for `query`
  (`Destructive=true`, `OpenWorld=true` are defaults) — every Mneme
  tool must set them explicitly.

- **S11 — `Mneme.Mcp` should be a thin DI client to `Mneme.Core`.**
  Basic Memory's pattern: MCP tools are HTTP clients to a local
  FastAPI service. Mneme's analogue: MCP tools have constructor-
  injected `IMnemeQueryService`, `IMnemeIngestService`,
  `IMnemeDistillService`, all from DI. Per-request instance lifecycle
  (`WithTools<T>()` re-resolves per call). The MCP layer is just
  protocol translation; no business logic. Backlog: confirm
  `Mneme.Mcp` design separates protocol from service.

- **S2 — Identity injection via env-var + capability token (stdio)
  or JWT Bearer (HTTP).** Stdio: `MNEME_CAPABILITY_TOKEN` env var
  at process launch (Mem0 precedent for local-first). HTTP: JWT
  Bearer with `workstream` + `scopes` claims, validated by
  `AddJwtBearer` + `RequireAuthorization()`. **Never put capability
  tokens in tool args** — every LLM in the chain sees them and could
  leak them. Backlog: `mem-mcp-auth-token-flow`.

- **S3 — Mneme.Mcp ships OpenTelemetry from day 1.** The
  `AspNetCoreMcpServer` sample wires OTLP tracing/metrics out of the
  box. Backlog: copy that wire-up.

- **S6 — Use `IProgress<ProgressNotificationValue>` for distill
  progress.** Distillation is multi-second; emit progress notifications
  via the auto-injected `IProgress<>`. Clients show progress bars.
  Backlog: surface progress emission in `mem-distillation-bundle`.

- **S6 — Mark `distill` with `[Experimental("MCP_Tasks")]
  TaskSupport = ToolTaskSupport.Optional`** when the SDK feature
  stabilizes. Clients track distillation as a long-running task,
  not a blocked request. MAF already wraps such tools in
  `TaskAwareMcpClientAIFunction`.

- **S2 — Path-traversal guard pattern (Basic Memory).** Validate
  every workstream ID parameter against a strict regex
  (`^[a-z0-9][a-z0-9-_]{0,63}$` recommended). Reject early before
  any storage operation. Same pattern Basic Memory uses for filenames
  (`validate_project_path()`). Backlog: `mem-workstream-id-validation`.

- **S11 — Description as system-prompt injection.** Mem0's
  `search_memory` description: *"This method is called EVERYTIME the
  user asks anything."* The description is implicit system-prompt
  guidance to the LLM about when to call the tool. Mneme's tool
  descriptions should be similarly directive (`query`: *"Call before
  responding to questions that may benefit from prior context. Use
  `since` parameter to scope to recent decisions."*).

**Stress-test for Mneme.**

- **Mneme's planned tool names (`query/distill/ingest/revoke`) do not
  match community muscle memory.** Agents trained on Mem0, Cognee,
  Graphiti, Basic Memory reach for `remember`, `add_memory`,
  `recall`, `search`, `forget`. The descriptions can compensate
  partially but agents pick tools by name-match first. **Decision
  required**: rename or alias. Recommendation above: rename `ingest`
  → `remember`, `revoke` → `forget`, keep `query` and `distill`.
- **No `list` tool is a real gap.** Every competitor has one. Agents
  need it to avoid duplicate ingest. Add `list_recent`.
- **Bi-temporal querying isn't exposed in current MCP servers.**
  This is Mneme's strongest differentiator but consumers must know to
  use it. The `query` tool's `since` / `valid_at` parameters need
  rich descriptions and examples (in the `[Description]` attribute)
  explaining "what was true on date X" use cases — otherwise agents
  will never call them.
- **Capability-token onboarding is the adoption risk.** Mem0's
  three-line `add_memories(text)` (with identity from URL) is far
  lower friction than Mneme's "construct CapabilityToken from
  workstream + scope + signature". For the `Mneme.Mcp` stdio
  deployment path, ergonomics must match Mem0: ship a startup
  helper that constructs a default token from `MNEME_WORKSTREAM`
  env var; expose the full capability machinery only for the HTTP
  multi-tenant deployment.
- **MCP elicitation requires stateful HTTP** which conflicts with
  Mneme's local-first stdio default. Mneme needs *two* deployment
  modes: `Mneme.Mcp.Stdio` (stdio, no elicitation; entity-merge
  confirmation falls back to Mneme's own propose queue) and
  `Mneme.Mcp.Http` (stateful HTTP, full elicitation support). Don't
  paper over this distinction — document it.
- **Microsoft.Agents.AI.Mem0 already exists.** MAF developers
  evaluating memory will reach for it first. Mneme's competitive
  positioning vs Mem0Provider must be clearly articulated:
  bi-temporal, structured (7 categories), local-first/SQLite, .NET-
  native (no Python server). Ship the comparison in the
  `Mneme.Agents.AI` README.

**Source-code deep dive (MCP ecosystem).**

- **Reference server tool registration**
  (`modelcontextprotocol/servers:src/memory/index.ts:240-500`, SHA
  `7b4c683`). Pattern via `server.registerTool(name, {title,
  description, inputSchema, outputSchema, annotations}, async handler)`.
  Dual return: `content` (text fallback) + `structuredContent` (typed
  JSON). All 9 tools shipped with full annotations after commit
  `64b1cb0` (PR #3874, 2026-05-30).
- **Reference server storage** (`index.ts:60-115`). `loadGraph()`
  reads + splits JSONL; `saveGraph()` **rewrites entire file** on
  every mutation (`fs.writeFile`). Race condition under concurrent
  writers; no transactions; no append. Mneme's append-only event log
  is architecturally superior. Search (`index.ts:157-185`) is
  `toLowerCase().includes(query)` substring match — no vector, no BM25,
  no temporal.
- **Mem0 OpenMemory tool decorators**
  (`mem0ai/mem0:openmemory/api/app/mcp_server.py:64,149,227,296,370`,
  SHA `70aa523`). FastMCP `@mcp.tool(description="...")` decorator on
  five async functions: `add_memories`, `search_memory`,
  `list_memories`, `delete_memories`, `delete_all_memories`. The
  `search_memory` description: *"This method is called EVERYTIME the
  user asks anything"* — that's deliberate prompt injection.
- **Mem0 ACL-before-vector-search**
  (`openmemory/api/app/mcp_server.py:170-193`):
  ```python
  user_memories = db.query(Memory).filter(Memory.user_id == user.id).all()
  accessible_memory_ids = [m.id for m in user_memories
                           if check_memory_access_permissions(db, m, app.id)]
  hits = memory_client.vector_store.search(query, ..., filters={"user_id": uid})
  allowed = set(str(mid) for mid in accessible_memory_ids)
  results = [r for h in hits if (h.id in allowed)]
  ```
  Mneme's `Query` should follow the same shape: capability-token
  resolution → workstream filter → backing-store query → post-filter
  by resolved IDs.
- **Mem0 URL-path identity injection**
  (`openmemory/api/app/mcp_server.py:435-462`). Two mounts:
  `/{client_name}/sse/{user_id}` (SSE) and
  `/{client_name}/http/{user_id}` (Streamable HTTP). Identity →
  `ContextVar.set()` at mount time → tools read context vars. No
  identity in tool args. Best-in-class auth pattern; Mneme's HTTP
  deployment should adopt JWT-Bearer equivalent (claim
  `workstream` instead of path segment).
- **Graphiti async-queue ingest**
  (`getzep/graphiti:mcp_server/src/graphiti_mcp_server.py:387-400`,
  SHA `833bc5d`):
  ```python
  await queue_service.add_episode(group_id=..., name=..., content=...,
                                   source_description=..., episode_type=..., uuid=...)
  return SuccessResponse(message=f"Episode '{name}' queued for processing in group '{effective_group_id}'")
  ```
  Mneme's MCP `remember` should mirror this immediate-return shape
  after WAL commit. Tool surface confirmed in test
  `mcp_server/tests/test_mcp_integration.py:55-67` (SHA `7560ba7`):
  `['add_memory', 'search_memory_nodes', 'search_memory_facts',
  'get_episodes', 'delete_episode', 'delete_entity_edge',
  'get_entity_edge', 'clear_graph']`. **Rename evidence**:
  `add_episode → add_memory`, `search_nodes → search_memory_nodes`
  — Graphiti is aligning to the community vocabulary.
- **Basic Memory thin-MCP / fat-service pattern**
  (`basicmachines-co/basic-memory:src/basic_memory/mcp/tools/write_note.py:195-235`,
  SHA `0007e72`):
  ```python
  knowledge_client = KnowledgeClient(client, active_project.external_id)
  try:
      result = await knowledge_client.create_entity(entity.model_dump())
  except Exception as e:
      if "409" in str(e) and not effective_overwrite:
          return _format_overwrite_error(...)
      ...
  ```
  Plus path-traversal guard
  (`write_note.py:162-175`):
  `validate_project_path(directory, project_path)`. **Mneme analogue
  for workstream IDs**: regex-validate at MCP boundary, before any
  storage call.
- **Basic Memory tool annotations**
  (`write_note.py:26-47`): explicit `annotations={"destructiveHint":
  True, "idempotentHint": False, "openWorldHint": False}`. Note
  `output_format: Literal["text", "json"]` parameter — caller can
  request structured output. Mneme's `query` could expose
  `output_format` for the same reason.
- **C# SDK `McpServerToolAttribute`**
  (`modelcontextprotocol/csharp-sdk:src/ModelContextProtocol.Core/Server/McpServerToolAttribute.cs`,
  SHA `d67bac1`). **Dangerous defaults**: `DestructiveDefault = true`,
  `OpenWorldDefault = true`, `IdempotentDefault = false`,
  `ReadOnlyDefault = false`. Every Mneme tool **must** explicitly set
  all four annotation properties. The `UseStructuredContent` and
  `OutputSchemaType` properties enable strongly-typed output;
  `IconSource` lets tools ship icons; `TaskSupport` (experimental,
  `[Experimental("MCP_Tasks")]`) marks long-running tools.
- **C# SDK auto-injection** (same file, doc comments):
  `CancellationToken`, `IServiceProvider`, `McpServer thisServer`,
  `IProgress<ProgressNotificationValue>`, `[FromKeyedServices(key)]`
  are all auto-injected — not in the JSON schema, not from tool args.
  Mneme tools should declare these directly in method signatures
  rather than reaching for static context.
- **C# SDK tool registration via reflection**
  (`src/ModelContextProtocol/McpServerBuilderExtensions.cs:34-54`,
  SHA `da63dc3`): `WithTools<TToolType>()` discovers methods with
  `[McpServerTool]` and registers as DI singletons. Instance methods
  get fresh `TToolType` instance per call via
  `CreateTarget(r.Services, typeof(TToolType))` — so constructor-
  injected services are resolved fresh per call. **Mneme should make
  `MnemeMemoryTools` an instance class, not static**, so it can
  receive `IMnemeQueryService` via constructor injection.
- **C# SDK protected (OAuth/JWT) server pattern**
  (`samples/ProtectedMcpServer/Program.cs:1-80`, SHA `f539e73`):
  `AddAuthentication().AddJwtBearer(...).AddMcp(options => {
  options.ResourceMetadata = new() { AuthorizationServers = {...},
  ScopesSupported = ["mcp:tools"] } })` + `app.MapMcp().RequireAuthorization()`.
  `AddMcp()` auto-registers `/.well-known/oauth-protected-resource`
  (RFC 9470). JWT Bearer validates `aud` against the server URL
  (RFC 8707).
- **C# SDK sampling**
  (`samples/AspNetCoreMcpServer/Tools/SampleLlmTool.cs:1-35`, SHA
  `e694774`):
  ```csharp
  var result = await thisServer.AsSamplingChatClient()
      .GetResponseAsync(prompt, options, cancellationToken);
  ```
  `AsSamplingChatClient()` returns an `IChatClient` that wraps
  `sampling/createMessage`. Requires `Stateless = false`. Mneme's
  `distill` should check `thisServer.ClientCapabilities?.Sampling`
  before attempting; fall back to local `IChatClient` if absent.
- **C# SDK distributed sessions**
  (`src/ModelContextProtocol/Server/DistributedCacheEventStreamStore.cs`,
  SHA `d0a315`): persists session state to `IDistributedCache`
  (Redis, SQL Server, etc.) for horizontal scaling with stateful
  HTTP. Mneme's HTTP deployment can scale beyond a single instance
  while still supporting sampling and elicitation.
- **Recent commits** (direction):
  - `modelcontextprotocol/servers` PR #3874 (2026-05-30): tool
    annotations backfilled across reference server.
  - `csharp-sdk` PR #1519 (2026-06-06): SSE response stream leak
    fixed.
  - `csharp-sdk` PR #1600 (2026-06-05): `inputSchema` is now
    required on deserialized `Tool` objects (spec compliance).
  - `getzep/graphiti` `f723545` (2026-06-07): default LLM bumped to
    `gpt-5.5`; reasoning effort disabled for extraction.
  - `basicmachines-co/basic-memory` (daily commits) — rapid
    iteration on cloud product; MCP tool surface stable.

**Sources.**
`modelcontextprotocol/servers:src/memory/index.ts` (`7b4c683`),
`README.md`; `mem0ai/mem0:openmemory/api/app/mcp_server.py` (`70aa523`,
lines 64-462); `getzep/graphiti:mcp_server/src/graphiti_mcp_server.py`
(`833bc5d`, lines 321-700+), `tests/test_mcp_integration.py`
(`7560ba7`); `basicmachines-co/basic-memory:src/basic_memory/mcp/tools/`
(SHA `0007e72`, `66e9312`); `topoteretes/cognee:cognee/api/v1/`
(SHA `ce63145`); `modelcontextprotocol/csharp-sdk:samples/{Protected,AspNetCore}McpServer/`
(SHAs `f539e73`, `f35d0ef`, `e694774`, `aaf6d11`),
`src/ModelContextProtocol.Core/Server/McpServerToolAttribute.cs`
(`d67bac1`), `src/ModelContextProtocol/McpServerBuilderExtensions.cs`
(`da63dc3`); `modelcontextprotocol.io/specification/2025-06-18/`
{`server/resources.md`, `server/prompts.md`, `server/tools.md`,
`client/sampling.md`, `client/elicitation.md`, `client/roots.md`}.

### 2.9 OpenAI Assistants / ChatGPT Memory

**Snapshot.** Assistants API supports thread-based conversation history and
file-based vector stores (file_search tool). No persistent cross-thread
episodic memory API. ChatGPT Memory is a consumer product feature, not
exposed to developers.

**Strengths.**

- Thread + file_search is the right abstraction for "single conversation +
  retrieve from corpus." Many agent products are built on it.

**Weaknesses.**

- Cloud-only, per-token cost, no episodic memory API at all.
- ChatGPT Memory is not addressable from developer code.

**Borrowable design ideas for Mneme.**

- **S6 — Query API:** the Assistants `file_search` tool naming is widely
  understood. If Mneme's MCP server exposes a corpus-search-style tool in
  Phase 8, naming alignment ("search" or "search_facts" rather than the
  more generic "query") reduces consumer mental model switching. See §2.8
  for ecosystem-wide naming conventions (fresh research).

**Stress-test for Mneme.**

- OpenAI deliberately did not ship an episodic memory API despite obvious
  demand. Possible reasons: (a) memory is hard to do well at API scale;
  (b) memory creates user-data complexity (right-to-be-forgotten, PII
  exposure); (c) memory is product-shaped, not infrastructure-shaped. All
  three apply to Mneme. (a) is exactly why Mneme exists (we're solving the
  hard parts). (b) is why classification + revocation are first-class. (c)
  is the warning: Mneme should treat the consumer-facing memory product as
  separate from the substrate. The substrate has good contracts; the
  product is up to each consumer. Don't let "what would the product look
  like?" reasoning leak into Phase 0–4 design.

### 2.10 Anthropic Claude Memory

**Snapshot.** No native memory API. Anthropic's official answer is MCP
(Claude → MCP server → memory). No vendor memory primitive.

**Strengths.**

- The decision to push memory to MCP rather than build a proprietary API
  validates Mneme's Phase 8 bet. Every Anthropic-integrated agent will reach
  for MCP memory tools by default. Mneme should be one of the obvious
  picks.

**Weaknesses.**

- No comparison point at the vendor layer; only the MCP ecosystem is
  relevant.

**Borrowable design ideas for Mneme.**

- See §2.8 (MCP memory server ecosystem) for everything actionable.

**Stress-test for Mneme.**

- Anthropic's bet on MCP for memory means Mneme is competing with whatever
  MCP memory server is closest to "good enough" for the average user. The
  competition isn't Graphiti/Mem0/Zep — it's the default MCP memory server
  the user already has wired up. Mneme must be either (a) drop-in
  replaceable for the default server (same tool names, same payload
  shapes) or (b) noticeably better at the moment of differentiation
  (distilled bundles vs. raw entity lists). Phase 8 should aim for both:
  expose the same primitives the user already knows ("create_entity",
  "search_nodes") AND additional Mneme-specific primitives that show the
  uplift ("distill_workstream", "decision_rationale"). See §2.8 (fresh
  research) for ecosystem-current tool-name conventions.

### 2.11 Google ADK / Gemini Memory

**Snapshot.** Three `MemoryService` impls: `InMemoryMemoryService` (no
persistence), `VertexAiMemoryBankService` (managed, LLM extraction +
consolidation, semantic search, cloud-only), `VertexAiRagMemoryService`
(corpus + similarity, cloud-only). Apache 2.0 SDK; multiple language
SDKs but no .NET. All production options require Vertex AI.

**Strengths.**

- ADK's `MemoryService` interface is the right *contract shape*: a small
  pluggable interface that any backend can implement, with cloud-managed and
  in-memory variants shipped out of the box.
- `VertexAiMemoryBankService` runs LLM-based extraction and consolidation
  asynchronously to ingest (not on the hot path). This is the right separation:
  capture is cheap; synthesis is async and can be slower.

**Weaknesses.**

- Cloud lock to Vertex AI for any production deployment.
- No .NET SDK; ADK is Python/TypeScript/Go/Java/Kotlin only.
- No epistemic categories, no temporal graph, no event log.

**Borrowable design ideas for Mneme.**

- **S5 — Distillation:** ADK's "ingest is sync; consolidate is async" pattern
  is correct and Mneme should adopt it explicitly. Mneme's
  `plan.md` distillation pipeline lists 7 steps that happen on each event;
  in practice steps 3–7 (extract, resolve, project, synthesize, index)
  should be **enqueued** for async processing, not done synchronously inside
  `IngestAsync`. Sync part: validate, classify, redact, persist to event log
  (steps 1–2). Async part: everything else. Backlog item: split
  `mem-ingest-path` into sync ingest + async distillation worker; document
  ingest latency target (target <50ms p50 for sync portion).
- **S6 — Query API:** ADK's `MemoryService` having a tiny surface
  (`add_session_to_memory`, `search_memory`) is a useful counter-example to
  Mneme's planned wider surface. The wider surface (query / distill /
  propose-merges / confirm-merge / revoke) is justified by Mneme's richer
  semantics, but each method should be examined: does it deserve its own
  API call, or does it collapse into a typed `MemoryRequest` variant?
  Specifically, `ProposeEntityMergesAsync` + `ConfirmEntityMergeAsync` can
  arguably be one call (`ResolveEntityMergeAsync(token)`) plus a query
  filter. Revisit when finalizing `IMemoryQueryAPI` shape in Phase 0.

**Stress-test for Mneme.**

- ADK chose two-method memory contracts because most users don't need more.
  Is Mneme's eight-or-so-method contract overdesigned? Argue back: the
  additional methods (distill, propose-merges, revoke) correspond to actual
  Mneme features that ADK doesn't offer. They're not bloat; they're the
  differentiator. But naming and grouping matter: organize methods so the
  "just-use-the-defaults" path is a one-liner, with advanced methods
  available but not in the path of casual use.

### 2.12 Pinecone

**Snapshot.** Managed vector DB (Apache 2.0 Python SDK; REST API; no official
.NET SDK). Cloud-only (Serverless or pods). Used as a backend by Mem0,
LangGraph stores, etc. Not an agent-memory framework.

**Strengths.**

- Serverless pricing model and namespace isolation are well-tuned for
  multi-tenant agent memory backends. Latency p99 <100ms at moderate scale.
- Metadata filtering on every query is fast and ergonomic — the right model
  for combining structured filters with vector search.

**Weaknesses.**

- Cloud-only — incompatible with local-first.
- No local/embedded option.
- No .NET SDK; community SDKs only.

**Borrowable design ideas for Mneme.**

- **S4 — Vector projection (Phase 11):** when Mneme adds vector search,
  always combine vector similarity with metadata filters (workstream,
  category, time range, classification) **in the same query**. Pinecone's
  API forces this pattern by making metadata filtering a first-class
  parameter; Mneme should do the same. The temptation in SQLite + sqlite-vec
  will be to do vector top-k first, then filter the result set — that
  pattern produces empty results when filters are restrictive. Adopt
  Pinecone's pattern: filter first, vector-rank second, or use a single
  combined query.

**Stress-test for Mneme.**

- Pinecone's existence shows vector search at scale is a non-trivial product
  that big-data companies pay real money for. Mneme's bet on sqlite-vec
  embedded is right for the design-target scale (single-user desktop), but
  the Phase 11 plan should document the explicit upper bound: at what
  embeddings count does sqlite-vec stop being fast enough? (Sqlite-vec
  performance characteristics aren't well-published; a Phase 11 milestone
  should include a benchmark of `cosine_similarity` over 1M, 5M, 10M
  embeddings before committing.) Without that bound, "ship vector search
  in v2" is a hand-wave.

### 2.13 Weaviate

**Snapshot.** Open-source vector DB (BSD-3 core). Multi-tenancy, hybrid
search (BM25 + vector), typed collections schema. Go server with
Python/TypeScript/Java/Go SDKs and a community .NET client. Self-hostable
via Docker or Weaviate Cloud.

**Strengths.**

- Multi-tenancy as a first-class concept; tenants isolate storage and query
  scope. Maps directly to Mneme's workstream isolation pattern.
- Hybrid search (BM25 + vector with RRF fusion) is built in — same recipe
  Graphiti uses, but exposed as a single API call.
- Typed schema with classes / properties — closer to relational than
  Pinecone's metadata-blob approach. Easier to reason about.

**Weaknesses.**

- Server process required (Docker). Wrong deployment shape for embedded
  Mneme.
- .NET client is community-maintained, not official; check maintenance
  status before any production dependency.
- Not an agent-memory system; commodity vector store.

**Borrowable design ideas for Mneme.**

- **S4 — Projections:** Weaviate's multi-tenancy primitive is a useful
  reference for how workstream isolation should manifest in SQL.
  Specifically, every table should have a `workstream_id` column with an
  index, every query should be required to filter on it via the
  `IMemoryQueryAPI` capability check, and there should be no "cross-tenant"
  query path in the schema (different from `pg_rls` row-level security,
  which permits cross-tenant queries with the right role). This matches
  Mneme's "no raw SQL escape" rule.
- **S4 — Hybrid search (Phase 11):** Weaviate's single-API-call hybrid
  search (with `alpha` parameter to weight BM25 vs. vector) is the right
  surface for `IMemoryQueryAPI.QueryAsync` when vector search lands.
  Consumers shouldn't have to call FTS5 and vector separately and fuse
  themselves.

**Stress-test for Mneme.**

- Weaviate's multi-tenancy *with* cross-tenant query options shows the
  shape of "default isolated, explicit grant unlocks cross-tenant" that
  Mneme is targeting via capability tokens. Worth studying Weaviate's
  permission model docs for naming and grant-flow patterns — cheap UX
  research before finalizing capability-token grant semantics in Phase 4.

### 2.14 Chroma

**Snapshot.** Open-source vector DB (Apache 2.0, Python/TypeScript). Runs
embedded (in-process) or as a server. No .NET SDK.

**Strengths.**

- Embedded mode (in-process Python) is the right shape for *Python* agent-
  memory tools. SQLite-backed by default; sensible local-first defaults.
- Simple API; collections + metadata; sensible defaults for embedding model
  (`all-MiniLM-L6-v2`).

**Weaknesses.**

- No .NET embedding path. HTTP-only from .NET, which negates the embedded
  benefit.
- Schema is loose; not designed for the structured semantics Mneme needs.

**Borrowable design ideas for Mneme.**

- **S4 — Vector projection (Phase 11):** Chroma's default of bundling
  `all-MiniLM-L6-v2` for embeddings is a useful local-first pattern.
  Mneme should ship with a default local embedding model wrapped behind
  the pluggable LLM provider abstraction, so users without an external API
  key can still get vector search at v2. Recommend: a small ONNX model
  loaded via `Microsoft.ML.OnnxRuntime` (BAAI/bge-small-en-v1.5 or similar).

**Stress-test for Mneme.**

- Chroma proves an embedded vector store is feasible at single-user scale.
  Mneme's plan to ship sqlite-vec is aligned. Watch sqlite-vec maturity
  (`research-zep-sqlite-deepdive.md` §4.6 — pre-v1 as of June 2026); if it
  slips, fallback to a per-workstream HNSW index in a sibling SQLite table
  is a reasonable Plan B.

### 2.15 Microsoft Agent Framework (MAF)

**Snapshot.** MIT, .NET 8/9/10 + Standard 2.0 + Framework 4.7.2 multi-target
NuGet packages under `Microsoft.Agents.AI.*`. Source HEAD `fa9e0865`.
**Correction to internal docs**: all 1.x packages are still preview-labeled
(latest `1.9.0` preview, June 2026); claim of "production-ready v1.0" is
inaccurate. Built on `Microsoft.Extensions.AI ≥ 10.4.0` (`IChatClient`
abstraction) and `Microsoft.Extensions.VectorData.Abstractions ≥ 9.7.0`.
Ships sub-packages: `.Abstractions`, `.Workflows`, `.Mcp`, `.Mem0`,
`.Foundry`, `.A2A`, `.AGUI`, `.DurableTask`, `.Hosting.*`, `.Purview`.

**Strengths.**

- **`AIAgent` abstract class is the agent base type.** Includes
  session-creation (`CreateSessionAsync`), JSON serialization round-trip
  (`SerializeSessionAsync` / `DeserializeSessionAsync`), and run/stream
  variants delegating to `RunCoreAsync` / `RunStreamingCoreAsync`.
  Source: `dotnet/src/Microsoft.Agents.AI.Abstractions/AIAgent.cs`.
- **`AIAgentBuilder` middleware pipeline** (identical pattern to
  `IChatClientBuilder` and ASP.NET Core): `.Use(factory)`,
  `.UseAIContextProviders(...)`, `.UseOpenTelemetry()`. Clean .NET-
  idiomatic composition.
- **`AgentRunContext.CurrentRunContext` is a static async-local** —
  middleware can flow `Agent`, `Session`, `InputMessages`, `RunOptions`
  through arbitrary call depths without explicit parameter passing.
- **MCP integration ships out of the box.** `Microsoft.Agents.AI.Mcp`
  with `McpClientTaskExtensions.ListAgentToolsWithTaskSupportAsync()`
  surfaces MCP servers as `IReadOnlyList<AIFunction>`; long-running
  tools (`ToolTaskSupport.Required`) auto-wrap in
  `TaskAwareMcpClientAIFunction`. SEP-2640 Agent Skills via
  `AgentMcpSkillsSource` (reads `skill://index.json`).
- **`OpenTelemetryAgent`** decorator (17KB) — full traces + metrics
  via `System.Diagnostics.DiagnosticSource` + `OpenTelemetry.Api`;
  attached via `builder.UseOpenTelemetry()`.
- **A2A and AG-UI protocols** for agent-to-agent communication —
  `Microsoft.Agents.AI.A2A`, `.Hosting.A2A.AspNetCore`, `.AGUI`,
  `.Hosting.AGUI.AspNetCore`.
- **`Microsoft.Extensions.Compliance.Abstractions ≥ 10.4.0`** for
  PII marking / redaction at the data-flow layer.
- **`Microsoft.Agents.AI.Purview`** suggests Purview governance
  integration is in scope.

**Weaknesses (critical for Mneme positioning).**

- **There is no `IMemoryStore` interface in MAF.** The memory/context
  extension point is `MessageAIContextProvider` (abstract class).
  This is **the single architectural seam through which Mneme must
  plug in** if it wants to be a first-class MAF memory provider.
- **No `IAgentThread` interface.** Thread concept is `AgentSession`
  (in-memory by default; manual JSON serialization for persistence;
  no pluggable thread store).
- **Workflow checkpointing is snapshot-based, NOT event-sourced.**
  `ICheckpointStore<TStoreObject>` stores opaque JSON blobs
  (`JsonElement`); parent links create a linked-list for "time travel"
  by walking back. Default is `InMemoryCheckpointManager`. **This is a
  production gap Mneme could fill.**
- **No auth / capability propagation through agent → tool calls.**
  Scope fields exist on memory providers (`Mem0ProviderScope` has
  `ApplicationId`, `AgentId`, `ThreadId`, `UserId`) but no token
  threading. Capability tokens would need to live in
  `AgentSession.StateBag` or `AgentRunContext`.
- **Vector memory covers the 90% case via `ChatHistoryMemoryProvider`.**
  Uses `VectorStore` + `VectorStoreCollection<object, Dictionary<string,
  object?>>`; modes are `AutoInjection` or `OnDemandFunctionCalling`.
  Most developers won't reach beyond this for "what did we discuss?".
- **Semantic Kernel is superseded by MAF; MAF uses `Microsoft.Extensions.AI`
  directly.** The internal Mneme plan referencing "default Semantic Kernel"
  as the LLM provider abstraction is outdated. **The right
  abstraction is `IChatClient`** (from `Microsoft.Extensions.AI`).

**Borrowable design ideas for Mneme — and required integration plan.**

This is the most consequential section in this document. Mneme is a
.NET-native memory substrate; MAF is the default .NET agent framework.
Mneme must integrate cleanly with MAF to win .NET adoption.

- **S13 — Switch LLM-provider abstraction from Semantic Kernel to
  `Microsoft.Extensions.AI.IChatClient` (high-priority correction).**
  Every MAF user already has `IChatClient` registered; using it
  means zero additional dependency for them. Update `AGENTS.md`
  locked-decisions table accordingly and remove the SK references from
  `plan.md`. Backlog: amend `mem-llm-classifier` to specify
  `IChatClient`; remove SK references throughout.
- **S14 — Ship `Mneme.Agents.AI` NuGet package** that implements MAF's
  `MessageAIContextProvider` extension point. This is the **single
  highest-value integration deliverable.** Sketch:

  ```csharp
  // Package: Mneme.Agents.AI
  public sealed class MnemeContextProvider : MessageAIContextProvider
  {
      private readonly IMemoryQueryAPI _query;
      private readonly CapabilityToken _token;

      protected override async ValueTask<IEnumerable<ChatMessage>>
          ProvideMessagesAsync(InvokingContext context, CancellationToken ct)
      {
          var workstreamId = ResolveWorkstreamId(context.Session);
          var bundle = await _query.DistillAsync(
              new DistillationRequest { WorkstreamId = workstreamId },
              _token, ct);
          return [new ChatMessage(ChatRole.User, bundle.RenderAsMarkdown())];
      }

      protected override async ValueTask StoreAIContextAsync(
          InvokedContext context, CancellationToken ct)
      {
          // Map ChatRole.Assistant outputs → Evidence/Action/Outcome
          // via a classifier; ingest into Mneme event log.
      }
  }

  // Wire-up reads like Mem0's:
  AIAgent agent = new AIAgentBuilder(baseAgent)
      .UseAIContextProviders(services.GetRequiredService<MnemeContextProvider>())
      .Build(services);
  ```

  Add new backlog item `mneme-maf-integration` as a Phase 8.5
  sibling to `mem-mcp-server`.
- **S10 — Ship `MnemeCheckpointStore : ICheckpointStore<JsonElement>`**
  in the same `Mneme.Agents.AI` package. MAF's default is in-memory
  only; replacing it gives durable MAF workflows for free, and the
  storage is just one more event category in `memory_events`
  (`category = WorkflowCheckpoint`, payload = `JsonElement`, parent =
  previous checkpoint's `event_id`). Three-method interface. Lowest-
  friction high-value contribution to the .NET ecosystem.
- **S11 — Mneme MCP server doesn't need MAF-specific wrappers.** MAF
  consumes any conformant MCP server via
  `McpClientTaskExtensions.ListAgentToolsWithTaskSupportAsync()`.
  `Mneme.Mcp` just needs to be MCP-spec-conformant; MAF integration is
  automatic. (Validates the Phase 8 plan.)
- **S5 — Long-running distillation should declare
  `ToolTaskSupport.Required`.** MAF wraps such tools in
  `TaskAwareMcpClientAIFunction` for async-friendly execution. Mneme's
  `distill` tool should set this flag (distillation is a multi-second
  LLM operation) so MAF agents don't time out.
- **S6 — Use `AgentSession.StateBag` for capability-token storage.**
  Avoid making consumers re-pass tokens on every call. Recommended
  pattern: `services.AddMnemeMemory(opts)` registers a startup that
  drops the token into the session bag on agent creation; the context
  provider reads it from there transparently.
- **S3 — Mirror `OpenTelemetryAgent.cs` as the template for Mneme's
  telemetry decorator.** Same span/metric/attribute discipline. The
  file is 17KB and a complete example.
- **S11 — Expose `skill://index.json` resource on `Mneme.Mcp`** so
  MAF consumers can discover Mneme via SEP-2640
  `AgentMcpSkillsSource`. Skills could be: `recall-decision-rationale`,
  `recall-recent-outcomes`, `summarize-workstream`. Beyond MCP tools,
  this gives Mneme's surface a knowledge-base presence in MAF agents.
- **S14 — Package naming.** Mneme should ship three NuGet packages:
  `Mneme.Contracts` (already planned), `Mneme` (implementation),
  `Mneme.Mcp` (already planned), AND **`Mneme.Agents.AI`** (new) —
  the MAF integration package. Naming convention matches
  `Microsoft.Agents.AI.Mem0`. Backlog: new top-level entry.

**Integration playbook (concrete 5-line setup).**

```csharp
// dotnet add package Mneme.Agents.AI

builder.Services.AddMnemeMemory(opts =>
{
    opts.SqliteConnectionString = "Data Source=mneme.db";
    opts.WorkstreamId = "my-agent-workstream";
});

AIAgent agent = new AIAgentBuilder(baseAgent)
    .UseAIContextProviders(app.Services.GetRequiredService<MnemeContextProvider>())
    .Build(app.Services);

AgentResponse response = await agent.RunAsync("What decisions did we make last week?");
```

**Stress-test for Mneme.**

- **MAF developers reach for `ChatHistoryMemoryProvider` first.** It's
  10 lines to wire and answers "what did we say before?" perfectly
  well for the 90% case. Mneme must answer "why would I switch?"
  before the developer abandons evaluation. The compelling demo:
  "summarize what we've decided AND what's still uncertain" — flat
  vector RAG can't do this; bi-temporal decision-log can. **Action:**
  ship this demo as the first example in the `Mneme.Agents.AI`
  README. Backlog: new doc item alongside `mneme-maf-integration`.
- **MAF's Process Framework overlaps Mneme's event log.** Real but
  manageable: MAF checkpoints answer "where was the workflow?"; Mneme
  events answer "what does the agent know?". Make this distinction
  explicit in docs. Then *use the overlap as a feature* by shipping
  `MnemeCheckpointStore` — one durable store, two views.
- **Capability tokens have no MAF equivalent.** Onboarding must hide
  this. The `AddMnemeMemory` extension should construct a token from
  ergonomic inputs (workstream ID + permitted categories) so the
  developer never sees a `CapabilityToken` type unless they need
  cross-workstream queries.
- **Schema-categorical positioning risk.** MAF's worldview is
  `IEnumerable<ChatMessage>` injected. Mneme's worldview is typed
  events / categories. These are different abstraction levels but
  developers will see them as competing. Recommendation: the
  `MnemeContextProvider` should render its bundle output as a
  *single, well-formatted markdown `ChatMessage`* — meeting MAF's
  worldview where it lives — and only expose category-typed APIs to
  developers who explicitly ask for richer semantics.

### 2.16 Microsoft Kernel Memory (KM)

**Snapshot.** MIT, but README is explicit: *"This is experimental
software. Expect things to break. Contributions are not accepted at this
stage. No stability or compatibility guarantees. No support provided."*
Source HEAD `94b69d34`; README `1e683aff`. Currently undergoing a
full rewrite (**KM²**) using the team's "Amplifier" metacognitive
engineering platform. Old code archived under `/archived`; new code is
two directories: `/src/Core` and `/src/Main`. **No published NuGet
packages for KM² exist.**

**Strengths.** None as a Mneme integration target — there is no stable
surface to integrate with, and contributions are not accepted.

**Weaknesses.** Pure RAG / document chunking; no categorical schema, no
bi-temporal model, no entity resolution, no distillation concept;
documentation is sparse and the architecture is in flux.

**Borrowable design ideas for Mneme.** None — KM is not a viable
dependency, integration target, or design source for any production
timeline.

**Stress-test for Mneme.** KM's "explicitly not production" framing is a
useful counter-position: Mneme's `Mneme.Contracts` "NuGet-shippable from
day one" approach (with `IsPackable = true` and `0.0.1-alpha.1`
versioning per the .csproj) is the opposite of KM's stance and is
correct. Adopters need a target they can build against; uncertainty kills
adoption. **Continue Mneme's "ship contracts early, even as alpha"
policy.**

**Source-code deep dive (MAF + KM²).** *Citations against MAF HEAD
`fa9e0865` (2026-06-05); KM² HEAD `94b69d34` (2025-12-18, frozen
6 months).*

**MAF NuGet versions confirmed**: `Microsoft.Agents.AI`,
`.Abstractions`, `.Workflows`, `.Mcp`, `.Mem0` all at
`1.9.0-preview.260605.2` (still preview-labeled). Builds on
`Microsoft.Extensions.AI ≥ 10.4.0`.

- **`AIAgent` abstract type** (`dotnet/src/Microsoft.Agents.AI.Abstractions/AIAgent.cs`,
  SHA `3431a4b5`, 31KB). Not an interface — abstract class.
  Public surface:
  ```csharp
  public abstract class AIAgent {
      public abstract string Id { get; }       // GUID
      public string? Name { get; set; }
      public string? Description { get; set; }
      public static AgentRunContext? CurrentRunContext { get; }  // AsyncLocal
      public abstract ValueTask<AgentSession> CreateSessionAsync(CancellationToken);
      public abstract ValueTask<JsonElement> SerializeSessionAsync(AgentSession, ...);
      public abstract ValueTask<AgentSession> DeserializeSessionAsync(JsonElement, ...);
      public Task<AgentResponse> RunAsync(...);
      public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(...);
      public virtual T? GetService<T>(object? serviceKey = null);
  }
  ```
  Sessions are externally persisted (serialize → store JSON → restore).
  **No thread store interface.** Mneme's workstream-id ↔ session-id
  mapping must live in the consumer's storage, accessed via the
  `MnemeContextProvider` reading `AgentSession.StateBag`.

- **`AgentSession` / `AgentSessionStateBag`** (`AgentSession.cs:60-115`
  SHA `1960a4ce`; `AgentSessionStateBag.cs:20-145` SHA `d78a866b`).
  No `IAgentThread`. `AgentSession` is the thread concept. `StateBag`
  is a `ConcurrentDictionary<string, AgentSessionStateBagValue>` with
  thread-safe `GetValue<T>(key)` / `SetValue<T>(key, value)` and JSON
  round-trip. **Critical**: `StateBag` survives serialization —
  Mneme must store its per-session state (workstream binding, last
  query position, capability-token reference) in `StateBag`, not in
  context-provider instance fields.

- **`AIContextProvider` is the integration seam, NOT `IMemoryStore`**
  (`Microsoft.Agents.AI.Abstractions/AIContextProvider.cs:1-511`, SHA
  `641825d1`, 30KB). The abstract class shape:
  - `StateKeys` (`IReadOnlyList<string>`, default `[GetType().Name]`)
    — declares which `StateBag` keys this provider owns.
  - **Pre-run hook**: `InvokingAsync(InvokingContext, ct)` →
    `InvokingCoreAsync` → filters to `External` messages →
    `ProvideAIContextAsync` (default returns empty `AIContext`).
  - **Post-run hook**: `InvokedAsync(InvokedContext, ct)` →
    `InvokedCoreAsync` → skips on exception → filters →
    `StoreAIContextAsync` (default no-op).
  - **Message source stamping** (line ~175): provider-returned messages
    stamped with `AgentRequestMessageSourceType.AIContextProvider` +
    provider type name. **Default filter passes only `External`
    messages to `ProvideAIContextAsync`** — prevents echo-chamber
    loops where a provider's own output triggers itself.
  - `InvokingContext` (lines 372-420):
    `{Agent, Session, AIContext (with caller messages + chat history
    pre-merged)}`.
  - `InvokedContext` (lines 430-510): success ctor with `RequestMessages`
    + `ResponseMessages`; failure ctor with `InvokeException`. Mneme
    should respect the failure path (don't ingest when the agent
    errored unless explicitly desired).
  - `AIContext` (the return type): `{Instructions, Messages, Tools}`.

- **`MessageAIContextProvider` is the recommended subclass for Mneme**
  (confirmed as base class of `ChatHistoryMemoryProvider.cs` SHA
  `2bc8408a` and `Mem0Provider.cs` SHA `8be799ac`). Simpler API:
  - `protected override ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(InvokingContext, CancellationToken)`
  - `protected override ValueTask StoreAIContextAsync(InvokedContext, CancellationToken)`

- **`ChatHistoryProvider` is a separate abstraction** — Mneme should
  **NOT** implement this. Mneme's conversation history is queryable
  via workstream queries; `ChatHistoryProvider` is for verbatim
  message replay. Mneme implements `MessageAIContextProvider`
  (semantic context injection) only.

- **`ICheckpointStore<TStoreObject>` — Mneme's clean integration win**
  (`Microsoft.Agents.AI.Workflows/Checkpointing/ICheckpointStore.cs`,
  SHA `af2fa542`). Three methods:
  ```csharp
  public interface ICheckpointStore<TStoreObject> {
      ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
          string sessionId, CheckpointInfo? withParent = null);
      ValueTask<CheckpointInfo> CreateCheckpointAsync(
          string sessionId, TStoreObject value, CheckpointInfo? parent = null);
      ValueTask<TStoreObject> RetrieveCheckpointAsync(
          string sessionId, CheckpointInfo key);
  }
  ```
  `Checkpoint` struct (SHA `3e3fa80d`): `{StepNumber, WorkflowInfo,
  RunnerStateData, StateData (per-scope), EdgeStateData (per-edge),
  Parent (CheckpointInfo)}`. **Parent forms a linked list** —
  "time-travel" is implemented as walking ancestors. **Snapshot-based,
  not event-sourced.** Mneme can implement this as
  `MnemeCheckpointStore : ICheckpointStore<JsonElement>` storing
  `JsonElement` payloads in `memory_events` as a `Checkpoint`
  event type. No schema changes; full MAF workflow durability for free.

- **`AgentRunOptions.AdditionalProperties` is the only capability-token
  channel** (`AgentRunOptions.cs`, SHA `e56155b8`). No first-class
  auth slot. The only sanctioned propagation path:
  ```csharp
  // Caller:
  await agent.RunAsync(messages, session, new AgentRunOptions {
      AdditionalProperties = new() { ["mneme:capability-token"] = token }
  });
  // Provider:
  var token = AIAgent.CurrentRunContext?.RunOptions
                     ?.AdditionalProperties?["mneme:capability-token"] as string;
  ```
  `AgentRunContext` (SHA `d860fa31`): `{Agent, Session,
  RequestMessages, RunOptions}` accessible via static
  `AsyncLocal<AgentRunContext?>`. Naming `"mneme:capability-token"`
  is a Mneme convention (no MAF standard). Document this clearly in
  `Mneme.Agents.AI` README.

- **`FunctionInvocationDelegatingAgent`** (`Microsoft.Agents.AI/FunctionInvocationDelegatingAgent.cs`,
  SHA `13f15ff1`). Wraps each `AIFunction` in a middleware pipeline
  via `MiddlewareEnabledFunction.InvokeAsync()`. `FunctionInvocationContext`
  carries `Arguments`, `Function`, `CallContent` — but **no slot for
  capability tokens**. Auth flows ONLY via:
  1. `AdditionalProperties` on `AgentRunOptions` (via `CurrentRunContext`)
  2. HTTP transport headers on MCP connections (set at connection time)
  3. Process env vars for stdio MCP servers
  Mneme's MCP server (Phase 8) gets capability tokens via #2/#3,
  not from MAF's per-call mechanism.

- **`OpenTelemetryAgent`** (`Microsoft.Agents.AI/OpenTelemetryAgent.cs`,
  SHA `cffde717`, 17KB). Emits per OpenTelemetry GenAI Semantic
  Conventions v1.37:
  ```csharp
  activity.DisplayName = $"invoke_agent {agent.Name}({agent.Id})";
  activity.SetTag("gen_ai.operation.name", "invoke_agent");
  activity.SetTag("gen_ai.provider.name", metadata.ProviderName);
  activity.SetTag("gen_ai.agent.id",          agent.Id);
  activity.SetTag("gen_ai.agent.name",        agent.Name);
  activity.SetTag("gen_ai.agent.description", agent.Description);
  ```
  `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` enables
  full message-content capture (PII risk — Mneme should redact at
  ingest, not rely on this flag). **Auto-wiring** (default `true`):
  `OpenTelemetryAgent` also wraps the underlying `IChatClient`.
  **Mneme should emit `mneme.distill`, `mneme.ingest`,
  `mneme.entity.resolve`, `mneme.projection.rebuild` spans under
  the same `ActivitySource` family so Mneme operations appear as
  children of `invoke_agent` in traces.** Backlog: align Mneme's
  span names with `gen_ai.*` namespace conventions.

- **MCP integration** (`Microsoft.Agents.AI.Mcp/McpClientTaskExtensions.cs`,
  SHA `77bbf605`):
  ```csharp
  IReadOnlyList<AIFunction> tools = await mcpClient
      .ListAgentToolsWithTaskSupportAsync(cancellationToken: ct);
  ```
  Returns `TaskAwareMcpClientAIFunction` (SHA `45ffdb4c`) wrapping
  each `McpClientTool`. Calls `McpClient.CallToolAsTaskAsync(name,
  args)` → polls `GetTaskResultAsync` → string. Fallback: if server
  returns `McpErrorCode.MethodNotFound`, falls back to
  `McpClient.CallToolAsync` (synchronous). **Mneme's `distill` tool
  should declare `ToolTaskSupport.Required`** so MAF wraps it
  task-aware — critical because distillation can take 5-30s.
  SEP-2640 skill discovery via `AgentMcpSkillsSource` (SHA
  `2a43814b`) reads `skill://index.json` from the MCP server —
  Mneme.Mcp should expose this with skills like
  `recall-decision-rationale`, `summarize-workstream`.

- **`Microsoft.Extensions.AI.IChatClient` is the LLM abstraction**
  (not Semantic Kernel). MAF depends on `Microsoft.Extensions.AI ≥
  10.4.0`. Everything message/function comes from MEAI:
  `ChatMessage`, `ChatRole`, `AIFunction`, `AIFunctionFactory`,
  `ChatOptions`, `IChatClient`, `ChatClientBuilder`,
  `UseOpenTelemetry()`, `UseFunctionInvocation()`. `ChatClientAgentRunOptions`
  subclasses `AgentRunOptions` with a `ChatClientFactory: Func<IChatClient, IChatClient>`
  for per-run client transformation. **Mneme's distillation +
  classifier + entity-resolution LLM calls must use `IChatClient`
  directly** — adds no extra dependency for MAF users; Semantic
  Kernel is not in scope.

- **Verified 5-line consumer integration** (compilable sketch):
  ```csharp
  // Program.cs
  services.AddMneme(opts => opts.ConnectionString = "Data Source=mneme.db");

  var agent = new ChatClientAgentBuilder(services.BuildServiceProvider())
      .UseChatClient(openAiClient)
      .UseAIContextProviders(
          new MnemeContextProvider(
              sp.GetRequiredService<IMemoryQueryAPI>(),
              new MnemeScope { DefaultWorkstreamId = "proj-alpha" }))
      .Build();
  ```
  Plus `MnemeContextProvider : MessageAIContextProvider` with
  `ProvideMessagesAsync` returning Mneme's bundle as a single
  `ChatMessage(ChatRole.System, bundle.ToMarkdown())` and
  `StoreAIContextAsync` ingesting `context.RequestMessages` +
  `context.ResponseMessages` back into Mneme. State restored from
  `context.Session?.StateBag.GetValue<MnemeState>("MnemeContextProvider")`.

- **`MnemeCheckpointStore : ICheckpointStore<JsonElement>`** (the
  cleanest second-level integration). Map MAF checkpoints onto
  Mneme's append-only event log:
  ```csharp
  public async ValueTask<CheckpointInfo> CreateCheckpointAsync(
      string sessionId, JsonElement value, CheckpointInfo? parent = null,
      CancellationToken ct = default) {
      var id = Guid.NewGuid().ToString("N");
      await _events.AppendAsync(new MemoryEvent {
          EventType = "workflow.checkpoint",
          WorkstreamId = sessionId,
          ExternalId = id,
          Payload = value.GetRawText(),
          ParentId = parent?.CheckpointId
      }, ct);
      return new CheckpointInfo(sessionId, id);
  }
  ```
  Pattern: snapshot payloads are technical events (not epistemic).
  Mneme's category enum needs a non-epistemic "Workflow" or
  "Technical" channel for these so they don't pollute the 7
  epistemic categories. Backlog: extend `contracts-event-categories`
  with `EventChannel = { Epistemic, Technical }`.

- **Recent MAF commits (architectural direction)**:
  - `fa9e086` (2026-06-05): `.NET: fix preserve foreach record values`
    — workflow iteration bug fix.
  - `dcc218d` (2026-06-05): `Python: Add MCP client OTel spans per
    GenAI semantic conventions` — span attributes `mcp.method.name`,
    `gen_ai.tool.name`, `mcp.session.id`, `network.transport`.
    **Mneme.Mcp should emit the same span attributes for parity.**
  - `6bd2cfe` (2026-06-05): `.NET: [BREAKING] Add auto-approval
    rules (heuristics) to ToolApprovalAgent` — rule-based
    auto-approve. Mneme's entity-merge confirmation flow can adopt
    the same pattern (auto-approve when deterministic-key matches
    above threshold; require human only for ambiguous cases).
  - `ab8ba8f` (2026-06-05): persists HITL approval decisions across
    sessions.
  - `9cafd7e` (2026-06-05): Python unifies HITL approval with
    general pending-request pattern.
  Strong signal: MAF is hardening for production HITL workflows.
  Mneme's propose-then-confirm pipeline should ride this wave by
  integrating with `ToolApprovalAgent` patterns where applicable.

- **Kernel Memory (KM²) source verification** (HEAD `94b69d34`,
  2025-12-18, **frozen 6 months**):
  - `ISearchService` (`src/Core/Search/ISearchService.cs`, SHA
    `b4b89d46`): two methods — `SearchAsync(SearchRequest)`,
    `ValidateQueryAsync(string)`.
  - `IVectorIndex` (`SqliteVectorIndex` default): normalized
    dot-product over SQLite.
  - `IFtsIndex` (`SqliteFtsIndex` default): SQLite FTS5 with
    stemming.
  - `IContentStorage` (`ContentStorageService`, SHA `e1839acf`,
    33KB): EF Core + SQLite, **MUTABLE CRUD** — not append-only.
  - `WeightedDiminishingReranker` combines vector + FTS scores with
    configurable per-node weights.
  - Tests pin behavior: `SimpleSearchTest`,
    `FtsIndexPersistenceTest`, `FtsIntegrationTests`,
    `SqliteVectorIndexTests`, `SearchServiceIndexWeightsTests` —
    all work with flat `{id, title, description, content, tags,
    metadata.*}`. **No epistemic categories, no bi-temporal model,
    no entity resolution.**
  - Recent commits: `94b69d3` (2025-12-18) and `9c6ba4f` (2025-12-18)
    — that's it. Repo dormant.
  - **No NuGet package** for KM².

- **Strong technical takeaway from KM² source**: KM²'s `SqliteVectorIndex`
  + `SqliteFtsIndex` are clean reference implementations of
  SQLite-native vector + FTS5 search Mneme could study when
  building Phase 11 (`mem-vector-search`, `mem-text-index`). MIT-
  licensed, ~100 lines each. Worth a one-time read even though we
  won't depend on KM².

- **Locked-decision flag (urgent)**: `AGENTS.md` locked-decisions
  table includes `Semantic Kernel as planned indirection`. The
  source dive confirms MAF has moved past Semantic Kernel to
  `Microsoft.Extensions.AI.IChatClient` directly. **Action**: revisit
  this decision; update `AGENTS.md` to specify `IChatClient` as
  the LLM abstraction. Mneme keeps the indirection but switches
  the underlying contract. See §4.7 and §5 below.

- **`Microsoft.Agents.AI.Purview` package contents not yet
  source-confirmed** — a follow-up if Mneme needs governance
  integration (Phase 7+ data-classification).

### 2.17 KurrentDB (formerly EventStoreDB)

**Snapshot.** Purpose-built append-only event store with streams,
projections, subscriptions, and server-side filtering. Official .NET
client (Apache 2.0). Server is BSL (Business Source License) for recent
versions; older v22/v23 LTS are Apache 2.0. Requires separate server
process.

**Strengths.**

- Event-store-as-product: streams, projections, subscriptions, idempotent
  appends, optimistic concurrency. Exactly the abstractions Mneme builds
  by hand on top of SQLite.
- Server-side projections evaluate against the event log without round-
  tripping events to the client — a substantial perf advantage at scale.
- Official .NET client is mature and well-supported.

**Weaknesses.**

- Server process required. Wrong deployment shape for embedded Mneme.
- BSL on recent server versions adds legal-review friction for commercial
  distribution; "competing event-store service" prohibition could be argued
  to apply to a memory service that exposes event-log semantics. Needs
  legal opinion if adopted.
- No graph, no epistemic semantics, no classification — orthogonal to
  Mneme's value layer.

**Borrowable design ideas for Mneme.**

- **S1 — Event log schema:** Kurrent's stream + event_number + global_position
  pattern is the right abstraction for `memory_events`. Mneme should expose
  events with a `(workstream_id, event_number)` pair (the per-workstream
  sequence) AND a global `event_position` (the across-workstream sequence),
  with both indexed. This is necessary for both per-workstream replay (use
  event_number) and cross-workstream sync (use event_position). The
  ULID is the natural global key; event_number is `ROW_NUMBER()` within
  workstream. Backlog: `mem-store-tables` schema should include both.
- **S4 — Projections:** Kurrent's "projection" concept is a server-side
  query that emits new streams. Mneme's projections are SQL views /
  materialized tables — same conceptual shape. Worth using Kurrent's
  terminology in code and docs ("projection" rather than "read model" or
  "view") because the event-sourcing community already uses it; lowers the
  learning curve for incoming contributors. Already aligned (`plan.md`
  uses "projection").
- **S1 — Idempotency:** Kurrent's expected-version optimistic concurrency
  is overkill for Mneme's idempotent-on-ULID model, but the *pattern* of
  client passing an `ExpectedVersion` and server rejecting on mismatch is
  the right escape hatch when a consumer wants stricter than ULID
  idempotency. Don't ship this in Phase 1, but reserve the `expected_version`
  parameter in `IngestAsync` for future use.

**Stress-test for Mneme.**

- Kurrent's existence raises: "why are we writing an event store rather than
  embedding Kurrent's client and pointing at a Kurrent server?" Answer
  (documented in `research-existing-systems.md` §2.17 and earlier
  conclusions): server process is incompatible with embedded local-first
  deployment, and BSL is a legal-review tax. SQLite + a thin event-log layer
  is ~500 LOC and avoids both. The trade is real but the answer is settled.

### 2.18 Marten

**Snapshot.** .NET library for a transactional document DB + ACID event
store, both backed by PostgreSQL. Append-only event streams with typed
events, user-defined projections (aggregate, flat table, live), optimistic
concurrency, snapshots, multi-tenancy. MIT, .NET native.

**Strengths.**

- The closest existing .NET-native event store; the .NET community standard
  for event sourcing. Mature, well-documented, MIT.
- Projection model is rich: aggregate projections (build one entity from
  events), flat-table projections (build a query-friendly table), live
  projections (compute on read). Mneme's planned projections are mostly
  flat-table; Marten's pattern is directly applicable.
- Multi-tenancy as a first-class concept; tenant-id is a column on every
  document/event.
- Async daemon for rebuilding projections from events — exactly what Mneme
  needs for Phase 3's "rebuild from scratch" tooling.

**Weaknesses.**

- Requires PostgreSQL. Embedded-Postgres options exist (`EmbeddedPostgres`
  NuGet, ~30 MB) but add packaging complexity for desktop distribution.
- PostgreSQL is overkill for single-user local-first scale; SQLite is the
  right substrate for v1.
- No graph, no epistemic semantics; lower-level than Mneme.

**Borrowable design ideas for Mneme.**

- **S1 — Event log:** Marten's `IEventStore` interface and `Stream<T>` /
  `Append(events)` / `LoadAsync<T>` API is the .NET-idiomatic shape for an
  event store. Mneme's internal storage abstraction should mirror it (so a
  future `Mneme.PostgreSql` package could use Marten as the backend), but
  Mneme's *public* `IMemoryAgent.IngestAsync` stays the higher-level
  capability-checked surface. Backlog: when designing `Mneme/Storage/`,
  reference Marten's API for vocabulary.
- **S4 — Projections:** Marten's async daemon (`AsyncProjectionDaemon`)
  pattern — projections rebuild in a background loop, with sharded
  workers and progress tracking — is the right model for Mneme's projection
  rebuild. Document the pattern in backlog `mem-projections`: rebuild is
  not a one-shot operation; it runs continuously with checkpoints to
  resume on crash. Don't reimplement this from scratch; lift Marten's
  shape directly.
- **S4 — Projection types:** Marten's distinction between
  *aggregate projections* (entity reconstruction from events) and
  *flat-table projections* (query-optimized views) is genuinely useful.
  Mneme's `current_facts`, `current_goals`, `entity_index` are flat-table;
  `hypothesis_states` and `decision_chains` are aggregate (entity-per-
  hypothesis state machine, decision-per-decision chain). Tagging the type
  in code (`[FlatProjection]` / `[AggregateProjection]` attributes or
  interface markers) clarifies intent for future contributors.

**Stress-test for Mneme.**

- A reasonable counter-question: should `Mneme.PostgreSql` (Marten-backed)
  ship in v1 *alongside* the SQLite backend? Pro: server deployments
  (sidecar / service modes per Phase 9) want PostgreSQL; Marten gives us
  that for free. Con: Phase 1 doubles in scope; SQLite + Marten compat
  layer is two backends to maintain from day one. **Recommended:**
  ship SQLite-only in Phase 1; design `IEventStore` internal abstraction
  cleanly enough that `Mneme.PostgreSql` can ship as a v2 package without
  refactoring the public API. Marten is the natural implementation for
  that backend.

### 2.19 Neo4j .NET Driver

**Snapshot.** Official Neo4j driver (NuGet `Neo4j.Driver`, Apache 2.0).
Targets .NET 8/9/10. Bolt protocol; full Cypher support. Neo4j Community
Edition is GPL; Neo4j Enterprise is commercial. Requires Neo4j server
process.

**Strengths.**

- Official, mature, well-supported .NET integration with the most
  battle-tested graph DB on the market.
- Cypher is expressive; recursive graph traversal in 3–5 lines that takes
  20+ lines in SQL CTEs.

**Weaknesses.**

- Neo4j server process required. Embedded Neo4j is Java-only; no .NET
  embedding path.
- Wrong deployment shape for embedded Mneme.
- For Mneme's fixed-depth (3-hop) BFS at single-user scale, SQLite
  recursive CTE is sufficient and avoids the server tax.

**Borrowable design ideas for Mneme.**

- **S4 — Graph projection:** Cypher's `MATCH (a)-[r:RELATES_TO*1..3]-(b)` is
  *much* easier to read than the equivalent SQL CTE. When Mneme writes
  graph-traversal SQL (in `mem-graph-projection`), wrap each CTE in a
  static method named after what it does (`BreadthFirstSearch3Hop`,
  `EntityNeighborhood`, etc.) so call sites read like Cypher even though
  the implementation is SQL. Don't try to invent a Cypher-in-C# DSL; just
  give the wrappers Cypher-ish names.

**Stress-test for Mneme.**

- Neo4j is the right substrate at high scale (millions of nodes, deep
  traversals). Mneme's target scale (single user, ~10k–100k events per
  workstream) does not need Neo4j. The locked decision to use SQLite is
  correct, and `research-zep-sqlite-deepdive.md` §3.5 demonstrates the
  3-hop BFS SQL works. If Mneme ever serves teams at scale, a
  `Mneme.Neo4j` projection backend is a plausible v3+ play; document the
  upgrade path in the locked-decisions section.

---

## 3. Cross-cutting design ideas worth adopting

*Synthesized from §2 deep-dives across all 19 frameworks plus the five
fresh source-level investigations (Mem0, Letta, MCP, Cognee, MAF + KM²).
Items mapped to Mneme design surfaces (S1-S14) from §1 and to specific
backlog tasks where relevant.*

### 3.1 API surface

**S11, S14 — Adopt `remember` / `recall` / `improve` / `forget` at the
MCP edge** (Cognee §2.5, Mem0 §2.1, Graphiti §2.4 latest rename to
`add_memory`). The .NET surface (`IMemoryQueryAPI`) keeps Mneme-native
names (`Query`, `Ingest`, `Distill`, `Revoke`) for type-safety and
discoverability inside .NET. The MCP edge renames to the community
vocabulary because that is what agents trained on Mem0/Cognee/Basic
Memory reach for. **Recommended Mneme.Mcp surface** (evidence from
§2.8): `remember` (→ `Ingest`), `query` (kept; alias `recall` in
description), `distill` (kept; differentiator), `forget` (→ `Revoke`),
`list_recent` (NEW). All five carry the four MCP tool annotations
explicitly (no defaults; defaults are wrong).

**S2 — Method-count discipline** (ADK §2.11). The four-method
`IMemoryQueryAPI` surface (`Query`, `Distill`, `Ingest`, `Revoke`)
matches every successful memory framework's count. Resist additions in
Phase 4; ship `Explain`, `ListRecent`, `RebuildProjection` on a
separate `IMnemeAdmin` interface to keep the consumer-facing surface
small.

**S5 — Two-tier bundle shape (`BundleIndex` + `BundleSection`)**
(Letta MemFS §2.2, Cognee `GlobalContextSummary` §2.5). Distillation
should return a thin always-loadable index (500-1000 tokens — what
bundles exist, their staleness, their labels) plus on-demand section
bodies (2-4k tokens each). Consumers pay the section cost only when
they need it; the index is cheap to ship every turn. Backlog: reshape
`contracts-distillation-bundle` to expose `BundleIndex`,
`BundleSection`, and a `BundleSection.Description` field for
progressive disclosure.

**S5 — `LookupHints` section in bundles** (Letta compaction prompt
§2.2). Every bundle includes short keyword pointers ("topic and key
terms") to the original event-log entries for facts that didn't fit
in the bundle. Consumers can re-query for the full detail when
needed. Cheap; high signal. Backlog: add `LookupHints` section to
`mem-distillation-bundle`.

**S6 — `Explain` flag returning per-signal score decomposition**
(Mem0 §2.1 `explain=True`; shipped at v3 launch, not bolted on). Mneme
Phase 4 `IMemoryQueryAPI.QueryAsync` should accept `Explain: bool`;
the result includes per-signal contributions (semantic, BM25, entity,
filter, capability-token resolution). Critical for diagnosing
workstream-isolation bugs and temporal-window mistakes. Backlog:
`arch-imemoryquery` and `mem-query-api-impl`.

**S6 — Composable bundles** (LlamaIndex §2.7). `ContextBundle` is a
composition of named `BundleSection`s, not a monolithic markdown blob;
each section names its contributing distiller and its
`generated_at` / `events_covered_through`. Consumers can drop or
re-fetch sections.

**S2, S11 — Description-as-prompt-injection** (Mem0 §2.1
`search_memory` description: *"called EVERYTIME the user asks
anything"*). Tool descriptions are implicit system-prompt guidance to
the LLM about when to call. Mneme's tool descriptions should be
similarly directive (`query`: *"Call before responding to questions
that may benefit from prior context. Use `since` parameter to scope to
recent decisions."* `distill`: *"Call at session start to load the
workstream's persistent context."*). Backlog: `mem-mcp-tool-descriptions`.

**S6 — Interrupt-style pending-decision tokens** (LangGraph §2.6).
Entity-merge confirmation returns a `MergeProposal { ProposalId,
Candidates[], ProposedCanonicalName }`. The follow-up `ConfirmMerge`
call references the proposal ID. At the MCP edge, this maps cleanly
to elicitation (§2.8) — propose-then-confirm becomes a one-call
elicitation when stateful HTTP is available.

### 3.2 Ingest pipeline

**S2, S6 — Split synchronous ingest from asynchronous distillation
(critical, near-universal pattern)** (ADK §2.11, Cognee §2.5,
Graphiti §2.8 async queue, Mem0 §2.1 v3 single-pass returns
immediately, Letta §2.2 sleep-time compute). Mneme's `Ingest` returns
after the WAL commit (target <50ms). Background workers handle
classification, entity resolution, distillation. **All five fresh-
research targets converge on this pattern**; the only outlier is the
official MCP reference server, which is also the toy. Backlog:
confirm `mem-ingest-path` returns immediately after WAL commit; split
`mem-distillation-bundle` into a `DistillationJob` abstraction + a
worker.

**S5 — Single-pass ADD-only extraction prompt** (Mem0 §2.1, evidence:
+20 LoCoMo points). Mneme's ingest LLM call should extract facts
without simultaneously deciding invalidations of existing facts.
Invalidations are computed by a separate reconciliation pass that
flows through the propose-then-confirm pipeline. Backlog: re-scope
`mem-ingest-path` to remove synchronous LLM invalidation; add new
`mem-reconciliation-worker` (Phase 5).

**S5 — `ObservationDate` + `CurrentDate` dual-anchor prompting**
(Mem0 §2.1 `prompts.py:528-536`). Every Mneme ingest LLM call
includes both dates with explicit instruction to resolve relative
references against `ObservationDate` only. Improves Mneme's
`valid_at` accuracy at zero cost. Verbatim port-target.

**S5 — Integer-ID anti-hallucination** (Mem0 §2.1
`main.py:718-722`). When ingest/distillation prompts include existing
event/fact IDs for the LLM to reason over, pass sequential integers
(`0, 1, 2, ...`) as handles. Map back to ULIDs after the call.
Prevents LLM ID hallucination. Universally applicable across every
Mneme prompt that embeds a fact/event list.

**S5 — "Capture transitions, not just states"** (Mem0 §2.1
`prompts.py:611-622`). Distillation prompts explicitly instruct the
model to capture state transitions ("switched from X to Y after Z")
rather than only the latest state. Maps onto Mneme's
Decisions / Hypotheses / Outcomes arc — the transition is what makes
the fact useful.

**S2 — Inline secret redaction is non-negotiable (already in plan
+ Cognee verbatim regex port).** Cognee's regex set
(`(sk-…)`, `api[_-]?key\s*[=:]…`, `bearer …`, `password\s*[=:]…`,
replace with `prefix[:6] + "***REDACTED***"`) is battle-tested. Port
the regex set verbatim to Mneme's `IRedactor` Phase 1
implementation. Backlog: `mem-redactor-impl`.

**S6 — Async-queue immediate return shape** (Graphiti §2.8). The
MCP `remember` returns *"Event 'X' queued for processing in
workstream Y"* immediately after WAL commit. Mirror this exact
shape.

### 3.3 Distillation / retrieval

**S5 — Pipeline of named, swappable, batched task stages**
(Cognee §2.5 `get_default_tasks()`). Mneme's distillation should be
explicit named stages (classify → entity-resolve → category-bundle →
synthesize → write-projection) with per-stage `batch_size` config
and per-stage swappable implementations. Not a monolithic function.
Enables per-stage prompt iteration (re-process stage 3 only without
re-running stages 1-2). Backlog: new `mem-distillation-pipeline-stages`.

**S4 — Additive hybrid scoring with semantic-threshold gate, NOT RRF**
(Mem0 §2.1 `scoring.py`). When Phase 11 sqlite-vec lands, the
fusion formula is `combined = (semantic + bm25 + entity_boost) /
max_possible`. Semantic below threshold (default 0.1) excludes the
candidate entirely — BM25 cannot rescue. This is the v2→v3 +20-point
LoCoMo lesson. Backlog: `mem-vector-search` scoring algorithm
specification.

**S4 — Query-length-adaptive BM25 sigmoid normalization** (Mem0 §2.1
`get_bm25_params`). Pure function, ~10 lines, normalizes raw FTS5
BM25 to [0,1] with five lookup parameter sets (1-3, 4-6, 7-9, 10-15,
15+ terms). Ports trivially to C# for `mem-text-index` Phase 4 work.

**S4 — Normalize all signals to [0,1] higher-is-better BEFORE
fusion** (Mem0 §2.1 PR #5391, "score normalization bug fix across
all backends"). Multiple Mem0 vector backends shipped returning
distance (lower=better) instead of similarity. Mneme: when sqlite-vec
lands, validate that every signal source returns similarity in
[0,1] higher-is-better — write a test fixture that pins this. Backlog:
add `mem-vector-search-normalization-tests`.

**S4 — Entity memory-count penalty** (Mem0 §2.1 `main.py:1515-1517`).
Quadratic dampening `weight = 1.0 / (1.0 + 0.001 * (n-1)^2)` prevents
widely-shared entities from dominating every query. Constant `0.001`
is tunable. Cite Mem0 in implementation.

**S5 — `SummarizedContent` prompt shape** (Cognee §2.5
`extract_graph_and_summarize`). *"One leading sentence stating what
the input is about, followed by a bulleted list of self-contained
facts."* The **self-contained** constraint is critical — each bullet
survives mid-context truncation. Adopt for Mneme bundle sections.

**S5 — `WorkstreamOrientationSummary` (single-paragraph prepend)**
(Cognee §2.5 `GlobalContextSummary`). After distillation, generate a
one-paragraph "where are we" summary prepended to every bundle.
Orients the consuming LLM before the detailed bullets. Backlog:
`mem-distillation-orientation`.

**S4 — Auto-routing query dispatcher** (Cognee §2.5
`query_router.py`; tests pin 20-char negation window). Cheap regex-
based router: quoted phrases → lexical search; year ranges →
temporal; "summarize…" → bundle synthesis; relationship questions →
graph context. Mneme's `IMemoryQueryAPI` dispatcher Phase 4 should
adopt this shape — saves the consumer LLM from choosing strategy.

**S4 — Hybrid search exposed as single query** (Graphiti §2.4,
Weaviate §2.13). BM25 + vector + filter in one query call.
RRF / additive-with-gate fusion happens server-side.

**S4 — Filter-first, vector-rank-second** (Pinecone §2.12). Avoid
"vector top-k then filter" — produces empty results when filters are
restrictive. Apply category/workstream/temporal filters *before*
the vector search.

**S5 — Token-aware truncation per section** (LlamaIndex §2.7). Each
distiller targets a token budget; bundle total enforced post-
composition with the LLM's tokenizer.

**S5 — Per-workstream prompt overrides** (Cognee §2.5 v1.1.2 UI
feature; `GRAPH_PROMPT_PATH` env var). Ship Mneme with default
prompts; allow per-workstream override via config. Different domains
need different extraction prompts.

### 3.4 Entity resolution

**S7 — Three-tier resolution (Mneme's plan is already strongest)**:
1. Deterministic-key auto-merge (UUID5 from identity fields, Cognee
   §2.5 `_generate_identity_id` mechanism) — declare canonicalization
   spec per identity type (emails: lowercase + strip dots in
   localpart; IDs: as-is; names: lowercase + collapse whitespace).
2. Embedding-similarity backup (Mem0 §2.1 0.95 cosine threshold,
   `main.py:919`) — when no deterministic key, fall back to
   embedding similarity above threshold.
3. LLM-propose + human-confirm (Graphiti `dedupe_nodes.py` prompt;
   port for the LLM-propose half). Confirmation via MCP elicitation
   (§2.8) when stateful HTTP, via Mneme's own propose queue
   otherwise.

Backlog: `mem-entity-resolution-deterministic` references Cognee's
`_generate_identity_id` mechanism + Mneme's canonicalization spec
per identity type. `mem-entity-resolution-llm-propose` references
the Graphiti prompt port.

**S7 — Exact-string confirmation guard** (Letta §2.2
`core_memory_replace`). Entity-merge confirmation API must cite the
pre-merge canonical names exactly; mismatch returns
`StaleProposalError`. Prevents confirmations against stale proposals.

**S7 — Elicitation for merge confirmation** (MCP §2.8). When Mneme
runs as stateful HTTP MCP, propose-then-confirm collapses to a
single `elicitation/create` call. Backlog: `mem-mcp-elicitation`.

### 3.5 Capability / scope model

**S2 — Mneme's capability tokens are stronger than the field**.
Comparison from §2:
- **Mem0 §2.1**: three-parameter scope keys (`user_id`, `agent_id`,
  `run_id`); enforced at SQL query time. Easy to forget.
- **Cognee §2.5**: RBAC (User → Tenant → Role → ACL → Dataset),
  enforced via **application-level Python filter**, not DB-level RLS.
  Easy to bypass via direct DB access.
- **Graphiti §2.4**: `group_id` namespace only; no access control.
- **Mneme**: capability token IS the authorization, not an out-of-
  band check. Token contents are the contract.

Trade-off: Mneme's ceremony is heavier. **Mitigation**: ship
`AddMnemeMemory(opts => { opts.WorkstreamId = "x"; opts.UserId =
"alice" })` developer ergonomic that constructs the capability token
internally; expose `CapabilityToken` only to consumers who need
cross-workstream queries. Backlog: `mem-capability-token-ergonomic-api`.

**S2 — Workstream isolation by working directory (capture-side
default)** (Cognee §2.5 Claude Code plugin pattern). When the
consumer doesn't pass an explicit workstream, derive one from cwd,
git branch, or process group. Document as a recommended pattern in
`consumer-architecture-reference.md`.

**S11 — URL-path identity injection for HTTP deployment** (Mem0
§2.8 OpenMemory pattern; JWT-Bearer equivalent for Mneme). Identity
travels in URL path or JWT claims, never in tool args. Mneme.Mcp
HTTP mode: capability token as JWT Bearer claim, validated by
`AddJwtBearer` + `RequireAuthorization()`. Stdio mode: env var
`MNEME_CAPABILITY_TOKEN` set at process launch.

**S2 — Path-traversal guard pattern** (Basic Memory §2.8
`validate_project_path()`). Regex-validate every workstream-id
parameter at the MCP boundary before any storage operation.
Backlog: `mem-workstream-id-validation`.

### 3.6 Process / deployment

**S6 — Sleep-time compute pattern** (Letta §2.2
`sleeptime_multi_agent_v4.py`; arXiv 2504.13171). The primary
consumer agent owns user-facing latency; a background worker (sleep-
time agent) does heavy memory writes. Mneme's distillation worker IS
the sleep-time pattern — make this explicit in docs. Use
`safe_create_task` (asyncio.create_task analogue in C# is
`Task.Run` with `ContinueWith` for tracking) for fire-and-forget;
track jobs via `DistillationJob` with status (`created → running →
completed/failed`).

**S6 — Single-session mutex via lock acquisition** (Cognee §2.5
`try_acquire_improve_lock`, Letta §2.2 optimistic locking + retry).
Mneme's `DistillationJob` worker must hold a per-workstream lock
during its run to prevent concurrent distillations from corrupting
state. Cognee uses Python's `cognee/infrastructure/locks/`; Mneme
should use a SQLite-row-level pessimistic lock (table
`distillation_locks(workstream_id PRIMARY KEY, holder_id, acquired_at)`
with `ON CONFLICT DO NOTHING` semantics). Idle/quiet-watcher
triggers + SessionEnd hooks both target the same worker; the lock
deduplicates.

**S10 — Snapshot checkpoints for projection rebuild** (Letta §2.2
`BlockHistory` full-row snapshots, Letta v3 compaction). Periodic
snapshots (every N events or every T hours) make rebuilds O(1) from
nearest snapshot rather than O(N) from genesis. Backlog: new
`mem-projection-snapshots` (Phase 4 sub-task).

**S10 — Embedded → Sidecar → Service** (already in plan; Letta
self-host → Letta Cloud, Zep self-host → Zep Cloud all validate
this progression).

**S10 — Workstream export format** (Marten / KurrentDB pattern;
Letta `.af` agent-file). Single-file export (event log + projections
+ artifacts for one workstream) for support cases, GDPR Article 20
data-portability, and migration. Backlog: new `cross-cutting-
workstream-export`.

### 3.7 MCP exposure

**S11 — Final Mneme.Mcp tool surface** (full evidence in §2.8).
Five tools, one prompt, one resource:
- `remember` (replaces `ingest`; community vocabulary)
- `query` (kept; alias `recall` in description)
- `distill` (kept; differentiator; returns `resource_link` to
  `mneme://workstream/{id}/context`; `TaskSupport=Optional` once
  experimental flag stabilizes)
- `forget` (replaces `revoke`)
- `list_recent` (NEW; every competitor has it)
- `mneme_context` MCP prompt (`/mneme_context` slash command in
  Claude Desktop, VS Code Copilot)
- `mneme://workstream/{id}/context` subscribable resource with
  `notifications/resources/updated` after background distillation

All five tools ship with **explicit** annotations (the C# SDK's
defaults `Destructive=true`, `OpenWorld=true` are wrong for `query`).

Backlog: rewrite `mem-mcp-server` with this exact surface; add
`mem-mcp-prompts`, `mem-mcp-resources`, `mem-mcp-list-recent`,
`mem-mcp-elicitation`, `mem-mcp-sampling-mode`.

**S11 — Mneme.Mcp is a thin DI client to Mneme.Core** (Basic Memory
§2.8 pattern). MCP tools have constructor-injected
`IMnemeQueryService`, `IMnemeIngestService`, `IMnemeDistillService`.
Per-request instance lifecycle. Protocol translation only; no
business logic.

**S11 — Subscribable distill resource (Mneme differentiator)**.
None of the surveyed servers ship subscribable evolving context. Push
updates after distillation completes. Plays directly to Mneme's
projection-rebuild architecture.

**S11 — Sampling-based distillation (graceful fallback)**. When
`thisServer.ClientCapabilities?.Sampling != null`, send
`sampling/createMessage` with structured fact bundle as
`systemPrompt`. Client's LLM synthesizes. Falls back to local
`IChatClient` if absent. Mneme becomes model-agnostic by design.

**S11 — IProgress<ProgressNotificationValue> for distill progress**.
Distillation is multi-second; emit progress notifications via the
auto-injected `IProgress<>`. Clients show progress bars.

**S11 — Two deployment modes**: `Mneme.Mcp.Stdio` (Claude Desktop,
no elicitation, no sampling, capability token from env var) and
`Mneme.Mcp.Http` (multi-client, stateful, JWT Bearer auth, full
elicitation + sampling). Document the split clearly.

### 3.8 Observability

**S3 — OpenTelemetry from Phase 1 with Mneme-specific semantic
attributes** (Cognee §2.5 pattern). Activation guard
(`MnemeNullSpan` no-op when tracing disabled — zero overhead in
default builds). Span names:
- `mneme.ingest.event`
- `mneme.classify.run`
- `mneme.redactor.run`
- `mneme.entity.resolve` (tags: `method=deterministic|embedding|llm-proposed|human-confirmed`)
- `mneme.distill.run` (tags: input/output tokens, bundle size,
  workstream)
- `mneme.projection.rebuild`
- `mneme.query.execute` (tags: signal count, gated_count,
  capability check)

Backlog: new cross-cutting item `obs-otel-baseline` (Phase 1).

**S3 — Align with `gen_ai.*` namespace from MAF's OpenTelemetryAgent**
(MAF §2.15 OpenTelemetry Semantic Conventions for GenAI v1.37).
Mneme spans should be children of MAF's `invoke_agent` span when
consumed via `Mneme.Agents.AI`. Use the same `ActivitySource` family
name. MCP spans should set `mcp.method.name`, `gen_ai.tool.name`,
`mcp.session.id`, `network.transport` per MAF Python PR `dcc218d`
(2026-06-05).

**S3 — Secret redaction at span-attribute write time** (Cognee §2.5
`tracing.py:redact_secrets()`). Apply Mneme's `IRedactor` to all
span attribute values inline at emission — not at log emission time.
Port Cognee's regex set verbatim.

**S3 — In-memory trace buffer for developer ergonomics** (Cognee
§2.5 `CogneeSpanExporter` circular buffer of last 50 traces).
Mneme could ship `IMneme.GetLastDistillationTrace()` /
`IMneme.GetAllTraces()` returning
`{operation, total_duration_ms, span_count, breakdown_by_span_name,
errors}`. Highly valuable when developing consumer agents.

**S3 — `Explain` flag carries all the same provenance** (Mem0 §2.1).
Per-query score decomposition (semantic, BM25, entity, filter,
capability) lives in `QueryResult.ScoreDetails`. Cite-traceable in
production.

**S3 — Distillation provenance baked into every derived item**.
Per `plan.md`, every `Fact`/`Decision`/etc records `Provenance`
(source, agent, model, prompt hash). Bake into the distiller worker
output; expose in `QueryResult.Provenance`.

---

## 4. Where the Mneme plan looks weakest (stress-test)

*Consolidates "Stress-test for Mneme" notes from §2 plus the synthesis
above. Items 4.7 and 4.8 are new findings from the fresh research.*

### 4.1 The seven epistemic categories — possibly over-rich

Letta succeeds with 2 default blocks (`human` + `persona`); MemFS
agents produce 5-10 markdown files in practice (§2.2). Mem0 ships
three types (semantic / episodic / procedural, §2.1). Graphiti uses
3 tiers (§2.4); LangGraph uses 3 types (§2.6). Mneme bets seven
categories enable better category-specific distillation prompts.

**Mitigation**: Surface all 7 in storage; expose a small *index*
in the MCP bundle (the always-loaded part); make full category-typed
bundle sections opt-in. Aligns with the BundleIndex pattern in §3.1.
**Validation milestone**: on the first real consumer workstream
(MuxiMuxi), audit how often events get misclassified between
categories and whether distillation outputs are meaningfully
different per category. If not, collapse to typed Facts with
category labels.

**Mitigation 2**: Make epistemic categories *runtime-extensible*
post-v1 (Cognee §2.5 `DataPoint` precedent). Consumers register
custom categories via capability-token-scoped registration.
Backlog: new `arch-category-extensibility` (post-v1).

### 4.2 Synchronous distillation pipeline — latency risk

Mem0's evidence is decisive: +20 LoCoMo points from dropping
synchronous LLM-driven invalidation (§2.1). ADK §2.11, Cognee §2.5,
Graphiti §2.8 async queue, and Letta §2.2 sleep-time all converge on
async pipelines.

**Action**: Document the sync/async split explicitly in Phase 1
(`mem-ingest-path`). Ingest returns after WAL commit (<50ms target).
Distillation is a separate `DistillationJob` with status tracking.
Surface job status via `IMemoryQueryAPI` so consumers can poll.

**Document**: distillation lag is real; consumers will sometimes
query before distillation completes. **Bundle responses must include
`generated_at` + `events_covered_through`** so consumers can detect
staleness. Provide a `force_refresh` parameter on `Distill` for
explicit re-run. Backlog: `mem-distillation-staleness-indicator`.

### 4.3 Substrate scaling bound — undefined

`research-zep-sqlite-deepdive.md` confirms SQLite is the right v1
substrate, but the upper bound is hand-waved. Pinecone (§2.12)
demonstrates real customers pay for vector scale. Mneme's Phase 11
should include a benchmark establishing the empirical upper bound
of sqlite-vec at 1M, 5M, 10M embeddings before committing to it.
Without this, "v2 vector search" is a hand-wave. Backlog:
`mem-vector-benchmark` (Phase 11 prerequisite).

### 4.4 Storage abstraction not cleanly internal

The plan describes the SQLite schema in detail but doesn't define an
internal `IEventStore` abstraction inside `Mneme/Storage/`. Marten
(§2.18) and KurrentDB (§2.17) show the .NET-idiomatic shape. Without
an internal abstraction, shipping `Mneme.PostgreSql` or
`Mneme.SqlServer` later requires a refactor. **Recommended**: in
Phase 1, design the storage layer behind an internal interface (not
in `Mneme.Contracts`; just in `Mneme/Storage/`) so SQLite is one
implementation among potentially others.

### 4.5 MCP tool naming — confirmed misaligned

§2.8 source-level research confirms the field has converged on
`remember` / `recall` / `forget` (with `add_memories` / `search_memory`
/ `delete_memories` as the Mem0 variant). Mneme's planned
`query` / `distill` / `ingest` / `revoke` matches none of these.
**Action**: rename `ingest` → `remember`, `revoke` → `forget` at the
MCP edge; keep `query` and `distill` (both distinctive). Keep the
.NET surface (`IMemoryQueryAPI`) unchanged — only the MCP edge is
renamed. Backlog: rewrite `mem-mcp-server` task with new tool names.

### 4.6 No "managed deployment" path documented

Zep (§2.3) and Letta Cloud (§2.2) show the commercial shape of
memory substrates is a managed service. Mneme's plan has Phase 9
(sidecar) and Phase 10 (cloud snapshot sync) but no "Mneme as a
hosted multi-tenant service" phase. Not urgent for v1, but the
capability-token + workstream isolation model already supports it;
the omission is a documentation gap, not an architectural one. Add
a forward-looking note to `plan.md` or a Phase 12 stub.

### 4.7 **NEW: `Semantic Kernel as planned indirection` is outdated**

MAF §2.15 source-level research confirms: MAF uses
`Microsoft.Extensions.AI.IChatClient` directly (not Semantic Kernel).
Semantic Kernel was superseded by MAF, which depends on
`Microsoft.Extensions.AI ≥ 10.4.0`. Every MAF user already has
`IChatClient` registered.

**Action**: `AGENTS.md` locked-decisions table includes
`Semantic Kernel as planned indirection`. **Revisit this decision.**
The right LLM abstraction is `IChatClient` (from
`Microsoft.Extensions.AI`). Update `mem-llm-classifier`,
`mem-distillation-bundle`, `mem-entity-resolution-llm-propose` to
specify `IChatClient`. Remove Semantic Kernel references from
`plan.md`. Adds zero dependency for MAF users; works with
non-MAF callers too. Backlog: `agents-md-revisit-sk-decision`.

### 4.8 **NEW: Mneme has no published benchmark numbers**

Mem0 publishes 92.5 LoCoMo / 94.4 LongMemEval at
`github.com/mem0ai/memory-benchmarks` (§2.1). Memory-product
credibility comes from numbers. Mneme's bi-temporal claim is
unverified.

**Action**: Run LoCoMo + LongMemEval as soon as Phase 4 is
queryable. **Expect Mneme to win on the temporal subcategory** —
that's the dimension where Mneme's bi-temporal model should
architecturally beat single-timestamp competitors. If Mneme doesn't
beat Mem0 on temporal LoCoMo, something is wrong. Publish the
results — they're the credibility shortcut. Backlog: new
`mem-benchmark-locomo-longmemeval` (Phase 4.5).

### 4.9 **NEW: Onboarding ergonomics gap vs Mem0**

Mem0's three-line `add_memories(text)` (with identity from URL) is
far lower friction than Mneme's "construct `CapabilityToken` from
workstream + scope + signature." Mneme will lose adoption to
Mem0Provider (already shipping as `Microsoft.Agents.AI.Mem0`) unless
the onboarding path matches.

**Action**: Ship `AddMneme(opts => { opts.WorkstreamId = "x";
opts.SqlitePath = "mneme.db"; })` developer ergonomic
in `Mneme.Agents.AI` package. The full `CapabilityToken` machinery
stays available for multi-workstream / multi-tenant scenarios but
isn't required for the 90% case. Backlog: `mem-developer-ergonomic-defaults`.

### 4.10 **NEW: No "list" / status tool — confirmed gap**

§2.8 source research confirms every memory MCP server has a way to
dump/enumerate stored memories. Mneme has no `list` or `recent`
tool. Agents need it to avoid re-ingesting what they've already
stored. **Action**: add `list_recent(workstream?, limit=10)` to
`Mneme.Mcp` tool surface and corresponding `IMemoryQueryAPI.ListRecentAsync`.

### 4.11 **NEW: Vector-only / no fuzzy-recall path is a gap**

Letta archival memory hits the limits of vector-only at scale (§2.2),
but having *some* fuzzy-recall path is table-stakes. Mneme's plan
defers vector search to Phase 11. Without any fuzzy-recall path in
v1, fuzzy-recall users will reach for Letta or Mem0 even when they
need Mneme's bi-temporal strengths.

**Mitigation**: Ship SQLite FTS5 (text-index) Phase 4 (already in
plan), exposed via `IMemoryQueryAPI.QueryAsync` with description
that emphasizes fuzzy-text-match. Mneme's FTS5 + adaptive BM25
sigmoid (§3.3) is genuinely competitive for retrieval without
embeddings.

---

## 5. Suggested updates (not committed)

*Per user instruction, do NOT update `backlog.md` or `plan.md`
automatically. These are recommendations for follow-up review. Each
item is mapped to the Mneme design surface it touches and the source
§2 sub-section that justifies it.*

### 5.1 New backlog candidates (Phase-mapped)

**Phase 0 (Contracts)**
- `arch-category-extensibility` — runtime category extensibility design
  (post-v1; cite Cognee `DataPoint`); document as future option in
  `Mneme.Contracts` design notes. (S1, §2.5 + §4.1)
- `agents-md-revisit-sk-decision` — revisit Semantic Kernel → `IChatClient`
  in locked-decisions table; update prompts/specs. (S13, §2.15 + §4.7)
- `contracts-distillation-bundle-reshape` — split `ContextBundle` into
  `BundleIndex` + `BundleSection`; add `LookupHints` and
  `OrientationSummary` sections; add `generated_at` /
  `events_covered_through` for staleness. (S5, §2.2 + §2.5 + §3.1)
- `contracts-event-categories-extend` — add `EventChannel` enum
  (`Epistemic | Technical`) so workflow-checkpoint and similar
  non-epistemic events don't pollute the 7 categories. (S1, §2.15)

**Phase 1 (Bootstrap + ingest)**
- `mem-ingest-async-split` — confirm and document the
  sync-ingest / async-distillation split; ensure `Ingest` returns
  <50ms after WAL commit. (S2, §2.1 + §2.5 + §2.11 + §3.2)
- `mem-redactor-impl` — port Cognee's regex set verbatim. (S2,
  §2.5 + §3.2)
- `mem-developer-ergonomic-defaults` — ship
  `AddMneme(opts => { opts.WorkstreamId = ...; opts.SqlitePath = ... })`
  registration helper. (S2, §2.1 + §4.9)
- `obs-otel-baseline` — OpenTelemetry from Phase 1; `MnemeNullSpan`
  no-op default; span name + attribute taxonomy. (S3, §2.5 + §2.15 +
  §3.8)
- `mem-workstream-id-validation` — regex-validate at MCP boundary.
  (S2, §2.8 + §3.5)

**Phase 2 (Capability tokens)**
- `mem-capability-token-ergonomic-api` — ergonomic constructor
  hiding the token mechanics for the 90% case. (S2, §2.1 + §2.5 +
  §4.9)

**Phase 3 (Classifier + entity resolution)**
- `mem-entity-resolution-deterministic` — UUID5 mechanism (Cognee)
  + canonicalization spec per identity type (Mneme-specific
  contribution beyond Cognee). (S7, §2.5 + §3.4)
- `mem-entity-resolution-llm-propose` — port Graphiti's
  `dedupe_nodes.py` prompt; integer-ID anti-hallucination handling.
  (S7, §2.1 + §2.4 + §3.4)
- `mem-llm-classifier` — switch LLM abstraction to `IChatClient`
  (not Semantic Kernel). (S13, §2.15 + §4.7)

**Phase 4 (Projections + Query API)**
- `mem-projection-snapshots` — periodic full-row snapshots for O(1)
  rebuilds (Letta `BlockHistory` pattern). (S10, §2.2 + §3.6)
- `mem-pipeline-status-table` — `event_processing_log(event_id,
  projection_name, status, processed_at)` for selective re-projection
  (Cognee pattern). (S10, §2.5 + §3.3)
- `mem-query-api-explain-flag` — `Explain: bool` parameter returning
  per-signal score decomposition. (S6, §2.1 + §3.8)
- `mem-query-api-dispatcher` — rule-based query router (regex →
  strategy). (S4, §2.5 + §3.3)
- `mem-text-index-adaptive-bm25` — query-length-adaptive sigmoid
  normalization. (S4, §2.1 + §3.3)
- `mem-text-index-tests` — FTS5 + adaptive BM25 fixture tests; pin
  the formula. (S4, §2.1)
- `mem-benchmark-locomo-longmemeval` — run benchmarks; publish
  results; expect win on temporal subcategory. (S14, §2.1 + §4.8)

**Phase 5 (Distillation)**
- `mem-distillation-pipeline-stages` — explicit named stages with
  per-stage `batch_size`. (S5, §2.5 + §3.3)
- `mem-distillation-orientation` — `WorkstreamOrientationSummary`
  paragraph prepend. (S5, §2.5 + §3.1)
- `mem-distillation-lookup-hints` — `LookupHints` section in bundle.
  (S5, §2.2 + §3.1)
- `mem-distillation-staleness-indicator` — `generated_at`,
  `events_covered_through`, `force_refresh`. (S5, §2.2 + §4.2)
- `mem-distillation-prompt-observation-date` — port Mem0's
  Observation-Date / Current-Date dual-anchor prompting verbatim.
  (S5, §2.1 + §3.2)
- `mem-distillation-prompt-transitions` — port Mem0's "capture
  transitions" prompt instruction. (S5, §2.1 + §3.2)
- `mem-distillation-job-abstraction` — `DistillationJob` with
  status tracking (Letta `Run/RunStatus` pattern). (S6, §2.2 + §3.6)
- `mem-distillation-lock` — per-workstream pessimistic SQLite lock
  to deduplicate concurrent triggers. (S6, §2.5 + §3.6)
- `mem-reconciliation-worker` — async reconciliation pass that
  proposes invalidations through the propose-then-confirm pipeline
  (NOT synchronous in ingest LLM call). (S9, §2.1 + §3.2)

**Phase 7 (Outcomes)**
- `mem-outcome-closure-feedback-weights` — `feedback_weight +=
  alpha * (score - 0.5)` on Evidence records linked to Outcomes;
  Cognee's `improve()` pattern adapted to Mneme. (S8, §2.5 + §3.3)

**Phase 8 (MCP server) — rewrite scope**
- `mem-mcp-server` (rewrite) — final tool surface: `remember`,
  `query`, `distill`, `forget`, `list_recent`. Annotations explicit
  on every tool. (S11, §2.8)
- `mem-mcp-prompts` — `mneme_context` MCP prompt (slash command).
  (S11, §2.8)
- `mem-mcp-resources` — `mneme://workstream/{id}/context`
  subscribable resource. (S11, §2.8)
- `mem-mcp-list-recent` — new tool. (S11, §2.8 + §4.10)
- `mem-mcp-elicitation` — propose-then-confirm via
  `elicitation/create` when stateful HTTP. (S11, §2.8 + §3.4)
- `mem-mcp-sampling-mode` — distillation via
  `sampling/createMessage` when client supports it. (S11, §2.8)
- `mem-mcp-tool-descriptions` — prompt-injection-strength
  description text for each tool. (S11, §2.8 + §3.1)
- `mem-mcp-skills-source` — expose `skill://index.json` for SEP-2640
  agent skill discovery. (S11, §2.15)
- `mem-mcp-tool-task-support` — mark `distill` with
  `TaskSupport=Optional` once the experimental flag stabilizes. (S11,
  §2.15)
- `mem-mcp-auth-token-flow` — env var + JWT Bearer split between
  stdio/HTTP modes. (S2, §2.8)

**Phase 8.5 (new) — MAF integration**
- `mneme-maf-integration` — new `Mneme.Agents.AI` NuGet package
  implementing `MessageAIContextProvider`; ship demo + README
  comparing against `Mem0Provider`. (S14, §2.15)
- `mneme-maf-checkpoint-store` — `MnemeCheckpointStore :
  ICheckpointStore<JsonElement>` shipping in same package; durable
  MAF workflow checkpoints backed by `memory_events`. (S14, §2.15)
- `mneme-maf-purview-integration` — investigate
  `Microsoft.Agents.AI.Purview` for governance hooks. (S14, §2.15)

**Cross-cutting**
- `cross-cutting-workstream-export` — single-file workstream export
  (event log + projections + artifacts). GDPR Article 20
  data-portability. (S10, §2.18 + §2.2 + §3.6)
- `cross-cutting-doctor-command` — `mneme bundle health
  --workstream X` CLI reporting token usage, stale bundles,
  projection drift. (S3, §2.2 + §3.1)

### 5.2 Plan amendments

- **Split sync ingest from async distillation** as a documented
  architectural decision (currently implicit in the 7-step list).
  (S2, §4.2)
- **Add bundle staleness contract** to the public bundle schema
  documentation (`generated_at`, `events_covered_through`,
  `force_refresh` parameter). (S5, §4.2)
- **Document the propose-then-confirm pipeline as the canonical
  invalidation path** — synchronous LLM-driven invalidation in
  ingest is explicitly out-of-scope per Mem0's evidence. (S9, §3.2)
- **Document `EventChannel` distinction** (`Epistemic` vs
  `Technical`) so workflow-checkpoints and similar don't pollute
  the 7 epistemic categories. (S1, §2.15)
- **Document `Mneme.Agents.AI` package as a Phase 8.5 deliverable**
  alongside `Mneme.Mcp` Phase 8 — both are integration packages;
  Mneme's reach into the .NET agent ecosystem depends on shipping
  both. (S14, §2.15)
- **Document the LoCoMo + LongMemEval benchmark plan** with
  expected results (temporal subcategory win). (S14, §4.8)

### 5.3 Locked-decision revisits

- **`Semantic Kernel as planned indirection` → `Microsoft.Extensions.AI.IChatClient`**.
  Concrete update text:
  > LLM-provider abstraction: `Microsoft.Extensions.AI.IChatClient`
  > (`Microsoft.Extensions.AI ≥ 10.4.0`). Aligns with Microsoft Agent
  > Framework's own abstraction (HEAD `fa9e0865`, June 2026), avoids
  > the additional Semantic Kernel dependency. Mneme's `Mneme/Llm/`
  > project depends only on
  > `Microsoft.Extensions.AI.Abstractions`.
  (S13, §2.15 + §4.7)

- **No `IMemoryStore` integration in MAF** — the integration seam is
  `MessageAIContextProvider`. Add to locked decisions:
  `Mneme.Agents.AI` implements `MessageAIContextProvider` (from
  `Microsoft.Agents.AI`); no custom `IMemoryStore` interface is
  introduced. (S14, §2.15)

- **MCP tool naming**: stable community convention is
  `remember`/`recall`/`forget` (with `list_recent`). Lock the MCP
  edge naming to this; keep .NET `IMemoryQueryAPI` names unchanged.
  (S11, §2.8 + §4.5)

### 5.4 Forward-looking notes for plan.md

- **Phase 12+ (post-v1) candidates**:
  - Managed multi-tenant deployment (§4.6).
  - Runtime category extensibility (§4.1).
  - `AnswerAsync(question, workstream, token)` at MCP edge (synthesis
    from bundle) — currently rejected (Mneme is substrate not
    answer-engine), but accept if user demand is loud (§2.5
    stress-test).
  - Cross-workstream queries (current model is workstream-scoped).
  - Multi-agent concurrent-write story (Letta's MemFS git-worktree
    pattern; not relevant at v1 scale but real at v3 scale, §2.2).

---

## 6. Sources

*Per-system sources are cited inline in §2. Cross-cutting sources:*

- `plans/research-existing-systems.md` — original 19-system fit/no-fit
  survey
- `plans/research-zep-sqlite-deepdive.md` — Graphiti source-code
  analysis, SQLite capability proof, DDL blueprint
- `plans/plan.md` — Mneme design surfaces and 11-phase sequencing
- `plans/backlog.md` — dependency-ordered task list
- `plans/consumer-architecture-reference.md` — first-consumer (MuxiMuxi)
  integration shape
- `AGENTS.md` — locked decisions and architectural rules

Fresh-research sources for all five fast-mover frameworks are cited
inline per-section in §2 with commit SHAs and file:line ranges:
- Mem0 §2.1 — `github.com/mem0ai/mem0` HEAD `366945965`
- Letta §2.2 — `github.com/letta-ai/letta` HEAD `11315357` +
  `letta-ai/letta-code` HEAD `9e514fdf`
- Cognee §2.5 — `github.com/topoteretes/cognee` HEAD `cfb0aa4d`
- MCP ecosystem §2.8 — `modelcontextprotocol/{servers,csharp-sdk}`
  plus 5 server repos
- MAF + KM² §§2.15-2.16 — `microsoft/agent-framework` HEAD `fa9e0865`
  + `microsoft/kernel-memory` HEAD `94b69d34`

All source-level citations include exact file paths and line ranges
or function names for navigation. Where verbatim prompts or SQL
schema definitions appear in the doc, they are quoted from the
indicated source line range.
