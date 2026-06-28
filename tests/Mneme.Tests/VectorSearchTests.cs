using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Search;

namespace Mneme.Tests;

public sealed class VectorSearchTests : IDisposable
{
    private readonly string _tmpDir;
    public VectorSearchTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-vec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private ServiceProvider Build(IEmbeddingProvider? embedder, string workstream)
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = workstream;
            o.SqlitePath = Path.Combine(_tmpDir, workstream + ".db");
            o.UserId = "alice";
        });
        if (embedder is not null) services.AddSingleton(embedder);
        return services.BuildServiceProvider();
    }

    private static CaptureEvent Evidence(string id, string ws, string content) =>
        new(new EventId(id), new WorkstreamId(ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload(content, "test"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")));

    [Fact]
    public async Task Backfill_embeds_all_events_and_is_idempotent()
    {
        using var sp = Build(new BagOfWordsEmbedder(), "vec-bf");
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var vectors = sp.GetRequiredService<VectorIndex>();
        var ws = new WorkstreamId("vec-bf");

        await agent.IngestAsync(Evidence("e1", "vec-bf", "the cat sat on the mat"));
        await agent.IngestAsync(Evidence("e2", "vec-bf", "a dog ran in the park"));

        Assert.True(vectors.IsEnabled);
        Assert.Equal(2, await vectors.BackfillAsync(ws));
        Assert.Equal(0, await vectors.BackfillAsync(ws)); // already embedded → no-op
    }

    [Fact]
    public async Task Semantic_search_ranks_by_meaning()
    {
        using var sp = Build(new BagOfWordsEmbedder(), "vec-sem");
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var vectors = sp.GetRequiredService<VectorIndex>();
        var ws = new WorkstreamId("vec-sem");

        await agent.IngestAsync(Evidence("dog", "vec-sem", "the puppy chased a ball in the yard"));
        await agent.IngestAsync(Evidence("fin", "vec-sem", "quarterly revenue exceeded forecasts"));
        await vectors.BackfillAsync(ws);

        var hits = await vectors.SearchAsync(ws, "the puppy chased a ball in the yard", 2);
        Assert.NotEmpty(hits);
        Assert.Equal("dog", hits[0].EventId.Value);
    }

    [Fact]
    public async Task Hybrid_query_finds_paraphrase_that_pure_fts_misses()
    {
        // "automobile" shares no token with "car", so FTS alone misses it; the
        // synonym embedder places them together so hybrid retrieval finds it.
        using var sp = Build(new SynonymEmbedder(), "vec-hyb");
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var query = sp.GetRequiredService<IMemoryQueryAPI>();
        var vectors = sp.GetRequiredService<VectorIndex>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var ws = new WorkstreamId("vec-hyb");

        await agent.IngestAsync(Evidence("h1", "vec-hyb", "we bought a new automobile last week"));
        await agent.IngestAsync(Evidence("h2", "vec-hyb", "the weather was sunny on the beach"));
        await vectors.BackfillAsync(ws);

        var result = await query.QueryAsync(
            new QueryRequest(new QuerySpec(ws, FreeText: "car"), Explain: true), token);
        Assert.Equal("hybrid-semantic-bm25", result.Explain!.DispatcherChoice);
        Assert.Contains(result.Items, i => i.EventId.Value == "h1");
        var top = result.Items.First(i => i.EventId.Value == "h1");
        Assert.NotNull(top.Details);
        Assert.True(top.Details!.Semantic > 0);
    }

    [Fact]
    public async Task No_embedder_falls_back_to_lexical_search()
    {
        using var sp = Build(embedder: null, "vec-none");
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var query = sp.GetRequiredService<IMemoryQueryAPI>();
        var vectors = sp.GetRequiredService<VectorIndex>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var ws = new WorkstreamId("vec-none");

        Assert.False(vectors.IsEnabled);
        Assert.Equal(0, await vectors.BackfillAsync(ws)); // no-op without a provider

        await agent.IngestAsync(Evidence("n1", "vec-none", "the quick brown fox"));
        var result = await query.QueryAsync(
            new QueryRequest(new QuerySpec(ws, FreeText: "fox"), Explain: true), token);
        Assert.Equal("lexical-fts5", result.Explain!.DispatcherChoice);
        Assert.Single(result.Items);
    }

    /// <summary>
    /// Deterministic offline embedder: bag-of-words hashed into a fixed-size
    /// vector so texts sharing words get similar vectors. No network, fully
    /// repeatable. <see cref="Normalize"/> lets a subclass fold synonyms.
    /// </summary>
    private class BagOfWordsEmbedder : IEmbeddingProvider
    {
        public virtual string Id => "test/bag-of-words@1";
        public int Dimensions => 64;
        protected virtual string Normalize(string token) => token;

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            var result = new List<ReadOnlyMemory<float>>(texts.Count);
            foreach (var t in texts)
            {
                var v = new float[Dimensions];
                foreach (var raw in t.ToLowerInvariant().Split(
                    new[] { ' ', '\t', '\n', '\r', '.', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var tok = Normalize(raw);
                    v[(uint)tok.GetHashCode() % Dimensions] += 1f;
                }
                result.Add(v);
            }
            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(result);
        }
    }

    private sealed class SynonymEmbedder : BagOfWordsEmbedder
    {
        public override string Id => "test/synonym@1";
        protected override string Normalize(string token) => token switch
        {
            "automobile" or "vehicle" => "car",
            _ => token,
        };
    }
}
