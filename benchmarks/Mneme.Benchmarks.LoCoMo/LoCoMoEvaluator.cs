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
    private readonly IngestMode _mode;
    private readonly int _topK;

    public LoCoMoEvaluator(string dataRoot, IEmbeddingProvider embedder, IAnswerer answerer, IJudge judge,
        ISessionDistiller? distiller, IngestMode mode, int topK)
    {
        _dataRoot = dataRoot;
        _embedder = embedder;
        _answerer = answerer;
        _judge = judge;
        _distiller = distiller;
        _mode = mode;
        _topK = topK;
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
                Enumerable.Range(0, sample.Questions.Count).All(qi => existing.ContainsKey((sample.SampleId, qi)));
            if (allDone)
            {
                for (var qi = 0; qi < sample.Questions.Count; qi++)
                {
                    records.Add(existing[(sample.SampleId, qi)]);
                }
                Console.Error.WriteLine($"[{sampleIndex}/{samples.Count}] {sample.SampleId}: all {sample.Questions.Count} cached, skipping.");
                continue;
            }

            Console.Error.WriteLine($"[{sampleIndex}/{samples.Count}] {sample.SampleId}: " +
                                    $"{sample.Turns.Count} turns, {sample.Questions.Count} questions");

            var (agent, query, token, vectors) = BuildWorkstream(sample.SampleId);
            var ws = new WorkstreamId(sample.SampleId);

            // Load the conversation per the configured ingest mode.
            if (_mode is IngestMode.Turns or IngestMode.Both)
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
            if (_mode is IngestMode.Facts or IngestMode.Both)
            {
                await DistillConversationAsync(agent, sample, token, ct).ConfigureAwait(false);
            }

            // Embed everything ingested (turns and/or facts) for semantic retrieval.
            await vectors.BackfillAsync(ws, ct).ConfigureAwait(false);

            // Answer + judge each question (skipping any already graded).
            for (var qi = 0; qi < sample.Questions.Count; qi++)
            {
                ct.ThrowIfCancellationRequested();
                if (existing.TryGetValue((sample.SampleId, qi), out var cached))
                {
                    records.Add(cached);
                    continue;
                }
                var qa = sample.Questions[qi];
                var result = await query.QueryAsync(new QueryRequest(
                    new QuerySpec(new WorkstreamId(sample.SampleId), FreeText: qa.Question, Limit: _topK)),
                    token, ct).ConfigureAwait(false);
                var context = result.Items.Select(i => i.Summary).ToArray();
                var contextTokens = context.Sum(c => ApproxTokens(c));

                var predicted = await _answerer.AnswerAsync(qa.Question, context, ct).ConfigureAwait(false);
                var correct = await _judge.IsCorrectAsync(qa.Question, qa.Answer, predicted, ct).ConfigureAwait(false);

                var record = new QaRecord(sample.SampleId, qi, qa.CategoryId, qa.CategoryLabel,
                    qa.Question, qa.Answer, predicted, correct, contextTokens);
                records.Add(record);
                store?.Append(record); // durable after every graded question → resumable
            }
        }

        return LoCoMoReport.Aggregate(records, _embedder.Id, _answerer.Id, _judge.Id, _topK,
            _mode.ToString().ToLowerInvariant(), _distiller?.Id ?? "(none)");
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
        if (File.Exists(dbPath)) File.Delete(dbPath);

        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = sampleId;
            o.SqlitePath = dbPath;
            o.UserId = "locomo";
        });
        services.AddSingleton(_embedder);
        if (_distiller is not null) services.AddSingleton(_distiller);
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IMemoryAgent>(),
                sp.GetRequiredService<IMemoryQueryAPI>(),
                sp.GetRequiredService<CapabilityToken>(),
                sp.GetRequiredService<VectorIndex>());
    }

    // Rough token estimate (~0.75 words/token) for the context-size column.
    private static int ApproxTokens(string s) =>
        (int)Math.Ceiling(s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 0.75);
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
    string DistillerId)
{
    public static LoCoMoReport Aggregate(IReadOnlyList<QaRecord> rows, string embedderId, string answererId,
        string judgeId, int topK, string ingestMode, string distillerId)
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
            byCat, embedderId, answererId, judgeId, topK, ingestMode, distillerId);
    }

    public string ToConsole()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("================ LoCoMo Results ================");
        sb.AppendLine($"  ingest   : {IngestMode}");
        sb.AppendLine($"  distiller: {DistillerId}");
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
