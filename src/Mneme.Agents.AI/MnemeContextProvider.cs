using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Mneme.Capture;
using Mneme.Contracts;

namespace Mneme.Agents.AI;

/// <summary>
/// Drop-in <see cref="AIContextProvider"/> that hydrates Mneme's distilled
/// context bundle into a Microsoft Agent Framework agent's chat context.
/// </summary>
/// <remarks>
/// <para>
/// On every agent invocation:
/// <list type="number">
///   <item>Reads the latest <see cref="ContextBundle"/> for the
///         configured workstream via <see cref="IMemoryQueryAPI.DistillAsync"/>.</item>
///   <item>Returns it as a single <see cref="ChatRole.System"/>
///         <see cref="ChatMessage"/> rendered as Markdown so the agent
///         sees prior context as a header before its real conversation.</item>
///   <item>After the agent responds, optionally captures the round-trip
///         through any registered host <see cref="ICapturePolicy"/> (via
///         <see cref="CaptureSession"/>) so the next turn's distillation
///         reflects what just happened.</item>
/// </list>
/// </para>
/// <para>
/// All three operations honor the SDK's locked-decision invariants:
/// the capability token is supplied at construction; capture flows
/// through the same redaction/classification/append-only pipeline as
/// every other ingest path.
/// </para>
/// </remarks>
public sealed class MnemeContextProvider : AIContextProvider
{
    private readonly IMemoryQueryAPI _query;
    private readonly CaptureSession? _capture;
    private readonly WorkstreamId _workstream;
    private readonly CapabilityToken _token;
    private readonly int _tokenBudget;
    private readonly string _systemPromptPrefix;

    public MnemeContextProvider(
        IMemoryQueryAPI query,
        CapabilityToken token,
        WorkstreamId workstream,
        CaptureSession? capture = null,
        int tokenBudget = 4096,
        string systemPromptPrefix = "Prior context from Mneme memory:")
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(token);
        _query = query;
        _token = token;
        _workstream = workstream;
        _capture = capture;
        _tokenBudget = tokenBudget;
        _systemPromptPrefix = systemPromptPrefix;
    }

    /// <inheritdoc/>
    public override async ValueTask<AIContext> InvokingAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var bundle = await _query.DistillAsync(_workstream, new DistillOptions(TokenBudget: _tokenBudget), _token, cancellationToken)
            .ConfigureAwait(false);
        var md = RenderMarkdown(bundle);
        var systemMsg = new ChatMessage(ChatRole.System, _systemPromptPrefix + "\n\n" + md);
        return new AIContext
        {
            Messages = new List<ChatMessage> { systemMsg },
        };
    }

    /// <inheritdoc/>
    public override async ValueTask InvokedAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (_capture is null) return; // no capture wired — caller manages ingest separately.
        var now = DateTimeOffset.UtcNow;
        // Pump request + response through the host's capture policy.
        foreach (var msg in context.RequestMessages ?? Array.Empty<ChatMessage>())
        {
            var turn = new ConversationTurn(
                Speaker: new PrincipalId(msg.AuthorName ?? RoleToSpeaker(msg.Role)),
                Content: msg.Text ?? string.Empty,
                At: now,
                SessionId: null);
            if (!string.IsNullOrWhiteSpace(turn.Content))
            {
                await _capture.ProcessTurnAsync(turn, _workstream, cancellationToken).ConfigureAwait(false);
            }
        }
        foreach (var msg in context.ResponseMessages ?? Array.Empty<ChatMessage>())
        {
            var turn = new ConversationTurn(
                Speaker: new PrincipalId(msg.AuthorName ?? "agent"),
                Content: msg.Text ?? string.Empty,
                At: now,
                SessionId: null);
            if (!string.IsNullOrWhiteSpace(turn.Content))
            {
                await _capture.ProcessTurnAsync(turn, _workstream, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Render a <see cref="ContextBundle"/> as markdown suitable for a system prompt.</summary>
    public static string RenderMarkdown(ContextBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var sb = new StringBuilder();
        sb.Append("**Where we are:** ").AppendLine(bundle.Orientation.Paragraph);
        sb.AppendLine();
        foreach (var section in bundle.Sections)
        {
            sb.Append("### ").AppendLine(section.Title);
            sb.AppendLine(section.Content);
            sb.AppendLine();
        }
        if (bundle.Hints.Hints.Count > 0)
        {
            sb.AppendLine("### Lookup hints");
            foreach (var hint in bundle.Hints.Hints)
            {
                sb.Append("- `").Append(hint.Keyword).Append("` → ").Append(hint.Pointer.Value)
                  .Append(" — ").AppendLine(hint.Context);
            }
        }
        sb.AppendLine();
        sb.Append("_distilled at ").Append(bundle.GeneratedAt.ToString("O"))
          .Append(" by ").Append(bundle.Index.Distiller)
          .Append(" — covers up to event ").Append(bundle.EventsCoveredThrough.Value).AppendLine("_");
        return sb.ToString();
    }

    private static string RoleToSpeaker(ChatRole role) =>
        role == ChatRole.User ? "user" :
        role == ChatRole.Assistant ? "agent" :
        role == ChatRole.System ? "system" : "tool";
}
