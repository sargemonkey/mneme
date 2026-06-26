using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;

namespace Mneme.Benchmarks.Perf;

/// <summary>
/// Shared setup for the performance benchmarks: spins up a real Mneme stack
/// against a throwaway on-disk SQLite database and exposes helpers to mint
/// events. On-disk (not in-memory) is deliberate — WAL commit latency to a
/// real file is what production hosts actually pay.
/// </summary>
internal sealed class BenchHarness : IDisposable
{
    public const string Workstream = "perf-ws";

    private readonly string _dir;
    public ServiceProvider Provider { get; }
    public IMemoryAgent Agent { get; }
    public IMemoryQueryAPI Query { get; }
    public CapabilityToken Token { get; }
    public WorkstreamId Ws { get; } = new(Workstream);

    public BenchHarness()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mneme-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = Workstream;
            o.SqlitePath = Path.Combine(_dir, "perf.db");
            o.UserId = "bench";
        });
        Provider = services.BuildServiceProvider();
        Agent = Provider.GetRequiredService<IMemoryAgent>();
        Query = Provider.GetRequiredService<IMemoryQueryAPI>();
        Token = Provider.GetRequiredService<CapabilityToken>();
    }

    /// <summary>Build an Evidence event with a unique id and the given content.</summary>
    public CaptureEvent NewEvent(string id, string content, DateTimeOffset at)
        => new(
            EventId: new EventId(id),
            WorkstreamId: Ws,
            Channel: EventChannel.Epistemic,
            ValidAt: at,
            RecordedAt: at,
            Payload: new EvidencePayload(content, Source: "bench"),
            Provenance: new CaptureProvenance(
                new CaptureSourceId("bench"), Token.Principal,
                Citation: new Citation.Manual("bench", "perf fixture")));

    /// <summary>
    /// Deterministic, vocabulary-rich content so FTS queries hit a realistic
    /// term distribution rather than a single repeated string.
    /// </summary>
    public static string Sentence(int seed)
    {
        var subjects = new[] { "team", "service", "pipeline", "user", "agent", "release", "database", "model" };
        var verbs = new[] { "decided", "shipped", "deferred", "investigated", "confirmed", "rejected", "migrated", "benchmarked" };
        var objects = new[] { "the October launch", "the auth rewrite", "the SQLite backend", "the vector index",
                              "the distillation worker", "the entity resolver", "the curation flow", "the sync engine" };
        var reasons = new[] { "for latency", "to cut cost", "after review", "per the RFC", "due to a regression",
                              "to unblock QA", "ahead of the demo", "on the locked decision" };
        return $"The {subjects[seed % subjects.Length]} {verbs[(seed / 3) % verbs.Length]} " +
               $"{objects[(seed / 7) % objects.Length]} {reasons[(seed / 11) % reasons.Length]} (#{seed}).";
    }

    public void Dispose()
    {
        Provider.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
