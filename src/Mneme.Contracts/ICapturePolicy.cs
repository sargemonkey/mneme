namespace Mneme.Contracts;

/// <summary>
/// One side of a conversational exchange the host wants Mneme to consider
/// for capture. Hosts construct these from their chat client / runtime and
/// hand them to a <c>Mneme.Capture.CaptureSession</c> which runs them
/// through an <see cref="ICapturePolicy"/>.
/// </summary>
/// <param name="Speaker">Who said it (user, agent, system).</param>
/// <param name="Content">The full text of the turn.</param>
/// <param name="At">When the turn happened.</param>
/// <param name="SessionId">Free-text correlator (session / thread id) used downstream for provenance.</param>
public sealed record ConversationTurn(
    PrincipalId Speaker,
    string Content,
    DateTimeOffset At,
    string? SessionId = null);

/// <summary>
/// Host-supplied capture policy: given a single conversational turn, returns
/// zero or more <see cref="CaptureCandidate"/>s worth committing to memory.
/// Symmetric to <see cref="IDistiller"/> — the SDK never decides what's worth
/// remembering, the host does. Implementations may use any LLM, heuristic,
/// regex, or human-in-the-loop signal; Mneme has no opinion.
/// </summary>
/// <remarks>
/// Typical implementations:
/// <list type="bullet">
///   <item>A prompt to an LLM: "is this turn worth remembering?
///         If so, summarize it as one of the seven epistemic categories."</item>
///   <item>A regex policy that captures every user turn starting with
///         "remember:" or "note:".</item>
///   <item>A heuristic that captures every agent decision response above
///         some confidence threshold.</item>
///   <item>A no-op policy that always returns empty — when the host wants
///         to drive ingest manually via <see cref="IMemoryAgent.IngestAsync"/>
///         and only use capture-side filters/dedupe.</item>
/// </list>
/// </remarks>
public interface ICapturePolicy
{
    /// <summary>Stable identifier (e.g., <c>"openai/gpt-4o-mini-capture@2026-06"</c>). Stamped on every produced event's provenance.</summary>
    string Id { get; }

    /// <summary>Evaluate one turn. Return an empty list to skip it.</summary>
    Task<IReadOnlyList<CaptureCandidate>> EvaluateAsync(
        ConversationTurn turn,
        WorkstreamId workstream,
        CancellationToken ct = default);
}

/// <summary>
/// One unit a policy thinks is worth remembering. The SDK turns each
/// candidate into a <see cref="CaptureEvent"/> with appropriate provenance
/// and pushes it through <see cref="IMemoryAgent.IngestAsync"/> — the same
/// validation, redaction, classification, and idempotency apply.
/// </summary>
/// <param name="Content">The text to remember (will be redacted at ingest).</param>
/// <param name="Category">Epistemic category this candidate belongs to.</param>
/// <param name="ValidAt">When the claim was true. Defaults to the turn's timestamp at session-pump time.</param>
/// <param name="EventId">Optional caller-supplied id for idempotency. SDK auto-generates if omitted.</param>
/// <param name="Rationale">Why the policy chose to capture this. Surfaced in event provenance for audit.</param>
/// <param name="Confidence">0..1 — policy's self-assessed confidence. Filters may drop low-confidence candidates.</param>
public sealed record CaptureCandidate(
    string Content,
    EpistemicCategory Category,
    DateTimeOffset? ValidAt = null,
    EventId? EventId = null,
    string? Rationale = null,
    double Confidence = 1.0);

/// <summary>
/// Optional middleware that runs after a policy and before ingest. Filters
/// can dedupe against recent events, gate by confidence threshold, throttle,
/// or transform candidates (e.g., re-classify). Hosts compose any number of
/// filters; the SDK ships a small <c>Mneme.Capture.RecentDuplicateFilter</c>
/// as a useful default.
/// </summary>
public interface ICaptureFilter
{
    /// <summary>Apply this filter. Return a (possibly-empty, possibly-transformed) list.</summary>
    Task<IReadOnlyList<CaptureCandidate>> FilterAsync(
        IReadOnlyList<CaptureCandidate> candidates,
        WorkstreamId workstream,
        CancellationToken ct = default);
}
