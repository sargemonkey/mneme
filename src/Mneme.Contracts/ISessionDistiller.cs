namespace Mneme.Contracts;

/// <summary>
/// Host-supplied distiller that converts a slice of an agent session's
/// context (the entries between the last watermark and "right now") into
/// zero or more epistemic <see cref="EventPayload"/>s. The SDK orchestrates
/// the call (watermark read, idempotency guard, citation stamping,
/// ingest), the host's <see cref="ISessionDistiller"/> owns the LLM
/// invocation (or heuristic, or human-in-the-loop, or whatever else makes
/// sense for the deployment).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Responsibility split:</strong>
/// <list type="bullet">
///   <item>The SDK assembles the <see cref="SessionDistillationRequest"/>
///         (filters out entries at-or-below the watermark, attaches prior
///         facts for context, applies token budget).</item>
///   <item>The host's <see cref="ISessionDistiller"/> reads the request,
///         decides which entries are worth turning into epistemic events,
///         and returns a <see cref="SessionDistillationResult"/> of typed
///         payloads.</item>
///   <item>The SDK stamps each payload with a
///         <see cref="Citation.SessionRange"/> pointing back at the entries
///         it came from, ingests through the normal
///         <see cref="IMemoryAgent.IngestAsync"/> path (validate → redact →
///         classify → WAL), and advances the watermark atomically.</item>
/// </list>
/// </para>
/// <para>
/// Symmetric to <see cref="IDistiller"/> on the read side (which synthesizes
/// a <see cref="ContextBundle"/> from already-stored events). The two
/// distillers are usually different models / prompts: session distillation
/// is targeted extraction, bundle distillation is broad synthesis. Hosts
/// can wire one, the other, both, or neither.
/// </para>
/// </remarks>
public interface ISessionDistiller
{
    /// <summary>
    /// Stable identifier for this distiller (e.g., <c>"openai/gpt-4o-mini-session@2026-06"</c>).
    /// Stamped on the watermark and surfaced in the provenance of every
    /// event the distillation produced so consumers can tell which model /
    /// prompt revision produced a given memory.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Look at the entries in <paramref name="request"/> and return the
    /// epistemic events worth keeping. Implementations should drop noise
    /// silently or return it via <see cref="SessionDistillationResult.Dropped"/>
    /// for audit. Returning an empty <see cref="SessionDistillationResult.Events"/>
    /// is fine (and frequently correct for chit-chat-only slices).
    /// </summary>
    Task<SessionDistillationResult> DistillAsync(
        SessionDistillationRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Everything an <see cref="ISessionDistiller"/> needs to interpret a
/// slice of a session's context. The SDK builds this; the distiller
/// consumes it.
/// </summary>
/// <param name="Session">Which session is being distilled.</param>
/// <param name="Workstream">Which Mneme workstream the distilled events will land in.</param>
/// <param name="Entries">The entries to consider, in monotonic order. Strictly after the watermark passed by the host.</param>
/// <param name="PriorWatermark">The watermark before this call. <c>null</c> on first-ever distillation for the session.</param>
/// <param name="PriorFacts">A small set of facts already known about the session / workstream, surfaced so the distiller can avoid re-extracting things or can amend prior claims when warranted. SDK caps the count by the available token budget.</param>
/// <param name="TokenBudget">Soft total token budget for the request. The distiller should stay under this; the SDK does not enforce.</param>
public sealed record SessionDistillationRequest(
    SessionId Session,
    WorkstreamId Workstream,
    IReadOnlyList<ContextEntry> Entries,
    ContextWatermark? PriorWatermark,
    IReadOnlyList<PriorFact> PriorFacts,
    int TokenBudget);

/// <summary>
/// A previously-distilled fact the SDK surfaces to the distiller as
/// background so it can detect duplication / supersession.
/// </summary>
/// <param name="EventId">Event id of the prior fact.</param>
/// <param name="Category">Epistemic category.</param>
/// <param name="Statement">Canonical short summary of the fact.</param>
/// <param name="ValidAt">When the fact became / will become true.</param>
public sealed record PriorFact(
    EventId EventId,
    EpistemicCategory Category,
    string Statement,
    DateTimeOffset ValidAt);

/// <summary>
/// What the distiller returns. Payloads carry no <see cref="Citation"/>
/// themselves — the SDK stamps each with a <see cref="Citation.SessionRange"/>
/// covering the entries the distiller cited as supporting it.
/// </summary>
/// <param name="Events">The events to ingest, in arbitrary order.</param>
/// <param name="Dropped">Optional audit list of entries the distiller chose to skip.</param>
public sealed record SessionDistillationResult(
    IReadOnlyList<DistilledEvent> Events,
    IReadOnlyList<DroppedEntry>? Dropped = null);

/// <summary>
/// One event the distiller decided to extract from the context slice.
/// </summary>
/// <param name="Payload">The typed epistemic payload. Category is implicit in the payload type.</param>
/// <param name="SupportingEntryIds">The <see cref="ContextEntry.EntryId"/>s the LLM cited as evidence for this event. The SDK reduces these to the (min, max) range for the <see cref="Citation.SessionRange"/> stamp.</param>
/// <param name="ValidAt">When the claim was true. Defaults to the timestamp of the last supporting entry if omitted.</param>
/// <param name="EventId">Optional caller-supplied id for idempotency. SDK auto-generates if omitted.</param>
public sealed record DistilledEvent(
    EventPayload Payload,
    IReadOnlyList<string> SupportingEntryIds,
    DateTimeOffset? ValidAt = null,
    EventId? EventId = null);
