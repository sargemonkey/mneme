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
    public const int Version = 2;

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
        """;
}
