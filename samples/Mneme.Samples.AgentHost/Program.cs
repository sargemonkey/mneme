using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;

namespace Mneme.Samples.AgentHost;

/// <summary>
/// End-to-end sample: a host that already owns the full session chat history
/// (Mneme never stores it) periodically asks Mneme to distill the slice
/// since the last watermark. Mneme runs the host-supplied
/// <see cref="ISessionDistiller"/>, ingests the resulting epistemic events
/// with <see cref="Citation.SessionRange"/> stamps, and advances the
/// watermark atomically.
/// </summary>
/// <remarks>
/// Two notional LLM clients are wired up — one for session distillation
/// (chat -> events) and one for the read-side bundle synthesis (events ->
/// orientation paragraph). Both are stubs so the sample runs offline; in
/// production, swap each for an <see cref="IChatClient"/> against any
/// provider. Mneme has no opinion.
/// </remarks>
internal static class Program
{
    public static async Task Main()
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = "agent-host-demo";
            o.SqlitePath   = Path.Combine(AppContext.BaseDirectory, "data", "demo.db");
            o.UserId       = "alice";
        });

        IChatClient sessionChat = new StubChatClient("gpt-4o-mini-stub");
        IChatClient bundleChat  = new StubChatClient("gpt-4o-stub");

        services.AddSingleton<ISessionDistiller>(_ => new LlmSessionDistiller(sessionChat));
        services.AddSingleton<IDistiller>(_ => new LlmBundleSynthesizer(bundleChat));

        await using var sp = services.BuildServiceProvider();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var api   = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var session = new SessionId("session-42");

        // -- Imagine the host's session has grown to 5 entries. The host
        //    has them in its own chat-log store; Mneme never sees them
        //    except when explicitly asked to distill.
        var now = DateTimeOffset.UtcNow;
        var allEntries = new List<ContextEntry>
        {
            new("0001", now.AddMinutes(-30), ContextEntryKind.UserMessage,
                "I'm preparing for the Q3 planning meeting on Friday.", SourceRef: "chat#0001"),
            new("0002", now.AddMinutes(-29), ContextEntryKind.AssistantMessage,
                "Got it — I'll keep that on the radar. Anything specific?", SourceRef: "chat#0002"),
            new("0003", now.AddMinutes(-28), ContextEntryKind.UserMessage,
                "Yes — we decided to ship the v2 redesign in October, not September. " +
                "Two engineers are out in September.", SourceRef: "chat#0003"),
            new("0004", now.AddMinutes(-27), ContextEntryKind.AssistantMessage,
                "Understood. Decision: v2 redesign ships October, not September, " +
                "because two engineers are out in September.", SourceRef: "chat#0004"),
            new("0005", now.AddMinutes(-26), ContextEntryKind.UserMessage,
                "Hmm, weather looks nice today.", SourceRef: "chat#0005"),
        };

        // -- First distillation pass: watermark is null, so all 5 entries
        //    are eligible. The host distiller returns whatever epistemic
        //    events it judges worth keeping.
        Console.WriteLine($"watermark before: {(await agent.GetWatermarkAsync(session))?.LastDistilledEntryId ?? "<none>"}");
        var first = await agent.DistillSessionAsync(session, allEntries, token);
        Console.WriteLine($"first call: {first.NewEvents.Count} event(s), no-op={first.WasNoOp}, " +
                          $"new watermark={first.NewWatermark.LastDistilledEntryId}");

        // -- Idempotency check: replaying the same entries is a no-op.
        var replay = await agent.DistillSessionAsync(session, allEntries, token);
        Console.WriteLine($"replay    : {replay.NewEvents.Count} event(s), no-op={replay.WasNoOp}");

        // -- Session grows by two more entries; only the new tail is
        //    processed.
        allEntries.Add(new("0006", now.AddMinutes(-2), ContextEntryKind.UserMessage,
            "Update: October ship date confirmed by the team in this morning's sync.",
            SourceRef: "chat#0006"));
        allEntries.Add(new("0007", now.AddMinutes(-1), ContextEntryKind.AssistantMessage,
            "Noted. Outcome: v2 redesign October ship date — confirmed.",
            SourceRef: "chat#0007"));
        var second = await agent.DistillSessionAsync(session, allEntries, token);
        Console.WriteLine($"second    : {second.NewEvents.Count} event(s), no-op={second.WasNoOp}, " +
                          $"new watermark={second.NewWatermark.LastDistilledEntryId}");

        // -- Read-side: distill the accumulated workstream into a bundle
        //    for the next agent invocation to consume.
        var bundle = await api.DistillAsync(new WorkstreamId("agent-host-demo"), new DistillOptions(), token);
        Console.WriteLine();
        Console.WriteLine($"=== ORIENTATION (from {bundle.Orientation.Distiller}) ===");
        Console.WriteLine(bundle.Orientation.Paragraph);
        Console.WriteLine($"=== {bundle.Sections.Count} SECTION(S) ===");
        foreach (var section in bundle.Sections)
        {
            Console.WriteLine($"-- {section.Title} ({section.Category}, ~{section.TokenCount} tokens) --");
            Console.WriteLine(section.Content);
            Console.WriteLine();
        }
    }
}

/// <summary>
/// Host-owned session distiller. Sees a slice of session entries; returns
/// the epistemic events worth keeping (with supporting entry-id citations).
/// </summary>
internal sealed class LlmSessionDistiller : ISessionDistiller
{
    private readonly IChatClient _chat;
    public LlmSessionDistiller(IChatClient chat) { _chat = chat; }
    public string Id => "sample/session-distiller@1";

    private const string SystemPrompt = """
        You are extracting durable epistemic memory from a slice of an agent
        session's conversation. For each entry, decide if it's worth
        remembering. Worth remembering = a Fact, Decision, Goal, Hypothesis,
        Action, or Outcome the team would benefit from recalling next week.
        Small talk / greetings / weather / status acknowledgements: SKIP.

        Reply with JSON:
        {"events":[{"category":"Decision","content":"...","supporting":["0003","0004"]}],
         "dropped":[{"entry_id":"0005","reason":"small talk"}]}.
        category ∈ {Evidence, Fact, Decision, Hypothesis, Goal, Action, Outcome}.
        Empty events array is fine.
        """;

    public async Task<SessionDistillationResult> DistillAsync(SessionDistillationRequest req, CancellationToken ct = default)
    {
        var user = new StringBuilder();
        user.Append("session=").AppendLine(req.Session.Value);
        user.Append("workstream=").AppendLine(req.Workstream.Value);
        user.AppendLine("entries:");
        foreach (var e in req.Entries)
        {
            user.Append("  ").Append(e.EntryId).Append(' ').Append(e.Kind).Append(' ').AppendLine(e.Text);
        }
        var response = await _chat.GetResponseAsync(new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, user.ToString()),
        }, new ChatOptions { Temperature = 0, MaxOutputTokens = 400 }, ct).ConfigureAwait(false);

        var events = new List<DistilledEvent>();
        var dropped = new List<DroppedEntry>();
        try
        {
            using var doc = JsonDocument.Parse(response.Text ?? "{}");
            if (doc.RootElement.TryGetProperty("events", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var category = Enum.Parse<EpistemicCategory>(el.GetProperty("category").GetString()!, true);
                    var content = el.GetProperty("content").GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(content)) continue;
                    var supporting = el.TryGetProperty("supporting", out var s) && s.ValueKind == JsonValueKind.Array
                        ? s.EnumerateArray().Select(x => x.GetString()!).ToArray()
                        : Array.Empty<string>();
                    EventPayload payload = category switch
                    {
                        EpistemicCategory.Evidence   => new EvidencePayload(content, Source: req.Session.Value),
                        EpistemicCategory.Fact       => new FactPayload(content, Array.Empty<EventId>()),
                        EpistemicCategory.Decision   => new DecisionPayload(content, "extracted from session", Array.Empty<EventId>(), new PrincipalId("agent")),
                        EpistemicCategory.Hypothesis => new HypothesisPayload(content, HypothesisState.Open),
                        EpistemicCategory.Goal       => new GoalPayload(content, GoalState.Active),
                        EpistemicCategory.Action     => new ActionPayload(content, null, req.Session.Value),
                        EpistemicCategory.Outcome    => new OutcomePayload(content, EventId.None, OutcomePolarity.Neutral),
                        _ => new EvidencePayload(content, Source: req.Session.Value),
                    };
                    events.Add(new DistilledEvent(payload, supporting));
                }
            }
            if (doc.RootElement.TryGetProperty("dropped", out var dArr))
            {
                foreach (var el in dArr.EnumerateArray())
                {
                    dropped.Add(new DroppedEntry(
                        el.GetProperty("entry_id").GetString() ?? "",
                        el.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : ""));
                }
            }
        }
        catch
        {
            // Malformed model output → drop everything silently for the sample.
        }
        return new SessionDistillationResult(events, dropped);
    }
}

/// <summary>
/// Host-owned bundle synthesizer (read side). Uses the SDK's canonical
/// prompt helpers for the user message and asks the LLM only for the
/// orientation paragraph; falls back to the SDK's heuristic for the
/// section bullets to keep this sample compact.
/// </summary>
internal sealed class LlmBundleSynthesizer : IDistiller
{
    private readonly IChatClient _chat;
    public LlmBundleSynthesizer(IChatClient chat) { _chat = chat; }
    public string Id => "sample/bundle-synth@1";

    public async Task<ContextBundle> DistillAsync(DistillationRequest request, CancellationToken ct = default)
    {
        var response = await _chat.GetResponseAsync(new List<ChatMessage>
        {
            new(ChatRole.System, Mneme.Distillation.DistillationPromptBuilder.SystemPrompt),
            new(ChatRole.User, Mneme.Distillation.DistillationPromptBuilder.BuildUserPrompt(request)),
        }, new ChatOptions { Temperature = 0, MaxOutputTokens = Math.Min(request.TokenBudget, 1024) }, ct).ConfigureAwait(false);
        var orientation = response.Text?.Trim() ?? "";
        var heuristic = Mneme.Distillation.DistillationPromptBuilder.BuildHeuristicBundle(request, Id);
        return heuristic with
        {
            Orientation = heuristic.Orientation with
            {
                Paragraph = orientation.Length > 0 ? orientation : heuristic.Orientation.Paragraph
            }
        };
    }
}

/// <summary>Offline IChatClient stub so the sample runs with no API key.</summary>
internal sealed class StubChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; }
    public StubChatClient(string modelId) { Metadata = new ChatClientMetadata(modelId); }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var user = messages.Last(m => m.Role == ChatRole.User).Text ?? "";
        string reply;
        if (user.Contains("entries:", StringComparison.Ordinal))
        {
            // Session distillation: extract decisions, goals, outcomes.
            var events = new List<string>();
            var dropped = new List<string>();
            foreach (var line in user.Split('\n'))
            {
                var l = line.Trim();
                if (!l.StartsWith("000", StringComparison.Ordinal)) continue;
                var parts = l.Split(' ', 3);
                if (parts.Length < 3) continue;
                var id = parts[0]; var rest = parts[2];
                if (rest.Contains("decided", StringComparison.OrdinalIgnoreCase) || rest.Contains("ship", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add($"{{\"category\":\"Decision\",\"content\":\"{rest.Replace("\"", "'")}\",\"supporting\":[\"{id}\"]}}");
                }
                else if (rest.Contains("preparing for", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add($"{{\"category\":\"Goal\",\"content\":\"{rest.Replace("\"", "'")}\",\"supporting\":[\"{id}\"]}}");
                }
                else if (rest.Contains("confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add($"{{\"category\":\"Outcome\",\"content\":\"{rest.Replace("\"", "'")}\",\"supporting\":[\"{id}\"]}}");
                }
                else if (rest.Contains("weather", StringComparison.OrdinalIgnoreCase))
                {
                    dropped.Add($"{{\"entry_id\":\"{id}\",\"reason\":\"small talk\"}}");
                }
            }
            reply = "{\"events\":[" + string.Join(",", events) + "],\"dropped\":[" + string.Join(",", dropped) + "]}";
        }
        else
        {
            reply = "Where we are: the team committed to shipping the v2 redesign in October " +
                    "(deferred from September because two engineers are out). The October ship " +
                    "date has since been confirmed by the team.";
        }
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("stub does not stream");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
