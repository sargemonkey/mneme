namespace Mneme.Ingest.Redaction;

/// <summary>
/// Inline secret redaction at ingest. Replaces matched patterns with a
/// structure-preserving marker (e.g., <c>&lt;REDACTED:aws-key&gt;</c>) so
/// downstream consumers can see *that* a secret was present without ever
/// seeing the value. Non-bypassable: there is no API path for raw bodies
/// to reach the WAL — the redactor sits in the sync ingest stage.
/// </summary>
public interface IRedactor
{
    /// <summary>
    /// Redact secrets out of <paramref name="content"/>. Returns the
    /// redacted text and a list of one <see cref="RedactionHit"/> per
    /// match (rule name + character range in the *original* input).
    /// </summary>
    RedactionResult Redact(string content);
}

/// <summary>The outcome of a single <see cref="IRedactor.Redact"/> call.</summary>
/// <param name="RedactedContent">Content with every match replaced by its marker.</param>
/// <param name="Hits">One entry per matched pattern, in input order. Empty when no secrets were found.</param>
public sealed record RedactionResult(string RedactedContent, IReadOnlyList<RedactionHit> Hits)
{
    /// <summary>True when at least one pattern matched.</summary>
    public bool HadHits => Hits.Count > 0;
}

/// <summary>A single pattern match — what rule fired and where in the original input.</summary>
/// <param name="RuleName">Stable name of the matched rule (e.g., <c>aws-access-key</c>).</param>
/// <param name="Marker">The marker that replaced the match (e.g., <c>&lt;REDACTED:aws-access-key&gt;</c>).</param>
/// <param name="StartIndex">Inclusive start index of the match in the original input.</param>
/// <param name="Length">Length of the match in the original input.</param>
public sealed record RedactionHit(string RuleName, string Marker, int StartIndex, int Length);
