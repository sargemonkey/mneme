using System.Text;
using System.Text.Json;
using Mneme.Contracts;
using EventId = Mneme.Contracts.EventId;

namespace Mneme.Studio.Agent.Memory;

/// <summary>
/// Mneme's session-distillation logic backed by a real LLM. It converts a slice
/// of the turn-based conversation into atomic, self-contained epistemic events
/// (Fact / Decision / Goal / Hypothesis / Outcome) — genuine extraction, not the
/// verbatim capture the heuristic distiller does. The LLM (GitHub Copilot over
/// ACP) is invoked through <see cref="IChatCompletion"/>; the SDK still owns the
/// pipeline (watermark, citation stamping, ingest) — this is only the
/// interpretation step.
/// </summary>
/// <remarks>
/// If the LLM call fails (e.g., the copilot CLI isn't available), it degrades to
/// the offline <see cref="HeuristicSessionDistiller"/> so the app keeps working.
/// </remarks>
internal sealed class LlmSessionDistiller : ISessionDistiller
{
    private readonly IChatCompletion _chat;
    private readonly HeuristicSessionDistiller _fallback = new();

    public LlmSessionDistiller(IChatCompletion chat) => _chat = chat;

    public string Id => $"studio-agent/llm-session-distiller[{_chat.Id}]@1";

    private const string System = """
        You extract durable memory from a slice of a conversation. For each NEW
        turn below, decide whether it carries information worth remembering next
        week, and if so express it as ONE atomic, self-contained sentence that
        stands alone without the surrounding dialogue (resolve pronouns to names;
        keep any dates). Attribute each fact to the speaker. Classify each into
        exactly one category:
          - Decision: a choice that was made (often with a rationale)
          - Goal: an objective someone is pursuing
          - Outcome: something that happened / was confirmed / completed
          - Hypothesis: a claim under investigation ("we think…", "the risk is…")
          - Fact: any other durable fact, preference, plan, or relationship
        Skip greetings, acknowledgements, small talk, and pure questions.
        Cite the entry id(s) each fact came from in "supporting".
        Reply with JSON only, no prose, no tool use:
        {"facts":[{"category":"Decision","statement":"…","supporting":["000123"]}]}
        An empty facts array is fine.
        """;

    public async Task<SessionDistillationResult> DistillAsync(SessionDistillationRequest req, CancellationToken ct = default)
    {
        if (req.Entries.Count == 0)
        {
            return new SessionDistillationResult(Array.Empty<DistilledEvent>());
        }

        var user = BuildSlice(req);
        string reply;
        try
        {
            reply = await _chat.CompleteAsync(System, user, ct).ConfigureAwait(false);
        }
        catch
        {
            // LLM unavailable — degrade to the offline heuristic so nothing stalls.
            return await _fallback.DistillAsync(req, ct).ConfigureAwait(false);
        }

        var sliceIds = req.Entries.Select(e => e.EntryId).ToHashSet(StringComparer.Ordinal);
        var events = new List<DistilledEvent>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(reply));
            if (doc.RootElement.TryGetProperty("facts", out var facts) && facts.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in facts.EnumerateArray())
                {
                    var statement = f.TryGetProperty("statement", out var s) ? s.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(statement)) continue;

                    var category = ParseCategory(f.TryGetProperty("category", out var c) ? c.GetString() : null);
                    var supporting = ResolveSupporting(f, sliceIds, req);
                    events.Add(new DistilledEvent(ToPayload(category, statement), supporting));
                }
            }
        }
        catch
        {
            // Malformed model output → degrade to heuristic for this slice.
            return await _fallback.DistillAsync(req, ct).ConfigureAwait(false);
        }

        return new SessionDistillationResult(events);
    }

    private static string BuildSlice(SessionDistillationRequest req)
    {
        var sb = new StringBuilder();
        if (req.PriorFacts.Count > 0)
        {
            sb.AppendLine("Already known (do not repeat):");
            foreach (var pf in req.PriorFacts.Take(8))
            {
                sb.Append("  - ").AppendLine(pf.Statement);
            }
            sb.AppendLine();
        }
        sb.AppendLine("New turns (entryId | speaker: text):");
        foreach (var e in req.Entries)
        {
            var speaker = e.Metadata is not null && e.Metadata.TryGetValue("speaker", out var sp) && !string.IsNullOrWhiteSpace(sp)
                ? sp
                : (e.Kind == ContextEntryKind.UserMessage ? "User" : "Agent");
            sb.Append(e.EntryId).Append(" | ").Append(speaker).Append(": ").AppendLine(e.Text);
        }
        return sb.ToString();
    }

    private static string[] ResolveSupporting(JsonElement fact, HashSet<string> sliceIds, SessionDistillationRequest req)
    {
        if (fact.TryGetProperty("supporting", out var sup) && sup.ValueKind == JsonValueKind.Array)
        {
            var ids = sup.EnumerateArray()
                .Select(x => x.GetString() ?? "")
                .Where(x => sliceIds.Contains(x))
                .ToArray();
            if (ids.Length > 0) return ids;
        }
        // No usable citation from the model — attribute to the newest entry in the slice.
        return new[] { req.Entries[^1].EntryId };
    }

    private static EpistemicCategory ParseCategory(string? raw)
        => Enum.TryParse<EpistemicCategory>(raw, ignoreCase: true, out var cat) ? cat : EpistemicCategory.Fact;

    private static EventPayload ToPayload(EpistemicCategory category, string statement) => category switch
    {
        EpistemicCategory.Decision => new DecisionPayload(statement, "extracted from conversation", Array.Empty<EventId>(), new PrincipalId("agent")),
        EpistemicCategory.Goal => new GoalPayload(statement, GoalState.Active),
        EpistemicCategory.Outcome => new OutcomePayload(statement, EventId.None, OutcomePolarity.Neutral),
        EpistemicCategory.Hypothesis => new HypothesisPayload(statement, HypothesisState.Open),
        EpistemicCategory.Evidence => new EvidencePayload(statement, Source: "conversation"),
        _ => new FactPayload(statement, Array.Empty<EventId>()),
    };

    // Models sometimes wrap JSON in prose/fences; pull out the first {...} block.
    private static string ExtractJson(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : "{\"facts\":[]}";
    }
}
