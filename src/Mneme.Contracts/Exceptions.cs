namespace Mneme.Contracts;

/// <summary>
/// Thrown when a curation or entity-merge confirmation API was given a
/// pre-state hash that does not match the target's current canonical state.
/// Prevents a curator from confirming a change against state that has been
/// advanced by a concurrent curator in the meantime.
/// </summary>
/// <remarks>
/// Pattern from Letta <c>core_memory_replace</c> (<c>base.py:262-280</c>):
/// every mutation cites the pre-mutation content; mismatch fails the call
/// rather than overwriting. See <c>plans/plan.md</c> §"Human-in-the-loop
/// curation" → "Stale-state guard".
/// </remarks>
public sealed class StaleProposalError : InvalidOperationException
{
    /// <summary>The id of the event/fact the curation was targeting.</summary>
    public EventId Target { get; }

    /// <summary>Hash the curator believed to be the target's current state.</summary>
    public string ExpectedHash { get; }

    /// <summary>Hash actually observed at the moment of validation.</summary>
    public string ActualHash { get; }

    /// <summary>Create a stale-proposal error for the given target.</summary>
    public StaleProposalError(EventId target, string expectedHash, string actualHash)
        : base($"Stale state guard: target '{target}' expected pre-state hash '{expectedHash}' but found '{actualHash}'. Re-read the target and retry.")
    {
        Target = target;
        ExpectedHash = expectedHash ?? throw new ArgumentNullException(nameof(expectedHash));
        ActualHash = actualHash ?? throw new ArgumentNullException(nameof(actualHash));
    }
}

/// <summary>
/// Thrown when a <see cref="CapabilityToken"/> or <see cref="CurationCapability"/>
/// does not grant the requested operation. Always raised by the read/write
/// path itself — never by callers — so the privilege boundary is enforced
/// at one layer.
/// </summary>
public sealed class CapabilityDeniedError : UnauthorizedAccessException
{
    /// <summary>Free-text reason describing the missing capability (e.g., "CanAmend not granted").</summary>
    public string Reason { get; }

    /// <summary>Create a capability-denied error with a human-readable reason.</summary>
    public CapabilityDeniedError(string reason)
        : base($"Capability denied: {reason}.")
    {
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
    }

    /// <summary>Create a capability-denied error with a reason and inner exception.</summary>
    public CapabilityDeniedError(string reason, Exception innerException)
        : base($"Capability denied: {reason}.", innerException)
    {
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
    }
}
