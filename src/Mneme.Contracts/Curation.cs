namespace Mneme.Contracts;

/// <summary>
/// Result of a successful <see cref="IMemoryCurator"/> operation. Lets the
/// caller correlate the curation back to the resulting event in the log
/// (e.g., to subscribe to projection update notifications).
/// </summary>
/// <param name="CurationEventId">Id of the emitted curation event (e.g., <c>fact.amended</c>).</param>
/// <param name="RecordedAt">When the curation event was committed to the WAL.</param>
/// <param name="PreStateHash">The pre-state hash that was matched (for amend / split / merge); empty otherwise.</param>
public sealed record CurationResult(
    EventId CurationEventId,
    DateTimeOffset RecordedAt,
    string PreStateHash);

/// <summary>
/// Payload for <see cref="IMemoryCurator.AmendFactAsync"/>.
/// </summary>
/// <param name="NewContent">The corrected fact statement.</param>
/// <param name="Rationale">Why the curator is amending. Stored verbatim in the curation event for audit.</param>
/// <param name="ValidAt">Override <c>valid_at</c> for the amended fact. <c>null</c> = inherit source's <c>valid_at</c>, preserving bi-temporal honesty.</param>
public sealed record FactAmendment(
    string NewContent,
    string Rationale,
    DateTimeOffset? ValidAt = null);

/// <summary>
/// One part of a <see cref="IMemoryCurator.SplitFactAsync"/> operation.
/// </summary>
/// <param name="Content">The content of this split part.</param>
/// <param name="Category">The epistemic category of this part. Often <see cref="EpistemicCategory.Fact"/> but can be any.</param>
/// <param name="ValidAt">Override <c>valid_at</c> for this part. <c>null</c> = inherit source's <c>valid_at</c>.</param>
public sealed record FactSplitPart(
    string Content,
    EpistemicCategory Category,
    DateTimeOffset? ValidAt = null);

/// <summary>
/// Target fact for a <see cref="IMemoryCurator.MergeFactsAsync"/> operation.
/// </summary>
/// <param name="Content">The merged fact content.</param>
/// <param name="Category">The epistemic category of the merged fact.</param>
/// <param name="ValidAt">When the merged fact was true. Convention: earliest <c>valid_at</c> across the source facts.</param>
public sealed record FactMerged(
    string Content,
    EpistemicCategory Category,
    DateTimeOffset ValidAt);

/// <summary>
/// A row in the <c>curation_log</c> projection, returned by
/// <see cref="ICurationLog"/>. Answers "who curated what, when, with what
/// rationale".
/// </summary>
/// <param name="CurationEventId">Id of the curation event itself.</param>
/// <param name="Curator">Principal who performed the curation.</param>
/// <param name="TargetEventId">Id of the event the curation targets (the fact being amended / pinned / split / etc.).</param>
/// <param name="Type">Which kind of curation this was.</param>
/// <param name="Rationale">The curator's stated reason. Verbatim from the curation event.</param>
/// <param name="OccurredAt">When the curation was performed.</param>
/// <param name="PreStateHash">For amend / split / merge: the pre-state hash that was matched. Empty otherwise.</param>
/// <param name="Workstream">Workstream the target event belongs to. Useful for filtering.</param>
public sealed record CurationEntry(
    EventId CurationEventId,
    PrincipalId Curator,
    EventId TargetEventId,
    CurationType Type,
    string Rationale,
    DateTimeOffset OccurredAt,
    string PreStateHash,
    WorkstreamId Workstream);

/// <summary>
/// A pending item in the pre-distillation review queue for a workstream
/// running in <see cref="WorkstreamMode.ReviewBeforeDistill"/>. The
/// distillation worker skips these until a curator approves them.
/// </summary>
/// <param name="EventId">Id of the captured but un-distilled event.</param>
/// <param name="Workstream">Workstream this event belongs to.</param>
/// <param name="CapturedAt">When the event entered the queue.</param>
/// <param name="Summary">Short human-readable description so reviewers can triage without loading the full event.</param>
public sealed record PendingReviewItem(
    EventId EventId,
    WorkstreamId Workstream,
    DateTimeOffset CapturedAt,
    string Summary);
