using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;

namespace Mneme.Projections.Projectors;

/// <summary>
/// Projects <see cref="SkillPayload"/> events into <c>projection_skills</c> —
/// Mneme's procedural-memory read model. Skills ride in the append-only log
/// under <see cref="EpistemicCategory.Evidence"/> (so the seven epistemic
/// categories stay locked, per ADR-0004 §Tension 2); this projector matches on
/// the Evidence category and then filters by payload <em>type</em>, so ordinary
/// <see cref="EvidencePayload"/> events are ignored.
/// </summary>
public sealed class SkillsProjector : IProjector
{
    /// <inheritdoc/>
    public string Name => "skills";
    /// <inheritdoc/>
    public EpistemicCategory Category => EpistemicCategory.Evidence;

    /// <inheritdoc/>
    public void Apply(SqliteConnection c, SqliteTransaction tx, EventEnvelope e)
    {
        if (e.Payload is not SkillPayload p) return; // ignore non-skill Evidence
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO projection_skills(workstream_id, event_id, name, procedure,
                trigger, supporting_events_json, classification, valid_at, created_at, revoked_at)
            VALUES ($ws, $eid, $name, $proc, $trig, $sup, $cls, $va, $ca, $rev)
            ON CONFLICT(workstream_id, event_id) DO UPDATE SET
                name = excluded.name,
                procedure = excluded.procedure,
                trigger = excluded.trigger,
                supporting_events_json = excluded.supporting_events_json,
                classification = excluded.classification,
                valid_at = excluded.valid_at,
                created_at = excluded.created_at,
                revoked_at = excluded.revoked_at;
            """;
        cmd.Parameters.AddWithValue("$ws", e.WorkstreamId.Value);
        cmd.Parameters.AddWithValue("$eid", e.EventId.Value);
        cmd.Parameters.AddWithValue("$name", p.Name);
        cmd.Parameters.AddWithValue("$proc", p.Procedure);
        cmd.Parameters.AddWithValue("$trig", (object?)p.Trigger ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sup", JsonSerializer.Serialize(p.SupportingEvents.Select(x => x.Value)));
        cmd.Parameters.AddWithValue("$cls", (int)e.Classification);
        cmd.Parameters.AddWithValue("$va", FactsProjector.Fmt(e.ValidAt));
        cmd.Parameters.AddWithValue("$ca", FactsProjector.Fmt(e.CreatedAt));
        cmd.Parameters.AddWithValue("$rev", e.RevokedAt.HasValue ? FactsProjector.Fmt(e.RevokedAt.Value) : (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public int Rebuild(SqliteConnection c, SqliteTransaction tx)
    {
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM projection_skills;";
            del.ExecuteNonQuery();
        }
        var n = 0;
        foreach (var e in EventEnvelopeReader.ReadAll(c, tx, Category))
        {
            if (e.Payload is not SkillPayload) continue;
            Apply(c, tx, e);
            n++;
        }
        return n;
    }
}
