using Mneme.Contracts;

namespace Mneme.Writer;

/// <summary>
/// An <b>open commitment</b> — a setup the manuscript established but has not yet
/// paid off (an unpaid promise / dangling setup). Surfaced by
/// <see cref="IWriterMemory.GetOpenCommitmentsAsync"/> for the author to resolve
/// or deliberately drop; never auto-closed.
/// </summary>
/// <param name="CommitmentId">Stable slug of the promise (e.g. <c>commitment:chekhovs-gun</c>).</param>
/// <param name="Kind">Sub-domain flavour (<c>foreshadow</c> / <c>promise</c> / <c>hook</c> / <c>contribution</c>).</param>
/// <param name="Description">Human-readable statement of the promise.</param>
/// <param name="SetupEvent">The event that established the setup.</param>
/// <param name="ScenePath">Manuscript passage that established it, if recorded.</param>
/// <param name="Quote">Verbatim anchor for that passage, if recorded.</param>
/// <param name="StoryTime">When the setup happens in the narrated world (the event's <c>valid_at</c>).</param>
/// <param name="RevealTime">When the draft/reader first got the setup (the event's <c>created_at</c>).</param>
public sealed record NarrativeCommitment(
    string CommitmentId,
    string Kind,
    string Description,
    EventId SetupEvent,
    string? ScenePath,
    string? Quote,
    DateTimeOffset StoryTime,
    DateTimeOffset RevealTime);
