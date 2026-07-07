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

    /// <summary>
    /// Extract normalized subject keys from a free-text query. LoCoMo-style
    /// questions are person-centric ("What does Melanie's necklace symbolize?"),
    /// so capitalized tokens outside a question-word stoplist are a high-precision
    /// signal for the entity the answer should be attributed to. Returns the
    /// distinct normalized keys (possessive-stripped, lowercased).
    /// </summary>
    public static IReadOnlyList<string> ExtractSubjects(string? freeText)
    {
        if (string.IsNullOrWhiteSpace(freeText)) return Array.Empty<string>();
        var keys = new List<string>();
        foreach (Match m in ProperNounRegex().Matches(freeText))
        {
            if (QueryStop.Contains(m.Value)) continue;
            var k = Normalize(m.Value);
            if (k.Length > 0 && !keys.Contains(k)) keys.Add(k);
        }
        return keys;
    }

    private static readonly HashSet<string> QueryStop = new(StringComparer.OrdinalIgnoreCase)
    {
        "What","When","Where","Which","Who","Whom","Whose","Why","How","Did","Do","Does",
        "Is","Are","Was","Were","Has","Have","Had","The","A","An","In","On","At","Of","To",
        "For","And","Or","But","If","As","By","With","From","This","That","These","Those","I",
    };

    [GeneratedRegex(@"\b[A-Z][a-zA-Z]+\b")]
    private static partial Regex ProperNounRegex();

    [GeneratedRegex(@"['’]s\b")]
    private static partial Regex PossessiveRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
