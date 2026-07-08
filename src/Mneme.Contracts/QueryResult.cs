namespace Mneme.Contracts;

/// <summary>
/// Result of a <see cref="IMemoryQueryAPI.QueryAsync"/> call.
/// </summary>
/// <param name="Items">Matched events, ranked by final score (highest first).</param>
/// <param name="TotalMatched">Total matches before the <see cref="QuerySpec.Limit"/> was applied. <c>Items.Count</c> &lt;= this.</param>
/// <param name="Explain">Global diagnostic info (set when <see cref="QueryRequest.Explain"/> is true).</param>
/// <param name="SubjectTriples">
/// Subject-scoped fact triples surfaced as an answer-context <em>supplement</em>
/// when <see cref="QueryRequest.SupplementSubjectTriples"/> is set. These are
/// structured assertions about the entities named in the query; a consumer
/// appends them alongside <see cref="Items"/> (never replacing them) so the
/// asked-about person's attributed facts are present without displacing the
/// semantic result. Empty unless requested.
/// </param>
public sealed record QueryResult(
    IReadOnlyList<QueryResultItem> Items,
    int TotalMatched,
    QueryExplain? Explain = null,
    IReadOnlyList<SubjectTripleHit>? SubjectTriples = null);

/// <summary>
/// A subject-attributed fact triple surfaced as an answer-context supplement
/// (see <see cref="QueryResult.SubjectTriples"/>). Carries the triple, the date
/// its claim was valid, and the source event so a consumer can cite it.
/// </summary>
/// <param name="Triple">The structured (subject, predicate, object) assertion.</param>
/// <param name="ValidAt">When the underlying fact's claim was true in the world.</param>
/// <param name="SourceEvent">The fact event the triple was extracted from.</param>
public sealed record SubjectTripleHit(
    FactTriple Triple,
    DateTimeOffset ValidAt,
    EventId SourceEvent);

/// <summary>A single hit from a query.</summary>
/// <param name="EventId">The matched event's id.</param>
/// <param name="Category">The matched event's epistemic category.</param>
/// <param name="ValidAt">When the event's claim was true in the world.</param>
/// <param name="RecordedAt">When Mneme committed the event.</param>
/// <param name="Summary">A short human-readable description of the matched event.</param>
/// <param name="Score">Final fused retrieval score in [0,1].</param>
/// <param name="Annotations">Any human-attached annotations (from <c>fact.annotated</c> curation events).</param>
/// <param name="Details">Score decomposition, populated when <see cref="QueryRequest.Explain"/> is true.</param>
public sealed record QueryResultItem(
    EventId EventId,
    EpistemicCategory Category,
    DateTimeOffset ValidAt,
    DateTimeOffset RecordedAt,
    string Summary,
    double Score,
    IReadOnlyList<string> Annotations,
    ScoreDetails? Details = null);

/// <summary>
/// Score-decomposition diagnostic emitted when <see cref="QueryRequest.Explain"/>
/// is true. All component scores are normalized to <c>[0,1]</c>, higher = better,
/// <em>before</em> fusion. See <c>plans/plan.md</c> §"Retrieval scoring".
/// </summary>
/// <param name="Semantic">Semantic similarity contribution (vector / embedding distance).</param>
/// <param name="Bm25">FTS5 BM25 contribution, sigmoid-normalized to [0,1].</param>
/// <param name="EntityBoost">Entity-graph boost.</param>
/// <param name="CurationMultiplier">Multiplier from pin/demote curation events (1.0 = no curation effect).</param>
/// <param name="Fused">Additive-with-gate fused score before <see cref="CurationMultiplier"/>.</param>
/// <param name="Final">Final score = <paramref name="Fused"/> × <paramref name="CurationMultiplier"/>.</param>
/// <param name="PassedSemanticThreshold">Whether the semantic component cleared the hard gate (default 0.1).</param>
/// <param name="GateReason">If <paramref name="PassedSemanticThreshold"/> is false, why the result was nonetheless included (e.g., "explicit AsOf query").</param>
public sealed record ScoreDetails(
    double Semantic,
    double Bm25,
    double EntityBoost,
    double CurationMultiplier,
    double Fused,
    double Final,
    bool PassedSemanticThreshold,
    string? GateReason = null);

/// <summary>
/// Global query-level diagnostic. Emitted alongside <see cref="QueryResult"/>
/// when <see cref="QueryRequest.Explain"/> is true.
/// </summary>
/// <param name="DispatcherChoice">Which retrieval strategy the query dispatcher chose (e.g., <c>"lexical"</c>, <c>"temporal"</c>, <c>"graph"</c>).</param>
/// <param name="CapabilityCheck">A human-readable trace of the capability check (e.g., which categories were allowed, was cross-workstream invoked).</param>
/// <param name="CandidatesConsidered">How many candidate events were scored before <see cref="QuerySpec.Limit"/>.</param>
/// <param name="CandidatesGatedOut">How many candidates were dropped by score gates or capability filters.</param>
public sealed record QueryExplain(
    string DispatcherChoice,
    string CapabilityCheck,
    int CandidatesConsidered,
    int CandidatesGatedOut);
