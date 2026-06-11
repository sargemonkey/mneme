namespace Mneme.Contracts;

/// <summary>
/// Curation authorization. Required on every <see cref="IMemoryCurator"/> call
/// and every mutating <see cref="IReviewQueue"/> call. <strong>Deliberately a
/// separate token type from <see cref="CapabilityToken"/></strong> so a
/// principal with ingest+read rights does not automatically get
/// curation rights — curation is the privileged path that can mutate the
/// effective state of memory.
/// </summary>
/// <remarks>
/// Per-operation flags follow the principle of least authority: a curator
/// who is only supposed to pin / demote (e.g., a reviewer role) should not
/// have <see cref="CanAmend"/>, <see cref="CanSplit"/>, or <see cref="CanMerge"/>.
/// See <c>plans/plan.md</c> §"Human-in-the-loop curation".
/// </remarks>
/// <param name="Principal">Who is performing the curation. Recorded in every emitted curation event.</param>
/// <param name="Workstream">Workstream scope, or <c>null</c> for instance-wide curation rights.</param>
/// <param name="NotBefore">Earliest moment the token is valid.</param>
/// <param name="NotAfter">Latest moment the token is valid.</param>
/// <param name="CanAmend">May call <see cref="IMemoryCurator.AmendFactAsync"/>.</param>
/// <param name="CanAnnotate">May call <see cref="IMemoryCurator.AnnotateAsync"/>.</param>
/// <param name="CanPin">May call <see cref="IMemoryCurator.PinAsync"/>.</param>
/// <param name="CanDemote">May call <see cref="IMemoryCurator.DemoteAsync"/>.</param>
/// <param name="CanSplit">May call <see cref="IMemoryCurator.SplitFactAsync"/>.</param>
/// <param name="CanMerge">May call <see cref="IMemoryCurator.MergeFactsAsync"/>.</param>
/// <param name="CanRevert">May call <see cref="IMemoryCurator.RevertCurationAsync"/>.</param>
/// <param name="CanReview">May call <see cref="IReviewQueue.ApproveAsync"/> / <see cref="IReviewQueue.RejectAsync"/> / <see cref="IReviewQueue.DeferAsync"/>.</param>
/// <param name="Signature">Optional opaque signature for transport / forwarded scenarios.</param>
public sealed record CurationCapability(
    PrincipalId Principal,
    WorkstreamId? Workstream,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool CanAmend = false,
    bool CanAnnotate = false,
    bool CanPin = false,
    bool CanDemote = false,
    bool CanSplit = false,
    bool CanMerge = false,
    bool CanRevert = false,
    bool CanReview = false,
    string? Signature = null)
{
    /// <summary>True if the token's validity window contains <paramref name="instant"/>.</summary>
    public bool IsValidAt(DateTimeOffset instant) => instant >= NotBefore && instant <= NotAfter;
}
