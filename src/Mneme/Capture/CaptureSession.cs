using Mneme.Contracts;

namespace Mneme.Capture;

/// <summary>
/// Pumps <see cref="ConversationTurn"/>s through a host-supplied
/// <see cref="ICapturePolicy"/>, an optional filter chain, and then into
/// <see cref="IMemoryAgent.IngestAsync"/>. The host owns the policy
/// (what's worth remembering) and the SDK owns the wiring (ingest,
/// provenance, idempotency, capability).
/// </summary>
/// <remarks>
/// Typical wiring on the host side:
/// <code>
/// services.AddMneme(o => { ... });
/// services.AddSingleton&lt;ICapturePolicy&gt;(sp =&gt; new MyLlmPolicy(...));
/// services.AddSingleton&lt;ICaptureFilter, RecentDuplicateFilter&gt;();
/// // Then per-turn:
/// var session = sp.GetRequiredService&lt;CaptureSession&gt;();
/// await session.ProcessTurnAsync(turn, workstream);
/// </code>
/// </remarks>
public sealed class CaptureSession
{
    private readonly IMemoryAgent _agent;
    private readonly ICapturePolicy _policy;
    private readonly IReadOnlyList<ICaptureFilter> _filters;
    private readonly TimeProvider _clock;

    public CaptureSession(IMemoryAgent agent, ICapturePolicy policy)
        : this(agent, policy, Array.Empty<ICaptureFilter>(), TimeProvider.System) { }

    public CaptureSession(IMemoryAgent agent, ICapturePolicy policy, IEnumerable<ICaptureFilter> filters)
        : this(agent, policy, filters, TimeProvider.System) { }

    public CaptureSession(IMemoryAgent agent, ICapturePolicy policy, IEnumerable<ICaptureFilter> filters, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(clock);
        _agent = agent;
        _policy = policy;
        _filters = filters.ToArray();
        _clock = clock;
    }

    /// <summary>
    /// Evaluate one turn against the host policy, run the filter chain,
    /// and ingest each surviving candidate. Returns the ingest results
    /// (including duplicates so the caller can see what was a no-op).
    /// </summary>
    public async Task<IReadOnlyList<IngestResult>> ProcessTurnAsync(
        ConversationTurn turn, WorkstreamId workstream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var candidates = await _policy.EvaluateAsync(turn, workstream, ct).ConfigureAwait(false);
        if (candidates.Count == 0) return Array.Empty<IngestResult>();

        foreach (var filter in _filters)
        {
            candidates = await filter.FilterAsync(candidates, workstream, ct).ConfigureAwait(false);
            if (candidates.Count == 0) return Array.Empty<IngestResult>();
        }

        var results = new List<IngestResult>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var eventId = candidate.EventId ?? new EventId("cap-" + Guid.NewGuid().ToString("N"));
            var validAt = candidate.ValidAt ?? turn.At;
            var payload = BuildPayload(candidate, turn);
            var provenance = new CaptureProvenance(
                Source: new CaptureSourceId("capture/" + _policy.Id),
                Principal: turn.Speaker,
                Context: candidate.Rationale ?? turn.SessionId);
            var evt = new CaptureEvent(
                EventId: eventId,
                WorkstreamId: workstream,
                Channel: EventChannel.Epistemic,
                ValidAt: validAt,
                RecordedAt: _clock.GetUtcNow(),
                Payload: payload,
                Provenance: provenance);
            results.Add(await _agent.IngestAsync(evt, ct).ConfigureAwait(false));
        }
        return results;
    }

    private static EventPayload BuildPayload(CaptureCandidate c, ConversationTurn turn) => c.Category switch
    {
        EpistemicCategory.Evidence   => new EvidencePayload(c.Content, Source: turn.SessionId),
        EpistemicCategory.Fact       => new FactPayload(c.Content, Array.Empty<EventId>()),
        EpistemicCategory.Decision   => new DecisionPayload(c.Content, c.Rationale ?? string.Empty, Array.Empty<EventId>(), turn.Speaker),
        EpistemicCategory.Hypothesis => new HypothesisPayload(c.Content, HypothesisState.Open),
        EpistemicCategory.Goal       => new GoalPayload(c.Content, GoalState.Active),
        EpistemicCategory.Action     => new ActionPayload(c.Content, null, turn.SessionId),
        EpistemicCategory.Outcome    => new OutcomePayload(c.Content, EventId.None, OutcomePolarity.Neutral),
        _ => throw new InvalidOperationException($"Unknown category {c.Category}"),
    };
}
