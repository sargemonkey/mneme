using BenchmarkDotNet.Attributes;
using Mneme.Contracts;

namespace Mneme.Benchmarks.Perf;

/// <summary>
/// Measures read-path latency against a pre-populated store of
/// <see cref="StoreSize"/> events: free-text FTS query, category-filtered
/// query, and list-recent. The store is built once per parameter value in
/// <see cref="Setup"/> so the measured methods are pure reads.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class QueryBenchmarks
{
    private BenchHarness _h = null!;

    /// <summary>Number of events to pre-load before measuring read latency.</summary>
    [Params(1_000, 10_000)]
    public int StoreSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _h = new BenchHarness();
        var baseTime = DateTimeOffset.UtcNow.AddDays(-30);
        for (var i = 0; i < StoreSize; i++)
        {
            var evt = _h.NewEvent($"ev-{i:D9}", BenchHarness.Sentence(i), baseTime.AddSeconds(i));
            _h.Agent.IngestAsync(evt).GetAwaiter().GetResult();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _h.Dispose();

    /// <summary>Free-text FTS5 query (adaptive BM25 + recency fusion).</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> FreeTextQuery()
    {
        var spec = new QuerySpec(_h.Ws, FreeText: "shipped October launch", Limit: 25);
        var result = await _h.Query.QueryAsync(new QueryRequest(spec), _h.Token);
        return result.Items.Count;
    }

    /// <summary>Free-text query with the diagnostic score decomposition on.</summary>
    [Benchmark]
    public async Task<int> FreeTextQueryExplain()
    {
        var spec = new QuerySpec(_h.Ws, FreeText: "migrated the SQLite backend", Limit: 25);
        var result = await _h.Query.QueryAsync(new QueryRequest(spec, Explain: true), _h.Token);
        return result.Items.Count;
    }

    /// <summary>Category-filtered structured query (no FTS term).</summary>
    [Benchmark]
    public async Task<int> CategoryQuery()
    {
        var spec = new QuerySpec(_h.Ws, Categories: new[] { EpistemicCategory.Evidence }, Limit: 25);
        var result = await _h.Query.QueryAsync(new QueryRequest(spec), _h.Token);
        return result.Items.Count;
    }

    /// <summary>List the 25 most-recent events (the dedupe-check hot path).</summary>
    [Benchmark]
    public async Task<int> ListRecent()
    {
        var items = await _h.Query.ListRecentAsync(_h.Ws, 25, _h.Token);
        return items.Count;
    }
}
