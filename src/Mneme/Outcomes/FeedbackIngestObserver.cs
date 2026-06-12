using Mneme.Contracts;
using Mneme.Ingest;
using Mneme.Projections;

namespace Mneme.Outcomes;

/// <summary>
/// Bridges the Phase 7 <see cref="FeedbackLearner"/> into the
/// <see cref="IIngestObserver"/> chain so each Outcome event landing on
/// a closed chain immediately nudges feedback weights on supporting
/// evidence. No agent action needed.
/// </summary>
public sealed class FeedbackIngestObserver : IIngestObserver
{
    private readonly FeedbackLearner _learner;
    private readonly Mneme.Storage.SqliteConnectionFactory _connections;

    public FeedbackIngestObserver(FeedbackLearner learner, Mneme.Storage.SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(learner);
        ArgumentNullException.ThrowIfNull(connections);
        _learner = learner;
        _connections = connections;
    }

    /// <inheritdoc/>
    public void OnIngested(EventEnvelope envelope)
    {
        if (envelope.Payload is not OutcomePayload o || !o.ActionEvent.HasValue) return;
        // Walk Outcome -> Action -> Decision; nudge weights from the
        // decision side once we have it.
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM memory_events WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", o.ActionEvent.Value);
        var json = cmd.ExecuteScalar() as string;
        if (json is null) return;
        var actionPayload = Mneme.Storage.EventSerialization.DeserializePayload(json);
        if (actionPayload is not ActionPayload action || action.DecisionEvent is not { } decisionId || !decisionId.HasValue) return;
        _learner.ApplyFromOutcome(decisionId, o.Polarity);
    }
}
