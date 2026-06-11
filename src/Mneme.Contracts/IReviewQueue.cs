namespace Mneme.Contracts;

/// <summary>
/// Pre-distillation review queue for workstreams running in
/// <see cref="WorkstreamMode.ReviewBeforeDistill"/>. The distillation worker
/// skips events flagged pending review until a curator calls
/// <see cref="ApproveAsync"/>; rejected events are tombstoned.
/// </summary>
/// <remarks>
/// Default workstream mode is <see cref="WorkstreamMode.AutoDistill"/> —
/// this interface is only relevant for sensitive workstreams that opt in.
/// </remarks>
public interface IReviewQueue
{
    /// <summary>
    /// List events currently pending review in the given workstream.
    /// </summary>
    /// <param name="workstream">Workstream to inspect.</param>
    /// <param name="cap">Curation capability — must have <see cref="CurationCapability.CanReview"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Pending items in oldest-first order.</returns>
    /// <exception cref="CapabilityDeniedError">If <paramref name="cap"/> does not have <see cref="CurationCapability.CanReview"/>.</exception>
    Task<IReadOnlyList<PendingReviewItem>> GetPendingAsync(
        WorkstreamId workstream,
        CurationCapability cap,
        CancellationToken ct = default);

    /// <summary>
    /// Approve a pending event so the distillation worker can process it.
    /// Appends an <c>event.review_approved</c> technical event so the
    /// reviewer's go-ahead is part of the audit trail.
    /// </summary>
    /// <param name="pending">Event id to approve.</param>
    /// <param name="cap">Curation capability — must have <see cref="CurationCapability.CanReview"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="CapabilityDeniedError">If <paramref name="cap"/> does not have <see cref="CurationCapability.CanReview"/>.</exception>
    Task ApproveAsync(
        EventId pending,
        CurationCapability cap,
        CancellationToken ct = default);

    /// <summary>
    /// Reject a pending event. Appends an <c>event.review_rejected</c>
    /// technical event and tombstones the source.
    /// </summary>
    /// <param name="pending">Event id to reject.</param>
    /// <param name="reason">Why the reviewer rejected. Stored verbatim in the technical event.</param>
    /// <param name="cap">Curation capability — must have <see cref="CurationCapability.CanReview"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="CapabilityDeniedError">If <paramref name="cap"/> does not have <see cref="CurationCapability.CanReview"/>.</exception>
    Task RejectAsync(
        EventId pending,
        string reason,
        CurationCapability cap,
        CancellationToken ct = default);

    /// <summary>
    /// Defer review of a pending event until <paramref name="until"/>. The
    /// event remains in the queue but is hidden from <see cref="GetPendingAsync"/>
    /// until that instant.
    /// </summary>
    /// <param name="pending">Event id to defer.</param>
    /// <param name="until">When the event should re-appear in the queue.</param>
    /// <param name="cap">Curation capability — must have <see cref="CurationCapability.CanReview"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="CapabilityDeniedError">If <paramref name="cap"/> does not have <see cref="CurationCapability.CanReview"/>.</exception>
    Task DeferAsync(
        EventId pending,
        DateTimeOffset until,
        CurationCapability cap,
        CancellationToken ct = default);
}
