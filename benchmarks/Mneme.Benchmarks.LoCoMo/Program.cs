using Mneme.Contracts;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// LoCoMo evaluation harness for Mneme.
///
/// Usage:
///   dotnet run -c Release --project benchmarks/Mneme.Benchmarks.LoCoMo -- [options]
///
/// Options:
///   --dataset &lt;path&gt;   LoCoMo JSON (default: bundled mini fixture).
///   --k &lt;int&gt;          Top-k memory snippets retrieved per question (default 10).
///   --limit &lt;int&gt;      Max samples to evaluate (default: all).
///
/// Real run (turnkey, OpenAI-compatible — OpenAI / Azure / Ollama / vLLM / LM Studio):
///   set MNEME_LLM_BASE_URL   = https://api.openai.com   (or your gateway)
///   set MNEME_LLM_API_KEY    = sk-...
///   set MNEME_LLM_MODEL      = gpt-4o-mini
///   set MNEME_EMBED_MODEL    = text-embedding-3-small
///   set MNEME_EMBED_DIM      = 1536
///   (MNEME_EMBED_BASE_URL / MNEME_EMBED_API_KEY override the chat ones if your
///    embeddings live elsewhere.)
///
/// With no LLM env set, the harness runs in --dry-run mode: offline bag-of-words
/// embedder + echo answerer + token-F1 judge. That exercises the full pipeline
/// with no network but does NOT produce a score comparable to Mem0 / Zep.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var dataset = GetArg(args, "--dataset")
            ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "sample-locomo-mini.json");
        var topK = int.TryParse(GetArg(args, "--k"), out var k) ? k : 10;
        var limit = int.TryParse(GetArg(args, "--limit"), out var l) ? l : int.MaxValue;

        if (!File.Exists(dataset))
        {
            Console.Error.WriteLine($"Dataset not found: {dataset}");
            Console.Error.WriteLine("Download LoCoMo from https://github.com/snap-research/locomo and pass --dataset <path>.");
            return 2;
        }

        var samples = LoCoMoDataset.Load(dataset);
        if (samples.Count > limit) samples = samples.Take(limit).ToList();
        Console.Error.WriteLine($"Loaded {samples.Count} sample(s) from {Path.GetFileName(dataset)}.");

        var (embedder, answerer, judge, mode) = BuildClients();
        Console.Error.WriteLine($"Mode: {mode}");

        var dataRoot = Path.Combine(AppContext.BaseDirectory, "locomo-data");
        var evaluator = new LoCoMoEvaluator(dataRoot, embedder, answerer, judge, topK);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var report = await evaluator.RunAsync(samples, cts.Token).ConfigureAwait(false);
        Console.WriteLine(report.ToConsole());

        if (mode.StartsWith("dry-run", StringComparison.Ordinal))
        {
            Console.WriteLine("NOTE: dry-run numbers are NOT a real LoCoMo score (offline echo answerer +");
            Console.WriteLine("      heuristic judge). Set MNEME_LLM_* env vars for a comparable run.");
        }
        return 0;
    }

    private static (IEmbeddingProvider embedder, IAnswerer answerer, IJudge judge, string mode) BuildClients()
    {
        var baseUrl = Environment.GetEnvironmentVariable("MNEME_LLM_BASE_URL");
        var apiKey = Environment.GetEnvironmentVariable("MNEME_LLM_API_KEY") ?? "";
        var model = Environment.GetEnvironmentVariable("MNEME_LLM_MODEL");

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            return (new OfflineEmbedder(), new OfflineAnswerer(), new OfflineJudge(), "dry-run (offline; not a real score)");
        }

        var embedModel = Environment.GetEnvironmentVariable("MNEME_EMBED_MODEL") ?? "text-embedding-3-small";
        var embedDim = int.TryParse(Environment.GetEnvironmentVariable("MNEME_EMBED_DIM"), out var d) ? d : 1536;
        var embedBaseUrl = Environment.GetEnvironmentVariable("MNEME_EMBED_BASE_URL") ?? baseUrl;
        var embedKey = Environment.GetEnvironmentVariable("MNEME_EMBED_API_KEY") ?? apiKey;

        var chat = new OpenAICompatibleChat(baseUrl, apiKey, model);
        var embedder = new OpenAICompatibleEmbedder(embedBaseUrl, embedKey, embedModel, embedDim);
        return (embedder, chat, chat, $"live ({model} + {embedModel})");
    }

    private static string? GetArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
