using Mneme.Contracts;

namespace Mneme.Writer;

/// <summary>
/// The writing domain's read surface over Mneme. Two thin-slice reads:
/// <see cref="GetContinuityConflictsAsync"/> — the deterministic manuscript
/// continuity check that falls out of projecting authoring claims into the
/// shared canon — and <see cref="GetOpenCommitmentsAsync"/> — the unpaid-promise
/// / dangling-setup ledger (ADR-0005 / Phase 15.B). Later phases add
/// character-dossier and as-of-section queries here.
/// </summary>
/// <remarks>
/// Capability-guarded like every Mneme read (locked decision #8): the token
/// scopes the caller to a workstream and its principal is the viewer used to
/// enforce per-event visibility.
/// </remarks>
public interface IWriterMemory
{
    /// <summary>
    /// List open continuity conflicts in the token's workstream: pairs of
    /// currently-valid claims that share a subject + attribute but assert a
    /// different value. A conflict is returned only if the token's principal is
    /// authorized to see <b>both</b> sides (neither is another author's Private
    /// event).
    /// </summary>
    /// <param name="token">Capability token; must be valid, workstream-scoped, and allow the Fact category.</param>
    /// <param name="limit">Maximum conflicts to return (most recently detected first).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="CapabilityDeniedError">If the token is expired, not workstream-scoped, or does not allow the Fact category.</exception>
    Task<IReadOnlyList<WriterContinuityConflict>> GetContinuityConflictsAsync(
        CapabilityToken token,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// List open commitments in the token's workstream: setups that have no
    /// payoff sharing their commitment id — the manuscript's unpaid promises and
    /// dangling setups, ordered earliest-in-story first. A payoff only discharges
    /// a promise if the viewer is authorized to see it, so a setup the viewer can
    /// see is reported open unless a viewer-visible payoff exists (a hidden
    /// payoff never silently closes a promise, and never leaks its existence).
    /// </summary>
    /// <param name="token">Capability token; must be valid, workstream-scoped, and allow the Goal category.</param>
    /// <param name="limit">Maximum open commitments to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="CapabilityDeniedError">If the token is expired, not workstream-scoped, or does not allow the Goal category.</exception>
    Task<IReadOnlyList<NarrativeCommitment>> GetOpenCommitmentsAsync(
        CapabilityToken token,
        int limit = 100,
        CancellationToken ct = default);
}
