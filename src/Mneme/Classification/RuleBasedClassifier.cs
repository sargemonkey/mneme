using System.Text.RegularExpressions;
using Mneme.Contracts;

namespace Mneme.Classification;

using ClassificationLabel = Mneme.Contracts.Classification;

/// <summary>
/// Deterministic, dependency-free <see cref="IClassifier"/> used as the
/// default whenever no LLM is configured. The rules are intentionally
/// conservative — they over-label rather than miss a real secret.
/// </summary>
/// <remarks>
/// Priority (first match wins):
/// <list type="number">
///   <item><see cref="Mneme.Contracts.Classification.Secret"/> when the redactor fired.</item>
///   <item><see cref="Mneme.Contracts.Classification.Pii"/> when the body looks like it
///         contains email addresses, US-style SSNs, or phone numbers.</item>
///   <item><see cref="Mneme.Contracts.Classification.Confidential"/> when the body
///         explicitly self-labels (e.g., contains <c>[confidential]</c>,
///         <c>NDA</c>, <c>customer:</c>).</item>
///   <item><see cref="Mneme.Contracts.Classification.Internal"/> as the default for
///         non-evidence categories (Decisions, Hypotheses, etc., are
///         workstream-internal by default).</item>
///   <item><see cref="Mneme.Contracts.Classification.Public"/> otherwise.</item>
/// </list>
/// </remarks>
public sealed class RuleBasedClassifier : IClassifier
{
    private static readonly Regex EmailLike = new(
        @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex SsnLike = new(
        @"\b\d{3}-\d{2}-\d{4}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex PhoneLike = new(
        @"\b(?:\+?\d{1,3}[ \-]?)?(?:\(\d{3}\)|\d{3})[ \-]?\d{3}[ \-]?\d{4}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex ConfidentialHint = new(
        @"(?i)\b(?:confidential|nda|customer:|internal\s*use\s*only|do\s*not\s*share)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <inheritdoc/>
    public Task<ClassificationLabel> ClassifyAsync(
        string content,
        bool hadRedactionHits,
        EpistemicCategory category,
        CancellationToken ct = default)
    {
        if (hadRedactionHits)
        {
            return Task.FromResult(ClassificationLabel.Secret);
        }
        if (string.IsNullOrEmpty(content))
        {
            return Task.FromResult(ClassificationLabel.Public);
        }
        if (EmailLike.IsMatch(content) || SsnLike.IsMatch(content) || PhoneLike.IsMatch(content))
        {
            return Task.FromResult(ClassificationLabel.Pii);
        }
        if (ConfidentialHint.IsMatch(content))
        {
            return Task.FromResult(ClassificationLabel.Confidential);
        }
        return Task.FromResult(category == EpistemicCategory.Evidence
            ? ClassificationLabel.Public
            : ClassificationLabel.Internal);
    }
}
