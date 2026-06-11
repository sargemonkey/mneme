using Mneme.Contracts;

namespace Mneme.Ingest;

/// <summary>
/// Chooses the <see cref="ContentShape"/> for each ingested event. The
/// Phase 1 default (<see cref="AlwaysRedactedContent"/>) always picks
/// <see cref="ContentShape.RedactedContent"/>; a richer implementation
/// arrives with the synopsis pipeline in a later phase.
/// </summary>
public interface IContentShapeSelector
{
    /// <summary>
    /// Choose a shape for <paramref name="evt"/>. May inspect any field
    /// of the event including the payload size.
    /// </summary>
    ContentShape Select(CaptureEvent evt);
}

/// <summary>
/// Phase 1 default — always returns <see cref="ContentShape.RedactedContent"/>.
/// </summary>
public sealed class AlwaysRedactedContent : IContentShapeSelector
{
    /// <inheritdoc/>
    public ContentShape Select(CaptureEvent evt) => ContentShape.RedactedContent;
}
