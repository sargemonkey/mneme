using System.Text;
using System.Text.Json;
using Mneme.Contracts;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// LLM listwise reranker (host-supplied <see cref="IReranker"/>). GitHub Models
/// / OpenAI expose no dedicated cross-encoder endpoint, so this asks the chat
/// model to score the retrieved candidates against the question and return the
/// best ones. A true cross-encoder (local ONNX, Cohere/Jina rerank API) plugs
/// into the same interface — this is the chat-only stand-in for the benchmark.
/// </summary>
public sealed class LlmReranker : IReranker
{
    private readonly IChatCompletion _chat;
    public string Id { get; }

    public LlmReranker(IChatCompletion chat)
    {
        _chat = chat;
        Id = $"reranker/llm-listwise/{_chat.Id}";
    }

    private const string System = """
        You rerank memory snippets by how useful each is for answering a
        question. Read the question and the numbered snippets, then return the
        snippet numbers ordered from most to least relevant. Include only
        snippets that genuinely help; drop irrelevant ones.
        Reply with JSON only: {"order":[<numbers>]}
        """;

    public async Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query, IReadOnlyList<RerankCandidate> candidates, int topK, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return Array.Empty<RerankResult>();

        var sb = new StringBuilder();
        sb.Append("Question: ").AppendLine(query);
        sb.AppendLine("Snippets:");
        for (var i = 0; i < candidates.Count; i++)
        {
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(candidates[i].Text);
        }
        sb.Append("Return the most relevant snippet numbers (up to ").Append(topK).AppendLine("), best first.");

        string reply;
        try { reply = await _chat.CompleteAsync(System, sb.ToString(), ct).ConfigureAwait(false); }
        catch { return Fallback(candidates, topK); }

        var order = ParseOrder(reply, candidates.Count);
        if (order.Count == 0) return Fallback(candidates, topK);

        var results = new List<RerankResult>(Math.Min(topK, order.Count));
        var rank = order.Count;
        foreach (var idx in order)
        {
            results.Add(new RerankResult(candidates[idx - 1].EventId, rank--)); // descending score by position
            if (results.Count >= topK) break;
        }
        return results;
    }

    private static List<int> ParseOrder(string reply, int n)
    {
        var order = new List<int>();
        var seen = new HashSet<int>();
        try
        {
            var start = reply.IndexOf('{');
            var end = reply.LastIndexOf('}');
            if (start < 0 || end <= start) return order;
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            if (doc.RootElement.TryGetProperty("order", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Number) continue;
                    var v = el.GetInt32();
                    if (v >= 1 && v <= n && seen.Add(v)) order.Add(v);
                }
            }
        }
        catch { /* fall back to retrieval order */ }
        return order;
    }

    private static IReadOnlyList<RerankResult> Fallback(IReadOnlyList<RerankCandidate> candidates, int topK)
        => candidates.Take(topK).Select((c, i) => new RerankResult(c.EventId, candidates.Count - i)).ToArray();
}
