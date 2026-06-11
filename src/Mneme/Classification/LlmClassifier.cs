using Microsoft.Extensions.AI;
using Mneme.Contracts;

namespace Mneme.Classification;

using ClassificationLabel = Mneme.Contracts.Classification;

/// <summary>
/// LLM-driven <see cref="IClassifier"/> built on top of
/// <see cref="IChatClient"/> (<c>Microsoft.Extensions.AI.Abstractions</c>).
/// Per AGENTS.md locked decision #10, every LLM call in Mneme goes through
/// <see cref="IChatClient"/>; never hard-code OpenAI / Anthropic / Azure.
/// </summary>
/// <remarks>
/// <para>
/// Prompt is small and deterministic by design — short output,
/// temperature 0. Output is parsed leniently: anything matching a known
/// label name wins; otherwise we fall back to the
/// <see cref="RuleBasedClassifier"/> result so a flaky LLM never blocks
/// ingest.
/// </para>
/// <para>
/// This classifier is intended to run in a background pass (Phase 5
/// distillation worker), not on the hot sync ingest path — calling an
/// LLM synchronously would blow the &lt;50ms p99 budget. The current
/// code nonetheless implements <see cref="IClassifier"/> so an
/// integrator can opt in by registering it in DI.
/// </para>
/// </remarks>
public sealed class LlmClassifier : IClassifier
{
    private readonly IChatClient _chat;
    private readonly RuleBasedClassifier _fallback = new();

    /// <summary>Construct against an existing <see cref="IChatClient"/>.</summary>
    public LlmClassifier(IChatClient chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
    }

    private const string SystemPrompt = """
        You classify the sensitivity of short text snippets for a memory
        system. Answer with exactly one of these labels and nothing else:
          PUBLIC        - safe to share publicly
          INTERNAL      - team-internal reasoning, not customer-facing
          CONFIDENTIAL  - sensitive: NDA, customer data, business-secret
          SECRET        - high-risk: contains or hints at credentials,
                          keys, or tokens
          PII           - contains personally identifiable information
                          (names, emails, phone, SSN, address) that is
                          not also SECRET
        Output the label, in uppercase, on a single line. No prose.
        """;

    /// <inheritdoc/>
    public async Task<ClassificationLabel> ClassifyAsync(
        string content,
        bool hadRedactionHits,
        EpistemicCategory category,
        CancellationToken ct = default)
    {
        if (hadRedactionHits)
        {
            return ClassificationLabel.Secret;
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            return ClassificationLabel.Public;
        }

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, $"Category: {category}\n---\n{content}"),
            };
            var options = new ChatOptions { Temperature = 0f, MaxOutputTokens = 8 };
            var response = await _chat.GetResponseAsync(messages, options, ct).ConfigureAwait(false);

            var text = response.Text?.Trim();
            if (TryParse(text, out var label))
            {
                return label;
            }
        }
        catch
        {
            // Any failure (transport, parse, timeout) falls back to the
            // rule-based result. Classification is never the reason an
            // ingest fails.
        }
        return await _fallback.ClassifyAsync(content, hadRedactionHits, category, ct)
            .ConfigureAwait(false);
    }

    private static bool TryParse(string? text, out ClassificationLabel label)
    {
        label = ClassificationLabel.Public;
        if (string.IsNullOrEmpty(text)) return false;
        var token = text.AsSpan().Trim();
        if (token.StartsWith("PUBLIC", StringComparison.OrdinalIgnoreCase))       { label = ClassificationLabel.Public;       return true; }
        if (token.StartsWith("INTERNAL", StringComparison.OrdinalIgnoreCase))     { label = ClassificationLabel.Internal;     return true; }
        if (token.StartsWith("CONFIDENTIAL", StringComparison.OrdinalIgnoreCase)) { label = ClassificationLabel.Confidential; return true; }
        if (token.StartsWith("SECRET", StringComparison.OrdinalIgnoreCase))       { label = ClassificationLabel.Secret;       return true; }
        if (token.StartsWith("PII", StringComparison.OrdinalIgnoreCase))          { label = ClassificationLabel.Pii;          return true; }
        return false;
    }
}
