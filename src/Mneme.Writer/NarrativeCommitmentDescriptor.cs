using Mneme.Contracts;
using Mneme.Hosting.Profiles;
using Mneme.Ingest.Redaction;

namespace Mneme.Writer;

/// <summary>
/// Self-describing metadata for <see cref="NarrativeCommitmentPayload"/> so base
/// Mneme can (de)serialize, redact, full-text index, and summarize it without a
/// compile-time <c>switch</c> (ADR-0005 / Phase 15.A). Registered by
/// <see cref="WriterProfile"/>.
/// </summary>
public sealed class NarrativeCommitmentDescriptor : IPayloadDescriptor
{
    /// <inheritdoc/>
    public Type PayloadType => typeof(NarrativeCommitmentPayload);

    /// <inheritdoc/>
    public string Discriminator => "NarrativeCommitmentPayload";

    /// <inheritdoc/>
    public (EventPayload Payload, bool HadHits, int HitCount) Redact(EventPayload payload, IRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        var p = (NarrativeCommitmentPayload)payload;

        // Free-text fields only (locked decision #11). CommitmentId is a slug
        // identifier and Kind is controlled vocabulary — left intact, same as
        // the claim descriptor leaves ThreadId / Status.
        var description = redactor.Redact(p.Description);
        var had = description.HadHits;
        var hits = description.Hits.Count;

        string? scenePath = p.ScenePath;
        if (scenePath is not null)
        {
            var r = redactor.Redact(scenePath);
            scenePath = r.RedactedContent;
            had |= r.HadHits;
            hits += r.Hits.Count;
        }

        string? quote = p.Quote;
        if (quote is not null)
        {
            var r = redactor.Redact(quote);
            quote = r.RedactedContent;
            had |= r.HadHits;
            hits += r.Hits.Count;
        }

        return (p with
        {
            Description = description.RedactedContent,
            ScenePath = scenePath,
            Quote = quote,
        }, had, hits);
    }

    /// <inheritdoc/>
    public string ExtractText(EventPayload payload)
    {
        var p = (NarrativeCommitmentPayload)payload;
        var parts = new[] { p.CommitmentId, p.Description, p.Quote ?? string.Empty };
        return string.Join(' ', parts.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <inheritdoc/>
    public string Summarize(EventPayload payload)
    {
        var p = (NarrativeCommitmentPayload)payload;
        return $"[{p.Role}] {p.Kind}: {p.Description}";
    }
}
