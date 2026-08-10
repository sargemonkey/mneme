using Mneme.Contracts;
using Mneme.Ingest.Redaction;

namespace Mneme.Hosting.Profiles;

/// <summary>
/// Self-describing metadata for a <em>satellite</em> (domain-profile)
/// <see cref="EventPayload"/> type. A profile registers one descriptor per
/// payload it introduces (via <c>AddMnemeProfile</c>) so base Mneme can
/// (de)serialize it, redact it, index it for full-text search, and summarize
/// it — <b>without</b> a compile-time <c>switch</c> in the base.
/// </summary>
/// <remarks>
/// The eight built-in payloads stay attribute-declared on
/// <see cref="EventPayload"/> and switch-handled in the base; descriptors
/// <b>extend</b> that closed set with additional, trusted, app-chosen profile
/// types. This preserves the closed-set deserialization property (ADR-0005):
/// only explicitly-registered, compiled types resolve — an unknown
/// <c>$type</c> is still rejected.
/// </remarks>
public interface IPayloadDescriptor
{
    /// <summary>The concrete payload record type (must derive from <see cref="EventPayload"/>).</summary>
    Type PayloadType { get; }

    /// <summary>Stable <c>$type</c> discriminator written into <c>payload_json</c>. Never reuse across types.</summary>
    string Discriminator { get; }

    /// <summary>
    /// Redact every free-text field the payload can hold and return the new
    /// payload value plus hit info. MUST cover all human-authored strings —
    /// this is the payload's contribution to inline secret redaction
    /// (locked decision #11). Records are immutable, so return a new value.
    /// </summary>
    (EventPayload Payload, bool HadHits, int HitCount) Redact(EventPayload payload, IRedactor redactor);

    /// <summary>Concatenated free text of the payload, for the FTS index.</summary>
    string ExtractText(EventPayload payload);

    /// <summary>One-line human-readable summary (for prompts / query results).</summary>
    string Summarize(EventPayload payload);
}
