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
///   --out &lt;dir&gt;        Output directory for results.jsonl + results.csv
///                      (default: &lt;build-dir&gt;/locomo-results).
///   --fresh            Ignore + overwrite any prior results in --out.
///
/// Resume: graded questions are appended to results.jsonl as they complete, so
/// re-running the same command skips everything already done (survives rate
/// limits and Ctrl-C).
///
/// Real run with GitHub Models (uses Copilot's catalog — gpt-4o-mini, etc.):
///   set MNEME_LLM_PROVIDER = github-models
///   set GITHUB_TOKEN       = &lt;a GitHub token with the models:read scope&gt;
///   (optional) MNEME_LLM_MODEL=openai/gpt-4o-mini  MNEME_EMBED_MODEL=openai/text-embedding-3-small
///
/// Real run with any other OpenAI-compatible endpoint (OpenAI / Azure / Ollama / vLLM):
///   set MNEME_LLM_BASE_URL   = https://api.openai.com
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
        var outDir = GetArg(args, "--out") ?? Path.Combine(AppContext.BaseDirectory, "locomo-results");
        var store = new RunStore(Path.Combine(outDir, "results.jsonl"));
        if (args.Contains("--fresh"))
        {
            store.Delete();
            Console.Error.WriteLine("--fresh: cleared any prior results, starting over.");
        }

        var evaluator = new LoCoMoEvaluator(dataRoot, embedder, answerer, judge, topK);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        LoCoMoReport report;
        try
        {
            report = await evaluator.RunAsync(samples, store, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Interrupted. Progress saved to {store.JsonlPath}.");
            Console.Error.WriteLine("Re-run the same command to resume where you left off.");
            return 130;
        }

        var csvPath = store.ExportCsv(store.LoadExisting().Values.OrderBy(r => r.SampleId).ThenBy(r => r.QuestionIndex));
        var mdPath = Path.ChangeExtension(store.JsonlPath, ".md");
        File.WriteAllText(mdPath, report.ToMarkdown());
        Console.WriteLine(report.ToConsole());
        Console.Error.WriteLine($"Per-question grades: {store.JsonlPath}");
        Console.Error.WriteLine($"CSV export         : {csvPath}");
        Console.Error.WriteLine($"Markdown report    : {mdPath}");

        if (mode.StartsWith("dry-run", StringComparison.Ordinal))
        {
            Console.WriteLine("NOTE: dry-run numbers are NOT a real LoCoMo score (offline echo answerer +");
            Console.WriteLine("      heuristic judge). Set MNEME_LLM_* env vars for a comparable run.");
        }
        return 0;
    }

    private static (IEmbeddingProvider embedder, IAnswerer answerer, IJudge judge, string mode) BuildClients()
    {
        var provider = (Environment.GetEnvironmentVariable("MNEME_LLM_PROVIDER") ?? "").Trim().ToLowerInvariant();

        // --- GitHub Models (the "use Copilot / GitHub models" path) ---------
        // OpenAI-compatible inference over https://models.github.ai/inference,
        // authenticated with a GitHub token carrying the models:read scope.
        // Model ids are publisher-prefixed (openai/gpt-4o-mini).
        if (provider is "github-models" or "github" or "copilot")
        {
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                ?? Environment.GetEnvironmentVariable("MNEME_LLM_API_KEY")
                ?? "";
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("github-models requires a GitHub token with 'models:read' in GITHUB_TOKEN.");
                Console.Error.WriteLine("Falling back to offline dry-run.");
                return (new OfflineEmbedder(), new OfflineAnswerer(), new OfflineJudge(), "dry-run (no GITHUB_TOKEN)");
            }
            const string ghBase = "https://models.github.ai";
            var ghHeaders = new Dictionary<string, string>
            {
                ["Accept"] = "application/vnd.github+json",
                ["X-GitHub-Api-Version"] = "2026-03-10",
            };
            var chatModel = Environment.GetEnvironmentVariable("MNEME_LLM_MODEL") ?? "openai/gpt-4o-mini";
            var embModel = Environment.GetEnvironmentVariable("MNEME_EMBED_MODEL") ?? "openai/text-embedding-3-small";
            var embDim = int.TryParse(Environment.GetEnvironmentVariable("MNEME_EMBED_DIM"), out var gd) ? gd : 1536;

            var ghChat = new OpenAICompatibleChat(ghBase, token, chatModel, "inference/chat/completions", ghHeaders);
            var ghEmbed = new OpenAICompatibleEmbedder(ghBase, token, embModel, embDim, "inference/embeddings", ghHeaders);
            return (ghEmbed, ghChat, ghChat, $"github-models ({chatModel} + {embModel})");
        }

        // --- Generic OpenAI-compatible endpoint -----------------------------
        var baseUrl = Environment.GetEnvironmentVariable("MNEME_LLM_BASE_URL");
        var apiKey = Environment.GetEnvironmentVariable("MNEME_LLM_API_KEY") ?? "";
        var model = Environment.GetEnvironmentVariable("MNEME_LLM_MODEL");

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            return (new OfflineEmbedder(), new OfflineAnswerer(), new OfflineJudge(), "dry-run (offline; not a real score)");
        }

        var embedModel = Environment.GetEnvironmentVariable("MNEME_EMBED_MODEL") ?? "text-embedding-3-small";
        var embedDim = int.TryParse(Environment.GetEnvironmentVariable("MNEME_EMBED_DIM"), out var dd) ? dd : 1536;
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
