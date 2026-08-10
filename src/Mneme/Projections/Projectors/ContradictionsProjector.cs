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
        // Delegate to the shared contradiction engine (also used by
        // domain-profile projectors, ADR-0005). Runs after FactTriplesProjector
        // in the pipeline, so this event's triples are already present.
        FactTripleProjection.DetectContradictions(c, tx, e.WorkstreamId, e.EventId, e.ValidAt);
    }
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
