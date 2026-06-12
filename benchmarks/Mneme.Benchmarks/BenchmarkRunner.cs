using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;

namespace Mneme.Benchmarks;

public sealed class BenchmarkRunner
{
    private readonly string _dataRoot;
    public BenchmarkRunner(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        Directory.CreateDirectory(dataRoot);
        _dataRoot = dataRoot;
    }

    public async Task<BenchmarkReport> RunAsync(BenchmarkFixture fixture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var dbPath = Path.Combine(_dataRoot, fixture.Name + ".db");
        if (File.Exists(dbPath)) File.Delete(dbPath);

        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = fixture.Workstream;
            o.SqlitePath = dbPath;
            o.UserId = "bench";
        });
        await using var sp = services.BuildServiceProvider();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var api   = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();

        var ingestLatencies = new List<long>(fixture.Turns.Count);
        var sw = new Stopwatch();
        var ingestN = 0;
        foreach (var turn in fixture.Turns)
        {
            if (!turn.ShouldCapture) continue;
            var category = Enum.TryParse<EpistemicCategory>(turn.Category, true, out var c) ? c : EpistemicCategory.Evidence;
            var payload = BuildPayload(category, turn);
            var evt = new CaptureEvent(
                EventId: new EventId($"bench-{fixture.Name}-{ingestN:D5}"),
                WorkstreamId: new WorkstreamId(fixture.Workstream),
                Channel: EventChannel.Epistemic,
                ValidAt: turn.At,
                RecordedAt: turn.At,
                Payload: payload,
                Provenance: new CaptureProvenance(new CaptureSourceId("bench"), new PrincipalId(turn.Speaker)));
            sw.Restart();
            await agent.IngestAsync(evt, ct).ConfigureAwait(false);
            sw.Stop();
            ingestLatencies.Add(sw.ElapsedMilliseconds);
            ingestN++;
        }

        var probeResults = new List<ProbeResult>(fixture.Probes.Count);
        foreach (var probe in fixture.Probes)
        {
            sw.Restart();
            var spec = new QuerySpec(
                Workstream: new WorkstreamId(fixture.Workstream),
                FreeText: probe.Question,
                AsOf: probe.AsOf,
                Limit: 10);
            var result = await api.QueryAsync(new QueryRequest(spec), token, ct).ConfigureAwait(false);
            sw.Stop();
            var hit = result.Items.Any(i =>
                i.Summary.Contains(probe.ExpectedSubstring, StringComparison.OrdinalIgnoreCase));
            probeResults.Add(new ProbeResult(probe.Question, probe.ExpectedSubstring, hit, sw.ElapsedMilliseconds, result.Items.Count));
        }

        return new BenchmarkReport(
            FixtureName: fixture.Name,
            EventsIngested: ingestN,
            IngestP50Ms: Percentile(ingestLatencies, 50),
            IngestP99Ms: Percentile(ingestLatencies, 99),
            ProbeResults: probeResults);
    }

    private static EventPayload BuildPayload(EpistemicCategory category, BenchmarkTurn turn) => category switch
    {
        EpistemicCategory.Evidence   => new EvidencePayload(turn.Content, "bench"),
        EpistemicCategory.Fact       => new FactPayload(turn.Content, Array.Empty<EventId>()),
        EpistemicCategory.Decision   => new DecisionPayload(turn.Content, "", Array.Empty<EventId>(), new PrincipalId(turn.Speaker)),
        EpistemicCategory.Hypothesis => new HypothesisPayload(turn.Content, HypothesisState.Open),
        EpistemicCategory.Goal       => new GoalPayload(turn.Content, GoalState.Active),
        EpistemicCategory.Action     => new ActionPayload(turn.Content, null, null),
        EpistemicCategory.Outcome    => new OutcomePayload(turn.Content, EventId.None, OutcomePolarity.Neutral),
        _ => new EvidencePayload(turn.Content, "bench"),
    };

    private static long Percentile(List<long> samples, int pct)
    {
        if (samples.Count == 0) return 0;
        samples.Sort();
        var i = Math.Max(0, (samples.Count * pct / 100) - 1);
        return samples[i];
    }
}

public sealed record BenchmarkReport(
    string FixtureName,
    int EventsIngested,
    long IngestP50Ms,
    long IngestP99Ms,
    IReadOnlyList<ProbeResult> ProbeResults)
{
    public int Hits => ProbeResults.Count(r => r.Hit);
    public int Misses => ProbeResults.Count - Hits;
    public double Recall => ProbeResults.Count == 0 ? 0.0 : (double)Hits / ProbeResults.Count;
}

public sealed record ProbeResult(
    string Question,
    string ExpectedSubstring,
    bool Hit,
    long QueryLatencyMs,
    int CandidatesReturned);
