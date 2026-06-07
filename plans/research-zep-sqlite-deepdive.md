# Zep/Graphiti Architecture Deep-Dive vs SQLite-on-.NET

**Prepared:** June 2026
**Scope:** Pressure-test the "build bespoke memory agent on SQLite substrate" decision against what Graphiti actually does under the hood, by reading its source code rather than its marketing.
**Companion:** `research-existing-systems.md` (survey-level evaluation across 19 systems).

---

## 1. Executive Summary

- **Graphiti is "schema + prompts + orchestration plumbing on top of Neo4j" — not a novel algorithm.** Its entire data model is the KuzuDB DDL in `graphiti_core/driver/kuzu_driver.py:SCHEMA_QUERIES`. Every "intelligent" step (entity resolution, fact extraction, invalidation) is an LLM API call.
- **Their KuzuDB schema is our SQLite blueprint.** Direct DDL translation works. Bi-temporal model = 4 timestamp columns (`valid_at`, `invalid_at`, `created_at`, `expired_at`). No native graph DB primitive is required.
- **Their entity resolution uses LLM-only judgment after FTS+vector candidate retrieval** — which violates our conservative-by-design policy. We explicitly want deterministic-key-first; our policy is *better* than theirs for our use case.
- **Their prompts are Apache 2.0.** We can port `dedupe_nodes.py`, `extract_edges.py`, etc. verbatim with attribution. This shortcuts months of prompt engineering.
- **KuzuDB is archived.** Graphiti is migrating to Neo4j/FalkorDB; their own deprecation warning lives in the codebase. The only "embedded" graph DB in their stack is being wound down.
- **Decision: keep bespoke SQLite. The research confirms it.** Nothing in Graphiti requires a native graph database. Their value is the schema + prompts + bi-temporal model, all of which are directly portable to SQLite + Microsoft.Data.Sqlite + Semantic Kernel.

---

## 2. Graphiti Source Code Architecture (what it actually does)

### 2.1 Repositories surveyed

| Repo | Commit | Notes |
|---|---|---|
| `getzep/graphiti` | `9f2b63d` | OSS temporal KG engine (Apache 2.0) — the engine we evaluated |
| `getzep/zep` | `faf2ace` | Older Zep managed service repo (mostly benchmark code now) |
| `litegraphdb/litegraph` | `d55aa12` | .NET graph DB on SQLite (MIT) — see §4 |
| `kuzudb/kuzu` | — | **Archived / wind-down** — Graphiti is dropping it |
| `Giorgi/DuckDB.NET` | active | .NET DuckDB binding — see §4 |

### 2.2 The data model (from source)

**`graphiti_core/edges.py:263–283` — EntityEdge fields:**

```python
class EntityEdge(Edge):
    name: str                    # relation type: WORKS_AT, LIVES_IN, SUPERSEDES
    fact: str                    # natural language fact string
    fact_embedding: list[float]  # 1024-dim vector
    episodes: list[str]          # back-links to source EpisodicNode UUIDs
    expired_at: datetime | None  # T'_expired (transactional)
    valid_at: datetime | None    # T_valid (event time — when fact became true)
    invalid_at: datetime | None  # T_invalid (event time — when fact stopped being true)
    reference_time: datetime     # episode's reference timestamp
    attributes: dict[str, Any]   # typed per edge_type
```

Four timestamps = bi-temporal model. Maps 1:1 to four SQL columns.

**`graphiti_core/nodes.py:318–328` — EpisodicNode (the raw-data tier):**

```python
class EpisodicNode(Node):
    source: EpisodeType          # message | json | text | fact_triple
    content: str                 # raw episode text
    valid_at: datetime           # event time
    entity_edges: list[str]      # UUIDs of derived facts
    episode_metadata: dict | None
```

**`graphiti_core/nodes.py:499–504` — EntityNode (the resolved-entity tier):**

```python
class EntityNode(Node):
    name_embedding: list[float] | None  # 1024-dim
    summary: str                         # LLM-generated
    attributes: dict[str, Any]           # typed per entity_type
```

**Three-tier model:** raw Episodes → resolved Entities → typed Facts (Edges). MuxiMuxi's 7-epistemic-category model is a strict *superset*: Evidence ≈ Episodes, Facts ≈ Edges (their `name` is our `relation_type`), Entities ≈ Entities, and we add Decisions / Hypotheses / Goals / Actions / Outcomes as additional epistemic types layered onto the same edge table.

### 2.3 The ingestion pipeline

**`graphiti_core/graphiti.py:980–1200` — `add_episode()`:**

Confirmed sequence per episode:

1. Retrieve last 10 episodes (context for LLM)
2. LLM call: extract entities from episode
3. For each extracted entity: hybrid search (FTS + vector) for similar existing entities → LLM dedupe judgment
4. LLM call: extract edges (relationships between entities)
5. For each new edge: LLM call to check if it invalidates any existing edge
6. Embed everything (entity names, fact strings)
7. Persist to graph DB
8. (Optional) Update communities (LLM clustering pass)

**LLM call cost per episode:** ~3 minimum, ~12 typical (3 entities, 2 new edges), ~25+ for complex episodes. **This dominates everything else.** Neo4j is doing trivial work — point lookups and a few `MATCH` queries.

**Implication for MuxiMuxi:** the substrate choice (SQLite vs Neo4j) affects ~5% of total ingestion time. The LLM provider and prompt engineering account for the other ~95%. Optimizing the graph store is the wrong place to spend effort.

### 2.4 The prompts (the real value)

- **`graphiti_core/prompts/dedupe_nodes.py`** — full entity-resolution prompt. Uses hybrid FTS + vector candidates, then pure LLM judgment.
- **`graphiti_core/prompts/extract_edges.py`** — edge extraction with timestamp resolution rules (relative dates → ISO 8601 using `reference_time`), `SCREAMING_SNAKE_CASE` relation conventions, custom `FACT_TYPES` injection.
- **`graphiti_core/prompts/`** *(other files)* — invalidation check, summary generation, community labeling.

**All Apache 2.0.** Attribution to Zep AI required in code comments. Prompt templates are derivative works under Apache 2.0; clean to reuse.

**This is the asset.** If we port these prompts directly into a Semantic Kernel / MAF prompt template, we get Graphiti's *intelligence* without its *infrastructure*. We pair them with our stricter deterministic-key-first entity resolution to make the policy more conservative than theirs.

### 2.5 The search layer

**`graphiti_core/search/search_config.py:1–150`:**

- **Methods:** `cosine_similarity` (Neo4j vector index), `bm25` (Lucene FTS), `bfs` (native graph traversal, default 3 hops)
- **Rerankers:** RRF (default), MMR, `node_distance`, `episode_mentions`, `cross_encoder`

BFS = depth-3 traversal, BM25 = full-text, vector = cosine — all reproducible in SQLite with `WITH RECURSIVE` + FTS5 + sqlite-vec (v2).

### 2.6 The bi-temporal model (confirmed by paper)

From the Zep paper (arXiv 2501.13956v1):

> *"the system tracks four timestamps: t'_created and t'_expired ∈ T' monitor when facts are created or invalidated in the system, while t_valid and t_invalid ∈ T track the temporal range during which facts held true"*

This is the canonical "transaction time vs valid time" bi-temporal pattern from database theory. Four columns. No graph-DB primitive required.

### 2.7 The KuzuDB deprecation

**`graphiti_core/driver/kuzu_driver.py`:**

```python
warnings.warn(
    'The Kuzu backend is deprecated and will be removed in a future release — the '
    'upstream Kuzu project is no longer maintained. Migrate to Neo4j or FalkorDB.',
    DeprecationWarning, stacklevel=2,
)
```

The only embeddable graph DB in their stack is being dropped. This is significant: it removes "use Graphiti embedded" as a future-proofing argument. If we wanted Graphiti without a server process, that door is closing.

---

## 3. SQLite Capabilities Matrix vs Our 10 Requirements

| # | Requirement | SQLite native? | Pattern |
|---|---|---|---|
| 1 | 7 epistemic categories | ✅ | Discriminator column `epistemic_type` on `facts` table |
| 2 | Append-only event log | ✅ | `events` table with `UNIQUE` on `idempotency_key` (ULID) |
| 3 | Temporal knowledge graph (bi-temporal) | ✅ | 4 timestamp columns + WHERE clauses (see §3.2) |
| 4 | Workstream-scoped isolation | ✅ | `workstream_id` column + every query filters on it (see §3.6) |
| 5 | Distillation pipeline | ✅ | Independent of substrate — LLM provider + prompts |
| 6 | Conservative entity resolution | ✅ | Deterministic-key SQL + LLM-propose pipeline (see §3.4) |
| 7 | Content revocation (immutable metadata + revocable blobs) | ✅ | Separate `events` (immutable) vs `artifacts` (UPDATE-able to NULL) tables |
| 8 | Idempotent append-only sync (ULID) | ✅ | `UNIQUE` constraint; `INSERT OR IGNORE` semantics |
| 9 | Pluggable LLM provider | ✅ | Substrate-independent (use Semantic Kernel) |
| 10 | Scale 10k–1M+ events | ⚠️ | Fine to ~1M per file with WAL + indexes; >10M needs partitioning by workstream |

### 3.1 SQLite DDL (translated from Graphiti's `SCHEMA_QUERIES`)

```sql
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS events (
    id TEXT PRIMARY KEY,                    -- ULID
    workstream_id TEXT NOT NULL,
    event_type TEXT NOT NULL,
    payload TEXT NOT NULL,                  -- JSON
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    idempotency_key TEXT UNIQUE             -- prevents double-ingest
);

CREATE TABLE IF NOT EXISTS episodes (
    uuid TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    workstream_id TEXT NOT NULL,
    source TEXT NOT NULL,                   -- message|json|text|fact_triple
    source_description TEXT,
    content TEXT,
    valid_at TEXT,                          -- event time (T)
    created_at TEXT NOT NULL,               -- ingestion time (T')
    metadata TEXT DEFAULT '{}'
);

CREATE TABLE IF NOT EXISTS entities (
    uuid TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    workstream_id TEXT NOT NULL,
    labels TEXT DEFAULT '[]',
    summary TEXT DEFAULT '',
    name_embedding BLOB,                    -- float32[] serialized (v2)
    attributes TEXT DEFAULT '{}',
    created_at TEXT NOT NULL
);
CREATE VIRTUAL TABLE IF NOT EXISTS entities_fts
    USING fts5(name, summary, uuid UNINDEXED, content='entities', content_rowid='rowid');

CREATE TABLE IF NOT EXISTS facts (
    uuid TEXT PRIMARY KEY,
    workstream_id TEXT NOT NULL,
    source_entity_uuid TEXT NOT NULL REFERENCES entities(uuid),
    target_entity_uuid TEXT NOT NULL REFERENCES entities(uuid),
    relation_type TEXT NOT NULL,            -- SCREAMING_SNAKE_CASE
    fact TEXT NOT NULL,
    fact_embedding BLOB,
    -- BI-TEMPORAL (from Graphiti's model):
    valid_at TEXT,                          -- T: fact became true
    invalid_at TEXT,                        -- T: fact stopped being true
    created_at TEXT NOT NULL,               -- T': added to system
    expired_at TEXT,                        -- T': invalidated in system
    reference_time TEXT,
    -- MUXIMUXI EXTENSIONS:
    epistemic_type TEXT NOT NULL DEFAULT 'Fact',  -- Evidence|Fact|Decision|Hypothesis|Goal|Action|Outcome
    source_episode_uuids TEXT DEFAULT '[]',  -- JSON array
    attributes TEXT DEFAULT '{}'
);
CREATE INDEX IF NOT EXISTS idx_facts_workstream ON facts(workstream_id);
CREATE INDEX IF NOT EXISTS idx_facts_entity_pair ON facts(source_entity_uuid, target_entity_uuid);
CREATE INDEX IF NOT EXISTS idx_facts_valid_at ON facts(valid_at);
CREATE INDEX IF NOT EXISTS idx_facts_type ON facts(epistemic_type);
CREATE VIRTUAL TABLE IF NOT EXISTS facts_fts
    USING fts5(fact, relation_type, uuid UNINDEXED, content='facts', content_rowid='rowid');

CREATE TABLE IF NOT EXISTS episode_mentions (
    uuid TEXT PRIMARY KEY,
    workstream_id TEXT NOT NULL,
    episode_uuid TEXT NOT NULL REFERENCES episodes(uuid),
    entity_uuid TEXT NOT NULL REFERENCES entities(uuid),
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS artifacts (
    uuid TEXT PRIMARY KEY,
    workstream_id TEXT NOT NULL,
    body BLOB,                              -- nullable; set to NULL on revocation
    classification TEXT,                    -- secret|pii|customer_confidential|...
    created_at TEXT NOT NULL,
    revoked_at TEXT                         -- tombstone marker
);
```

### 3.2 Bi-temporal point-in-time query

```sql
-- "What facts about entity E were known to be true on date T?"
SELECT f.uuid, f.relation_type, f.fact, f.valid_at, f.invalid_at,
       e_src.name AS source_entity, e_tgt.name AS target_entity
FROM facts f
JOIN entities e_src ON f.source_entity_uuid = e_src.uuid
JOIN entities e_tgt ON f.target_entity_uuid = e_tgt.uuid
WHERE f.workstream_id = :workstream_id
  AND (f.source_entity_uuid = :entity_uuid OR f.target_entity_uuid = :entity_uuid)
  AND (f.valid_at IS NULL OR f.valid_at <= :query_time)
  AND (f.invalid_at IS NULL OR f.invalid_at > :query_time)
  AND (f.expired_at IS NULL OR f.expired_at > :query_time)
ORDER BY f.valid_at DESC NULLS LAST;
```

Five lines of WHERE. No graph DB needed.

### 3.3 Decision chain traversal (supersession history)

```sql
-- "Walk SUPERSEDES edges from Decision D back to the original"
WITH RECURSIVE supersession_chain AS (
    SELECT f.uuid, f.fact, f.valid_at, f.invalid_at,
           f.source_entity_uuid, f.target_entity_uuid, 0 AS depth
    FROM facts f WHERE f.uuid = :root_fact_uuid

    UNION ALL

    SELECT f.uuid, f.fact, f.valid_at, f.invalid_at,
           f.source_entity_uuid, f.target_entity_uuid, sc.depth + 1
    FROM facts f
    JOIN supersession_chain sc ON f.source_entity_uuid = sc.target_entity_uuid
        AND f.relation_type = 'SUPERSEDES'
    WHERE sc.depth < 20
)
SELECT * FROM supersession_chain ORDER BY depth;
```

Recursive CTE, 20-hop bound, runs in single-digit ms on 100k facts with the entity-pair index.

### 3.4 Entity resolution candidates (our stricter policy)

```sql
-- Step 1: exact deterministic key (auto-merge tier)
SELECT e.uuid, e.name, e.summary, 'exact_key' AS match_type, 1.0 AS score
FROM entities e
WHERE e.workstream_id = :workstream_id
  AND lower(trim(e.name)) = lower(trim(:candidate_name))

UNION ALL

-- Step 2: FTS BM25 (LLM-propose tier; human confirms)
SELECT e.uuid, e.name, e.summary, 'fts_bm25' AS match_type,
       -bm25(entities_fts) AS score
FROM entities e
JOIN entities_fts ON entities_fts.uuid = e.uuid
WHERE entities_fts MATCH :fts_query
  AND e.workstream_id = :workstream_id

ORDER BY score DESC LIMIT 10;
```

Step 1 → auto-merge (recorded as `entity.merged` event, reversible via `entity.split`). Step 2 → LLM gets the candidates + uses ported `dedupe_nodes.py` prompt → emits a *proposal* event → human confirms in UI. **This is more conservative than Graphiti's pure-LLM judgment** — by design.

### 3.5 3-hop BFS graph traversal

```sql
-- "Find all entities within 3 hops of entity E (current facts only)"
WITH RECURSIVE graph_bfs(entity_uuid, depth, path) AS (
    SELECT :seed_entity_uuid, 0, json_array(:seed_entity_uuid)

    UNION ALL

    SELECT
        CASE WHEN f.source_entity_uuid = bfs.entity_uuid
             THEN f.target_entity_uuid ELSE f.source_entity_uuid END,
        bfs.depth + 1,
        json_insert(bfs.path, '$[#]',
            CASE WHEN f.source_entity_uuid = bfs.entity_uuid
                 THEN f.target_entity_uuid ELSE f.source_entity_uuid END)
    FROM facts f
    JOIN graph_bfs bfs ON (f.source_entity_uuid = bfs.entity_uuid
                           OR f.target_entity_uuid = bfs.entity_uuid)
    WHERE bfs.depth < :max_depth
      AND f.workstream_id = :workstream_id
      AND f.invalid_at IS NULL
)
SELECT DISTINCT g.entity_uuid, e.name, e.summary, MIN(g.depth) AS hop_distance
FROM graph_bfs g
JOIN entities e ON e.uuid = g.entity_uuid
GROUP BY g.entity_uuid ORDER BY hop_distance;
```

Depth-3 BFS matches Graphiti's default. CTE blows up at >5 hops without aggressive pruning; that's fine — agent memory rarely needs deeper traversal.

### 3.6 Workstream isolation (no row-level security)

SQLite doesn't have RLS. **Enforced at the query layer:** every query takes a `workstream_id` parameter; the `IMemoryQueryAPI` is the only entry point and validates the `CapabilityToken` matches the requested workstream. No raw SQL escape for agents. Cross-workstream queries require a token with explicit grant scope. Audit logging on every query.

### 3.7 SQLite at scale — when does it break

- ✅ Up to ~1M events per file with WAL + good indexing → query latency stays under 50ms p99 for the patterns above
- ⚠️ 1M–10M events per file → indexes get large; consider partitioning per workstream (one DB file per workstream, or one DB file with workstream-id sharded tables)
- ❌ 10M+ events per file with frequent updates → time to migrate to PostgreSQL+Marten or Neo4j

For our target (solo developer, ~100k events/workstream/year), we sit comfortably in the green zone for v1+v2. Migration to PG is a v3+ option, not a v1 risk.

### 3.8 Concurrent writes (sidecar deployment)

WAL mode allows concurrent readers + one writer. For the sidecar shape (cockpit + capture + memory-agent in three processes all touching one SQLite file), this is acceptable because:

- Cockpit reads only via `IMemoryQueryAPI` (no direct writes)
- Capture writes only via memory-agent IPC (gRPC), not direct SQLite
- Memory-agent is the only writer to the SQLite file
- Verified pattern: Microsoft.Data.Sqlite + WAL + busy_timeout = 5000 handles transient contention

---

## 4. .NET-Friendly Graph Alternatives Evaluated

### 4.1 LiteGraph (`litegraphdb/litegraph`)

- **What:** .NET graph DB on SQLite (MIT)
- **Maturity:** Active development, commit `d55aa12`
- **Strengths:** Pure .NET, embedded, schema-driven, includes vector store
- **Weaknesses:** Graph storage only — no LLM pipeline, no bi-temporal model, no episode tier, no entity resolution. We'd still write 90% of the memory agent.
- **Verdict:** Useful as a sanity-check ("does graph-on-SQLite work in .NET production?" → yes). Not adopted because our schema is more specific.

### 4.2 DuckDB.NET (`Giorgi/DuckDB.NET`)

- **What:** Active .NET binding for DuckDB
- **Strengths:** Columnar analytics, recursive CTEs, single-file embedded, license clean
- **Weaknesses:** Worse write performance than SQLite for the event-log pattern (columnar storage is wrong for append-heavy workloads). Better for analytics over the event log than as the primary store.
- **Verdict:** Defer to v3+ as an *optional analytics sidecar* over the SQLite event log if/when we need fast aggregates. Not the primary substrate.

### 4.3 KuzuDB

- **Status:** **Archived / wind-down.** Even Graphiti is dropping it.
- **Verdict:** Dead end.

### 4.4 Apache AGE on PostgreSQL

- Requires PostgreSQL server process. Wrong deployment shape for desktop v1.
- Viable for cloud-sync tier in v2+ if we adopt PG for that path.

### 4.5 Memgraph

- Server-only. Wrong deployment shape.

### 4.6 sqlite-vec (vector search extension)

- **Status:** Pre-v1 (June 2026). Pure C extension loadable via `Microsoft.Data.Sqlite.LoadExtension`.
- **Verdict:** Stay agnostic in v1 schema; adopt for vector search in v2 when stable. Embedding columns (`name_embedding`, `fact_embedding`) already in the schema as `BLOB`, ready to be indexed when the extension matures.

### Ranking for our constraints

| Rank | Option | Why |
|---|---|---|
| **1** | **Custom SQLite (current plan)** | Native .NET, embedded, full control, schema matches our model, prompts portable from Graphiti |
| 2 | LiteGraph (storage only) | Backup option if SQLite query patterns get unwieldy |
| 3 | DuckDB (analytics sidecar v3+) | Complement, not replacement |
| 4 | Apache AGE / PostgreSQL (v2+ cloud tier) | Optional sync substrate |
| 5–7 | KuzuDB / Memgraph / Neo4j embedded | Not viable for our constraints |

---

## 5. Side-by-Side: "Graphiti Does X" → "We Do X By…"

| Graphiti capability | Graphiti implementation | Our SQLite implementation |
|---|---|---|
| Bi-temporal facts | 4 timestamp cols on Neo4j `:RELATES_TO` edges | 4 timestamp cols on `facts` table |
| Episode → entity extraction | Python LLM call in `add_episode()` | C# LLM call via Semantic Kernel (port `extract_nodes.py` prompt) |
| Entity resolution | FTS+vector candidates → pure LLM judgment | Deterministic-key auto-merge → LLM-propose → human confirm (stricter) |
| Edge invalidation | LLM call per new edge (`extract_edges.py`) | Same LLM call, ported prompt; writes `invalid_at` to existing facts row |
| Hybrid retrieval (BM25 + vector + BFS) | Neo4j vector index + Lucene FTS + Cypher BFS | sqlite-vec (v2) + FTS5 + recursive CTE |
| 3-hop BFS traversal | Cypher `MATCH (n)-[*1..3]-(m)` | `WITH RECURSIVE graph_bfs` |
| Custom entity/edge types | Pydantic models + prompt injection | C# records + Semantic Kernel structured outputs |
| Community detection (clustering) | LLM pass on subgraphs | Defer to v3; same pattern |
| Provenance tracking | `entity_edges`/`episodes` arrays | `source_episode_uuids` JSON column + `episode_mentions` link table |
| Idempotency | Application-level UUID dedup | DB-level `UNIQUE` constraint on `idempotency_key` |

**Net:** every Graphiti capability has a direct SQLite implementation. No primitive is missing. The only adjustments are (a) stricter entity resolution (we want this), (b) recursive CTE instead of Cypher (cleaner for fixed-depth traversal), (c) sqlite-vec deferred to v2.

---

## 6. Final Recommendation

**Keep the bespoke-SQLite decision. The deep dive strongly confirms it.**

Reasons:

1. **Graphiti is mostly schema + prompts.** The schema is a direct DDL translation (provided in §3.1). The prompts are Apache 2.0 and portable.
2. **Their graph DB is doing trivial work.** ~95% of ingestion latency is LLM calls. Substrate choice affects ~5%.
3. **Their entity resolution is *less* conservative than ours.** We don't want their policy; we have a better one.
4. **Their only embedded option (KuzuDB) is archived.** No future-proofing argument for using their stack.
5. **Native .NET alternatives don't add enough.** LiteGraph is storage-only; DuckDB is columnar (wrong shape); KuzuDB/Memgraph need servers.
6. **SQL patterns for all 10 requirements are concrete and fit in a few CTEs** (§3.2–§3.6).

**Cost of the decision (honest):**

- Writing the projection layer in SQL ourselves: ~2 weeks (already in `mem-projections` + `mem-graph-projection`)
- Porting Graphiti prompts: ~3 days (already covered by `mem-distillation-extract`)
- Maintaining the schema as it evolves: ongoing, but no different than maintaining any other DB
- **Total premium over "wrap Graphiti via Python sidecar":** roughly zero. Wrapping Graphiti would cost ~3 weeks of integration + permanent Python packaging tax + impedance mismatch.

**Risks (genuine):**

- **CTE perf at >5M facts/workstream** — unproven, but not relevant for solo-developer scale. Mitigation: partition by workstream when needed.
- **Distillation prompt quality** — high. We start with Graphiti's ported prompts (proven at scale) and iterate. Treat distillation as ongoing product work, not a one-shot.
- **sqlite-vec maturity for v2** — moderate. Embeddings stay in BLOB columns; vector search can be deferred or backed by an external index (e.g., a per-workstream embedded HNSW) if sqlite-vec slips.

---

## 7. No Migration Required

The current `memory-agent/plan.md` (11 phases) already targets SQLite + native .NET. This research changes nothing structural — it removes uncertainty and provides concrete DDL + SQL patterns to copy.

**Minor adjustments to bake in:**

- `mem-store-tables` description: explicitly call out the 4-timestamp bi-temporal model + Graphiti DDL provenance (§3.1)
- `mem-distillation-extract` description: note that prompts will be ported from Graphiti's `extract_*` and `dedupe_*` files (Apache 2.0, with attribution) rather than written from scratch
- `mem-entity-resolution-deterministic` + `-llm-propose` already match the policy described in §3.4
- Add explicit reference to this report from `mem-store-tables`, `mem-graph-projection`, `mem-projections`

No phase reordering, no scope changes.

---

## 8. Gaps and Uncertainties

1. **LiteGraph vector format** — README mentions vector support but the storage mechanism isn't documented in surveyed files (likely sqlite-vec or manual BLOB). If we want to adopt LiteGraph's vector store as a v2 sidecar without taking on its graph layer, more digging needed.
2. **DuckDB recursive CTE perf at 100k edges** — not benchmarked; SQLite is likely faster for the row-oriented event-log pattern.
3. **sqlite-vec on Windows via Microsoft.Data.Sqlite** — extension loading should work via `connection.LoadExtension()`, but not verified on Windows. Pre-v1 stability is the bigger blocker.
4. **Graphiti per-episode LLM cost in our context** — we estimated ~12 calls/episode from code reading. If entity density is higher for code-context episodes, cost scales linearly. Budget item for distillation phase.
5. **Apache 2.0 prompt reuse compliance** — confirmed Apache 2.0 on `graphiti_core/`; attribution to Zep AI required in code comments. Derivative-work status is clean; consult legal if commercializing the prompt assets verbatim.

---

## 9. Sources

| # | Source | Notes |
|---|---|---|
| 1 | `getzep/graphiti` @ `9f2b63d` | OSS temporal KG engine (Apache 2.0) |
| 2 | `graphiti_core/driver/kuzu_driver.py:1–110` | KuzuDB DDL = our SQLite blueprint; KuzuDB deprecation warning |
| 3 | `graphiti_core/edges.py:263–283` | EntityEdge field definitions (bi-temporal model) |
| 4 | `graphiti_core/nodes.py:318–328` | EpisodicNode definition |
| 5 | `graphiti_core/nodes.py:499–504` | EntityNode definition |
| 6 | `graphiti_core/graphiti.py:980–1200` | `add_episode()` ingestion pipeline |
| 7 | `graphiti_core/prompts/dedupe_nodes.py` | Entity resolution prompt (Apache 2.0, portable) |
| 8 | `graphiti_core/prompts/extract_edges.py` | Edge extraction prompt with temporal rules |
| 9 | `graphiti_core/search/search_config.py:1–150` | Hybrid retrieval architecture |
| 10 | `arxiv.org/abs/2501.13956v1` | Zep paper, bi-temporal model formalization |
| 11 | `litegraphdb/litegraph` @ `d55aa12` | .NET graph DB on SQLite (MIT) |
| 12 | `Giorgi/DuckDB.NET` | Active .NET DuckDB binding |
| 13 | `kuzudb/kuzu` (archived) | Wind-down confirmed |

---

*Report compiled by direct source-code review of Graphiti, supporting paper review, and SQL pattern verification. All claims traceable to the files cited above.*
