namespace Mneme.Contracts;

/// <summary>
/// The read side of Mneme. All queries are capability-checked; there is no
/// raw-SQL escape on this interface, by design.
/// </summary>
/// <remarks>
/// Capability check semantics:
/// <list type="bullet">
///   <item>Every method requires a <see cref="CapabilityToken"/>.</item>
///   <item>The token must be valid at call time (<see cref="CapabilityToken.IsValidAt"/>).</item>
///   <item>The query's workstream must match the token's workstream, unless the token has <see cref="CapabilityToken.CrossWorkstream"/> = <c>true</c> AND <see cref="CapabilityToken.Workstream"/> = <c>null</c>.</item>
///   <item>Only <see cref="EventChannel.Epistemic"/> events are returned unless the token has <see cref="CapabilityToken.IncludeTechnical"/> = <c>true</c>.</item>
///   <item>Categories returned are the intersection of the request's <see cref="QuerySpec.Categories"/> (or all if empty) and the token's <see cref="CapabilityToken.AllowedCategories"/>.</item>
/// </list>
/// </remarks>
public interface IMemoryQueryAPI
{
    /// <summary>
    /// Run a filtered / ranked query against the event log.
    /// </summary>
    /// <param name="request">Query parameters + execution flags.</param>
    /// <param name="token">Capability token authorizing the call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching events, ranked by score (highest first).</returns>
    /// <exception cref="CapabilityDeniedError">If the token does not authorize the requested workstream, category, or channel.</exception>
    Task<QueryResult> QueryAsync(
        QueryRequest request,
        CapabilityToken token,
        CancellationToken ct = default);

    /// <summary>
    /// Get the distilled <see cref="ContextBundle"/> for a workstream. May
    /// return a cached bundle; the result's <see cref="ContextBundle.IsStale"/>
    /// indicates whether events have been ingested since the bundle was
    /// synthesized.
    /// </summary>
    /// <param name="workstream">Workstream to distill.</param>
    /// <param name="options">Distillation options (e.g., <see cref="DistillOptions.ForceRefresh"/>).</param>
    /// <param name="token">Capability token authorizing the call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The bundle for the workstream.</returns>
    /// <exception cref="CapabilityDeniedError">If the token does not authorize the workstream.</exception>
    Task<ContextBundle> DistillAsync(
        WorkstreamId workstream,
        DistillOptions options,
        CapabilityToken token,
        CancellationToken ct = default);

    /// <summary>
    /// Enumerate the most-recently-recorded events in a workstream. Useful
    /// for agents to avoid re-ingesting what they have already stored, and
    /// for debugging.
    /// </summary>
    /// <param name="workstream">Workstream to list from.</param>
    /// <param name="limit">Maximum number of items to return. Hard upper bound enforced by the agent.</param>
    /// <param name="token">Capability token authorizing the call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The most recent events, newest first.</returns>
    /// <exception cref="CapabilityDeniedError">If the token does not authorize the workstream.</exception>
    Task<IReadOnlyList<QueryResultItem>> ListRecentAsync(
        WorkstreamId workstream,
        int limit,
        CapabilityToken token,
        CancellationToken ct = default);
}
