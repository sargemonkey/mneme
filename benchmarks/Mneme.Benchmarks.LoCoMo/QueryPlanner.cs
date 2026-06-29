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
        Break a question into the minimal set of focused retrieval queries
        needed to answer it. Use multiple only for multi-hop questions; one is
        fine for simple ones. Each query is a short keyword phrase.
        Reply JSON only: {"queries":["...","..."]}  (max 3)
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
            }
        }
        catch { /* fall back to the original question only */ }
        return set;
    }
}
