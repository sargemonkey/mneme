using Microsoft.Data.Sqlite;

namespace Mneme.Storage;

/// <summary>
/// Owns the Phase 1 SQLite schema for the Mneme event log. The schema is
/// append-only: <c>memory_events</c> is the source of truth, and projections
/// (Phase 3) are derived. There are no <c>UPDATE</c> or <c>DELETE</c>
/// statements anywhere in the codebase that touch <c>memory_events</c> —
/// revocation is modelled by tombstoning the artifact body in
/// <c>memory_artifacts</c>, not by deleting the event row.
/// </summary>
/// <remarks>
/// <para>
/// Bi-temporal model: every fact-bearing row carries four timestamps —
/// <c>valid_at</c> / <c>invalid_at</c> (event time: when the claim is /
/// was true in the world) and <c>created_at</c> / <c>expired_at</c>
/// (transaction time: when Mneme knew about the claim and when it was
/// superseded). See <c>plans/research-zep-sqlite-deepdive.md §3.1</c>
/// for the underlying Graphiti DDL this translates.
/// </para>
/// <para>
/// Connection settings (WAL, foreign keys, busy timeout) are applied by
/// <see cref="SqliteConnectionFactory"/> on every connection open.
/// </para>
/// </remarks>
public static class SqliteSchema
{
    /// <summary>Current schema version. Bumped when the DDL changes incompatibly.</summary>
    public const int Version = 10;

    /// <summary>
    /// Idempotently create all Phase 1 tables, indexes, and the
    /// <c>schema_meta</c> bookkeeping row. Safe to call on every startup.
    /// </summary>
    public static void Initialize(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = Ddl;
            cmd.ExecuteNonQuery();
        }

        // Schema v2 migration: idempotently add the classification column
        // to existing memory_events tables. Safe to run on every startup —
        // SQLite ignores the ALTER if the column already exists (we catch
        // the duplicate-column error rather than feature-detect).
        TryAlter(connection, tx,
            "ALTER TABLE memory_events ADD COLUMN classification INTEGER NOT NULL DEFAULT 0;");

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO schema_meta(key, value)
                VALUES ('version', $version)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$version", Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void TryAlter(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Column already present — migration was applied in a previous run.
        }
    }

    // memory_events is the append-only event log — source of truth.
    // memory_artifacts holds the body blobs separately so a revocation
    // tombstone can null-out the body without touching event metadata.
    // memory_edges is the placeholder for the entity/fact graph
    // populated in Phase 3/4; created now so the schema is stable.
    // distillation_queue is a tiny outbox of event ids whose async
    // distillation has not yet been processed (populated in the sync
    // ingest stage; drained by the worker in Phase 5).
    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS schema_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS memory_artifacts (
            artifact_id     TEXT NOT NULL PRIMARY KEY,
            workstream_id   TEXT NOT NULL,
            event_id        TEXT NOT NULL,
            created_at      TEXT NOT NULL,
            body            BLOB,
            body_hash       TEXT,
            redacted        INTEGER NOT NULL DEFAULT 0,
            revoked_at      TEXT,
            revocation_reason TEXT
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS idx_memory_artifacts_event
            ON memory_artifacts(event_id);

        CREATE TABLE IF NOT EXISTS memory_events (
            event_id        TEXT NOT NULL PRIMARY KEY,
            workstream_id   TEXT NOT NULL,
            event_channel   INTEGER NOT NULL,
            category        INTEGER NOT NULL,
            schema_version  INTEGER NOT NULL,
            valid_at        TEXT NOT NULL,
            invalid_at      TEXT,
            created_at      TEXT NOT NULL,
            expired_at      TEXT,
            payload_json    TEXT NOT NULL,
            provenance_json TEXT NOT NULL,
            content_shape   INTEGER NOT NULL,
            classification  INTEGER NOT NULL DEFAULT 0,
            artifact_id     TEXT,
            FOREIGN KEY (artifact_id) REFERENCES memory_artifacts(artifact_id)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS idx_memory_events_workstream
            ON memory_events(workstream_id, created_at);
        CREATE INDEX IF NOT EXISTS idx_memory_events_workstream_channel
            ON memory_events(workstream_id, event_channel, created_at);
        CREATE INDEX IF NOT EXISTS idx_memory_events_category
            ON memory_events(workstream_id, category, valid_at);
        CREATE INDEX IF NOT EXISTS idx_memory_events_valid_at
            ON memory_events(valid_at);

        CREATE TABLE IF NOT EXISTS memory_edges (
            edge_id         TEXT NOT NULL PRIMARY KEY,
            workstream_id   TEXT NOT NULL,
            source_id       TEXT NOT NULL,
            target_id       TEXT NOT NULL,
            relation        TEXT NOT NULL,
            valid_at        TEXT NOT NULL,
            invalid_at      TEXT,
            created_at      TEXT NOT NULL,
            expired_at      TEXT,
            evidence_event_id TEXT,
            FOREIGN KEY (evidence_event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS idx_memory_edges_workstream
            ON memory_edges(workstream_id);
        CREATE INDEX IF NOT EXISTS idx_memory_edges_source
            ON memory_edges(workstream_id, source_id);
        CREATE INDEX IF NOT EXISTS idx_memory_edges_target
            ON memory_edges(workstream_id, target_id);

        CREATE TABLE IF NOT EXISTS distillation_queue (
            event_id     TEXT NOT NULL PRIMARY KEY,
            workstream_id TEXT NOT NULL,
            enqueued_at  TEXT NOT NULL,
            attempts     INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS idx_distillation_queue_workstream
            ON distillation_queue(workstream_id, enqueued_at);

        -- Phase 2 — sidecar revocation table. Append-only itself:
        -- PRIMARY KEY on event_id enforces one revocation per event.
        -- memory_events stays untouched so the source-of-truth invariant
        -- still holds; the body in memory_artifacts is nulled by the
        -- revocation service in the same transaction.
        CREATE TABLE IF NOT EXISTS memory_revocations (
            event_id        TEXT NOT NULL PRIMARY KEY,
            workstream_id   TEXT NOT NULL,
            revoked_at      TEXT NOT NULL,
            revoked_by      TEXT NOT NULL,
            reason          TEXT NOT NULL,
            FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS idx_memory_revocations_workstream
            ON memory_revocations(workstream_id, revoked_at);

        -- Phase 3 — projections. Read-models derived from memory_events.
        -- Rebuildable from scratch by replaying the log; updated
        -- incrementally by the projector worker. Each projection has its
        -- own (workstream_id, event_id) primary key so a single event
        -- produces at most one row per projection.

        CREATE TABLE IF NOT EXISTS projection_facts (
            workstream_id   TEXT NOT NULL,
            event_id        TEXT NOT NULL,
            statement       TEXT NOT NULL,
            supporting_events_json TEXT NOT NULL,
            classification  INTEGER NOT NULL DEFAULT 0,
            valid_at        TEXT NOT NULL,
            invalid_at      TEXT,
            created_at      TEXT NOT NULL,
            expired_at      TEXT,
            revoked_at      TEXT,
            PRIMARY KEY (workstream_id, event_id),
            FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_projection_facts_valid
            ON projection_facts(workstream_id, valid_at);

        CREATE TABLE IF NOT EXISTS projection_decisions (
            workstream_id   TEXT NOT NULL,
            event_id        TEXT NOT NULL,
            statement       TEXT NOT NULL,
            rationale       TEXT NOT NULL,
            approver        TEXT NOT NULL,
            supporting_events_json TEXT NOT NULL,
            classification  INTEGER NOT NULL DEFAULT 0,
            valid_at        TEXT NOT NULL,
            invalid_at      TEXT,
            created_at      TEXT NOT NULL,
            expired_at      TEXT,
            revoked_at      TEXT,
            PRIMARY KEY (workstream_id, event_id),
            FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_projection_decisions_valid
            ON projection_decisions(workstream_id, valid_at);

        CREATE TABLE IF NOT EXISTS projection_goals (
            workstream_id   TEXT NOT NULL,
            event_id        TEXT NOT NULL,
            statement       TEXT NOT NULL,
            state           INTEGER NOT NULL,
            classification  INTEGER NOT NULL DEFAULT 0,
            valid_at        TEXT NOT NULL,
            invalid_at      TEXT,
            created_at      TEXT NOT NULL,
            expired_at      TEXT,
            revoked_at      TEXT,
            PRIMARY KEY (workstream_id, event_id),
            FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_projection_goals_state
            ON projection_goals(workstream_id, state);

        CREATE TABLE IF NOT EXISTS projection_hypotheses (
            workstream_id   TEXT NOT NULL,
            event_id        TEXT NOT NULL,
            statement       TEXT NOT NULL,
            state           INTEGER NOT NULL,
            classification  INTEGER NOT NULL DEFAULT 0,
            valid_at        TEXT NOT NULL,
            invalid_at      TEXT,
            created_at      TEXT NOT NULL,
            expired_at      TEXT,
            revoked_at      TEXT,
            PRIMARY KEY (workstream_id, event_id),
            FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_projection_hypotheses_state
            ON projection_hypotheses(workstream_id, state);

        -- Per-projection processing log. Lets a single projection be
        -- replayed in isolation without rebuilding everything else.
        -- Pattern from Cognee — see research-design-lessons.md §3.3.
        CREATE TABLE IF NOT EXISTS event_processing_log (
            event_id        TEXT NOT NULL,
            projection_name TEXT NOT NULL,
            status          INTEGER NOT NULL,
            processed_at    TEXT NOT NULL,
            error           TEXT,
            PRIMARY KEY (event_id, projection_name),
            FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_event_processing_log_projection
            ON event_processing_log(projection_name, status, processed_at);

        -- FTS5 text index over redacted free-text content. Workstream
        -- and category live in non-indexed UNINDEXED columns so they
        -- can be used as MATCH-side equality filters cheaply.
        CREATE VIRTUAL TABLE IF NOT EXISTS event_text_index USING fts5(
            content,
            workstream_id UNINDEXED,
            event_id      UNINDEXED,
            category      UNINDEXED,
            created_at    UNINDEXED,
            tokenize      = 'unicode61 remove_diacritics 2'
        );

        -- Phase 7.5 — HITL curation. Curation events are append-only
        -- (a revert is itself a new curation event whose target_event_id
        -- points at the curation being reversed). curated_target is the
        -- epistemic event being mutated; reverted_by is set when this
        -- curation is later undone.
        CREATE TABLE IF NOT EXISTS curation_events (
            event_id          TEXT NOT NULL PRIMARY KEY,
            target_event_id   TEXT NOT NULL,
            workstream_id     TEXT NOT NULL,
            curation_type     INTEGER NOT NULL,
            curator           TEXT NOT NULL,
            rationale         TEXT NOT NULL,
            occurred_at       TEXT NOT NULL,
            pre_state_hash    TEXT NOT NULL,
            payload_json      TEXT NOT NULL,
            reverted_by       TEXT
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_curation_events_target
            ON curation_events(target_event_id, occurred_at);
        CREATE INDEX IF NOT EXISTS idx_curation_events_workstream
            ON curation_events(workstream_id, occurred_at);
        CREATE INDEX IF NOT EXISTS idx_curation_events_curator
            ON curation_events(curator, occurred_at);

        -- Phase 5 — distillation cache. One row per workstream holds the
        -- latest synthesized ContextBundle. The cache is invalidated by
        -- comparing events_covered_through against the newest event id
        -- in memory_events; staleness is computed at read time so we
        -- never serve a bundle that doesn't honor the most recent
        -- curation.
        CREATE TABLE IF NOT EXISTS distillation_cache (
            workstream_id           TEXT NOT NULL PRIMARY KEY,
            bundle_json             TEXT NOT NULL,
            events_covered_through  TEXT NOT NULL,
            generated_at            TEXT NOT NULL,
            distiller               TEXT NOT NULL,
            token_count             INTEGER NOT NULL
        ) WITHOUT ROWID;

        -- Phase 6 — entity resolution. entity_index holds the
        -- canonical entities; entity_mentions binds each mention back
        -- to its source event; entity_merges records confirmed merges;
        -- entity_merge_proposals holds Tier 3 LLM proposals pending
        -- human confirmation.

        CREATE TABLE IF NOT EXISTS entity_index (
            entity_id       TEXT NOT NULL PRIMARY KEY,
            workstream_id   TEXT NOT NULL,
            kind            INTEGER NOT NULL,
            canonical_key   TEXT NOT NULL,
            display_name    TEXT NOT NULL,
            first_seen_at   TEXT NOT NULL,
            last_seen_at    TEXT NOT NULL,
            mention_count   INTEGER NOT NULL DEFAULT 0
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_entity_index_workstream
            ON entity_index(workstream_id, kind);
        CREATE INDEX IF NOT EXISTS idx_entity_index_canonical
            ON entity_index(workstream_id, kind, canonical_key);

        CREATE TABLE IF NOT EXISTS entity_mentions (
            entity_id           TEXT NOT NULL,
            event_id            TEXT NOT NULL,
            asserted_display    TEXT NOT NULL,
            at                  TEXT NOT NULL,
            PRIMARY KEY (entity_id, event_id),
            FOREIGN KEY (entity_id) REFERENCES entity_index(entity_id),
            FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_entity_mentions_event
            ON entity_mentions(event_id);

        CREATE TABLE IF NOT EXISTS entity_merges (
            winner_id       TEXT NOT NULL,
            loser_id        TEXT NOT NULL,
            confirmed_by    TEXT NOT NULL,
            confirmed_at    TEXT NOT NULL,
            rationale       TEXT NOT NULL,
            PRIMARY KEY (winner_id, loser_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_entity_merges_loser
            ON entity_merges(loser_id);

        CREATE TABLE IF NOT EXISTS entity_merge_proposals (
            proposal_id         TEXT NOT NULL PRIMARY KEY,
            workstream_id       TEXT NOT NULL,
            winner_id           TEXT NOT NULL,
            loser_ids_json      TEXT NOT NULL,
            confidence          REAL NOT NULL,
            rationale           TEXT NOT NULL,
            proposed_by         TEXT NOT NULL,
            proposed_at         TEXT NOT NULL,
            winner_state_hash   TEXT NOT NULL,
            status              INTEGER NOT NULL DEFAULT 0,   -- 0=pending, 1=confirmed, 2=rejected
            resolved_by         TEXT,
            resolved_at         TEXT
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_entity_merge_proposals_pending
            ON entity_merge_proposals(workstream_id, status, proposed_at);

        -- Phase 7 — Outcome closure. decision_chains projects the
        -- Decision → Action → Outcome cause chain for retrieval and
        -- learning. Polarity is denormalised from the outcome payload
        -- so the chain is queryable without a payload re-parse.
        -- Foreign keys are intentionally NOT declared on
        -- decision_event_id / action_event_id / outcome_event_id —
        -- events can arrive in any order (Action before its Decision is
        -- a normal case), so the chain rows must be allowed to reference
        -- event ids that have not yet landed.
        CREATE TABLE IF NOT EXISTS decision_chains (
            workstream_id       TEXT NOT NULL,
            decision_event_id   TEXT NOT NULL,
            action_event_id     TEXT,
            outcome_event_id    TEXT,
            outcome_polarity    INTEGER,
            decision_at         TEXT NOT NULL,
            outcome_at          TEXT,
            closed              INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (workstream_id, decision_event_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_decision_chains_open
            ON decision_chains(workstream_id, closed, decision_at);
        CREATE INDEX IF NOT EXISTS idx_decision_chains_action
            ON decision_chains(action_event_id);

        -- Per-event feedback weight learned from outcomes. Default 1.0;
        -- nudged by alpha * (polarity_score - 0.5) on each outcome that
        -- closes a decision the event supported (Cognee improve() pattern,
        -- adapted to Mneme — research-design-lessons.md §3.3).
        CREATE TABLE IF NOT EXISTS event_feedback (
            event_id        TEXT NOT NULL PRIMARY KEY,
            feedback_weight REAL NOT NULL DEFAULT 1.0,
            updated_at      TEXT NOT NULL,
            update_count    INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;

        -- v8: per-session distillation watermark. One row per session;
        -- updated atomically (same SqliteTransaction) with the events the
        -- distillation produced so a crash mid-call never leaves the
        -- watermark ahead of the events.
        CREATE TABLE IF NOT EXISTS distillation_watermarks (
            session_id            TEXT NOT NULL PRIMARY KEY,
            last_entry_id         TEXT NOT NULL,
            distilled_at          TEXT NOT NULL,
            distiller_version     TEXT NOT NULL
        ) WITHOUT ROWID;

        -- v8: idempotency guard for DistillSessionAsync. The agent records
        -- (session, from, to) on every successful distillation; re-calling
        -- with the same triple short-circuits to a no-op result. Indexed by
        -- session so the lookup is cheap.
        CREATE TABLE IF NOT EXISTS distillation_runs (
            session_id      TEXT NOT NULL,
            from_entry_id   TEXT NOT NULL,
            to_entry_id     TEXT NOT NULL,
            distilled_at    TEXT NOT NULL,
            events_count    INTEGER NOT NULL,
            PRIMARY KEY (session_id, from_entry_id, to_entry_id)
        ) WITHOUT ROWID;

        -- v9: per-event embedding vectors for semantic retrieval. Stored as
        -- raw float32 little-endian BLOBs. Brute-force cosine KNN over the
        -- workstream's vectors serves semantic search at v1 scale (a few
        -- thousand events) in sub-millisecond time — sqlite-vec is only
        -- needed once corpora reach the millions (Phase 11). provider_id +
        -- dim are stored so a model/dimensionality change is detectable and
        -- triggers a re-embed rather than corrupting cosine math.
        CREATE TABLE IF NOT EXISTS event_embeddings (
            event_id        TEXT NOT NULL PRIMARY KEY,
            workstream_id   TEXT NOT NULL,
            provider_id     TEXT NOT NULL,
            dim             INTEGER NOT NULL,
            vector          BLOB NOT NULL,
            created_at      TEXT NOT NULL,
            FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_event_embeddings_ws
            ON event_embeddings(workstream_id, provider_id);

        -- Phase 12 — subject-attributed fact triples. Projected from
        -- FactPayload.Triples so retrieval can scope to facts ABOUT an
        -- entity (subject_entity_id, resolved via the entity resolver)
        -- rather than facts whose text merely mentions it. Derived +
        -- rebuildable from memory_events; the full statement stays in
        -- projection_facts (triples are an attribution index, not a
        -- replacement). subject_entity_id is nullable — an unresolved
        -- subject still indexes on its normalized surface key.
        CREATE TABLE IF NOT EXISTS projection_fact_triples (
            workstream_id     TEXT NOT NULL,
            event_id          TEXT NOT NULL,
            ordinal           INTEGER NOT NULL,
            subject_text      TEXT NOT NULL,
            subject_key       TEXT NOT NULL,
            subject_entity_id TEXT,
            predicate         TEXT NOT NULL,
            object            TEXT NOT NULL,
            valid_at          TEXT NOT NULL,
            revoked_at        TEXT,
            PRIMARY KEY (workstream_id, event_id, ordinal),
            FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_fact_triples_subject_key
            ON projection_fact_triples(workstream_id, subject_key);
        CREATE INDEX IF NOT EXISTS idx_fact_triples_subject_entity
            ON projection_fact_triples(workstream_id, subject_entity_id);
        """;
}
