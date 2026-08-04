using Mneme.Studio.Agent.Acp;

namespace Mneme.Studio.Agent.Memory;

/// <summary>Minimal single-shot chat-completion abstraction the distillers use.</summary>
internal interface IChatCompletion
{
    string Id { get; }
    Task<string> CompleteAsync(string system, string user, CancellationToken ct = default);

    /// <summary>Start any backing process early so the first real call isn't cold. No-op by default.</summary>
    Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Uses real GitHub Copilot — over its native <c>copilot --acp</c> ACP server —
/// as a plain chat-completion backend for Mneme's distillation logic. This is
/// deliberately a <em>separate</em> Copilot session from the visible
/// conversation: the distiller sends Mneme's extraction prompt and reads back
/// structured JSON, so we never store Copilot's conversational output verbatim —
/// we run Mneme's own distillation over the turn-based conversation with an LLM
/// doing the extraction.
/// </summary>
internal sealed class CopilotChatCompletion : IChatCompletion, IAsyncDisposable
{
    private readonly AcpAgentConnection _conn = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private bool _started;

    public string Id => "copilot-acp";

    /// <summary>Start the backing Copilot process early so the first real call isn't cold.</summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (_started) return;
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_started) return;
            await _conn.StartCopilotAsync(Environment.CurrentDirectory, ct).ConfigureAwait(false);
            _started = true;
        }
        finally { _startGate.Release(); }
    }

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        await WarmUpAsync(ct).ConfigureAwait(false);
        // ACP prompts are a single text block; fold the system instruction in.
        var prompt = string.IsNullOrEmpty(system) ? user : system + "\n\n" + user;
        return await _conn.PromptAsync(prompt, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _startGate.Dispose();
        return _conn.DisposeAsync();
    }
}
