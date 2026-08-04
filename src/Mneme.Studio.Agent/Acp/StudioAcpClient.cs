using Acp;
using Acp.Schema;

namespace Mneme.Studio.Agent.Acp;

/// <summary>
/// The ACP <see cref="IClient"/> half of the desktop app. In ACP the client
/// is the editor / host: it drives the conversation and receives the agent's
/// streamed output as <see cref="AgentMessageChunk"/> session updates. We
/// forward each text chunk to a subscriber (the <see cref="AcpAgentConnection"/>)
/// which accumulates it into the reply for the in-flight prompt — and, one
/// level up, into the host's context buffer that Mneme distills.
/// </summary>
/// <remarks>
/// Permission requests are auto-approved here for the self-contained demo. A
/// real editor would surface a prompt to the user; ACP models that as the
/// <see cref="RequestPermissionRequest"/> round-trip.
/// </remarks>
internal sealed class StudioAcpClient : IClient
{
    /// <summary>Raised for every text chunk the agent streams back.</summary>
    public event Action<string>? TextReceived;

    public Task SessionUpdateAsync(SessionNotification n, CancellationToken ct)
    {
        if (n.Update is AgentMessageChunk chunk && chunk.Content is TextContent text)
        {
            TextReceived?.Invoke(text.Text);
        }
        return Task.CompletedTask;
    }

    public Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest r, CancellationToken ct)
        => Task.FromResult(new RequestPermissionResponse { Outcome = new CancelledPermissionOutcome() });
}
