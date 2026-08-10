using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Resolution;

namespace Mneme.Projections.Projectors;

/// <summary>
/// Reusable engine over the shared <c>projection_fact_triples</c> /
/// <c>memory_contradictions</c> substrate: idempotent per-event triple
/// projection, plus deterministic contradiction-candidate detection. Extracted
/// verbatim from <see cref="FactTriplesProjector"/> and
/// <see cref="ContradictionsProjector"/> so a domain profile (ADR-0005) can feed
/// the <b>same</b> structured-canon and contradiction machinery from its own
/// payload type — without duplicating the SQL, re-deriving the subject-key
/// normalization, or reproducing the timestamp format.
/// </summary>
/// <remarks>
/// This is the seam that makes "structured canon → free contradiction
/// detection" real for satellite ontologies: a profile projector calls
/// <see cref="WriteTriples"/> to register its payload's
/// <c>(subject, predicate, object)</c> assertions in the shared triple index,
/// then <see cref="DetectContradictions"/> to surface conflicts against every
/// other currently-valid triple in the workstream — base facts and other
/// profile claims alike. Behaviour is identical to the built-in Fact path; the
/// built-in projectors now delegate here.
/// </remarks>
public static class FactTripleProjection
{
    /// <summary>
    /// Idempotently replace the triples <paramref name="e"/> contributes to
    /// <c>projection_fact_triples</c> (delete-then-insert keyed on
    /// <c>event_id</c>). Blank subject/predicate/object triples are skipped; the
    /// subject surface form is reduced to a stable <see cref="SubjectKey"/> and
    /// <c>subject_entity_id</c> is left null (names are Tier-1 ineligible for
    /// canonical ids). Returns the number of triples actually written.
    /// </summary>
    public static int WriteTriples(
        SqliteConnection c, SqliteTransaction tx, EventEnvelope e, IReadOnlyList<FactTriple> triples)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(e);

        // Idempotency: clear any prior triples for this event before re-inserting.
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM projection_fact_triples WHERE workstream_id = $ws AND event_id = $eid;";
            del.Parameters.AddWithValue("$ws", e.WorkstreamId.Value);
            del.Parameters.AddWithValue("$eid", e.EventId.Value);
            del.ExecuteNonQuery();
        }

        if (triples is not { Count: > 0 }) return 0;

        var revoked = e.RevokedAt.HasValue ? (object)FactsProjector.Fmt(e.RevokedAt.Value) : DBNull.Value;
        var validAt = FactsProjector.Fmt(e.ValidAt);

        // Reuse one prepared command across all triples (bind params per row)
        // instead of allocating + preparing a fresh command per triple.
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO projection_fact_triples(workstream_id, event_id, ordinal,
                subject_text, subject_key, subject_entity_id, predicate, object, valid_at, revoked_at)
            VALUES ($ws, $eid, $ord, $stext, $skey, NULL, $pred, $obj, $va, $rev);
            """;
        var pWs = cmd.Parameters.Add("$ws", SqliteType.Text); pWs.Value = e.WorkstreamId.Value;
        var pEid = cmd.Parameters.Add("$eid", SqliteType.Text); pEid.Value = e.EventId.Value;
        var pOrd = cmd.Parameters.Add("$ord", SqliteType.Integer);
        var pStext = cmd.Parameters.Add("$stext", SqliteType.Text);
        var pSkey = cmd.Parameters.Add("$skey", SqliteType.Text);
        var pPred = cmd.Parameters.Add("$pred", SqliteType.Text);
        var pObj = cmd.Parameters.Add("$obj", SqliteType.Text);
        var pVa = cmd.Parameters.Add("$va", SqliteType.Text); pVa.Value = validAt;
        var pRev = cmd.Parameters.Add("$rev", SqliteType.Text); pRev.Value = revoked;

        var ordinal = 0;
        foreach (var t in triples)
        {
            var subjectKey = SubjectKey.Normalize(t.Subject);
            if (subjectKey.Length == 0 || string.IsNullOrWhiteSpace(t.Predicate) || string.IsNullOrWhiteSpace(t.Object))
            {
                continue;
            }
            pOrd.Value = ordinal++;
            pStext.Value = t.Subject;
            pSkey.Value = subjectKey;
            pPred.Value = t.Predicate;
            pObj.Value = t.Object;
            cmd.ExecuteNonQuery();
        }
        return ordinal;
    }

    /// <summary>
    /// Detect deterministic contradiction candidates for the triples
    /// <paramref name="eventId"/> just contributed: any currently-valid
    /// (non-revoked) triple elsewhere in <paramref name="workstreamId"/> that
    /// shares this triple's <c>subject_key</c> + <c>predicate</c> but asserts a
    /// different <c>object</c>. Each conflicting pair is recorded once
    /// (deterministically ordered) in <c>memory_contradictions</c> for human
    /// review — never auto-resolved. Reads the triples already present for
    /// <paramref name="eventId"/>, so call it <em>after</em>
    /// <see cref="WriteTriples"/> for the same event.
    /// </summary>
    public static void DetectContradictions(
        SqliteConnection c, SqliteTransaction tx, WorkstreamId workstreamId, EventId eventId, DateTimeOffset validAt)
    {
        ArgumentNullException.ThrowIfNull(c);

        // The triples this event just contributed (inserted by WriteTriples).
        var mine = new List<(string SubjectKey, string Predicate, string Object)>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                SELECT subject_key, predicate, object
                FROM projection_fact_triples
                WHERE workstream_id = $ws AND event_id = $eid AND revoked_at IS NULL;
                """;
            q.Parameters.AddWithValue("$ws", workstreamId.Value);
            q.Parameters.AddWithValue("$eid", eventId.Value);
            using var r = q.ExecuteReader();
            while (r.Read()) mine.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        if (mine.Count == 0) return;

        var detectedAt = FactsProjector.Fmt(validAt);
        foreach (var (subjectKey, predicate, obj) in mine)
        {
            using var find = c.CreateCommand();
            find.Transaction = tx;
            find.CommandText = """
                SELECT DISTINCT event_id, object
                FROM projection_fact_triples
                WHERE workstream_id = $ws
                  AND subject_key = $sk
                  AND predicate = $pred
                  AND event_id <> $eid
                  AND revoked_at IS NULL
                  AND LOWER(TRIM(object)) <> LOWER(TRIM($obj));
                """;
            find.Parameters.AddWithValue("$ws", workstreamId.Value);
            find.Parameters.AddWithValue("$sk", subjectKey);
            find.Parameters.AddWithValue("$pred", predicate);
            find.Parameters.AddWithValue("$eid", eventId.Value);
            find.Parameters.AddWithValue("$obj", obj);

            var conflicts = new List<(string EventId, string Object)>();
            using (var r = find.ExecuteReader())
            {
                while (r.Read()) conflicts.Add((r.GetString(0), r.GetString(1)));
            }

            foreach (var (otherId, otherObj) in conflicts)
            {
                // Deterministic pair ordering so (A,B) and (B,A) dedupe to one row.
                var (aId, aObj, bId, bObj) = string.CompareOrdinal(eventId.Value, otherId) < 0
                    ? (eventId.Value, obj, otherId, otherObj)
                    : (otherId, otherObj, eventId.Value, obj);
                Insert(c, tx, workstreamId.Value, subjectKey, predicate, aId, aObj, bId, bObj, detectedAt);
            }
        }
    }

    private static void Insert(SqliteConnection c, SqliteTransaction tx, string ws, string subjectKey,
        string predicate, string aId, string aObj, string bId, string bObj, string detectedAt)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO memory_contradictions(workstream_id, subject_key, predicate,
                event_id_a, object_a, event_id_b, object_b, detected_at, status)
            VALUES ($ws, $sk, $pred, $a, $ao, $b, $bo, $at, 0)
            ON CONFLICT(workstream_id, subject_key, predicate, event_id_a, event_id_b) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$ws", ws);
        cmd.Parameters.AddWithValue("$sk", subjectKey);
        cmd.Parameters.AddWithValue("$pred", predicate);
        cmd.Parameters.AddWithValue("$a", aId);
        cmd.Parameters.AddWithValue("$ao", aObj);
        cmd.Parameters.AddWithValue("$b", bId);
        cmd.Parameters.AddWithValue("$bo", bObj);
        cmd.Parameters.AddWithValue("$at", detectedAt);
        cmd.ExecuteNonQuery();
    }
}
