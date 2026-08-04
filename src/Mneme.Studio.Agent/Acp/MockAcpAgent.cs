using System.Text;
using Acp;
using Acp.Schema;

namespace Mneme.Studio.Agent.Acp;

/// <summary>
/// A bundled, in-process ACP <see cref="IAgent"/> so the desktop app is fully
/// self-contained (no external agent, no API key). It plays the role a real
/// coding agent (gemini-cli, claude-code-acp, …) would: it receives a prompt
/// and streams back a reply as <see cref="AgentMessageChunk"/> updates.
/// </summary>
/// <remarks>
/// The reply is intentionally written the way a competent coding agent talks —
/// it restates the user's intent and commits to a plan/decision — so the
/// downstream <c>HeuristicSessionDistiller</c> has genuine epistemic content
/// (decisions, plans, facts) to extract. Swap this for a subprocess agent over
/// stdio to talk to a real model; the client half does not change.
/// </remarks>
internal sealed class MockAcpAgent : IAgent
{
    private readonly AgentSideConnection _client;

    public MockAcpAgent(AgentSideConnection client) => _client = client;

    public Task<InitializeResponse> InitializeAsync(InitializeRequest req, CancellationToken ct)
        => Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = Protocol.Version,
            AgentInfo = new Implementation { Name = "mneme-mock-agent", Version = "0.1.0" },
            AgentCapabilities = new AgentCapabilities(),
        });

    public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest req, CancellationToken ct)
        => Task.FromResult(new NewSessionResponse { SessionId = new SessionId(Guid.NewGuid().ToString("n")) });

    public async Task<PromptResponse> PromptAsync(PromptRequest req, CancellationToken ct)
    {
        var prompt = ExtractText(req.Prompt);
        var reply = Compose(prompt);

        // Stream the reply back in a few chunks, the way a real agent would.
        foreach (var chunk in Chunk(reply))
        {
            await _client.SessionUpdateAsync(new SessionNotification
            {
                SessionId = req.SessionId,
                Update = new AgentMessageChunk { Content = new TextContent { Text = chunk } },
            }, ct).ConfigureAwait(false);
        }

        return new PromptResponse { StopReason = StopReason.EndTurn };
    }

    public Task<AuthenticateResponse?> AuthenticateAsync(AuthenticateRequest req, CancellationToken ct)
        => Task.FromResult<AuthenticateResponse?>(new AuthenticateResponse());

    public Task CancelAsync(CancelNotification n, CancellationToken ct) => Task.CompletedTask;

    private static string ExtractText(IEnumerable<ContentBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            if (b is TextContent t) sb.Append(t.Text);
        }
        return sb.ToString().Trim();
    }

    // A canned-but-plausible coding-agent reply. It echoes the intent and,
    // when the prompt reads like a choice or a task, commits to a decision or
    // plan so the distiller extracts something durable.
    private static string Compose(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "I'm ready when you are — tell me what you'd like to build.";
        }

        var lower = prompt.ToLowerInvariant();
        var sb = new StringBuilder();
        sb.Append("Got it. ");

        if (lower.Contains("should we") || lower.Contains("decide") || lower.Contains("choose")
            || lower.Contains(" or "))
        {
            sb.Append("Decision: I'll go with the first option you named, because it keeps the ")
              .Append("blast radius small and is easy to reverse. ");
        }
        else if (lower.Contains("implement") || lower.Contains("add") || lower.Contains("build")
                 || lower.Contains("fix") || lower.Contains("write"))
        {
            sb.Append("Plan: I'll implement that in small, reviewable steps and add a test for ")
              .Append("each public change. ");
        }

        sb.Append("Here's my read: \"")
          .Append(prompt.Length > 160 ? prompt[..160] + "…" : prompt)
          .Append("\". I'll proceed on that basis and report back.");
        return sb.ToString();
    }

    private static IEnumerable<string> Chunk(string s)
    {
        const int size = 48;
        for (var i = 0; i < s.Length; i += size)
        {
            yield return s.Substring(i, Math.Min(size, s.Length - i));
        }
    }
}
