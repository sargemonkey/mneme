using Mneme.Contracts;

namespace Mneme.Classification;

using ClassificationLabel = Mneme.Contracts.Classification;

/// <summary>
/// Runs in the sync ingest stage to attach a <see cref="ClassificationLabel"/>
/// label to every event. Labels are <strong>metadata-only</strong>: they
/// never block capture and they never gate the redactor (the redactor
/// runs unconditionally before the classifier). A label of
/// <see cref="ClassificationLabel.Secret"/> at this point means
/// "the redactor matched at least one rule on this content" or
/// "structured cues — e.g., an explicit '[secret]' tag — suggest
/// sensitivity"; it does <em>not</em> mean a real secret is still in
/// the body.
/// </summary>
public interface IClassifier
{
    /// <summary>Classify the (already-redacted) text of an event.</summary>
    /// <param name="content">Free-text content (post-redaction).</param>
    /// <param name="hadRedactionHits">True if the redactor matched any rule.</param>
    /// <param name="category">The event's epistemic category — useful as a prior.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClassificationLabel> ClassifyAsync(
        string content,
        bool hadRedactionHits,
        EpistemicCategory category,
        CancellationToken ct = default);
}
