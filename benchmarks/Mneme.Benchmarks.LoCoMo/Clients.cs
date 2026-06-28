using System.Net.Http.Json;
using System.Text.Json;
using Mneme.Contracts;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>Generates an answer to a question given retrieved memory context.</summary>
public interface IAnswerer
{
    string Id { get; }
    Task<string> AnswerAsync(string question, IReadOnlyList<string> context, CancellationToken ct = default);
}

/// <summary>Judges whether a predicted answer matches the gold answer.</summary>
public interface IJudge
{
    string Id { get; }
    Task<bool> IsCorrectAsync(string question, string gold, string predicted, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Offline implementations — used by --dry-run. They let the full pipeline run
// (and be verified) with NO LLM and NO network. They do NOT produce a
// comparable LoCoMo score: the offline answerer just echoes the top retrieved
// snippet, and the offline judge is a token-F1 overlap heuristic. Use a real
// model (below) for a number you can put next to Mem0 / Zep.
// ---------------------------------------------------------------------------

public sealed class OfflineAnswerer : IAnswerer
{
    public string Id => "offline/echo-top-context";
    public Task<string> AnswerAsync(string question, IReadOnlyList<string> context, CancellationToken ct = default)
        => Task.FromResult(context.Count > 0 ? context[0] : "");
}

public sealed class OfflineJudge : IJudge
{
    private readonly double _threshold;
    public OfflineJudge(double f1Threshold = 0.5) { _threshold = f1Threshold; }
    public string Id => $"offline/token-f1>={_threshold:0.0}";

    public Task<bool> IsCorrectAsync(string question, string gold, string predicted, CancellationToken ct = default)
    {
        var g = Tokens(gold);
        var p = Tokens(predicted);
        if (g.Count == 0) return Task.FromResult(p.Count == 0);
        if (p.Count == 0) return Task.FromResult(false);
        // Exact normalized containment shortcut.
        if (predicted.Contains(gold, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(true);
        var overlap = g.Intersect(p).Count();
        var precision = (double)overlap / p.Count;
        var recall = (double)overlap / g.Count;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return Task.FromResult(f1 >= _threshold);
    }

    private static HashSet<string> Tokens(string s) =>
        s.ToLowerInvariant()
         .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries)
         .ToHashSet(StringComparer.Ordinal);
}

// ---------------------------------------------------------------------------
// OpenAI-compatible HTTP implementations — turnkey for a REAL run. They speak
// the /v1/chat/completions and /v1/embeddings REST shape, so they work against
// OpenAI, Azure OpenAI, Ollama, vLLM, LM Studio, or any compatible gateway by
// pointing the base URL + key at it. No SDK dependency; just HttpClient + JSON.
// ---------------------------------------------------------------------------

public sealed class OpenAICompatibleChat : IAnswerer, IJudge, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _chatPath;
    public string Id { get; }

    public OpenAICompatibleChat(string baseUrl, string apiKey, string model,
        string chatPath = "v1/chat/completions", IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(120) };
        if (!string.IsNullOrEmpty(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        }
        if (extraHeaders is not null)
        {
            foreach (var (k, v) in extraHeaders) _http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
        }
        _model = model;
        _chatPath = chatPath;
        Id = $"openai-compatible/{model}";
    }

    public async Task<string> AnswerAsync(string question, IReadOnlyList<string> context, CancellationToken ct = default)
    {
        var ctx = context.Count == 0 ? "(no relevant memory retrieved)"
            : string.Join("\n", context.Select((c, i) => $"[{i + 1}] {c}"));
        var system = "You answer questions using ONLY the provided memory snippets. " +
                     "Be concise — a few words or a short phrase. If the snippets don't contain " +
                     "the answer, say you don't know.";
        var user = $"Memory:\n{ctx}\n\nQuestion: {question}\nAnswer:";
        return await ChatAsync(system, user, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsCorrectAsync(string question, string gold, string predicted, CancellationToken ct = default)
    {
        var system = "You grade answers. Reply with exactly 'YES' if the predicted answer is " +
                     "semantically correct given the gold answer, otherwise 'NO'. Minor wording " +
                     "differences are fine; the meaning must match.";
        var user = $"Question: {question}\nGold answer: {gold}\nPredicted answer: {predicted}\nCorrect (YES/NO)?";
        var reply = await ChatAsync(system, user, ct).ConfigureAwait(false);
        return reply.TrimStart().StartsWith("YES", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ChatAsync(string system, string user, CancellationToken ct)
    {
        var body = new
        {
            model = _model,
            temperature = 0,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };
        using var resp = await _http.PostAsJsonAsync(_chatPath, body, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>OpenAI-compatible embedding provider (Mneme's <see cref="IEmbeddingProvider"/>).</summary>
public sealed class OpenAICompatibleEmbedder : IEmbeddingProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _embedPath;
    public string Id { get; }
    public int Dimensions { get; }

    public OpenAICompatibleEmbedder(string baseUrl, string apiKey, string model, int dimensions,
        string embedPath = "v1/embeddings", IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(120) };
        if (!string.IsNullOrEmpty(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        }
        if (extraHeaders is not null)
        {
            foreach (var (k, v) in extraHeaders) _http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
        }
        _model = model;
        _embedPath = embedPath;
        Dimensions = dimensions;
        Id = $"openai-compatible/{model}@{dimensions}";
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(_embedPath,
            new { model = _model, input = texts }, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var data = doc.RootElement.GetProperty("data");
        var result = new List<ReadOnlyMemory<float>>(texts.Count);
        foreach (var item in data.EnumerateArray())
        {
            var arr = item.GetProperty("embedding");
            var vec = new float[arr.GetArrayLength()];
            var i = 0;
            foreach (var f in arr.EnumerateArray()) vec[i++] = f.GetSingle();
            result.Add(vec);
        }
        return result;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Deterministic offline embedder for --dry-run: hashed bag-of-words. Lets
/// retrieval run with no embedding endpoint. Not for real scoring.
/// </summary>
public sealed class OfflineEmbedder : IEmbeddingProvider
{
    public string Id => "offline/bag-of-words@256";
    public int Dimensions => 256;

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<ReadOnlyMemory<float>>(texts.Count);
        foreach (var t in texts)
        {
            var v = new float[Dimensions];
            foreach (var tok in t.ToLowerInvariant().Split(
                new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?' }, StringSplitOptions.RemoveEmptyEntries))
            {
                v[(uint)tok.GetHashCode() % Dimensions] += 1f;
            }
            result.Add(v);
        }
        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(result);
    }
}
