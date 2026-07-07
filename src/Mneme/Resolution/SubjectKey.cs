using System.Text.RegularExpressions;

namespace Mneme.Resolution;

/// <summary>
/// Normalizes a fact-triple subject surface form ("Melanie", "Melanie's
/// grandma") to a stable lookup key ("melanie", "melanie grandma") used to
/// index and match <c>projection_fact_triples</c>. This is the lightweight,
/// deterministic subject-scoping key: the same person referred to the same way
/// resolves to the same key, so retrieval can scope to "facts about X" without
/// requiring full Tier-2/3 entity resolution (which names are ineligible for at
/// Tier 1). Full canonical entity ids remain a later refinement stored in the
/// nullable <c>subject_entity_id</c> column.
/// </summary>
public static partial class SubjectKey
{
    /// <summary>
    /// Lowercase, strip possessive <c>'s</c>, and collapse whitespace. Returns
    /// empty for a null/blank subject.
    /// </summary>
    public static string Normalize(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return string.Empty;
        var s = subject.Trim().ToLowerInvariant();
        s = PossessiveRegex().Replace(s, string.Empty);
        s = WhitespaceRegex().Replace(s, " ");
        return s.Trim();
    }

    [GeneratedRegex(@"['’]s\b")]
    private static partial Regex PossessiveRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
