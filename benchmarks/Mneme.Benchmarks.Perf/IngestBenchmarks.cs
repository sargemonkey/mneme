using BenchmarkDotNet.Attributes;
using Mneme.Contracts;

namespace Mneme.Benchmarks.Perf;

/// <summary>
/// Measures ingest latency and throughput through the full sync pipeline:
/// validate → redact → classify → WAL commit → post-commit observers
/// (projections + FTS index + feedback). This is the &lt;50ms-p99 path the
/// locked sync/async split promises.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class IngestBenchmarks
{
    private BenchHarness _h = null!;
    private int _counter;

    /// <summary>Number of events ingested per invocation in the batch benchmark.</summary>
    [Params(1, 100)]
    public int BatchSize { get; set; }

    [GlobalSetup]
    public void Setup() => _h = new BenchHarness();

    [GlobalCleanup]
    public void Cleanup() => _h.Dispose();

    /// <summary>
    /// Ingest <see cref="BatchSize"/> fresh events. Each gets a unique id so
    /// no invocation degenerates into an idempotent no-op.
    /// </summary>
    [Benchmark]
    public async Task<int> IngestBatch()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < BatchSize; i++)
        {
            var id = $"ev-{_counter++:D9}";
            await _h.Agent.IngestAsync(_h.NewEvent(id, BenchHarness.Sentence(_counter), now));
        }
        return BatchSize;
    }
}
