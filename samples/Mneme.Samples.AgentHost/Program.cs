using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mneme.Capture;
using Mneme.Contracts;
using Mneme.Distillation;
using Mneme.Hosting;

namespace Mneme.Samples.AgentHost;

/// <summary>
/// End-to-end sample: an agentic framework that has access to the
/// turn-by-turn conversation history and uses an LLM to (a) decide what's
/// worth capturing per turn and (b) distill the accumulated memory into a
/// compact bundle. Two different LLMs can be used for the two jobs.
/// </summary>
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

        // Two notional chat clients. In a real host, replace each with an
        // OpenAIChatClient / AnthropicChatClient / OllamaChatClient /
        // AzureOpenAIChatClient / your own IChatClient. Mneme doesn't care.
        IChatClient captureChatClient = new StubChatClient("gpt-4o-mini-stub");
        IChatClient distillChatClient = new StubChatClient("gpt-4o-stub");

        services.AddSingleton<ICapturePolicy>(sp => new LlmCapturePolicy(captureChatClient));
        services.AddSingleton<ICaptureFilter>(sp =>
            new RecentDuplicateFilter(sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>()));
        services.AddSingleton<IDistiller>(sp => new LlmDistiller(distillChatClient));

        await using var sp = services.BuildServiceProvider();

        // Feed turns into Mneme as they happen. In a real host this loop
        // lives wherever the framework gives you the turn — Semantic Kernel
        // pipeline, AutoGen handler, Copilot CLI hook, transcript watcher.
        var capture = sp.GetRequiredService<CaptureSession>();
        var workstream = new WorkstreamId("agent-host-demo");
        var session = "session-42";
        var now = DateTimeOffset.UtcNow;

        var transcript = new[]
        {
            new ConversationTurn(new PrincipalId("alice"),
                "I'm preparing for the Q3 planning meeting on Friday.",
                now.AddMinutes(-30), session),
            new ConversationTurn(new PrincipalId("agent"),
                "Got it — I'll keep that on the radar. Anything specific?",
                now.AddMinutes(-29), session),
            new ConversationTurn(new PrincipalId("alice"),
                "Yes — we decided to ship the v2 redesign in October, not September. " +
                "The reason is that two engineers are out in September.",
                now.AddMinutes(-28), session),
            new ConversationTurn(new PrincipalId("agent"),
                "Understood. Decision: v2 redesign ships October, not September, " +
                "because two engineers are out in September.",
                now.AddMinutes(-27), session),
            new ConversationTurn(new PrincipalId("alice"),
                "Hmm, weather looks nice today.", // small-talk: policy should skip
                now.AddMinutes(-26), session),
        };

        foreach (var turn in transcript)
        {
            var results = await capture.ProcessTurnAsync(turn, workstream);
            Console.WriteLine($"turn by {turn.Speaker.Value,6}: captured {results.Count} event(s)");
        }

        // When the agent next needs context, ask Mneme to distill.
        var api   = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var bundle = await api.DistillAsync(workstream, new DistillOptions(), token);

        Console.WriteLine();
        Console.WriteLine($"=== ORIENTATION (from {bundle.Orientation.Distiller}) ===");
        Console.WriteLine(bundle.Orientation.Paragraph);
        Console.WriteLine();
        Console.WriteLine($"=== {bundle.Sections.Count} SECTION(S) ===");
        foreach (var section in bundle.Sections)
        {
            Console.WriteLine($"-- {section.Title} ({section.Category}, ~{section.TokenCount} tokens) --");
            Console.WriteLine(section.Content);
            Console.WriteLine();
        }
    }
}

/// <summary>Host-owned capture policy that calls an LLM per turn.</summary>
internal sealed class LlmCapturePolicy : ICapturePolicy
{
    private readonly IChatClient _chat;
    public LlmCapturePolicy(IChatClient chat) { _chat = chat; }
    public string Id => "sample/llm-capture@1";

    private const string SystemPrompt = """
        Decide whether the turn is worth remembering. Worth remembering =
        a fact, decision, goal, hypothesis, action, or outcome the team
        would benefit from recalling next week. Small talk / greetings /
        weather: NOT worth remembering.
        Reply with JSON: {"capture":[{"category":"Decision","content":"..."}]}.
        Use one of: Evidence, Fact, Decision, Hypothesis, Goal, Action,
        Outcome. Empty {"capture":[]} when nothing is worth remembering.
        """;

    public async Task<IReadOnlyList<CaptureCandidate>> EvaluateAsync(
        ConversationTurn turn, WorkstreamId workstream, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"speaker={turn.Speaker.Value}\nturn={turn.Content}"),
        };
        var response = await _chat.GetResponseAsync(messages,
            new ChatOptions { Temperature = 0, MaxOutputTokens = 200 }, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(response.Text ?? "{\"capture\":[]}");
            if (!doc.RootElement.TryGetProperty("capture", out var arr)) return Array.Empty<CaptureCandidate>();
            var result = new List<CaptureCandidate>();
            foreach (var el in arr.EnumerateArray())
            {
                var category = Enum.Parse<EpistemicCategory>(el.GetProperty("category").GetString()!, true);
                var content = el.GetProperty("content").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(content)) continue;
                result.Add(new CaptureCandidate(content, category,
                    Rationale: $"captured from turn by {turn.Speaker.Value}"));
            }
            return result;
        }
        catch
        {
            return Array.Empty<CaptureCandidate>();
        }
    }
}

/// <summary>
/// Host-owned distiller. Uses Mneme's canonical SystemPrompt + UserPrompt
/// helpers so the LLM knows the response shape. For brevity this sample
/// only asks the LLM for the orientation paragraph and falls back to the
/// SDK's heuristic section assembly; a production distiller would parse a
/// structured LLM response into proper sections.
/// </summary>
internal sealed class LlmDistiller : IDistiller
{
    private readonly IChatClient _chat;
    public LlmDistiller(IChatClient chat) { _chat = chat; }
    public string Id => "sample/llm-distill@1";

    public async Task<ContextBundle> DistillAsync(DistillationRequest request, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DistillationPromptBuilder.SystemPrompt),
            new(ChatRole.User, DistillationPromptBuilder.BuildUserPrompt(request)),
        };
        var response = await _chat.GetResponseAsync(messages,
            new ChatOptions { Temperature = 0, MaxOutputTokens = Math.Min(request.TokenBudget, 1024) }, ct)
            .ConfigureAwait(false);
        var orientationText = response.Text?.Trim() ?? "";

        var heuristic = DistillationPromptBuilder.BuildHeuristicBundle(request, Id);
        return heuristic with
        {
            Orientation = heuristic.Orientation with
            {
                Paragraph = orientationText.Length > 0 ? orientationText : heuristic.Orientation.Paragraph
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
        if (user.Contains("turn=", StringComparison.Ordinal))
        {
            var text = user[(user.IndexOf("turn=", StringComparison.Ordinal) + 5)..];
            if (text.Contains("decided", StringComparison.OrdinalIgnoreCase) || text.Contains("ship", StringComparison.OrdinalIgnoreCase))
            {
                reply = "{\"capture\":[{\"category\":\"Decision\",\"content\":\"" + text.Replace("\"", "'") + "\"}]}";
            }
            else if (text.Contains("preparing for", StringComparison.OrdinalIgnoreCase))
            {
                reply = "{\"capture\":[{\"category\":\"Goal\",\"content\":\"" + text.Replace("\"", "'") + "\"}]}";
            }
            else
            {
                reply = "{\"capture\":[]}";
            }
        }
        else
        {
            reply = "Where we are: the team committed to shipping the v2 redesign in October " +
                    "(deferred from September because two engineers are out). The Q3 planning " +
                    "meeting on Friday is the next milestone.";
        }
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("stub does not stream");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
