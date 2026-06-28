using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Search;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// Runs the LoCoMo evaluation against Mneme: ingest each conversation into a
/// dedicated workstream, embed it, then for every question retrieve memory
/// (hybrid semantic + lexical), answer with the configured model, and judge
/// the answer. Scores are aggregated overall and per LoCoMo category.
/// </summary>
public sealed class LoCoMoEvaluator
{
    private readonly string _dataRoot;
    private readonly IEmbeddingProvider _embedder;
    private readonly IAnswerer _answerer;
    private readonly IJudge _judge;
    private readonly int _topK;

    public LoCoMoEvaluator(string dataRoot, IEmbeddingProvider embedder, IAnswerer answerer, IJudge judge, int topK)
    {
        _dataRoot = dataRoot;
        _embedder = embedder;
        _answerer = answerer;
        _judge = judge;
        _topK = topK;
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

            // Ingest every turn as Evidence stamped with its session time.
            var n = 0;
            foreach (var turn in sample.Turns)
            {
                await agent.IngestAsync(new CaptureEvent(
                    EventId: new EventId($"{sample.SampleId}-{n:D5}"),
                    WorkstreamId: new WorkstreamId(sample.SampleId),
                    Channel: EventChannel.Epistemic,
                    ValidAt: turn.At,
                    RecordedAt: turn.At,
                    Payload: new EvidencePayload($"{turn.Speaker}: {turn.Text}", $"session-{turn.SessionNumber}"),
                    Provenance: new CaptureProvenance(new CaptureSourceId("locomo"), new PrincipalId(turn.Speaker))),
                    ct).ConfigureAwait(false);
                n++;
            }

            // Embed for semantic retrieval.
            await vectors.BackfillAsync(new WorkstreamId(sample.SampleId), ct).ConfigureAwait(false);

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

        return LoCoMoReport.Aggregate(records, _embedder.Id, _answerer.Id, _judge.Id, _topK);
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
    int TopK)
{
    public static LoCoMoReport Aggregate(IReadOnlyList<QaRecord> rows, string embedderId, string answererId, string judgeId, int topK)
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
            byCat, embedderId, answererId, judgeId, topK);
    }

    public string ToConsole()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("================ LoCoMo Results ================");
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
}

/// <summary>Per-category score line.</summary>
public sealed record CategoryScore(int CategoryId, string Label, int Total, int Correct, double Accuracy);
