using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
///   <item>Reads the latest <see cref="ContextBundle"/> for the configured
///         workstream via <see cref="IMemoryQueryAPI.DistillAsync"/>.</item>
///   <item>Returns it as a single <see cref="ChatRole.System"/>
///         <see cref="ChatMessage"/> rendered as Markdown so the agent
///         sees prior context as a header before its real conversation.</item>
/// </list>
/// </para>
/// <para>
/// The provider is intentionally read-only on the MAF surface. Capture flows
/// through <see cref="IMemoryAgent.DistillSessionAsync"/> on the host's own
/// schedule (typically a periodic worker that hands Mneme the entries that
/// have accumulated in the session since the last watermark). This keeps
/// the "host owns the chat log; Mneme stores only the interpretation"
/// invariant from being undermined by an InvokedAsync hook that quietly
/// duplicates turns into the event log on every call.
/// </para>
/// </remarks>
public sealed class MnemeContextProvider : AIContextProvider
{
    private readonly IMemoryQueryAPI _query;
    private readonly WorkstreamId _workstream;
    private readonly CapabilityToken _token;
    private readonly int _tokenBudget;
    private readonly string _systemPromptPrefix;

    public MnemeContextProvider(
        IMemoryQueryAPI query,
        CapabilityToken token,
        WorkstreamId workstream,
        int tokenBudget = 4096,
        string systemPromptPrefix = "Prior context from Mneme memory:")
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(token);
        _query = query;
        _token = token;
        _workstream = workstream;
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
}
