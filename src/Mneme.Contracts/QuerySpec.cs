namespace Mneme.Contracts;

/// <summary>
/// Filter parameters for a memory query. Designed so the capability check
/// (which workstream? which categories? which channel?) is unambiguous from
/// the spec alone, with no implicit defaults that could leak data.
/// </summary>
/// <param name="Workstream">Workstream to query. Required unless the calling token has cross-workstream access.</param>
/// <param name="Categories">Epistemic categories to include. Empty = all the token allows.</param>
/// <param name="Channel">Event channel. Defaults to <see cref="EventChannel.Epistemic"/>.</param>
/// <param name="FreeText">Optional free-text query string for FTS5 lookup.</param>
/// <param name="Entity">Optional entity filter — only events that reference this entity.</param>
/// <param name="From">Inclusive lower bound on <see cref="CaptureEvent.ValidAt"/>.</param>
/// <param name="To">Inclusive upper bound on <see cref="CaptureEvent.ValidAt"/>.</param>
/// <param name="AsOf">Bi-temporal "as of" date. Query returns the state Mneme knew at this instant. <c>null</c> = current state.</param>
/// <param name="Limit">Maximum number of results to return. Hard upper bound enforced by the agent.</param>
/// <param name="Principal">Optional author filter — only events authored by this principal (agent/user). <c>null</c> = any author. Scopes reads to a single agent/role within a shared workstream, and backs O(index) data-subject access. See ADR-0004.</param>
public sealed record QuerySpec(
    WorkstreamId? Workstream,
    IReadOnlyCollection<EpistemicCategory>? Categories = null,
    EventChannel Channel = EventChannel.Epistemic,
    string? FreeText = null,
    EntityId? Entity = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    DateTimeOffset? AsOf = null,
    int Limit = 50,
    PrincipalId? Principal = null);

/// <summary>
/// Wraps a <see cref="QuerySpec"/> with execution-time flags (e.g., diagnostic
/// score-explanation). Separated from <see cref="QuerySpec"/> so the spec
/// can be cached / hashed independently of diagnostic options.
/// </summary>
/// <param name="Spec">The query parameters.</param>
/// <param name="Explain">If true, <see cref="QueryResultItem.Details"/> is populated with score-decomposition data. Off by default to keep responses lean.</param>
/// <param name="SupplementSubjectTriples">
/// If true, <see cref="QueryResult.SubjectTriples"/> is populated with
/// subject-scoped fact triples for the entities named in
/// <see cref="QuerySpec.FreeText"/>. These are an answer-context supplement the
/// consumer appends alongside the ranked items; the semantic result is left
/// untouched (no displacement). Off by default.
/// </param>
public sealed record QueryRequest(
    QuerySpec Spec,
    bool Explain = false,
    bool SupplementSubjectTriples = false);

/// <summary>
/// Distillation options for <see cref="IMemoryQueryAPI.DistillAsync"/>.
/// </summary>
/// <param name="ForceRefresh">If true, bypass any cached bundle and synthesize fresh. Useful after curation.</param>
/// <param name="TokenBudget">Soft token budget for the bundle total. <c>null</c> = use the agent's default.</param>
public sealed record DistillOptions(
    bool ForceRefresh = false,
    int? TokenBudget = null);
