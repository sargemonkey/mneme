using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;

namespace Mneme.Projections.Projectors;

/// <summary>Projects <see cref="FactPayload"/> events into <c>projection_facts</c>.</summary>
public sealed class FactsProjector : IProjector
{
    /// <inheritdoc/>
    public string Name => "facts";
    /// <inheritdoc/>
    public EpistemicCategory Category => EpistemicCategory.Fact;

    /// <inheritdoc/>
    public void Apply(SqliteConnection c, SqliteTransaction tx, EventEnvelope e)
    {
        if (e.Payload is not FactPayload p) return;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO projection_facts(workstream_id, event_id, statement,
                supporting_events_json, classification, valid_at, invalid_at,
                created_at, expired_at, revoked_at)
            VALUES ($ws, $eid, $statement, $sup, $cls, $va, $ia, $ca, $ea, $rev)
            ON CONFLICT(workstream_id, event_id) DO UPDATE SET
                statement = excluded.statement,
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
        cmd.Parameters.AddWithValue("$sup", JsonSerializer.Serialize(p.SupportingEvents.Select(x => x.Value)));
        cmd.Parameters.AddWithValue("$cls", (int)e.Classification);
        cmd.Parameters.AddWithValue("$va", Fmt(e.ValidAt));
        cmd.Parameters.AddWithValue("$ia", e.InvalidAt.HasValue ? Fmt(e.InvalidAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", Fmt(e.CreatedAt));
        cmd.Parameters.AddWithValue("$ea", e.ExpiredAt.HasValue ? Fmt(e.ExpiredAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$rev", e.RevokedAt.HasValue ? Fmt(e.RevokedAt.Value) : (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public int Rebuild(SqliteConnection c, SqliteTransaction tx)
    {
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM projection_facts;";
            del.ExecuteNonQuery();
        }
        var n = 0;
        foreach (var e in EventEnvelopeReader.ReadAll(c, tx, Category))
        {
            Apply(c, tx, e);
            n++;
        }
        return n;
    }

    internal static string Fmt(DateTimeOffset t) =>
        t.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
