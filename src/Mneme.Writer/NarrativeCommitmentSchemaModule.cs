using Microsoft.Data.Sqlite;
using Mneme.Hosting.Profiles;

namespace Mneme.Writer;

/// <summary>
/// Schema for the writing profile's <b>own</b> projection table,
/// <c>projection_narrative_commitments</c> — the setup→payoff ledger (ADR-0005 /
/// Phase 15.B). Unlike the continuity slice (which reuses base Mneme's
/// <c>projection_fact_triples</c>), the commitment ledger is a genuinely new
/// projection, so the writer profile contributes this <see cref="ISchemaModule"/>.
/// </summary>
/// <remarks>
/// Idempotent (<c>CREATE TABLE/INDEX IF NOT EXISTS</c>) and namespaced under
/// <see cref="Name"/> in <c>schema_meta</c>. One row per commitment <em>event</em>
/// (keyed on <c>event_id</c> for idempotent upsert); a single
/// <c>commitment_id</c> has one setup row and zero-or-more payoff rows, so an
/// "unpaid promise" is a commitment_id with a setup and no payoff. Both
/// bi-temporal axes are stored (<c>story_time</c> = the event's <c>valid_at</c>,
/// <c>reveal_time</c> = its <c>created_at</c>) so later phases can ask "what is
/// unpaid as of chapter N".
/// </remarks>
public sealed class NarrativeCommitmentSchemaModule : ISchemaModule
{
    /// <inheritdoc/>
    public string Name => "writer-commitments";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public void ApplyDdl(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS projection_narrative_commitments (
                workstream_id  TEXT NOT NULL,
                event_id       TEXT NOT NULL,
                commitment_id  TEXT NOT NULL,
                role           INTEGER NOT NULL,   -- 0 = setup, 1 = payoff
                kind           TEXT NOT NULL,
                description    TEXT NOT NULL,
                scene_path     TEXT,
                quote          TEXT,
                story_time     TEXT NOT NULL,      -- valid_at: when it happens in-story
                reveal_time    TEXT NOT NULL,      -- created_at: when the draft/reader gets it
                revoked_at     TEXT,
                PRIMARY KEY (workstream_id, event_id),
                FOREIGN KEY (event_id) REFERENCES memory_events(event_id)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS idx_narrative_commitments_commitment
                ON projection_narrative_commitments(workstream_id, commitment_id, role);
            """;
        cmd.ExecuteNonQuery();
    }
}
