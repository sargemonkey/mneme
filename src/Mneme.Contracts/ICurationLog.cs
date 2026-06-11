namespace Mneme.Contracts;

/// <summary>
/// Read-only access to the <c>curation_log</c> projection — answers "who
/// curated what, when, with what rationale". Gives Mneme GDPR Article 30
/// (records of processing) compliance as a fall-out of the append-only
/// design.
/// </summary>
public interface ICurationLog
{
    /// <summary>
    /// Get all curations targeting events in the given workstream since the
    /// given instant.
    /// </summary>
    /// <param name="workstream">Workstream to scope to. <c>null</c> = instance-wide (requires <see cref="CapabilityToken.CrossWorkstream"/>).</param>
    /// <param name="since">Earliest <see cref="CurationEntry.OccurredAt"/> to include.</param>
    /// <param name="token">Capability token authorizing the call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Curation entries ordered newest-first.</returns>
    /// <exception cref="CapabilityDeniedError">If the token does not authorize the workstream (or cross-workstream when <paramref name="workstream"/> is null).</exception>
    Task<IReadOnlyList<CurationEntry>> GetCurationHistoryAsync(
        WorkstreamId? workstream,
        DateTimeOffset since,
        CapabilityToken token,
        CancellationToken ct = default);

    /// <summary>
    /// Get all curations performed by the given principal since the given
    /// instant.
    /// </summary>
    /// <param name="curator">Principal whose curations to list.</param>
    /// <param name="since">Earliest <see cref="CurationEntry.OccurredAt"/> to include.</param>
    /// <param name="token">Capability token authorizing the call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Curation entries ordered newest-first.</returns>
    /// <exception cref="CapabilityDeniedError">If the token does not authorize the resulting set's workstreams.</exception>
    Task<IReadOnlyList<CurationEntry>> GetCurationsByPrincipalAsync(
        PrincipalId curator,
        DateTimeOffset since,
        CapabilityToken token,
        CancellationToken ct = default);
}
