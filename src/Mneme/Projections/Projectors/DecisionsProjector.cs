using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;

namespace Mneme.Projections.Projectors;

/// <summary>Projects <see cref="DecisionPayload"/> events into <c>projection_decisions</c>.</summary>
public sealed class DecisionsProjector : IProjector
{
    /// <inheritdoc/>
    public string Name => "decisions";
    /// <inheritdoc/>
    public EpistemicCategory Category => EpistemicCategory.Decision;

    /// <inheritdoc/>
    public void Apply(SqliteConnection c, SqliteTransaction tx, EventEnvelope e)
    {
        if (e.Payload is not DecisionPayload p) return;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO projection_decisions(workstream_id, event_id, statement, rationale,
                approver, supporting_events_json, classification, valid_at, invalid_at,
                created_at, expired_at, revoked_at)
            VALUES ($ws, $eid, $statement, $rationale, $approver, $sup, $cls, $va, $ia, $ca, $ea, $rev)
            ON CONFLICT(workstream_id, event_id) DO UPDATE SET
                statement = excluded.statement,
                rationale = excluded.rationale,
                approver = excluded.approver,
                supporting_events_json = excluded.supporting_events_json,
                classification = excluded.classification,
                valid_at = excluded.valid_at,
                invalid_at = excluded.invalid_at,
                created_at = excluded.created_at,
                expired_at = excluded.expired_at,
                revoked_at = excluded.revoked_at;
            """;
        cmd.Parameters.AddWithValue("$ws", e.WorkstreamId.Value);
        cmd.Parameters.AddWithValue("$eid", e.EventId.Value);
        cmd.Parameters.AddWithValue("$statement", p.Statement);
        cmd.Parameters.AddWithValue("$rationale", p.Rationale);
        cmd.Parameters.AddWithValue("$approver", p.Approver.Value);
        cmd.Parameters.AddWithValue("$sup", JsonSerializer.Serialize(p.SupportingEvents.Select(x => x.Value)));
        cmd.Parameters.AddWithValue("$cls", (int)e.Classification);
        cmd.Parameters.AddWithValue("$va", FactsProjector.Fmt(e.ValidAt));
        cmd.Parameters.AddWithValue("$ia", e.InvalidAt.HasValue ? FactsProjector.Fmt(e.InvalidAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", FactsProjector.Fmt(e.CreatedAt));
        cmd.Parameters.AddWithValue("$ea", e.ExpiredAt.HasValue ? FactsProjector.Fmt(e.ExpiredAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$rev", e.RevokedAt.HasValue ? FactsProjector.Fmt(e.RevokedAt.Value) : (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public int Rebuild(SqliteConnection c, SqliteTransaction tx)
    {
        using (var del = c.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM projection_decisions;"; del.ExecuteNonQuery(); }
        var n = 0;
        foreach (var e in EventEnvelopeReader.ReadAll(c, tx, Category)) { Apply(c, tx, e); n++; }
        return n;
    }
}
