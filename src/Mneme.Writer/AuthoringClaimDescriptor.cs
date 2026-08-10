using Mneme.Contracts;
using Mneme.Hosting.Profiles;
using Mneme.Ingest.Redaction;

namespace Mneme.Writer;

/// <summary>
/// Self-describing metadata for <see cref="AuthoringClaimPayload"/> so base
/// Mneme can (de)serialize, redact, full-text index, and summarize the writing
/// domain's payload without a compile-time <c>switch</c> in the base
/// (ADR-0005 / Phase 15.A). Registered by <see cref="WriterProfile"/>.
/// </summary>
public sealed class AuthoringClaimDescriptor : IPayloadDescriptor
{
    /// <inheritdoc/>
    public Type PayloadType => typeof(AuthoringClaimPayload);

    /// <inheritdoc/>
    public string Discriminator => "AuthoringClaimPayload";

    /// <inheritdoc/>
    public (EventPayload Payload, bool HadHits, int HitCount) Redact(EventPayload payload, IRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        var p = (AuthoringClaimPayload)payload;

        // Cover every human-authored free-text field (locked decision #11). The
        // structural fields (Status, ThreadId, Grounding) are controlled
        // vocabulary / identifiers, not free text, so they are left intact.
        var text = redactor.Redact(p.Text);
        var subject = redactor.Redact(p.Subject);
        var attribute = redactor.Redact(p.Attribute);
        var value = redactor.Redact(p.Value);
        var had = text.HadHits || subject.HadHits || attribute.HadHits || value.HadHits;
        var hits = text.Hits.Count + subject.Hits.Count + attribute.Hits.Count + value.Hits.Count;

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

        string? sourceRef = p.SourceRef;
        if (sourceRef is not null)
        {
            var r = redactor.Redact(sourceRef);
            sourceRef = r.RedactedContent;
            had |= r.HadHits;
            hits += r.Hits.Count;
        }

        return (p with
        {
            Text = text.RedactedContent,
            Subject = subject.RedactedContent,
            Attribute = attribute.RedactedContent,
            Value = value.RedactedContent,
            ScenePath = scenePath,
            Quote = quote,
            SourceRef = sourceRef,
        }, had, hits);
    }

    /// <inheritdoc/>
    public string ExtractText(EventPayload payload)
    {
        var p = (AuthoringClaimPayload)payload;
        var parts = new[] { p.Text, p.Subject, p.Attribute, p.Value, p.Quote ?? string.Empty };
        return string.Join(' ', parts.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <inheritdoc/>
    public string Summarize(EventPayload payload)
    {
        var p = (AuthoringClaimPayload)payload;
        return $"{p.Subject} — {p.Attribute}: {p.Value}";
    }
}
