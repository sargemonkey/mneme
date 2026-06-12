namespace Mneme.Contracts;

/// <summary>
/// Phase 5 — the SDK's contract for synthesizing a <see cref="ContextBundle"/>
/// from a <see cref="DistillationRequest"/>. Implementations are supplied by
/// the host (program / background service / agent runtime); the SDK never
/// owns the LLM call. This keeps Mneme model-agnostic by design — the same
/// memory log can be distilled by a host that uses OpenAI today, Anthropic
/// tomorrow, an on-device model the day after, or no LLM at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Responsibility split:</strong>
/// <list type="bullet">
///   <item>The SDK assembles the <see cref="DistillationRequest"/> — runs the
///         capability check, applies bi-temporal filtering, scores events,
///         honors curation overrides, splits the budget across sections.</item>
///   <item>The host's <see cref="IDistiller"/> takes that request, prompts
///         whatever LLM it wants (or uses a heuristic, or no LLM), and
///         returns a <see cref="ContextBundle"/> that fills in the prose
///         slots (<see cref="OrientationSummary"/>, <see cref="BundleSection.Content"/>,
///         <see cref="LookupHint.Context"/>).</item>
///   <item>The SDK then caches the returned bundle, stamps it with
///         <see cref="ContextBundle.EventsCoveredThrough"/>, and serves it
///         from <see cref="IMemoryQueryAPI.DistillAsync"/>.</item>
/// </list>
/// </para>
/// <para>
/// When no <see cref="IDistiller"/> is registered, the SDK falls back to a
/// degraded bundle whose <see cref="OrientationSummary.Paragraph"/> names
/// the missing distiller and links the host to the integration docs.
/// </para>
/// </remarks>
public interface IDistiller
{
    /// <summary>
    /// Stable identifier for this distiller (e.g., <c>"openai/gpt-4o-mini@2026-06"</c>).
    /// Stamped into <see cref="OrientationSummary.Distiller"/> and every
    /// <see cref="BundleSection.Distiller"/> so consumers can tell which
    /// model produced a synthesis.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Synthesize a bundle for the request. Implementations should respect
    /// <see cref="DistillationRequest.TokenBudget"/> and may inspect
    /// <see cref="DistillationRequest.PriorBundle"/> for incremental
    /// update (re-using prior section content when its
    /// <see cref="BundleSection.EventsCoveredThrough"/> hasn't been
    /// invalidated).
    /// </summary>
    Task<ContextBundle> DistillAsync(DistillationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Everything an <see cref="IDistiller"/> needs to synthesize a bundle.
/// The SDK builds this; the distiller consumes it.
/// </summary>
/// <param name="Workstream">Workstream being distilled.</param>
/// <param name="GeneratedAt">When the SDK began assembling this request (use for the bundle stamp).</param>
/// <param name="EventsCoveredThrough">The newest event id in the workstream at request time.</param>
/// <param name="TokenBudget">Soft total token budget. Distillers should split this across the orientation paragraph, sections, and hints.</param>
/// <param name="Events">All capability-allowed, non-revoked events in the workstream, with the SDK's pre-computed score (incorporating pin/demote multipliers). Newest first.</param>
/// <param name="Curations">Active (non-reverted) curations attached to the events, keyed by target event id. Distillers should surface annotations and treat pinned events as required-include and demoted events as optional/hints-only.</param>
/// <param name="PriorBundle">Previous bundle for this workstream if one is cached. <c>null</c> on cold-start or after a <see cref="DistillOptions.ForceRefresh"/>.</param>
public sealed record DistillationRequest(
    WorkstreamId Workstream,
    DateTimeOffset GeneratedAt,
    EventId EventsCoveredThrough,
    int TokenBudget,
    IReadOnlyList<DistillationEvent> Events,
    IReadOnlyDictionary<EventId, IReadOnlyList<DistillationCuration>> Curations,
    ContextBundle? PriorBundle);

/// <summary>One event, pre-decoded and pre-scored, ready for the distiller.</summary>
/// <param name="EventId">Source event id.</param>
/// <param name="Category">Epistemic category.</param>
/// <param name="Classification">Sensitivity label.</param>
/// <param name="ValidAt">Event-time stamp (when the claim was true).</param>
/// <param name="RecordedAt">Ingest-time stamp (when Mneme learned of it).</param>
/// <param name="Score">Final fused retrieval score in [0,1] (SDK-computed; respects pin/demote multipliers).</param>
/// <param name="Payload">Typed payload — the same shape stored in the event log.</param>
/// <param name="Provenance">Where this event came from.</param>
public sealed record DistillationEvent(
    EventId EventId,
    EpistemicCategory Category,
    Classification Classification,
    DateTimeOffset ValidAt,
    DateTimeOffset RecordedAt,
    double Score,
    EventPayload Payload,
    CaptureProvenance Provenance);

/// <summary>One non-reverted curation targeting an event.</summary>
/// <param name="CurationEventId">The curation event id.</param>
/// <param name="Type">Which curation kind.</param>
/// <param name="Curator">Principal who curated.</param>
/// <param name="Rationale">Curator's stated reason (for annotations: the annotation text).</param>
/// <param name="OccurredAt">When the curation was recorded.</param>
/// <param name="Multiplier">For pin/demote: the multiplier (default 1.0). For other types: 1.0.</param>
/// <param name="AmendedContent">For amend: the new content the SDK has already substituted into the event payload. <c>null</c> otherwise.</param>
public sealed record DistillationCuration(
    EventId CurationEventId,
    CurationType Type,
    PrincipalId Curator,
    string Rationale,
    DateTimeOffset OccurredAt,
    double Multiplier = 1.0,
    string? AmendedContent = null);
