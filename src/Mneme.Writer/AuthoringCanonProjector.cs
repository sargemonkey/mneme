using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Projections;
using Mneme.Projections.Projectors;

namespace Mneme.Writer;

/// <summary>
/// Projects each <see cref="AuthoringClaimPayload"/> into the <b>shared</b>
/// canon substrate (<c>projection_fact_triples</c>) and runs the shared
/// contradiction engine over it — so a writing-domain claim gets the same
/// deterministic continuity checking base facts get, "for free" (ADR-0005 /
/// Phase 15.B). This is the headline of the writer profile: a structured
/// <c>(subject, attribute, value)</c> canon under the existing LLM continuity
/// pass, instead of a flat digest handed to a model.
/// </summary>
/// <remarks>
/// <para>
/// Matches <see cref="EpistemicCategory.Fact"/> (the category authoring claims
/// ride under) and self-guards on payload type, so it ignores real
/// <see cref="FactPayload"/> events — those are already handled by the built-in
/// <see cref="FactTriplesProjector"/> / <see cref="ContradictionsProjector"/>.
/// </para>
/// <para>
/// Because domain projectors run <em>after</em> the built-ins in the pipeline,
/// this projector owns both halves for its own payload: it writes the claim's
/// triple, then detects contradictions against every currently-valid triple in
/// the workstream — base facts and other authoring claims alike. Detection is
/// symmetric and deduped, so a base-fact-vs-claim or claim-vs-claim conflict is
/// recorded exactly once regardless of ingest order.
/// </para>
/// <para>
/// Reuses the base <c>projection_fact_triples</c> and
/// <c>memory_contradictions</c> tables, so the writer profile ships no schema
/// module of its own. <see cref="Rebuild"/> upserts per-event (no table-wide
/// wipe), so it composes safely after the built-in fact-triple rebuild.
/// </para>
/// </remarks>
public sealed class AuthoringCanonProjector : IProjector
{
    /// <inheritdoc/>
    public string Name => "writer-authoring-canon";

    /// <inheritdoc/>
    public EpistemicCategory Category => EpistemicCategory.Fact;

    /// <inheritdoc/>
    public void Apply(SqliteConnection c, SqliteTransaction tx, EventEnvelope e)
    {
        if (e.Payload is not AuthoringClaimPayload claim) return;

        // 1. Register the claim's structured assertion in the shared canon index.
        FactTripleProjection.WriteTriples(c, tx, e, new[] { claim.ToTriple() });

        // 2. Detect continuity contradictions against the whole workstream canon.
        //    Revoked claims are excluded (WriteTriples stamped revoked_at, and the
        //    detector filters revoked_at IS NULL) so a retracted claim never
        //    contradicts and never gets contradicted.
        FactTripleProjection.DetectContradictions(c, tx, e.WorkstreamId, e.EventId, e.ValidAt);
    }

    /// <inheritdoc/>
    public int Rebuild(SqliteConnection c, SqliteTransaction tx)
    {
        // Per-event upsert only — NEVER a table-wide DELETE. During RebuildAll
        // the built-in FactTriplesProjector.Rebuild has already wiped and
        // repopulated projection_fact_triples with base fact triples; we add our
        // authoring triples alongside them and detect any resulting conflicts.
        var n = 0;
        foreach (var e in EventEnvelopeReader.ReadAll(c, tx, Category))
        {
            if (e.Payload is not AuthoringClaimPayload) continue;
            Apply(c, tx, e);
            n++;
        }
        return n;
    }
}
