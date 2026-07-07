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
///   --categories &lt;a,b&gt; Only evaluate questions in these category labels
///                      (e.g. adversarial,multi-hop). Default: all.
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

        var ingestArg = (GetArg(args, "--ingest") ?? "facts").Trim().ToLowerInvariant();
        var ingestMode = ingestArg switch
        {
            "turns" => IngestMode.Turns,
            "both" => IngestMode.Both,
            _ => IngestMode.Facts,
        };

        var (embedder, answerer, judge, distiller, mode) = BuildClients(args);

        // Optional judge override: f1 = token-F1 only (no LLM); hybrid = F1 OR LLM.
        var judgeArg = (GetArg(args, "--judge") ?? "llm").Trim().ToLowerInvariant();
        judge = judgeArg switch
        {
            "f1" => new OfflineJudge(),
            "hybrid" => new HybridJudge(judge),
            _ => judge,
        };

        var rerankArg = (GetArg(args, "--reranker") ?? (args.Contains("--rerank") ? "llm" : "off")).Trim().ToLowerInvariant();
        IReranker? reranker = rerankArg switch
        {
            "onnx" => BuildOnnxReranker(),
            "llm" => answerer is IChatCompletion chat ? new LlmReranker(chat) : null,
            _ => null,
        };
        var planner = args.Contains("--iterative") && answerer is IChatCompletion pchat
            ? new QueryPlanner(pchat) : null;
        var concurrency = int.TryParse(GetArg(args, "--concurrency"), out var cc) ? cc : 1;
        var recallRetry = args.Contains("--recall-retry");
        var categoriesArg = GetArg(args, "--categories");
        var categories = string.IsNullOrWhiteSpace(categoriesArg) ? null
            : new HashSet<string>(categoriesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
        Console.Error.WriteLine($"Mode: {mode}   ingest: {ingestMode.ToString().ToLowerInvariant()}" +
                                $"   judge: {judgeArg}   concurrency: {concurrency}" +
                                (reranker is not null ? "   rerank: on" : "") +
                                (planner is not null ? "   iterative: on" : "") +
                                (recallRetry ? "   recall-retry: on" : "") +
                                (categories is not null ? $"   categories: {string.Join(",", categories)}" : ""));

        var dataRoot = Path.Combine(AppContext.BaseDirectory, "locomo-data");
        var outDir = GetArg(args, "--out") ?? Path.Combine(AppContext.BaseDirectory, "locomo-results");
        var store = new RunStore(Path.Combine(outDir, "results.jsonl"));
        if (args.Contains("--fresh"))
        {
            store.Delete();
            Console.Error.WriteLine("--fresh: cleared any prior results, starting over.");
        }

        var kgExtractor = args.Contains("--kg") && answerer is IChatCompletion kgChat
            ? new TripleExtractor(kgChat) : null;
        var evaluator = new LoCoMoEvaluator(dataRoot, embedder, answerer, judge, distiller, reranker, planner, ingestMode, topK, concurrency, recallRetry, categories, args.Contains("--reuse-db"), args.Contains("--entity-boost"), kgExtractor, !args.Contains("--no-subject-boost"));

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

    private static (IEmbeddingProvider embedder, IAnswerer answerer, IJudge judge, ISessionDistiller? distiller, string mode)
        BuildClients(string[] args)
    {
        var provider = (Environment.GetEnvironmentVariable("MNEME_LLM_PROVIDER") ?? "").Trim().ToLowerInvariant();
        var rpm = double.TryParse(GetArg(args, "--rpm") ?? Environment.GetEnvironmentVariable("MNEME_LLM_RPM"), out var r) ? r : 120.0;
        var maxRetries = int.TryParse(Environment.GetEnvironmentVariable("MNEME_LLM_MAX_RETRIES"), out var mr) ? mr : 8;

        // --- GitHub Models (the "use Copilot / GitHub models" path) ---------
        if (provider is "github-models" or "github" or "copilot")
        {
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                ?? Environment.GetEnvironmentVariable("MNEME_LLM_API_KEY")
                ?? "";
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("github-models requires a GitHub token with 'models:read' in GITHUB_TOKEN.");
                Console.Error.WriteLine("Falling back to offline dry-run.");
                return Offline();
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

            // One shared ThrottledHttp so chat + embeddings draw from one rate budget.
            var http = new ThrottledHttp(ghBase, token, rpm, maxRetries, ghHeaders);
            var ghChat = new OpenAICompatibleChat(http, chatModel, "inference/chat/completions");
            var ghEmbed = new OpenAICompatibleEmbedder(http, embModel, embDim, "inference/embeddings");
            return (ghEmbed, ghChat, ghChat, new LlmSessionDistiller(ghChat),
                $"github-models ({chatModel} + {embModel}, {rpm:0} rpm)");
        }

        // --- Generic OpenAI-compatible endpoint -----------------------------
        var baseUrl = Environment.GetEnvironmentVariable("MNEME_LLM_BASE_URL");
        var apiKey = Environment.GetEnvironmentVariable("MNEME_LLM_API_KEY") ?? "";
        var model = Environment.GetEnvironmentVariable("MNEME_LLM_MODEL");

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            return Offline();
        }

        var embedModel = Environment.GetEnvironmentVariable("MNEME_EMBED_MODEL") ?? "text-embedding-3-small";
        var embedDim = int.TryParse(Environment.GetEnvironmentVariable("MNEME_EMBED_DIM"), out var dd) ? dd : 1536;
        var embedBaseUrl = Environment.GetEnvironmentVariable("MNEME_EMBED_BASE_URL") ?? baseUrl;
        var embedKey = Environment.GetEnvironmentVariable("MNEME_EMBED_API_KEY") ?? apiKey;

        var chatHttp = new ThrottledHttp(baseUrl, apiKey, rpm, maxRetries);
        var chat = new OpenAICompatibleChat(chatHttp, model);
        var embedHttp = embedBaseUrl == baseUrl && embedKey == apiKey
            ? chatHttp : new ThrottledHttp(embedBaseUrl, embedKey, rpm, maxRetries);
        var embedder = new OpenAICompatibleEmbedder(embedHttp, embedModel, embedDim);
        return (embedder, chat, chat, new LlmSessionDistiller(chat),
            $"live ({model} + {embedModel}, {rpm:0} rpm)");

        static (IEmbeddingProvider, IAnswerer, IJudge, ISessionDistiller?, string) Offline() =>
            (new OfflineEmbedder(), new OfflineAnswerer(), new OfflineJudge(),
             new LlmSessionDistiller(new OfflineChatCompletion()), "dry-run (offline; not a real score)");
    }

    private static string? GetArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static IReranker? BuildOnnxReranker()
    {
        var model = Path.Combine(AppContext.BaseDirectory, "models", "ms-marco-MiniLM-L6.onnx");
        var vocab = Path.Combine(AppContext.BaseDirectory, "models", "vocab.txt");
        if (!File.Exists(model) || !File.Exists(vocab))
        {
            Console.Error.WriteLine($"ONNX reranker model not found under {Path.GetDirectoryName(model)}; falling back to no rerank.");
            return null;
        }
        return new OnnxCrossEncoderReranker(model, vocab);
    }
}
