using Microsoft.Data.Sqlite;
using Mneme.Contracts;

namespace Mneme.Projections.Projectors;

/// <summary>
/// Detects contradiction <em>candidates</em> among structured fact triples and
/// records them in <c>memory_contradictions</c> for human review (Phase 13,
/// ADR-0004). Two currently-valid (non-revoked) triples in the same workstream
/// that share a <c>subject_key</c> and <c>predicate</c> but assert a different
/// <c>object</c> are a conflict — not a bi-temporal supersession, which assumes
/// sequential observation. Concurrent agents can produce exactly this, so the
/// substrate surfaces the conflict rather than silently picking a winner.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic and narrow by design (low false positives): it never uses an
/// LLM and only compares structured triples. Object comparison is
/// trim+case-insensitive. Multi-valued predicates (e.g. "likes") can still
/// produce benign candidates; those are resolved by the human reviewer, never
/// auto-applied.
/// </para>
/// <para>
/// Must run <em>after</em> <see cref="FactTriplesProjector"/> in the pipeline so
/// the current event's triples are already present. Matches the Fact category.
/// </para>
/// </remarks>
public sealed class ContradictionsProjector : IProjector
{
    /// <inheritdoc/>
    public string Name => "contradictions";
    /// <inheritdoc/>
    public EpistemicCategory Category => EpistemicCategory.Fact;

    /// <inheritdoc/>
    public void Apply(SqliteConnection c, SqliteTransaction tx, EventEnvelope e)
    {
        if (e.Payload is not FactPayload) return;

        // The triples this event just contributed (inserted by FactTriplesProjector).
        var mine = new List<(string SubjectKey, string Predicate, string Object)>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                SELECT subject_key, predicate, object
                FROM projection_fact_triples
                WHERE workstream_id = $ws AND event_id = $eid AND revoked_at IS NULL;
                """;
            q.Parameters.AddWithValue("$ws", e.WorkstreamId.Value);
            q.Parameters.AddWithValue("$eid", e.EventId.Value);
            using var r = q.ExecuteReader();
            while (r.Read()) mine.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        if (mine.Count == 0) return;

        var detectedAt = FactsProjector.Fmt(e.ValidAt);
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
            find.Parameters.AddWithValue("$ws", e.WorkstreamId.Value);
            find.Parameters.AddWithValue("$sk", subjectKey);
            find.Parameters.AddWithValue("$pred", predicate);
            find.Parameters.AddWithValue("$eid", e.EventId.Value);
            find.Parameters.AddWithValue("$obj", obj);

            var conflicts = new List<(string EventId, string Object)>();
            using (var r = find.ExecuteReader())
            {
                while (r.Read()) conflicts.Add((r.GetString(0), r.GetString(1)));
            }

            foreach (var (otherId, otherObj) in conflicts)
            {
                // Deterministic pair ordering so (A,B) and (B,A) dedupe to one row.
                var (aId, aObj, bId, bObj) = string.CompareOrdinal(e.EventId.Value, otherId) < 0
                    ? (e.EventId.Value, obj, otherId, otherObj)
                    : (otherId, otherObj, e.EventId.Value, obj);
                Insert(c, tx, e.WorkstreamId.Value, subjectKey, predicate, aId, aObj, bId, bObj, detectedAt);
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

    /// <inheritdoc/>
    public int Rebuild(SqliteConnection c, SqliteTransaction tx)
    {
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM memory_contradictions;";
            del.ExecuteNonQuery();
        }
        using var ins = c.CreateCommand();
        ins.Transaction = tx;
        // Self-join: each conflicting pair once (a.event_id < b.event_id).
        ins.CommandText = """
            INSERT INTO memory_contradictions(workstream_id, subject_key, predicate,
                event_id_a, object_a, event_id_b, object_b, detected_at, status)
            SELECT a.workstream_id, a.subject_key, a.predicate,
                   a.event_id, a.object, b.event_id, b.object,
                   MAX(a.valid_at, b.valid_at), 0
            FROM projection_fact_triples a
            JOIN projection_fact_triples b
              ON a.workstream_id = b.workstream_id
             AND a.subject_key = b.subject_key
             AND a.predicate = b.predicate
             AND a.event_id < b.event_id
             AND LOWER(TRIM(a.object)) <> LOWER(TRIM(b.object))
            WHERE a.revoked_at IS NULL AND b.revoked_at IS NULL
            ON CONFLICT(workstream_id, subject_key, predicate, event_id_a, event_id_b) DO NOTHING;
            """;
        return ins.ExecuteNonQuery();
    }
}
