using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// Prototype subject-attributed fact extractor: turns a slice of conversation
/// turns into <c>(subject, predicate, object)</c> triples where the subject is a
/// resolved person/thing name (pronouns resolved). This is the benchmark stand-in
/// for the real Mneme distiller upgrade — it lets us measure whether subject-scoped
/// retrieval beats statement-level retrieval before building the projection in
/// Mneme proper. Extraction runs once per conversation and is cached in a sidecar
/// <c>fact_triples</c> table (see <see cref="TripleStore"/>) so <c>--reuse-db</c>
/// runs pay the LLM cost only on the first pass.
/// </summary>
public sealed class TripleExtractor
{
    private readonly IChatCompletion _chat;

    public TripleExtractor(IChatCompletion chat) => _chat = chat;

    private const string System = """
        You extract structured knowledge triples from a slice of a personal
        conversation. For every durable fact, preference, plan, event, attribute,
        or relationship, emit one triple:
          - subject: the SPECIFIC entity the fact is ABOUT, as a proper name or a
            possessive chain (e.g. "Melanie", "Melanie's grandma", "Melanie's
            necklace"). Resolve pronouns to names. Never use "I"/"you"/"she".
          - predicate: a short relation in snake_case (e.g. likes, lives_in,
            works_as, symbolizes, nationality, gave, plans_to, felt).
          - object: the value/target of the relation (a phrase).
        Rules:
          - Attribute every triple to the RIGHT entity. If Melanie states a fact
            about herself, subject = Melanie; do not attribute it to the other
            speaker.
          - Prefer several precise triples over one vague sentence.
          - Skip pleasantries with no durable content.
        Reply with JSON only:
        {"triples":[{"subject":"...","predicate":"...","object":"...","supporting":["<entryId>"]}]}
        """;

    /// <summary>
    /// Extract triples from a chunk of dated, entry-tagged turns. Each returned
    /// triple carries the source entry id(s) so retrieval can date-stamp it.
    /// </summary>
    public async Task<IReadOnlyList<TripleRow>> ExtractAsync(
        IReadOnlyList<(string EntryId, DateTimeOffset At, string Text)> turns, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Conversation slice (entryId | speaker-tagged text):");
        foreach (var t in turns)
        {
            sb.Append(t.EntryId).Append(" | ").AppendLine(t.Text);
        }
        var reply = await _chat.CompleteAsync(System, sb.ToString(), ct).ConfigureAwait(false);

        var rows = new List<TripleRow>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(reply));
            if (!doc.RootElement.TryGetProperty("triples", out var triples) || triples.ValueKind != JsonValueKind.Array)
            {
                return rows;
            }
            var byEntry = turns.ToDictionary(t => t.EntryId, t => t.At);
            foreach (var tr in triples.EnumerateArray())
            {
                var subject = Str(tr, "subject");
                var predicate = Str(tr, "predicate");
                var obj = Str(tr, "object");
                if (subject.Length == 0 || predicate.Length == 0 || obj.Length == 0) continue;

                var support = tr.TryGetProperty("supporting", out var sup) && sup.ValueKind == JsonValueKind.Array
                    ? sup.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : Array.Empty<string>();
                var at = support.Select(s => byEntry.TryGetValue(s, out var d) ? d : (DateTimeOffset?)null)
                    .FirstOrDefault(d => d is not null) ?? turns[0].At;

                rows.Add(new TripleRow(NormalizeSubject(subject), subject, predicate, obj, at));
            }
        }
        catch
        {
            // Malformed reply → no triples for this slice; run continues.
        }
        return rows;
    }

    // Canonical subject key for indexing/matching (poor-man's entity resolution:
    // lowercase, strip possessive 's, collapse whitespace). The real build routes
    // this through Mneme's Phase-6 EntityResolver instead.
    public static string NormalizeSubject(string subject)
    {
        var s = subject.Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"['’]s\b", "");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    private static string ExtractJson(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : "{\"triples\":[]}";
    }
}

/// <summary>One extracted knowledge triple with its normalized subject key and date.</summary>
public sealed record TripleRow(string SubjectKey, string SubjectText, string Predicate, string Object, DateTimeOffset At);
