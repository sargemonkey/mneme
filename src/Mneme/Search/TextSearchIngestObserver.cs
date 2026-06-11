using Mneme.Contracts;
using Mneme.Ingest;
using Mneme.Projections;

namespace Mneme.Search;

/// <summary>Bridges <see cref="TextSearchService"/> into the <see cref="IIngestObserver"/> chain.</summary>
public sealed class TextSearchIngestObserver : IIngestObserver
{
    private readonly TextSearchService _search;
    public TextSearchIngestObserver(TextSearchService search)
    {
        ArgumentNullException.ThrowIfNull(search);
        _search = search;
    }
    /// <inheritdoc/>
    public void OnIngested(EventEnvelope envelope) =>
        _search.Index(envelope.EventId, envelope.WorkstreamId, envelope.Category,
            envelope.CreatedAt, Text(envelope.Payload));

    private static string Text(EventPayload p) => p switch
    {
        EvidencePayload e   => e.Content,
        FactPayload f       => f.Statement,
        DecisionPayload d   => d.Statement + " " + d.Rationale,
        HypothesisPayload h => h.Statement,
        GoalPayload g       => g.Statement,
        ActionPayload a     => a.Statement + " " + (a.ExternalReference ?? string.Empty),
        OutcomePayload o    => o.Statement,
        _                   => string.Empty,
    };
}
