using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Search;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>How a conversation is loaded into Mneme before retrieval.</summary>
public enum IngestMode
{
    /// <summary>Ingest each raw turn as an Evidence event (lexical baseline).</summary>
    Turns,
    /// <summary>Distill the conversation into Fact events first (Mneme's thesis).</summary>
    Facts,
    /// <summary>Ingest raw turns AND distilled facts (max recall).</summary>
    Both,
}

/// <summary>
/// Runs the LoCoMo evaluation against Mneme: load each conversation into a
/// dedicated workstream (raw turns and/or distilled facts), embed it, then for
/// every question retrieve memory (hybrid semantic + lexical), answer with the
/// configured model, and judge the answer. Aggregated overall + per category.
/// </summary>
public sealed class LoCoMoEvaluator
{
    private const int DistillChunk = 25; // turns per distillation call

    private readonly string _dataRoot;
    private readonly IEmbeddingProvider _embedder;
    private readonly IAnswerer _answerer;
    private readonly IJudge _judge;
    private readonly ISessionDistiller? _distiller;
    private readonly IReranker? _reranker;
    private readonly QueryPlanner? _planner;
    private readonly IngestMode _mode;
    private readonly int _topK;
    private readonly int _concurrency;
    private readonly bool _recallRetry;
    private readonly IReadOnlySet<string>? _categories;
    private readonly bool _reuseDb;
    private readonly bool _entityBoost;
    private readonly TripleExtractor? _tripleExtractor;
    private readonly bool _subjectBoost;

    public LoCoMoEvaluator(string dataRoot, IEmbeddingProvider embedder, IAnswerer answerer, IJudge judge,
        ISessionDistiller? distiller, IReranker? reranker, QueryPlanner? planner, IngestMode mode, int topK,
        int concurrency = 1, bool recallRetry = false, IReadOnlySet<string>? categories = null, bool reuseDb = false,
        bool entityBoost = false, TripleExtractor? tripleExtractor = null, bool subjectBoost = true)
    {
        _categories = categories is { Count: > 0 } ? categories : null;
        _reuseDb = reuseDb;
        _entityBoost = entityBoost;
        _tripleExtractor = tripleExtractor;
        _subjectBoost = subjectBoost;
        _dataRoot = dataRoot;
        _embedder = embedder;
        _answerer = answerer;
        _judge = judge;
        _distiller = distiller;
        _reranker = reranker;
        _planner = planner;
        _mode = mode;
        _topK = topK;
        _concurrency = Math.Max(1, concurrency);
        _recallRetry = recallRetry;
        if (mode is IngestMode.Facts or IngestMode.Both && distiller is null)
        {
            throw new InvalidOperationException($"Ingest mode '{mode}' requires a session distiller.");
        }
        Directory.CreateDirectory(dataRoot);
    }

    public async Task<LoCoMoReport> RunAsync(IReadOnlyList<LoCoMoSample> samples, RunStore? store = null, CancellationToken ct = default)
    {
        var existing = store?.LoadExisting() ?? new Dictionary<(string, int), QaRecord>();
        if (existing.Count > 0)
        {
            Console.Error.WriteLine($"Resuming: {existing.Count} question(s) already graded — they will be skipped.");
        }
        var records = new List<QaRecord>();
        var sampleIndex = 0;
        foreach (var sample in samples)
        {
            ct.ThrowIfCancellationRequested();
            sampleIndex++;

            // Skip ingest/embed entirely if every question in this sample is done.
            var allDone = sample.Questions.Count > 0 &&
                Enumerable.Range(0, sample.Questions.Count).Where(qi => IsSelected(sample, qi))
                    .All(qi => existing.ContainsKey((sample.SampleId, qi)));
            if (allDone)
            {
                for (var qi = 0; qi < sample.Questions.Count; qi++)
                {
                    if (IsSelected(sample, qi) && existing.TryGetValue((sample.SampleId, qi), out var cached))
                        records.Add(cached);
                }
                Console.Error.WriteLine($"[{sampleIndex}/{samples.Count}] {sample.SampleId}: all selected cached, skipping.");
                continue;
            }

            Console.Error.WriteLine($"[{sampleIndex}/{samples.Count}] {sample.SampleId}: " +
                                    $"{sample.Turns.Count} turns, {sample.Questions.Count} questions");

            var (agent, query, token, vectors) = BuildWorkstream(sample.SampleId);
            var ws = new WorkstreamId(sample.SampleId);

            // --reuse-db: skip re-ingest/distill/embed when a prior run already
            // built this workstream's DB. Isolates the read stage for fast A/Bs.
            var reused = _reuseDb && vectors.HasEmbeddings(ws);
            if (reused)
            {
                Console.Error.WriteLine($"    reusing existing DB for {sample.SampleId} (skipping ingest/distill/embed)");
            }

            // Load the conversation per the configured ingest mode.
            if (!reused && _mode is IngestMode.Turns or IngestMode.Both)
            {
                var n = 0;
                foreach (var turn in sample.Turns)
                {
                    await agent.IngestAsync(new CaptureEvent(
                        EventId: new EventId($"{sample.SampleId}-t{n:D5}"),
                        WorkstreamId: ws,
                        Channel: EventChannel.Epistemic,
                        ValidAt: turn.At,
                        RecordedAt: turn.At,
                        Payload: new EvidencePayload($"{turn.Speaker}: {turn.Text}", $"session-{turn.SessionNumber}"),
                        Provenance: new CaptureProvenance(new CaptureSourceId("locomo"), new PrincipalId(turn.Speaker))),
                        ct).ConfigureAwait(false);
                    n++;
                }
            }
            if (!reused && _mode is IngestMode.Facts or IngestMode.Both)
            {
                await DistillConversationAsync(agent, sample, token, ct).ConfigureAwait(false);
            }

            // Embed everything ingested (turns and/or facts) for semantic retrieval.
            if (!reused)
            {
                await vectors.BackfillAsync(ws, ct).ConfigureAwait(false);
            }

            // Knowledge-graph mode: extract subject-attributed triples from the raw
            // turns (once; cached in the DB's sidecar fact_triples table so
            // --reuse-db pays the cost only on the first pass).
            TripleStore? triples = null;
            if (_tripleExtractor is not null)
            {
                triples = new TripleStore(Path.Combine(_dataRoot, sample.SampleId + ".db"));
                triples.EnsureSchema();
                if (triples.Count() == 0)
                {
                    await ExtractTriplesAsync(triples, sample, ct).ConfigureAwait(false);
                }
                Console.Error.WriteLine($"    triples available: {triples.Count()}");
            }

            // Answer + judge each pending question, up to _concurrency in flight.
            // Ingest/embed above is sequential; only the LLM-heavy read+answer+
            // judge phase is parallelized (GitHub Models allows 20k req/min, so
            // the run is latency-bound — concurrency is the real speedup).
            var pending = Enumerable.Range(0, sample.Questions.Count)
                .Where(qi => IsSelected(sample, qi) && !existing.ContainsKey((sample.SampleId, qi)))
                .ToArray();
            foreach (var qi in Enumerable.Range(0, sample.Questions.Count).Where(existsCached))
            {
                records.Add(existing[(sample.SampleId, qi)]);
            }
            bool existsCached(int qi) => IsSelected(sample, qi) && existing.ContainsKey((sample.SampleId, qi));

            var gate = new SemaphoreSlim(_concurrency);
            var sink = new object();
            var done = 0;
            var tasks = pending.Select(async qi =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var qa = sample.Questions[qi];
                    QaRecord record;
                    try
                    {
                        var context = await RetrieveContextAsync(query, token, ws, qa.Question, _topK, triples, ct).ConfigureAwait(false);
                        var predicted = await _answerer.AnswerAsync(qa.Question, context, ct).ConfigureAwait(false);

                        // Recall-retry: a buried single fact (adversarial) often
                        // isn't in the first top-k. If the model abstained, cast a
                        // wider net (3× depth) and answer once more before giving up.
                        if (_recallRetry && IsAbstention(predicted))
                        {
                            var wider = await RetrieveContextAsync(query, token, ws, qa.Question, _topK * 3, triples, ct).ConfigureAwait(false);
                            var retry = await _answerer.AnswerAsync(qa.Question, wider, ct).ConfigureAwait(false);
                            if (!IsAbstention(retry)) { predicted = retry; context = wider; }
                        }

                        var contextTokens = context.Sum(ApproxTokens);
                        var correct = await _judge.IsCorrectAsync(qa.Question, qa.Answer, predicted, ct).ConfigureAwait(false);
                        var goldInContext = GoldSupportedBy(qa.Answer, context);
                        record = new QaRecord(sample.SampleId, qi, qa.CategoryId, qa.CategoryLabel,
                            qa.Question, qa.Answer, predicted, correct, contextTokens, goldInContext);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A question that fails even after retries is recorded as
                        // an incorrect "[error]" rather than crashing the whole
                        // run. It can be re-attempted later (delete its line + resume).
                        Console.Error.WriteLine($"    q{qi} failed: {ex.GetType().Name}: {Trunc(ex.Message)}");
                        record = new QaRecord(sample.SampleId, qi, qa.CategoryId, qa.CategoryLabel,
                            qa.Question, qa.Answer, "[error]", false, 0);
                    }
                    lock (sink)
                    {
                        records.Add(record);
                        store?.Append(record); // durable after every graded question → resumable
                        if (++done % 25 == 0) Console.Error.WriteLine($"    {done}/{pending.Length} graded");
                    }
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        return LoCoMoReport.Aggregate(records, _embedder.Id, _answerer.Id, _judge.Id, _topK,
            _mode.ToString().ToLowerInvariant(), _distiller?.Id ?? "(none)", _reranker?.Id ?? "(none)");
    }

    // Single-shot or iterative multi-hop retrieval. With a planner, decompose
    // into sub-queries, retrieve each, union by event id, and keep the best
    // topK summaries — surfaces facts a single query misses (the recall lever).
    // Each snippet is prefixed with the event's valid-at date so the answer
    // model has the temporal anchor it needs for date-difference questions.
    private async Task<IReadOnlyList<string>> RetrieveContextAsync(
        IMemoryQueryAPI query, CapabilityToken token, WorkstreamId ws, string question, int limit,
        TripleStore? triples, CancellationToken ct)
    {
        var queries = _planner is null ? new[] { question } : (await _planner.PlanAsync(question, ct).ConfigureAwait(false)).ToArray();
        // When entity-boosting, pull a wider candidate pool so entity-scoped facts
        // that a plain semantic/BM25 fusion ranked below the cut can be floated up.
        var poolLimit = _entityBoost ? Math.Min(limit * 4, 120) : limit;
        var entities = _entityBoost ? ExtractQueryEntities(question) : Array.Empty<string>();
        var best = new Dictionary<string, (double Score, string Text)>();
        foreach (var q in queries)
        {
            var res = await query.QueryAsync(new QueryRequest(new QuerySpec(ws, FreeText: q, Limit: poolLimit)), token, ct).ConfigureAwait(false);
            foreach (var item in res.Items)
            {
                var dated = $"[{item.ValidAt:yyyy-MM-dd}] {item.Summary}";
                // Entity-anchored boost: a fact that mentions an entity named in the
                // question is scoped to the asked-about person, so it should outrank
                // distractor facts about other people (the adversarial failure mode).
                var boost = 0.0;
                if (entities.Length > 0)
                {
                    var hits = entities.Count(e => item.Summary.Contains(e, StringComparison.OrdinalIgnoreCase));
                    if (hits > 0) boost = 1.0 + 0.1 * hits;
                }
                var score = item.Score + boost;
                if (!best.TryGetValue(item.EventId.Value, out var cur) || score > cur.Score)
                    best[item.EventId.Value] = (score, dated);
            }
        }
        var semantic = best.Values.OrderByDescending(v => v.Score).Take(limit).Select(v => v.Text);

        // Knowledge-graph mode: prepend subject-scoped triples for the entities the
        // question names. These are structurally attributed to the asked-about
        // person, so they front-load the answer with the right sub-graph and push
        // distractor facts out of the visible window.
        if (triples is not null)
        {
            var subjects = ExtractQueryEntities(question)
                .Select(TripleExtractor.NormalizeSubject)
                .Where(s => s.Length > 0).ToArray();
            var scoped = triples.SubjectScoped(subjects, Math.Max(6, limit / 3));
            if (scoped.Count > 0)
            {
                // Supplement (don't replace): keep the full-text semantic snippets
                // as primary evidence and APPEND the subject-scoped triples as an
                // attribution hint. Terse triples alone lose detail (objects get
                // abstracted), so they help only alongside the full facts.
                var sem = semantic.ToList();
                var extra = scoped.Where(s => !sem.Contains(s));
                return sem.Concat(extra).ToArray();
            }
        }
        return semantic.ToArray();
    }

    // Proper-noun entities named in the question (person/place names). LoCoMo
    // questions are person-centric ("What does Melanie's necklace symbolize?"),
    // so capitalized non-leading tokens outside a question-word stoplist are a
    // high-precision signal for the entity the answer must be attributed to.
    private static readonly HashSet<string> QueryStop = new(StringComparer.OrdinalIgnoreCase)
    {
        "What","When","Where","Which","Who","Whom","Whose","Why","How","Did","Do","Does",
        "Is","Are","Was","Were","Has","Have","Had","The","A","An","In","On","At","Of","To",
        "For","And","Or","But","If","As","By","With","From","This","That","These","Those","I",
    };

    private static string[] ExtractQueryEntities(string question) =>
        System.Text.RegularExpressions.Regex.Matches(question, @"\b[A-Z][a-zA-Z]+\b")
            .Select(m => m.Value)
            .Where(w => !QueryStop.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();


    private bool IsSelected(LoCoMoSample sample, int qi) =>
        _categories is null || _categories.Contains(sample.Questions[qi].CategoryLabel);

    // Diagnostic: is the gold answer's content actually present in the retrieved
    // snippets shown to the answer model? Token-recall ≥ 0.6 of gold's content
    // words (stopwords/short tokens dropped). Separates a retrieval miss (gold
    // absent → fix retrieval) from a generation miss (gold present, wrong/abstained
    // answer → fix the answer step). Not a scoring signal; recorded for analysis.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","is","was","were","of","to","in","on","at","for","and","with",
        "from","this","that","what","did","do","does","how","when","where","who","why","her","his",
    };

    private static bool GoldSupportedBy(string gold, IReadOnlyList<string> context)
    {
        var goldTokens = Tokenize(gold).Where(t => t.Length > 2 && !StopWords.Contains(t)).ToArray();
        if (goldTokens.Length == 0) return false;
        var haystack = string.Join(" \n ", context).ToLowerInvariant();
        var hits = goldTokens.Count(t => haystack.Contains(t, StringComparison.Ordinal));
        return (double)hits / goldTokens.Length >= 0.6;
    }

    private static IEnumerable<string> Tokenize(string s) =>
        System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value);

    private static bool IsAbstention(string answer) =>
        string.IsNullOrWhiteSpace(answer) ||
        answer.Contains("don't know", StringComparison.OrdinalIgnoreCase) ||
        answer.Contains("dont know", StringComparison.OrdinalIgnoreCase) ||
        answer.Contains("not enough", StringComparison.OrdinalIgnoreCase) ||
        answer.Contains("no information", StringComparison.OrdinalIgnoreCase) ||
        answer.StartsWith("[error]", StringComparison.OrdinalIgnoreCase);

    // Extract subject-attributed triples from the conversation's raw turns and
    // persist them to the sidecar store. Chunked like distillation so each LLM
    // call sees a coherent window; runs once per conversation (cached in the DB).
    private async Task ExtractTriplesAsync(TripleStore store, LoCoMoSample sample, CancellationToken ct)
    {
        var turns = sample.Turns.Select((t, i) =>
            ($"e{i:D5}", t.At, $"{t.Speaker}: {t.Text}")).ToList();
        var total = 0;
        for (var i = 0; i < turns.Count; i += DistillChunk)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = turns.Skip(i).Take(DistillChunk).ToList();
            var rows = await _tripleExtractor!.ExtractAsync(chunk, ct).ConfigureAwait(false);
            store.Insert(rows);
            total += rows.Count;
        }
        Console.Error.WriteLine($"    extracted {total} triples for {sample.SampleId}");
    }

    // Chunk the conversation and run Mneme's session distiller over each window,
    // turning raw turns into atomic Fact events (with session-range citations).
    private async Task DistillConversationAsync(IMemoryAgent agent, LoCoMoSample sample, CapabilityToken token, CancellationToken ct)
    {
        var session = new SessionId(sample.SampleId);
        var entries = sample.Turns.Select((t, i) => new ContextEntry(
            EntryId: $"e{i:D5}",
            Timestamp: t.At,
            Kind: ContextEntryKind.UserMessage,
            Text: $"{t.Speaker}: {t.Text}",
            SourceRef: $"session-{t.SessionNumber}")).ToArray();

        for (var i = 0; i < entries.Length; i += DistillChunk)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = entries.Skip(i).Take(DistillChunk).ToArray();
            await agent.DistillSessionAsync(session, chunk, token, ct).ConfigureAwait(false);
        }
    }

    private (IMemoryAgent agent, IMemoryQueryAPI query, CapabilityToken token, VectorIndex vectors)
        BuildWorkstream(string sampleId)
    {
        var dbPath = Path.Combine(_dataRoot, sampleId + ".db");
        if (!(_reuseDb && File.Exists(dbPath)) && File.Exists(dbPath)) File.Delete(dbPath);

        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = sampleId;
            o.SqlitePath = dbPath;
            o.UserId = "locomo";
            o.SubjectAttributionBoost = _subjectBoost;
        });
        services.AddSingleton(_embedder);
        if (_distiller is not null) services.AddSingleton(_distiller);
        if (_reranker is not null) services.AddSingleton(_reranker);
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IMemoryAgent>(),
                sp.GetRequiredService<IMemoryQueryAPI>(),
                sp.GetRequiredService<CapabilityToken>(),
                sp.GetRequiredService<VectorIndex>());
    }

    // Rough token estimate (~0.75 words/token) for the context-size column.
    private static int ApproxTokens(string s) =>
        (int)Math.Ceiling(s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 0.75);

    private static string Trunc(string s) => s.Length <= 120 ? s : s[..120] + "…";
}

/// <summary>Aggregated LoCoMo scores: overall + per-category accuracy and mean context tokens.</summary>
public sealed record LoCoMoReport(
    int Total,
    int Correct,
    double Accuracy,
    double MeanContextTokens,
    IReadOnlyList<CategoryScore> Categories,
    string EmbedderId,
    string AnswererId,
    string JudgeId,
    int TopK,
    string IngestMode,
    string DistillerId,
    string RerankerId)
{
    public static LoCoMoReport Aggregate(IReadOnlyList<QaRecord> rows, string embedderId, string answererId,
        string judgeId, int topK, string ingestMode, string distillerId, string rerankerId)
    {
        var total = rows.Count;
        var correct = rows.Count(r => r.Correct);
        var byCat = rows.GroupBy(r => (r.CategoryId, r.CategoryLabel))
            .OrderBy(g => g.Key.CategoryId)
            .Select(g => new CategoryScore(
                g.Key.CategoryId, g.Key.CategoryLabel, g.Count(), g.Count(r => r.Correct),
                g.Count() == 0 ? 0 : (double)g.Count(r => r.Correct) / g.Count()))
            .ToArray();
        return new LoCoMoReport(
            total, correct,
            total == 0 ? 0 : (double)correct / total,
            total == 0 ? 0 : rows.Average(r => r.ContextTokens),
            byCat, embedderId, answererId, judgeId, topK, ingestMode, distillerId, rerankerId);
    }

    public string ToConsole()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("================ LoCoMo Results ================");
        sb.AppendLine($"  ingest   : {IngestMode}");
        sb.AppendLine($"  distiller: {DistillerId}");
        sb.AppendLine($"  reranker : {RerankerId}");
        sb.AppendLine($"  embedder : {EmbedderId}");
        sb.AppendLine($"  answerer : {AnswererId}");
        sb.AppendLine($"  judge    : {JudgeId}");
        sb.AppendLine($"  top-k    : {TopK}");
        sb.AppendLine($"  mean context tokens/query : {MeanContextTokens:F0}");
        sb.AppendLine("  ---------------------------------------------");
        sb.AppendLine($"  {"category",-14} {"n",4} {"correct",8} {"acc",7}");
        foreach (var c in Categories)
        {
            sb.AppendLine($"  {c.Label,-14} {c.Total,4} {c.Correct,8} {c.Accuracy,6:P0}");
        }
        sb.AppendLine("  ---------------------------------------------");
        sb.AppendLine($"  {"OVERALL",-14} {Total,4} {Correct,8} {Accuracy,6:P0}");
        sb.AppendLine("================================================");
        return sb.ToString();
    }

    /// <summary>
    /// Render results as a ready-to-paste Markdown report, including a
    /// reference row of the latest published Mem0 / Zep LoCoMo numbers so the
    /// comparison is in one place. Reference figures are static and sourced
    /// (see footnotes); they are NOT produced by this harness.
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Mneme — LoCoMo results");
        sb.AppendLine();
        sb.AppendLine($"- **Run:** {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"- **Ingest mode:** {IngestMode}  (distiller: `{DistillerId}`)");
        sb.AppendLine($"- **Reranker:** `{RerankerId}`");
        sb.AppendLine($"- **Embedder:** `{EmbedderId}`");
        sb.AppendLine($"- **Answerer:** `{AnswererId}`");
        sb.AppendLine($"- **Judge:** `{JudgeId}`");
        sb.AppendLine($"- **Retrieval depth (top-k):** {TopK}");
        sb.AppendLine($"- **Mean context tokens / query:** {MeanContextTokens:F0}");
        sb.AppendLine();
        sb.AppendLine("## Accuracy by category");
        sb.AppendLine();
        sb.AppendLine("| Category | n | Correct | Accuracy |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var c in Categories)
        {
            sb.AppendLine($"| {c.Label} | {c.Total} | {c.Correct} | {c.Accuracy:P1} |");
        }
        sb.AppendLine($"| **Overall** | **{Total}** | **{Correct}** | **{Accuracy:P1}** |");
        sb.AppendLine();
        sb.AppendLine("## Reference: published LoCoMo overall (other memory layers)");
        sb.AppendLine();
        sb.AppendLine("| System | LoCoMo overall | Mean tokens / retrieval | Source |");
        sb.AppendLine("|---|---:|---:|---|");
        sb.AppendLine($"| **Mneme (this run)** | **{Accuracy:P1}** | **{MeanContextTokens:F0}** | this harness |");
        sb.AppendLine("| Mem0 | 92.5% | ~6,956 | mem0.ai/research (data May 2026) |");
        sb.AppendLine("| Zep | — (LongMemEval 71.2% w/ gpt-4o) | ~1,600 | getzep.com SOTA paper (Jan 2025) |");
        sb.AppendLine();
        sb.AppendLine("> Reference numbers are static, model-dependent, and measured by their");
        sb.AppendLine("> authors — not reproduced here. For a fair head-to-head, run this harness");
        sb.AppendLine("> with the same answer/judge model the reference used and hold retrieval");
        sb.AppendLine("> depth fixed; the only variable should be the memory layer.");
        return sb.ToString();
    }
}

/// <summary>Per-category score line.</summary>
public sealed record CategoryScore(int CategoryId, string Label, int Total, int Correct, double Accuracy);
