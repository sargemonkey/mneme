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

/// <summary>
/// Hybrid judge: counts a prediction correct if the inner (LLM) judge says yes
/// OR token-F1 against the gold clears a threshold. This matches LoCoMo's more
/// lenient official scoring, which gives partial credit for list/near-miss
/// answers a strict binary LLM judge would fail.
/// </summary>
public sealed class HybridJudge : IJudge
{
    private readonly IJudge _inner;
    private readonly OfflineJudge _f1;
    public HybridJudge(IJudge inner, double f1Threshold = 0.5)
    {
        _inner = inner;
        _f1 = new OfflineJudge(f1Threshold);
    }
    public string Id => $"hybrid({_inner.Id} OR {_f1.Id})";

    public async Task<bool> IsCorrectAsync(string question, string gold, string predicted, CancellationToken ct = default)
    {
        if (await _f1.IsCorrectAsync(question, gold, predicted, ct).ConfigureAwait(false)) return true;
        return await _inner.IsCorrectAsync(question, gold, predicted, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// LLM judge whose leniency is aligned with the LoCoMo J-score methodology used
/// by Mem0's public memory-benchmarks (Apache-2.0) so our numbers are
/// apples-to-apples with their reported figures. Rules (re-expressed, not
/// copied): partial credit (≥1 gold list item present ⇒ correct), paraphrases /
/// same-valence sentiments count, extra detail is fine, dates within ~14 days
/// (durations within ~50%) match, and same-referent answers count. Only WRONG
/// when the prediction shares no correct item or addresses a different topic.
/// </summary>
public sealed class MemAlignedJudge : IJudge
{
    private readonly IChatCompletion _chat;
    public MemAlignedJudge(IChatCompletion chat) => _chat = chat;
    public string Id => $"mem0-aligned/{_chat.Id}";

    private const string System =
        "You evaluate conversational-memory answer recall. Label the generated answer " +
        "CORRECT or WRONG against the gold answer, judging meaning not wording. Be lenient:\n" +
        "1. PARTIAL CREDIT: if the generated answer contains at least one correct item from " +
        "the gold answer, it is CORRECT (1 of 2, 2 of 4, etc.). WRONG only if it contains none.\n" +
        "2. PARAPHRASES: the same concept in different words is CORRECT. Emotions/sentiments in " +
        "the same positive/negative valence count as equivalent (proud = fulfilled = accomplished).\n" +
        "3. EXTRA DETAIL is fine — a longer answer that still conveys the gold's core fact is CORRECT.\n" +
        "4. DATE TOLERANCE: dates within ~14 days match; durations within ~50% match " +
        "(5 months ≈ six months); a specific date consistent with a vague reference is CORRECT.\n" +
        "5. SEMANTIC OVERLAP / SAME REFERENT: if the generated answer addresses the same topic and " +
        "identifies the same entity/person/concept as the gold, it is CORRECT even with different " +
        "phrasing or added description.\n" +
        "Mark WRONG only if the generated answer shares zero correct items with the gold or is about " +
        "a genuinely different topic. Reply with exactly 'CORRECT' or 'WRONG'.";

    public async Task<bool> IsCorrectAsync(string question, string gold, string predicted, CancellationToken ct = default)
    {
        var user = $"Question: {question}\nGold answer: {gold}\nGenerated answer: {predicted}\nLabel (CORRECT/WRONG)?";
        var reply = await _chat.CompleteAsync(System, user, ct).ConfigureAwait(false);
        // Look at the first decisive token; default to CORRECT-leaning only on explicit CORRECT.
        var t = reply.TrimStart();
        if (t.StartsWith("WRONG", StringComparison.OrdinalIgnoreCase)) return false;
        if (t.StartsWith("CORRECT", StringComparison.OrdinalIgnoreCase)) return true;
        // Fallback: contains CORRECT but not WRONG.
        var hasCorrect = t.Contains("CORRECT", StringComparison.OrdinalIgnoreCase);
        var hasWrong = t.Contains("WRONG", StringComparison.OrdinalIgnoreCase);
        return hasCorrect && !hasWrong;
    }
}

// ---------------------------------------------------------------------------
// OpenAI-compatible HTTP implementations — turnkey for a REAL run. They speak
// the chat/completions + embeddings REST shape (path configurable), so they
// work against OpenAI, Azure, GitHub Models, Ollama, vLLM, or any compatible
// gateway. All requests flow through a shared ThrottledHttp for rate limiting
// + retry, so a long run survives GitHub Models' free-tier limits.
// ---------------------------------------------------------------------------

/// <summary>Generic single-shot chat completion (used by the session distiller).</summary>
public interface IChatCompletion
{
    string Id { get; }
    Task<string> CompleteAsync(string system, string user, CancellationToken ct = default);
}

public sealed class OpenAICompatibleChat : IAnswerer, IJudge, IChatCompletion
{
    private readonly ThrottledHttp _http;
    private readonly string _model;
    private readonly string _chatPath;
    private readonly bool _mem0Answer;
    public string Id { get; }

    public OpenAICompatibleChat(ThrottledHttp http, string model, string chatPath = "v1/chat/completions", bool mem0Answer = false)
    {
        _http = http;
        _model = model;
        _chatPath = chatPath;
        _mem0Answer = mem0Answer;
        Id = $"openai-compatible/{model}" + (mem0Answer ? "+mem0-answer" : "");
    }

    // Mem0-aligned answer prompt: re-expresses the multi-step, anti-abstention
    // reasoning of Mem0's public LoCoMo answerer (mem0ai/memory-benchmarks,
    // Apache 2.0) in Mneme's own words so a published comparison matches their
    // answer procedure, not just their judge. No prompt text is copied verbatim.
    private const string Mem0AnswerSystem =
        "You answer a question using retrieved memories from past conversations. Work through " +
        "these steps in order:\n" +
        "1. SCAN EVERY memory below, first to last — relevant details are often scattered far down " +
        "the list; weight all positions equally. Each snippet is prefixed with [YYYY-MM-DD], the " +
        "date it was said.\n" +
        "2. VERIFY ATTRIBUTION: make sure each memory you use is about the person the question asks " +
        "about; do not borrow a fact stated about someone else. In two-person chats, both speakers' " +
        "actions are valid evidence, but keep the attribution correct.\n" +
        "3. COMBINE facts across memories about the same topic; for list/count questions extract " +
        "EVERY distinct item from ALL memories and enumerate before counting. Connect related facts " +
        "(a 'nearby lake' named elsewhere is that lake; 'bought in Paris' implies France).\n" +
        "4. PREFER THE MOST SPECIFIC answer — a name, title, number, or specific activity beats a " +
        "generic description. Compare each candidate to the SPECIFIC question, not its list position. " +
        "Report what someone actually DID, not what was merely offered or available.\n" +
        "5. TEMPORAL GROUNDING: these conversations occurred around the dates shown (2022–2024). " +
        "Compute intervals from the snippet dates; when something was 'shared/mentioned' on a date, " +
        "the event is usually shortly before it. Never invent dates or output 2025+.\n" +
        "6. INCLUSION: for lists/counts, include every supported item — the common mistake is " +
        "dropping valid items by over-filtering. The question assumes something happened; find what.\n" +
        "7. COMMIT: give a direct, specific answer — a word, phrase, date, or short list. Do NOT say " +
        "'not specified', 'not mentioned', or 'I don't know' when any memory holds relevant " +
        "information; give the best-supported answer. Never invent names/dates absent from the " +
        "memories. Put the final answer after 'ANSWER:'.";

    public async Task<string> AnswerAsync(string question, IReadOnlyList<string> context, CancellationToken ct = default)
    {
        var ctx = context.Count == 0 ? "(no relevant memory retrieved)"
            : string.Join("\n", context.Select((c, i) => $"[{i + 1}] {c}"));

        if (_mem0Answer)
        {
            var mUser = $"Memories:\n{ctx}\n\nQuestion: {question}\nWork through the steps, then answer after 'ANSWER:'.";
            var raw = await CompleteAsync(Mem0AnswerSystem, mUser, ct).ConfigureAwait(false);
            return ExtractFinalAnswer(raw);
        }

        var system = "You answer questions about a long personal conversation using the retrieved " +
                     "memory snippets as evidence. Each snippet is prefixed with [YYYY-MM-DD], the " +
                     "date it was said — use these for any 'when', 'how long', or date-difference " +
                     "question (compute the interval yourself). Reason over the snippets to infer the " +
                     "answer even when it is not stated verbatim (preferences, likelihoods, or facts " +
                     "implied across multiple snippets). For list questions, include every item the " +
                     "snippets support. Answer concisely — a word, phrase, date, or short list. Only " +
                     "answer \"I don't know\" if the snippets give no basis at all.";
        var user = $"Memory snippets:\n{ctx}\n\nQuestion: {question}\nShort answer:";
        return await CompleteAsync(system, user, ct).ConfigureAwait(false);
    }

    // The Mem0-style prompt emits chain-of-thought then "ANSWER:"; keep only the
    // final answer for grading (fall back to the whole reply if the marker is absent).
    private static string ExtractFinalAnswer(string raw)
    {
        var idx = raw.LastIndexOf("ANSWER:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return raw.Trim();
        return raw[(idx + "ANSWER:".Length)..].Trim();
    }

    public async Task<bool> IsCorrectAsync(string question, string gold, string predicted, CancellationToken ct = default)
    {
        var system = "You grade answers to questions about a long personal conversation. Mark " +
                     "'YES' if the predicted answer contains or entails the gold answer's " +
                     "information — extra detail is fine, and wording, formatting, or order may " +
                     "differ. For list answers, YES if the prediction includes the gold items " +
                     "(even among others). For dates, YES if it refers to the same time. Mark " +
                     "'NO' only if the prediction contradicts, omits, or fails to convey the gold " +
                     "answer. Reply with exactly 'YES' or 'NO'.";
        var user = $"Question: {question}\nGold answer: {gold}\nPredicted answer: {predicted}\nCorrect (YES/NO)?";
        var reply = await CompleteAsync(system, user, ct).ConfigureAwait(false);
        return reply.TrimStart().StartsWith("YES", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
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
        using var doc = await _http.PostJsonAsync(_chatPath, body, ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}

/// <summary>OpenAI-compatible embedding provider (Mneme's <see cref="IEmbeddingProvider"/>).</summary>
public sealed class OpenAICompatibleEmbedder : IEmbeddingProvider
{
    private readonly ThrottledHttp _http;
    private readonly string _model;
    private readonly string _embedPath;
    public string Id { get; }
    public int Dimensions { get; }

    public OpenAICompatibleEmbedder(ThrottledHttp http, string model, int dimensions, string embedPath = "v1/embeddings")
    {
        _http = http;
        _model = model;
        _embedPath = embedPath;
        Dimensions = dimensions;
        Id = $"openai-compatible/{model}@{dimensions}";
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        using var doc = await _http.PostJsonAsync(_embedPath, new { model = _model, input = texts }, ct).ConfigureAwait(false);
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

/// <summary>Trivial offline chat completion for --dry-run (no network).</summary>
public sealed class OfflineChatCompletion : IChatCompletion
{
    public string Id => "offline/passthrough";
    public Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
        => Task.FromResult("{\"facts\":[]}");
}
