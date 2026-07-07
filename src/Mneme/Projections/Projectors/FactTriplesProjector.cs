using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Resolution;

namespace Mneme.Projections.Projectors;

/// <summary>
/// Projects <see cref="FactPayload.Triples"/> into
/// <c>projection_fact_triples</c> — the subject-attributed index that lets
/// retrieval scope to facts <em>about</em> an entity rather than facts whose
/// text merely mentions it.
/// </summary>
/// <remarks>
/// Runs alongside <see cref="FactsProjector"/> (both match
/// <see cref="EpistemicCategory.Fact"/>); the full statement stays in
/// <c>projection_facts</c> while the triples are a derived attribution index
/// over it. The subject surface form is reduced to a stable
/// <see cref="SubjectKey"/>; <c>subject_entity_id</c> is left null (names are
/// Tier-1 ineligible for canonical ids — full resolution is a later pass).
/// A fact with no triples projects nothing here, so old statement-only facts
/// are unaffected. Idempotent: an event's triples are deleted and re-inserted
/// on re-apply.
/// </remarks>
public sealed class FactTriplesProjector : IProjector
{
    /// <inheritdoc/>
    public string Name => "fact-triples";
    /// <inheritdoc/>
    public EpistemicCategory Category => EpistemicCategory.Fact;

    /// <inheritdoc/>
    public void Apply(SqliteConnection c, SqliteTransaction tx, EventEnvelope e)
    {
        if (e.Payload is not FactPayload p) return;

        // Idempotency: clear any prior triples for this event before re-inserting.
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM projection_fact_triples WHERE workstream_id = $ws AND event_id = $eid;";
            del.Parameters.AddWithValue("$ws", e.WorkstreamId.Value);
            del.Parameters.AddWithValue("$eid", e.EventId.Value);
            del.ExecuteNonQuery();
        }

        if (p.Triples is not { Count: > 0 } triples) return;

        var revoked = e.RevokedAt.HasValue ? (object)FactsProjector.Fmt(e.RevokedAt.Value) : DBNull.Value;
        var validAt = FactsProjector.Fmt(e.ValidAt);
        var ordinal = 0;
        foreach (var t in triples)
        {
            var subjectKey = SubjectKey.Normalize(t.Subject);
            if (subjectKey.Length == 0 || string.IsNullOrWhiteSpace(t.Predicate) || string.IsNullOrWhiteSpace(t.Object))
            {
                continue;
            }
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO projection_fact_triples(workstream_id, event_id, ordinal,
                    subject_text, subject_key, subject_entity_id, predicate, object, valid_at, revoked_at)
                VALUES ($ws, $eid, $ord, $stext, $skey, NULL, $pred, $obj, $va, $rev);
                """;
            cmd.Parameters.AddWithValue("$ws", e.WorkstreamId.Value);
            cmd.Parameters.AddWithValue("$eid", e.EventId.Value);
            cmd.Parameters.AddWithValue("$ord", ordinal++);
            cmd.Parameters.AddWithValue("$stext", t.Subject);
            cmd.Parameters.AddWithValue("$skey", subjectKey);
            cmd.Parameters.AddWithValue("$pred", t.Predicate);
            cmd.Parameters.AddWithValue("$obj", t.Object);
            cmd.Parameters.AddWithValue("$va", validAt);
            cmd.Parameters.AddWithValue("$rev", revoked);
            cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc/>
    public int Rebuild(SqliteConnection c, SqliteTransaction tx)
    {
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM projection_fact_triples;";
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
}
