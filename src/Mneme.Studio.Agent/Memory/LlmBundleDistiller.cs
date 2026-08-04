using Mneme.Contracts;
using Mneme.Distillation;

namespace Mneme.Studio.Agent.Memory;

/// <summary>
/// Read-side ("sleep" / consolidation) distiller backed by a real LLM. Uses
/// Mneme's own <see cref="DistillationPromptBuilder"/> to build the prompt, asks
/// the LLM (GitHub Copilot over ACP) for the one-paragraph orientation, and
/// keeps Mneme's heuristic section grouping for the per-category bullets. The
/// result is the condensed <see cref="ContextBundle"/> the sleep overlay shows.
/// </summary>
/// <remarks>
/// Degrades to Mneme's pure-heuristic bundle if the LLM call fails.
/// </remarks>
internal sealed class LlmBundleDistiller : IDistiller
{
    private readonly IChatCompletion _chat;

    public LlmBundleDistiller(IChatCompletion chat) => _chat = chat;

    public string Id => $"studio-agent/llm-bundle-distiller[{_chat.Id}]@1";

    public async Task<ContextBundle> DistillAsync(DistillationRequest request, CancellationToken ct = default)
    {
        var heuristic = DistillationPromptBuilder.BuildHeuristicBundle(request, Id);
        try
        {
            var orientation = await _chat.CompleteAsync(
                DistillationPromptBuilder.SystemPrompt,
                DistillationPromptBuilder.BuildUserPrompt(request),
                ct).ConfigureAwait(false);
            orientation = orientation.Trim();
            if (orientation.Length == 0) return heuristic;

            return heuristic with
            {
                Orientation = heuristic.Orientation with { Paragraph = orientation, Distiller = Id },
            };
        }
        catch
        {
            return heuristic;
        }
    }
}
