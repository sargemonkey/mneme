using System.Text;
using Mneme.Contracts;

namespace Mneme.Distillation;

/// <summary>
/// Optional helper an <see cref="IDistiller"/> can use to render canonical
/// prompts from a <see cref="DistillationRequest"/>. Pure functions — no LLM
/// dependency. Distillers are free to ignore this and craft their own
/// prompts; the helper exists so a host that just wants "ask an LLM for a
/// reasonable bundle" can do it in five lines.
/// </summary>
public static class DistillationPromptBuilder
{
    /// <summary>
    /// Build a system prompt that instructs the LLM how to synthesize a
    /// Mneme bundle. Verbatim across calls — safe to cache.
    /// </summary>
    public const string SystemPrompt = """
        You are a distillation engine for an append-only agent-memory system
        called Mneme. You will be given a workstream's recent events grouped
        by epistemic category (Evidence, Fact, Decision, Hypothesis, Goal,
        Action, Outcome) and active human annotations. Your job is to produce
        a compact, decision-useful synthesis under a token budget. Output
        rules:
        - Begin with a single ORIENTATION paragraph (~80-120 tokens) titled
          "Where we are:" that orients a fresh reader. No bullet lists.
        - Then emit one SECTION per category that has events, in the order
          Decisions, Goals, Hypotheses, Facts, Actions, Outcomes, Evidence.
          Each section heading is the category name followed by a newline.
          Bullets within a section are short factual statements citing
          [event_id] at the end of each bullet so consumers can re-query.
        - Honor curation: include every PINNED event prominently;
          deprioritize DEMOTED events to the bottom of their section or to
          a "Lookup hints" appendix; surface ANNOTATIONS as parenthetical
          remarks next to the corresponding bullet.
        - When two events contradict, surface the contradiction explicitly.
        - End with a "Lookup hints" section: short keyword -> [event_id]
          pointers for events that didn't fit in a section.
        - Stay under the token budget. Truncate older Evidence first.
        Do NOT invent facts. Cite only event_ids present in the input.
        """;

    /// <summary>
    /// Render the user-side prompt: the workstream id, the budget, the
    /// events grouped by category with their score, and active curations.
    /// </summary>
    public static string BuildUserPrompt(DistillationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sb = new StringBuilder();
        sb.Append("Workstream: ").AppendLine(request.Workstream.Value);
        sb.Append("Generated at: ").AppendLine(request.GeneratedAt.ToString("O"));
        sb.Append("Token budget: ").Append(request.TokenBudget).AppendLine();
        sb.Append("Events covered through: ").AppendLine(request.EventsCoveredThrough.Value);
        sb.AppendLine();

        var byCategory = request.Events
            .GroupBy(e => e.Category)
            .OrderBy(g => CategoryOrder(g.Key));

        foreach (var group in byCategory)
        {
            sb.Append("== ").Append(group.Key).Append(" (").Append(group.Count()).AppendLine(") ==");
            foreach (var e in group.OrderByDescending(x => x.Score).ThenByDescending(x => x.ValidAt))
            {
                var statement = StatementOf(e.Payload);
                sb.Append("- [").Append(e.EventId.Value).Append("] (score=").Append(e.Score.ToString("F2"))
                  .Append(", valid_at=").Append(e.ValidAt.ToString("u")).Append(") ")
                  .AppendLine(Truncate(statement, 400));

                if (request.Curations.TryGetValue(e.EventId, out var curs))
                {
                    foreach (var cur in curs)
                    {
                        sb.Append("    · ").Append(cur.Type).Append(" by ").Append(cur.Curator.Value)
                          .Append(": ").AppendLine(Truncate(cur.Rationale, 200));
                    }
                }
            }
            sb.AppendLine();
        }

        if (request.PriorBundle is not null)
        {
            sb.AppendLine("PRIOR BUNDLE (for incremental refresh — reuse intact sections where possible):");
            sb.AppendLine(request.PriorBundle.Orientation.Paragraph);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build a degraded fallback bundle for hosts without a registered
    /// distiller. Pure-mechanical — concatenates the highest-scored events
    /// in each category as bullets, no synthesis. Honest about being a stub
    /// in its OrientationSummary.
    /// </summary>
    public static ContextBundle BuildHeuristicBundle(DistillationRequest request, string distillerId = "mneme/heuristic-fallback")
    {
        ArgumentNullException.ThrowIfNull(request);

        var sections = new List<BundleSection>();
        var hints = new List<LookupHint>();
        var perCategoryBudget = request.TokenBudget <= 0 ? 800 : Math.Max(200, request.TokenBudget / 8);
        var hintsBudget = request.TokenBudget <= 0 ? 200 : Math.Max(50, request.TokenBudget / 16);

        foreach (var group in request.Events.GroupBy(e => e.Category).OrderBy(g => CategoryOrder(g.Key)))
        {
            var sb = new StringBuilder();
            var provenance = new List<EventId>();
            var tokenEstimate = 0;
            foreach (var e in group.OrderByDescending(x => x.Score).ThenByDescending(x => x.ValidAt))
            {
                var line = "- " + Truncate(StatementOf(e.Payload), 200) + $" [{e.EventId.Value}]";
                var tokens = EstimateTokens(line);
                if (tokenEstimate + tokens > perCategoryBudget)
                {
                    hints.Add(new LookupHint(
                        Keyword: Keyword(e.Payload),
                        Pointer: e.EventId,
                        Context: Truncate(StatementOf(e.Payload), 80)));
                    continue;
                }
                sb.AppendLine(line);
                provenance.Add(e.EventId);
                tokenEstimate += tokens;
            }
            if (sb.Length > 0)
            {
                sections.Add(new BundleSection(
                    Id: group.Key.ToString().ToLowerInvariant(),
                    Title: group.Key.ToString(),
                    Category: group.Key,
                    Content: sb.ToString().TrimEnd(),
                    Distiller: distillerId,
                    GeneratedAt: request.GeneratedAt,
                    EventsCoveredThrough: request.EventsCoveredThrough,
                    TokenBudget: perCategoryBudget,
                    TokenCount: tokenEstimate,
                    Provenance: provenance));
            }
        }

        // Trim hints to budget.
        var trimmedHints = new List<LookupHint>();
        var hintTokens = 0;
        foreach (var h in hints)
        {
            var t = EstimateTokens(h.Keyword) + EstimateTokens(h.Context);
            if (hintTokens + t > hintsBudget) break;
            trimmedHints.Add(h);
            hintTokens += t;
        }

        var orientation = new OrientationSummary(
            Paragraph: $"Heuristic synthesis ({request.Events.Count} events across {sections.Count} categories). " +
                       "No LLM distiller is registered, so this is a pure-mechanical bullet list grouped by category. " +
                       "Register an IDistiller (e.g., an OpenAI/Anthropic-backed implementation) for prose synthesis.",
            Distiller: distillerId,
            GeneratedAt: request.GeneratedAt,
            EventsCoveredThrough: request.EventsCoveredThrough);

        var index = new BundleIndex(
            Distiller: distillerId,
            TokenBudget: request.TokenBudget,
            TokenCount: sections.Sum(s => s.TokenCount) + EstimateTokens(orientation.Paragraph) + hintTokens,
            GeneratedAt: request.GeneratedAt,
            EventsCoveredThrough: request.EventsCoveredThrough,
            SectionRefs: sections.Select(s => new BundleSectionRef(s.Id, s.Title, s.Category, s.TokenCount)).ToArray());

        return new ContextBundle(
            Workstream: request.Workstream,
            Orientation: orientation,
            Index: index,
            Sections: sections,
            Hints: new LookupHints(trimmedHints),
            GeneratedAt: request.GeneratedAt,
            EventsCoveredThrough: request.EventsCoveredThrough,
            IsStale: false);
    }

    private static int CategoryOrder(EpistemicCategory c) => c switch
    {
        EpistemicCategory.Decision   => 0,
        EpistemicCategory.Goal       => 1,
        EpistemicCategory.Hypothesis => 2,
        EpistemicCategory.Fact       => 3,
        EpistemicCategory.Action     => 4,
        EpistemicCategory.Outcome    => 5,
        EpistemicCategory.Evidence   => 6,
        _ => 99,
    };

    private static string StatementOf(EventPayload p) => p switch
    {
        EvidencePayload e   => e.Content,
        FactPayload f       => f.Statement,
        DecisionPayload d   => d.Statement + (string.IsNullOrEmpty(d.Rationale) ? "" : " — " + d.Rationale),
        HypothesisPayload h => h.Statement,
        GoalPayload g       => g.Statement,
        ActionPayload a     => a.Statement,
        OutcomePayload o    => o.Statement,
        SkillPayload s      => s.Name + (string.IsNullOrEmpty(s.Procedure) ? "" : " — " + s.Procedure),
        _ => string.Empty,
    };

    private static string Keyword(EventPayload p)
    {
        var s = StatementOf(p);
        // first 3 words, lowercased
        var words = s.Split(new[] { ' ', '\t', '\n', '\r' }, 4, StringSplitOptions.RemoveEmptyEntries);
        return string.Join('-', words.Take(3)).ToLowerInvariant();
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";

    // ~4 chars per token approximation — good enough for budget sizing.
    private static int EstimateTokens(string s) => (s.Length + 3) / 4;
}
