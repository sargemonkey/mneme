namespace Mneme.Contracts;

/// <summary>
/// The HITL curation surface for Mneme. Every operation appends a new
/// event to the log (Mneme never mutates projections or artifacts in place).
/// </summary>
/// <remarks>
/// <para>
/// Every mutating call requires a <see cref="CurationCapability"/> with the
/// per-operation flag set; a missing flag throws
/// <see cref="CapabilityDeniedError"/>.
/// </para>
/// <para>
/// <strong>Stale-state guard:</strong> <see cref="AmendFactAsync"/>,
/// <see cref="SplitFactAsync"/>, and <see cref="MergeFactsAsync"/> take a
/// <c>preStateHash</c> parameter. The implementation re-computes the
/// canonical hash of the target's current state and fails with
/// <see cref="StaleProposalError"/> if it doesn't match the caller's view.
/// Pattern from Letta <c>core_memory_replace</c>; prevents two curators
/// from racing and the second silently overwriting the first.
/// </para>
/// <para>
/// See <c>plans/plan.md</c> §"Human-in-the-loop curation" for the full
/// design rationale.
/// </para>
/// </remarks>
public interface IMemoryCurator
{
    /// <summary>
    /// Correct a fact's content. Appends a <c>fact.amended</c> event; the
    /// old fact is superseded but remains queryable bi-temporally
    /// (point-in-time queries before the amend return the original).
    /// </summary>
    /// <param name="target">The fact to amend.</param>
    /// <param name="preStateHash">Hash of the target's current canonical state, as observed by the curator.</param>
    /// <param name="amendment">The new content and the curator's rationale.</param>
    /// <param name="cap">Curation capability — must have <see cref="CurationCapability.CanAmend"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result with the new event id.</returns>
    /// <exception cref="CapabilityDeniedError">If <paramref name="cap"/> does not have <see cref="CurationCapability.CanAmend"/>.</exception>
    /// <exception cref="StaleProposalError">If <paramref name="preStateHash"/> does not match the current state.</exception>
    Task<CurationResult> AmendFactAsync(
        FactId target,
        string preStateHash,
        FactAmendment amendment,
        CurationCapability cap,
        CancellationToken ct = default);

    /// <summary>
    /// Attach human commentary to a target event. Non-destructive — does not
    /// change the target's content or retrieval weight. Surfaces in
    /// <see cref="QueryResultItem.Annotations"/>.
    /// </summary>
    /// <param name="target">Event to annotate.</param>
    /// <param name="commentary">The human-supplied annotation text.</param>
    /// <param name="cap">Curation capability — must have <see cref="CurationCapability.CanAnnotate"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result with the new event id.</returns>
    /// <exception cref="CapabilityDeniedError">If <paramref name="cap"/> does not have <see cref="CurationCapability.CanAnnotate"/>.</exception>
    Task<CurationResult> AnnotateAsync(
        EventId target,
        string commentary,
        CurationCapability cap,
        CancellationToken ct = default);

    /// <summary>
    /// Boost an event's retrieval weight by a multiplier. Distillation will
    /// always include pinned facts in the next bundle.
    /// </summary>
    /// <param name="target">Event to pin.</param>
    /// <param name="scope">Whether the pin applies workstream-locally or globally.</param>
    /// <param name="weightMultiplier">Retrieval-weight multiplier (typical: 2.0; must be &gt; 1.0 to be a meaningful pin).</param>
    /// <param name="cap">Curation capability — must have <see cref="CurationCapability.CanPin"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result with the new event id.</returns>
    /// <exception cref="CapabilityDeniedError">If <paramref name="cap"/> does not have <see cref="CurationCapability.CanPin"/>.</exception>
    Task<CurationResult> PinAsync(
        EventId target,
        PinScope scope,
        float weightMultiplier,
        CurationCapability cap,
        CancellationToken ct = default);

    /// <summary>
    /// Suppress an event's retrieval weight by a multiplier. Distillation
    /// will route demoted events to <see cref="LookupHints"/> rather than
    /// main sections, unless the consumer explicitly queries for them.
    /// </summary>
    /// <param name="target">Event to demote.</param>
    /// <param name="weightMultiplier">Retrieval-weight multiplier (typical: 0.3; must be in (0.0, 1.0) to be a meaningful demotion).</param>
    /// <param name="cap">Curation capability — must have <see cref="CurationCapability.CanDemote"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result with the new event id.</returns>
    /// <exception cref="CapabilityDeniedError">If <paramref name="cap"/> does not have <see cref="CurationCapability.CanDemote"/>.</exception>
    Task<CurationResult> DemoteAsync(
        EventId target,
        float weightMultiplier,
        CurationCapability cap,
        CancellationToken ct = default);

    /// <summary>
    /// Break an over-aggregated fact into N separate facts. The source fact
    /// is marked superseded; the N parts inherit the source's <c>valid_at</c>
    /// (bi-temporal honesty: we now know the source was actually N separate
    /// claims as of that observation).
    /// </summary>
    /// <param name="source">The fact to split.</param>
    /// <param name="parts">The replacement parts.</param>
    /// <param name="preStateHash">Hash of the source fact's current canonical state.</param>
    /// <param name="cap">Curation capability — must have <see cref="CurationCapability.CanSplit"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result with the new event id.</returns>
    /// <exception cref="CapabilityDeniedError">If <paramref name="cap"/> does not have <see cref="CurationCapability.CanSplit"/>.</exception>
    /// <exception cref="StaleProposalError">If <paramref name="preStateHash"/> does not match the current state.</exception>
    /// <exception cref="ArgumentException">If <paramref name="parts"/> is empty or has fewer than two parts.</exception>
    Task<CurationResult> SplitFactAsync(
        FactId source,
        IReadOnlyList<FactSplitPart> parts,
        string preStateHash,
        CurationCapability cap,
        CancellationToken ct = default);

    /// <summary>
    /// Combine N facts that say the same thing into one merged fact. The
    /// source facts are marked superseded; the merged fact's <c>valid_at</c>
    /// is the earliest <c>valid_at</c> across the sources.
    /// </summary>
    /// <param name="sources">The facts to merge.</param>
    /// <param name="target">The merged fact content.</param>
    /// <param name="preStateHash">Hash spanning all <paramref name="sources"/>' current canonical state.</param>
    /// <param name="cap">Curation capability — must have <see cref="CurationCapability.CanMerge"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result with the new event id.</returns>
    /// <exception cref="CapabilityDeniedError">If <paramref name="cap"/> does not have <see cref="CurationCapability.CanMerge"/>.</exception>
    /// <exception cref="StaleProposalError">If <paramref name="preStateHash"/> does not match the current spanning state.</exception>
    /// <exception cref="ArgumentException">If <paramref name="sources"/> has fewer than two entries.</exception>
    Task<CurationResult> MergeFactsAsync(
        IReadOnlyList<FactId> sources,
        FactMerged target,
        string preStateHash,
        CurationCapability cap,
        CancellationToken ct = default);

    /// <summary>
    /// Reverse a prior curation by appending a <c>curation.reverted</c>
    /// event. The projector inverts the original effect (restores the prior
    /// content / weight / fact rows). Reverting an already-reverted event
    /// is not supported; to re-curate, issue a fresh curation instead.
    /// </summary>
    /// <param name="curationEventId">The id of the curation event to reverse.</param>
    /// <param name="reason">Why the curator is reverting.</param>
    /// <param name="cap">Curation capability — must have <see cref="CurationCapability.CanRevert"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result with the new event id.</returns>
    /// <exception cref="CapabilityDeniedError">If <paramref name="cap"/> does not have <see cref="CurationCapability.CanRevert"/>.</exception>
    /// <exception cref="InvalidOperationException">If <paramref name="curationEventId"/> refers to a <c>curation.reverted</c> event itself.</exception>
    Task<CurationResult> RevertCurationAsync(
        EventId curationEventId,
        string reason,
        CurationCapability cap,
        CancellationToken ct = default);
}
