using Mneme.Contracts;
using EventId = Mneme.Contracts.EventId;

namespace Mneme.Studio.Agent.Memory;

/// <summary>
/// A fully offline, deterministic <see cref="ISessionDistiller"/> — no LLM, no
/// API key — so the desktop app runs anywhere. It scans each conversation
/// entry for cues and turns the substantive ones into epistemic events
/// (Decisions, Goals, Facts, Outcomes), dropping small talk. This is the
/// host-supplied piece of Mneme's locked "host owns the chat log; Mneme owns
/// the interpretation" split: the SDK stamps each returned event with a
/// <see cref="Citation.SessionRange"/> and advances the watermark.
/// </summary>
/// <remarks>
/// A production deployment swaps this for an <c>IChatClient</c>-backed distiller
/// (see the AgentHost sample / LoCoMo harness). The interface and the
/// <see cref="IMemoryAgent.DistillSessionAsync"/> call site are identical.
/// </remarks>
internal sealed class HeuristicSessionDistiller : ISessionDistiller
{
    public string Id => "studio-agent/heuristic-session-distiller@1";

    private static readonly string[] DecisionCues =
        { "decid", "we'll", "we will", "let's", "let us", "go with", "choose", "chosen", "plan:", "decision:" };
    private static readonly string[] GoalCues =
        { "goal", "want to", "need to", "aim to", "planning to", "objective", "trying to" };
    private static readonly string[] OutcomeCues =
        { "confirmed", "shipped", "done", "completed", "resolved", "fixed", "merged", "passed" };
    private static readonly string[] DropCues =
        { "hello", "hi ", "hey", "thanks", "thank you", "weather", "how are you", "good morning" };

    public Task<SessionDistillationResult> DistillAsync(SessionDistillationRequest req, CancellationToken ct = default)
    {
        var events = new List<DistilledEvent>();
        var dropped = new List<DroppedEntry>();

        foreach (var entry in req.Entries)
        {
            var text = entry.Text?.Trim() ?? string.Empty;
            if (text.Length == 0) continue;

            var lower = text.ToLowerInvariant();

            // Very short / obvious pleasantries carry no durable information.
            if (text.Length < 15 || StartsWithAny(lower, DropCues))
            {
                dropped.Add(new DroppedEntry(entry.EntryId, "small talk / no durable content"));
                continue;
            }

            var speaker = ResolveSpeaker(entry);
            var statement = $"{speaker}: {Trim(text)}";
            var supporting = new[] { entry.EntryId };

            EventPayload payload;
            if (ContainsAny(lower, DecisionCues))
            {
                payload = new DecisionPayload(
                    statement,
                    Rationale: "extracted from agent session",
                    SupportingEvents: Array.Empty<EventId>(),
                    Approver: new PrincipalId(speaker.ToLowerInvariant()));
            }
            else if (ContainsAny(lower, OutcomeCues))
            {
                payload = new OutcomePayload(statement, EventId.None, OutcomePolarity.Positive);
            }
            else if (ContainsAny(lower, GoalCues))
            {
                payload = new GoalPayload(statement, GoalState.Active);
            }
            else
            {
                payload = new FactPayload(statement, Array.Empty<EventId>());
            }

            events.Add(new DistilledEvent(payload, supporting, entry.Timestamp));
        }

        return Task.FromResult(new SessionDistillationResult(events, dropped));
    }

    private static string ResolveSpeaker(ContextEntry entry)
    {
        if (entry.Metadata is not null
            && entry.Metadata.TryGetValue("speaker", out var s)
            && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }
        return entry.Kind == ContextEntryKind.UserMessage ? "User" : "Agent";
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.Contains(n, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool StartsWithAny(string haystack, string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.StartsWith(n, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
