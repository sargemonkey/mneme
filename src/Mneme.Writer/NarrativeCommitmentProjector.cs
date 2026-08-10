using System.Globalization;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Projections;

namespace Mneme.Writer;

/// <summary>
/// Maintains the setup→payoff ledger (<c>projection_narrative_commitments</c>)
/// from <see cref="NarrativeCommitmentPayload"/> events (ADR-0005 / Phase 15.B).
/// One row per commitment event; the "unpaid promise" query
/// (<see cref="IWriterMemory.GetOpenCommitmentsAsync"/>) is a setup with no
/// payoff sharing its <c>commitment_id</c>.
/// </summary>
/// <remarks>
/// Matches <see cref="EpistemicCategory.Goal"/> (the category commitments ride
/// under) and self-guards on payload type, so it ignores real
/// <see cref="GoalPayload"/> events — those stay with the built-in
/// <c>GoalsProjector</c>. It owns its table, so <see cref="Rebuild"/> wipes and
/// re-derives it (unlike the shared-canon projector, which must not).
/// Idempotent per event via <c>ON CONFLICT(workstream_id, event_id)</c>.
/// </remarks>
public sealed class NarrativeCommitmentProjector : IProjector
{
    /// <inheritdoc/>
    public string Name => "writer-narrative-commitments";

    /// <inheritdoc/>
    public EpistemicCategory Category => EpistemicCategory.Goal;

    /// <inheritdoc/>
    public void Apply(SqliteConnection c, SqliteTransaction tx, EventEnvelope e)
    {
        if (e.Payload is not NarrativeCommitmentPayload p) return;

        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO projection_narrative_commitments(workstream_id, event_id, commitment_id,
                role, kind, description, scene_path, quote, story_time, reveal_time, revoked_at)
            VALUES ($ws, $eid, $cid, $role, $kind, $desc, $scene, $quote, $story, $reveal, $rev)
            ON CONFLICT(workstream_id, event_id) DO UPDATE SET
                commitment_id = excluded.commitment_id,
                role          = excluded.role,
                kind          = excluded.kind,
                description   = excluded.description,
                scene_path    = excluded.scene_path,
                quote         = excluded.quote,
                story_time    = excluded.story_time,
                reveal_time   = excluded.reveal_time,
                revoked_at    = excluded.revoked_at;
            """;
        cmd.Parameters.AddWithValue("$ws", e.WorkstreamId.Value);
        cmd.Parameters.AddWithValue("$eid", e.EventId.Value);
        cmd.Parameters.AddWithValue("$cid", p.CommitmentId);
        cmd.Parameters.AddWithValue("$role", (int)p.Role);
        cmd.Parameters.AddWithValue("$kind", p.Kind);
        cmd.Parameters.AddWithValue("$desc", p.Description);
        cmd.Parameters.AddWithValue("$scene", (object?)p.ScenePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$quote", (object?)p.Quote ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$story", Fmt(e.ValidAt));
        cmd.Parameters.AddWithValue("$reveal", Fmt(e.CreatedAt));
        cmd.Parameters.AddWithValue("$rev", e.RevokedAt.HasValue ? Fmt(e.RevokedAt.Value) : (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public int Rebuild(SqliteConnection c, SqliteTransaction tx)
    {
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM projection_narrative_commitments;";
            del.ExecuteNonQuery();
        }
        var n = 0;
        foreach (var e in EventEnvelopeReader.ReadAll(c, tx, Category))
        {
            if (e.Payload is not NarrativeCommitmentPayload) continue;
            Apply(c, tx, e);
            n++;
        }
        return n;
    }

    // Same ISO-8601 round-trip format the base projectors use for timestamp
    // columns (base FactsProjector.Fmt is internal to Mneme, so mirror it here).
    private static string Fmt(DateTimeOffset t) =>
        t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
