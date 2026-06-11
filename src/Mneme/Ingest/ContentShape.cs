namespace Mneme.Ingest;

/// <summary>
/// How the body of an event is stored. Decided at ingest time by the
/// configured <see cref="IContentShapeSelector"/> based on a quality
/// envelope (length, sensitivity, source kind).
/// </summary>
public enum ContentShape
{
    /// <summary>The full content (post-redaction) is stored verbatim.</summary>
    RedactedContent = 0,

    /// <summary>
    /// Only a source pointer and a sanitized synopsis is stored;
    /// the original body lives somewhere external (file URI, message-id,
    /// etc.). Used for very large or extra-sensitive bodies.
    /// </summary>
    ReferenceWithSynopsis = 1,
}
