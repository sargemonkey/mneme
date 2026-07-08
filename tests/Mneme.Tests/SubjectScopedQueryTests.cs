using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Search;

namespace Mneme.Tests;

/// <summary>
/// Subject-scoped attribution retrieval: a fact whose triple subject matches an
/// entity named in the query gets an additive boost, so the queried person's
/// sub-graph outranks distractor facts that merely mention the same names.
/// </summary>
public sealed class SubjectScopedQueryTests : IDisposable
{
    private readonly string _tmpDir;
    public SubjectScopedQueryTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-kgq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private ServiceProvider Build(string ws)
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = ws;
            o.SqlitePath = Path.Combine(_tmpDir, ws + ".db");
            o.UserId = "alice";
            o.SubjectAttributionBoost = true; // exercising the boost mechanism explicitly
        });
        services.AddSingleton<IEmbeddingProvider>(new BagOfWordsEmbedder());
        return services.BuildServiceProvider();
    }

    private static CaptureEvent Fact(string id, string ws, string statement, FactTriple triple) =>
        new(new EventId(id), new WorkstreamId(ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new FactPayload(statement, Array.Empty<EventId>(), new[] { triple }),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")));

    [Fact]
    public async Task Subject_attributed_fact_is_boosted_over_distractor()
    {
        using var sp = Build("kgq");
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var vectors = sp.GetRequiredService<VectorIndex>();
        var query = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var ws = new WorkstreamId("kgq");

        // Both facts mention the same words; only the subject differs.
        await agent.IngestAsync(Fact("kgq-mel", "kgq",
            "Melanie enjoys listening to Bach and Mozart",
            new FactTriple("Melanie", "enjoys", "Bach and Mozart")));
        await agent.IngestAsync(Fact("kgq-car", "kgq",
            "Caroline mentioned Bach and Mozart at the concert",
            new FactTriple("Caroline", "mentioned", "Bach and Mozart")));
        await vectors.BackfillAsync(ws);

        var result = await query.QueryAsync(new QueryRequest(
            new QuerySpec(ws, FreeText: "Which classical musicians does Melanie enjoy?"),
            Explain: true), token);

        var mel = result.Items.Single(i => i.EventId.Value == "kgq-mel");
        var car = result.Items.SingleOrDefault(i => i.EventId.Value == "kgq-car");

        // The Melanie-subject fact carries the subject boost; Caroline's does not.
        Assert.NotNull(mel.Details);
        Assert.True(mel.Details!.EntityBoost > 0, "subject-matched fact should be boosted");
        if (car?.Details is not null) Assert.Equal(0.0, car.Details.EntityBoost);

        // And it outranks the distractor.
        Assert.True(mel.Score > (car?.Score ?? 0.0));
        Assert.Equal("kgq-mel", result.Items[0].EventId.Value);
    }

    [Fact]
    public async Task Subject_only_fact_is_injected_even_without_lexical_overlap()
    {
        using var sp = Build("kgq2");
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var vectors = sp.GetRequiredService<VectorIndex>();
        var query = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var ws = new WorkstreamId("kgq2");

        // A fact about Melanie whose words don't overlap the question at all — it
        // would be missed by pure semantic/lexical retrieval, but the subject
        // match injects it.
        await agent.IngestAsync(Fact("kgq2-mel", "kgq2",
            "Melanie volunteers at the animal shelter on weekends",
            new FactTriple("Melanie", "volunteers_at", "animal shelter")));
        await agent.IngestAsync(Fact("kgq2-noise", "kgq2",
            "The quarterly budget review is scheduled for Friday",
            new FactTriple("budget review", "scheduled_for", "Friday")));
        await vectors.BackfillAsync(ws);

        var result = await query.QueryAsync(new QueryRequest(
            new QuerySpec(ws, FreeText: "Where does Melanie spend her free time?")), token);

        Assert.Contains(result.Items, i => i.EventId.Value == "kgq2-mel");
    }

    [Fact]
    public async Task Subject_triple_supplement_is_populated_without_displacing_items()
    {
        using var sp = Build("kgq3");
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var vectors = sp.GetRequiredService<VectorIndex>();
        var query = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var ws = new WorkstreamId("kgq3");

        await agent.IngestAsync(Fact("kgq3-mel", "kgq3",
            "Melanie enjoys listening to Bach and Mozart",
            new FactTriple("Melanie", "enjoys", "Bach and Mozart")));
        await agent.IngestAsync(Fact("kgq3-car", "kgq3",
            "Caroline mentioned Bach and Mozart at the concert",
            new FactTriple("Caroline", "mentioned", "Bach and Mozart")));
        await vectors.BackfillAsync(ws);

        // Without the flag: no supplement.
        var plain = await query.QueryAsync(new QueryRequest(
            new QuerySpec(ws, FreeText: "Which classical musicians does Melanie enjoy?")), token);
        Assert.True(plain.SubjectTriples is null || plain.SubjectTriples.Count == 0);
        var plainItemCount = plain.Items.Count;

        // With the flag: the Melanie-subject triple is supplied as a supplement,
        // and the ranked items are unchanged (no displacement).
        var supplemented = await query.QueryAsync(new QueryRequest(
            new QuerySpec(ws, FreeText: "Which classical musicians does Melanie enjoy?"),
            SupplementSubjectTriples: true), token);

        Assert.NotNull(supplemented.SubjectTriples);
        Assert.Contains(supplemented.SubjectTriples!, h => h.Triple.Subject == "Melanie");
        Assert.DoesNotContain(supplemented.SubjectTriples!, h => h.Triple.Subject == "Caroline");
        Assert.Equal(plainItemCount, supplemented.Items.Count);
    }

    [Fact]
    public async Task Subject_triple_supplement_empty_when_query_names_no_known_entity()
    {
        using var sp = Build("kgq4");
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var vectors = sp.GetRequiredService<VectorIndex>();
        var query = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var ws = new WorkstreamId("kgq4");

        await agent.IngestAsync(Fact("kgq4-mel", "kgq4",
            "Melanie enjoys hiking",
            new FactTriple("Melanie", "enjoys", "hiking")));
        await vectors.BackfillAsync(ws);

        var result = await query.QueryAsync(new QueryRequest(
            new QuerySpec(ws, FreeText: "what are the weekend plans?"),
            SupplementSubjectTriples: true), token);

        Assert.True(result.SubjectTriples is null || result.SubjectTriples.Count == 0);
    }

    private sealed class BagOfWordsEmbedder : IEmbeddingProvider
    {
        public string Id => "test/bag-of-words@64";
        public int Dimensions => 64;

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            var result = new List<ReadOnlyMemory<float>>(texts.Count);
            foreach (var t in texts)
            {
                var v = new float[Dimensions];
                foreach (var raw in t.ToLowerInvariant().Split(
                    new[] { ' ', '\t', '\n', '\r', '.', ',', '?' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    v[(uint)raw.GetHashCode() % Dimensions] += 1f;
                }
                result.Add(v);
            }
            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(result);
        }
    }
}
