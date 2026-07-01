using System.Text.Json;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// Decomposes a complex question into 1–3 focused sub-queries so multi-hop
/// questions can be answered by retrieving each hop separately and unioning
/// the evidence — the recall lever the miss-analysis pointed to (rerank only
/// reorders; it can't surface a fact that was never retrieved). The original
/// question is always included as one query.
/// </summary>
public sealed class QueryPlanner
{
    private readonly IChatCompletion _chat;
    public QueryPlanner(IChatCompletion chat) { _chat = chat; }

    private const string System = """
        You expand a question into retrieval queries that will find the answer in
        a long conversation. Produce:
        - "queries": the minimal focused sub-queries (short keyword phrases; use
          several only for multi-hop questions, one for simple ones).
        - "hyde": ONE hypothetical answer sentence — a plausible, specific
          sentence that would appear in the conversation if it stated the answer
          (this is used as an extra retrieval probe; it need not be true).
        Reply JSON only: {"queries":["...","..."],"hyde":"..."}  (max 3 queries)
        """;

    public async Task<IReadOnlyList<string>> PlanAsync(string question, CancellationToken ct = default)
    {
        var set = new List<string> { question };
        try
        {
            var reply = await _chat.CompleteAsync(System, "Question: " + question, ct).ConfigureAwait(false);
            var start = reply.IndexOf('{'); var end = reply.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
                if (doc.RootElement.TryGetProperty("queries", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var q in arr.EnumerateArray())
                    {
                        var s = q.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && !set.Contains(s, StringComparer.OrdinalIgnoreCase))
                            set.Add(s.Trim());
                        if (set.Count >= 4) break;
                    }
                }
                // HyDE: a hypothetical answer sentence is often lexically/semantically
                // closer to the stored fact than the question is — a strong recall probe.
                if (doc.RootElement.TryGetProperty("hyde", out var h) && h.ValueKind == JsonValueKind.String)
                {
                    var hyde = h.GetString();
                    if (!string.IsNullOrWhiteSpace(hyde)) set.Add(hyde.Trim());
                }
            }
        }
        catch { /* fall back to the original question only */ }
        return set;
    }
}
