using Mneme.Contracts;

namespace Mneme.Revocation;

/// <summary>
/// Reversible deletion of an event's body. The append-only
/// <c>memory_events</c> table is never touched — the event row, its
/// metadata, its provenance, and its bi-temporal stamps all stay intact.
/// What changes:
/// <list type="bullet">
///   <item>A row appears in the sidecar <c>memory_revocations</c> table
///         capturing who revoked, when, and why.</item>
///   <item>If the event has an associated artifact body
///         (<see cref="Ingest.ContentShape.ReferenceWithSynopsis"/>) the
///         body in <c>memory_artifacts</c> is zeroed and stamped with
///         the revocation timestamp + reason.</item>
/// </list>
/// </summary>
/// <remarks>
/// Revocation is idempotent on <c>event_id</c>: revoking the same event
/// twice returns <see cref="RevocationResult.AlreadyRevoked"/> on the
/// second call and the original revocation metadata wins.
/// </remarks>
public interface IRevocationService
{
    /// <summary>Revoke a single event by id, recording the principal and reason.</summary>
    Task<RevocationResult> RevokeAsync(
        EventId eventId,
        WorkstreamId workstreamId,
        PrincipalId revokedBy,
        string reason,
        CancellationToken ct = default);

    /// <summary>True if <paramref name="eventId"/> has a revocation record.</summary>
    Task<bool> IsRevokedAsync(EventId eventId, CancellationToken ct = default);
}

/// <summary>The outcome of a <see cref="IRevocationService.RevokeAsync"/> call.</summary>
/// <param name="EventId">The event that was revoked.</param>
/// <param name="RevokedAt">When the revocation was recorded (UTC).</param>
/// <param name="AlreadyRevoked">True if the event was already revoked before this call.</param>
/// <param name="BodyZeroed">True if an associated artifact body was zeroed out by this call.</param>
public sealed record RevocationResult(
    EventId EventId,
    DateTimeOffset RevokedAt,
    bool AlreadyRevoked,
    bool BodyZeroed);
