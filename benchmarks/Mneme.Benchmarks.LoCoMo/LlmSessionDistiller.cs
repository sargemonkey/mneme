using System.Text;
using System.Text.Json;
using Mneme.Contracts;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// Host-supplied <see cref="ISessionDistiller"/> that extracts atomic,
/// self-contained facts from a window of conversation turns using the chat
/// model. This is the piece that exercises Mneme's actual thesis — proactive
/// distillation — instead of retrieving over raw turns. The SDK chunks the
/// conversation (via repeated <see cref="IMemoryAgent.DistillSessionAsync"/>
/// calls) and ingests the returned facts with session-range citations.
/// </summary>
/// <remarks>
/// Statement extraction and subject-attributed triple extraction run as
/// <em>two separate LLM calls</em> (controlled by <c>extractTriples</c>). A
/// single combined prompt was measured to degrade both the fact statements and
/// the triples (each done at half-attention) — see ANALYSIS.md Experiments 6–7.
/// Splitting them keeps statement quality high (a dedicated prompt) and yields
/// richer triples (a dedicated prompt), at the cost of a second call per chunk.
/// Extracted triples are attached to the fact they most overlap by supporting
/// entry so they flow through <c>FactPayload.Triples</c> into
/// <c>projection_fact_triples</c>.
/// </remarks>
public sealed class LlmSessionDistiller : ISessionDistiller
{
    private readonly IChatCompletion _chat;
    private readonly bool _extractTriples;
    public string Id { get; }

    public LlmSessionDistiller(IChatCompletion chat, bool extractTriples = false)
    {
        _chat = chat;
        _extractTriples = extractTriples;
        Id = $"session-distiller/{_chat.Id}" + (extractTriples ? "+triples" : "");
    }

    // Pass 1 — statements only. Kept focused so fact quality is not diluted.
    private const string StatementSystem = """
        You convert a slice of a personal conversation into atomic, durable
        memory facts. Rules:
        - Each fact is ONE self-contained sentence that stands alone without
          the surrounding dialogue (resolve pronouns to names).
        - Capture concrete facts, preferences, plans, events, and relationships.
        - Preserve dates/times mentioned in or around the turns.
        - Attribute to the speaker by name.
        - Skip pure pleasantries that carry no durable information.
        Reply with JSON only:
        {"facts":[{"statement":"...","supporting":["<entryId>"]}]}
        where supporting lists the entry id(s) the fact came from.
        """;

    // Pass 2 — subject-attributed triples only. A dedicated prompt yields more,
    // and more precisely attributed, triples than a combined statement+triple one.
    private const string TripleSystem = """
        You extract subject-attributed knowledge triples from a slice of a
        personal conversation. For every durable fact, preference, plan, event,
        attribute, or relationship, emit one triple:
          - subject: the SPECIFIC entity the fact is ABOUT, as a proper name or a
            possessive chain (e.g. "Melanie", "Melanie's grandma", "Melanie's
            necklace"). Resolve pronouns to names. Never "I"/"you"/"she".
          - predicate: a short snake_case relation (likes, lives_in, works_as,
            symbolizes, nationality, gave, plans_to, felt).
          - object: the value/target of the relation (a phrase).
        Attribute every triple to the RIGHT entity; never borrow a fact about one
        person and attribute it to another. Prefer several precise triples over
        one vague one. Skip pleasantries.
        Reply with JSON only:
        {"triples":[{"subject":"...","predicate":"...","object":"...","supporting":["<entryId>"]}]}
        """;

    public async Task<SessionDistillationResult> DistillAsync(SessionDistillationRequest req, CancellationToken ct = default)
    {
        var slice = BuildSlice(req);

        // Pass 1: statements.
        var statementReply = await _chat.CompleteAsync(StatementSystem, slice, ct).ConfigureAwait(false);
        var facts = ParseFacts(statementReply, req);
        if (facts.Count == 0) return new SessionDistillationResult(Array.Empty<DistilledEvent>());

        // Pass 2: triples (optional), attached to the best-overlapping fact.
        if (_extractTriples)
        {
            var tripleReply = await _chat.CompleteAsync(TripleSystem, slice, ct).ConfigureAwait(false);
            AttachTriples(facts, ParseTriples(tripleReply));
        }

        return new SessionDistillationResult(facts.Select(f => f.ToEvent()).ToArray());
    }

    private static string BuildSlice(SessionDistillationRequest req)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Conversation slice (entryId | speaker-tagged text):");
        foreach (var e in req.Entries)
        {
            sb.Append(e.EntryId).Append(" | ").AppendLine(e.Text);
        }
        return sb.ToString();
    }

    private sealed class DraftFact
    {
        public required string Statement { get; init; }
        public required string[] Supporting { get; init; }
        public List<FactTriple> Triples { get; } = new();

        public DistilledEvent ToEvent() => new(
            new FactPayload(Statement, Array.Empty<EventId>(), Triples.Count > 0 ? Triples : null),
            Supporting);
    }

    private static List<DraftFact> ParseFacts(string reply, SessionDistillationRequest req)
    {
        var result = new List<DraftFact>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(reply, "facts"));
            if (!doc.RootElement.TryGetProperty("facts", out var facts) || facts.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            foreach (var f in facts.EnumerateArray())
            {
                var statement = f.TryGetProperty("statement", out var s) ? s.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(statement)) continue;
                var supporting = f.TryGetProperty("supporting", out var sup) && sup.ValueKind == JsonValueKind.Array
                    ? sup.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : Array.Empty<string>();
                if (supporting.Length == 0 && req.Entries.Count > 0)
                {
                    supporting = new[] { req.Entries[0].EntryId };
                }
                result.Add(new DraftFact { Statement = statement, Supporting = supporting });
            }
        }
        catch
        {
            // Malformed reply → no facts for this slice; the run continues.
        }
        return result;
    }

    private static List<(FactTriple Triple, string[] Supporting)> ParseTriples(string reply)
    {
        var result = new List<(FactTriple, string[])>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(reply, "triples"));
            if (!doc.RootElement.TryGetProperty("triples", out var triples) || triples.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            foreach (var t in triples.EnumerateArray())
            {
                var subj = t.TryGetProperty("subject", out var sv) ? sv.GetString() ?? "" : "";
                var pred = t.TryGetProperty("predicate", out var pv) ? pv.GetString() ?? "" : "";
                var obj = t.TryGetProperty("object", out var ov) ? ov.GetString() ?? "" : "";
                if (subj.Length == 0 || pred.Length == 0 || obj.Length == 0) continue;
                var supporting = t.TryGetProperty("supporting", out var sup) && sup.ValueKind == JsonValueKind.Array
                    ? sup.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : Array.Empty<string>();
                result.Add((new FactTriple(subj, pred, obj), supporting));
            }
        }
        catch
        {
            // Malformed reply → no triples for this slice.
        }
        return result;
    }

    // Attach each triple to the fact it shares the most supporting entries with,
    // so it lands on a real fact event (→ projection_fact_triples). Ties or no
    // overlap fall back to the first fact of the chunk.
    private static void AttachTriples(List<DraftFact> facts, List<(FactTriple Triple, string[] Supporting)> triples)
    {
        foreach (var (triple, support) in triples)
        {
            DraftFact? best = null;
            var bestOverlap = 0;
            foreach (var fact in facts)
            {
                var overlap = support.Count(s => fact.Supporting.Contains(s));
                if (overlap > bestOverlap) { bestOverlap = overlap; best = fact; }
            }
            (best ?? facts[0]).Triples.Add(triple);
        }
    }

    // Models sometimes wrap JSON in prose or fences; pull out the first {...}.
    private static string ExtractJson(string s, string emptyKey)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : $"{{\"{emptyKey}\":[]}}";
    }
}
