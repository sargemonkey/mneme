using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using Acp;
using Acp.Schema;
using Acp.Streaming;

namespace Mneme.Studio.Agent.Acp;

/// <summary>
/// Owns a live ACP session between this app (the <see cref="IClient"/> / host)
/// and an agent (the <see cref="IAgent"/>). Two transports:
/// <list type="bullet">
///   <item><see cref="StartMockAsync"/> — the bundled <see cref="MockAcpAgent"/>
///         wired in-process over a duplex <see cref="Pipe"/> (no subprocess,
///         no network, no API key).</item>
///   <item><see cref="StartCopilotAsync"/> — spawns real <c>copilot --acp</c>
///         and connects the client over its stdio. GitHub Copilot is the agent;
///         the app is the ACP client / Mneme host.</item>
/// </list>
/// Either way the rest of the app talks to the same <see cref="PromptAsync"/>
/// surface: send text, get the full reply.
/// </summary>
internal sealed class AcpAgentConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private readonly StudioAcpClient _client = new();

    private AgentSideConnection? _agentConn;
    private ClientSideConnection? _clientConn;
    private Process? _proc;
    private SessionId? _sessionId;
    private StringBuilder? _active;

    public string AgentName { get; private set; } = "agent";

    public AcpAgentConnection()
    {
        _client.TextReceived += OnText;
    }

    /// <summary>Wire the bundled in-process mock agent over a duplex pipe.</summary>
    public async Task StartMockAsync(string cwd, CancellationToken ct = default)
    {
        AgentName = "mneme-mock-agent";

        // Two half-duplex pipes crossed over into one full-duplex ACP link.
        var clientToAgent = new Pipe();
        var agentToClient = new Pipe();

        var clientStream = new NdJsonStream(
            agentToClient.Reader.AsStream(),   // client reads what the agent writes
            clientToAgent.Writer.AsStream());  // client writes to the agent
        var agentStream = new NdJsonStream(
            clientToAgent.Reader.AsStream(),   // agent reads what the client writes
            agentToClient.Writer.AsStream());  // agent writes to the client

        _agentConn = new AgentSideConnection(c => new MockAcpAgent(c), agentStream);
        _clientConn = new ClientSideConnection(_ => _client, clientStream);

        await HandshakeAsync(cwd, ct).ConfigureAwait(false);
    }

    /// <summary>Spawn <c>copilot --acp</c> and connect over its stdio.</summary>
    public async Task StartCopilotAsync(string cwd, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("copilot", "--acp --log-level none")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd,
        };
        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'copilot --acp'.");

        // Drain Copilot's stderr so its log lines never block the pipe.
        _ = _proc.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);

        var stream = new NdJsonStream(
            _proc.StandardOutput.BaseStream,   // client reads what Copilot writes
            _proc.StandardInput.BaseStream);   // client writes to Copilot
        _clientConn = new ClientSideConnection(_ => _client, stream);

        await HandshakeAsync(cwd, ct).ConfigureAwait(false);
    }

    private async Task HandshakeAsync(string cwd, CancellationToken ct)
    {
        var init = await _clientConn!.InitializeAsync(new InitializeRequest
        {
            ProtocolVersion = Protocol.Version,
            ClientInfo = new Implementation { Name = "mneme-studio-agent", Version = "0.1.0" },
            ClientCapabilities = new ClientCapabilities(),
        }, ct).ConfigureAwait(false);
        AgentName = init.AgentInfo?.Name ?? AgentName;

        var session = await _clientConn.NewSessionAsync(new NewSessionRequest
        {
            Cwd = cwd,
            McpServers = Array.Empty<McpServer>(),
        }, ct).ConfigureAwait(false);
        _sessionId = session.SessionId;
    }

    /// <summary>
    /// Send one prompt and return the agent's full reply text (all streamed
    /// chunks concatenated). Serialized: one prompt at a time.
    /// </summary>
    public async Task<string> PromptAsync(string text, CancellationToken ct = default)
    {
        if (_clientConn is null || _sessionId is null)
        {
            throw new InvalidOperationException("Call StartMockAsync/StartCopilotAsync before PromptAsync.");
        }

        await _promptGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _active = new StringBuilder();
            await _clientConn.PromptAsync(new PromptRequest
            {
                SessionId = _sessionId.Value,
                Prompt = new ContentBlock[] { new TextContent { Text = text } },
            }, ct).ConfigureAwait(false);
            return _active.ToString();
        }
        finally
        {
            _active = null;
            _promptGate.Release();
        }
    }

    private void OnText(string chunk) => _active?.Append(chunk);

    public async ValueTask DisposeAsync()
    {
        _client.TextReceived -= OnText;
        if (_clientConn is not null) await _clientConn.DisposeAsync().ConfigureAwait(false);
        if (_agentConn is not null) await _agentConn.DisposeAsync().ConfigureAwait(false);
        if (_proc is not null)
        {
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
            _proc.Dispose();
        }
        _promptGate.Dispose();
    }
}
