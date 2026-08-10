using Microsoft.Data.Sqlite;
using Mneme.Contracts;

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
/// <see cref="Mneme.Resolution.SubjectKey"/>; <c>subject_entity_id</c> is left null (names are
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
        // Delegate to the shared canon engine (also used by domain-profile
        // projectors, ADR-0005). WriteTriples clears prior triples for this
        // event first, so a fact with no triples still idempotently clears.
        FactTripleProjection.WriteTriples(c, tx, e, p.Triples ?? Array.Empty<FactTriple>());
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
