using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Ingest;
using Mneme.Query;
using Mneme.Search;

namespace Mneme.Tests;

public sealed class MemoryQueryApiTests : IDisposable
{
    private readonly string _tmpDir;
    public MemoryQueryApiTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-q-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private (ServiceProvider sp, IMemoryAgent agent, IMemoryQueryAPI query, CapabilityToken token)
        BuildHost(string workstream = "q-ws", string user = "alice", bool includeTechnical = false)
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = workstream;
            o.SqlitePath = Path.Combine(_tmpDir, workstream + ".db");
            o.UserId = user;
            o.IncludeTechnical = includeTechnical;
        });
        var sp = services.BuildServiceProvider();
        return (sp,
                sp.GetRequiredService<IMemoryAgent>(),
                sp.GetRequiredService<IMemoryQueryAPI>(),
                sp.GetRequiredService<CapabilityToken>());
    }

    private static CaptureEvent Evidence(string id, string ws, string content,
        DateTimeOffset? validAt = null, EventChannel channel = EventChannel.Epistemic) =>
        new(new EventId(id), new WorkstreamId(ws), channel,
            validAt ?? DateTimeOffset.UtcNow, validAt ?? DateTimeOffset.UtcNow,
            new EvidencePayload(content, "test"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")));

    [Fact]
    public async Task Free_text_query_with_punctuation_does_not_throw()
    {
        // Natural-language questions contain FTS5 metacharacters ("?", "'",
        // parens). They must be sanitized, not passed raw into MATCH.
        var (sp, agent, query, token) = BuildHost(workstream: "q-nl");
        using var _ = sp;
        await agent.IngestAsync(Evidence("nl-1", "q-nl", "Alice adopted a golden retriever named Max"));
        await agent.IngestAsync(Evidence("nl-2", "q-nl", "Bob is training for a marathon"));

        var result = await query.QueryAsync(new QueryRequest(
            new QuerySpec(new WorkstreamId("q-nl"), FreeText: "What kind of dog does Alice have?")), token);
        Assert.Contains(result.Items, i => i.EventId.Value == "nl-1");
    }

    [Fact]
    public async Task Query_single_category_filter_returns_only_that_category()
    {
        // Exercises the single-category equality fast path (e.category = $cat)
        // added so SQLite serves ORDER BY valid_at from the category index
        // instead of a temp-b-tree sort. This test guards its correctness.
        var (sp, agent, query, token) = BuildHost(workstream: "q-cat1");
        using var _ = sp;
        var ws = new WorkstreamId("q-cat1");
        await agent.IngestAsync(Evidence("c-ev1", "q-cat1", "an evidence item"));
        await agent.IngestAsync(new CaptureEvent(
            new EventId("c-fact1"), ws, EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new FactPayload("a fact", Array.Empty<EventId>()),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p"))));
        await agent.IngestAsync(Evidence("c-ev2", "q-cat1", "another evidence item"));

        var onlyEvidence = await query.QueryAsync(new QueryRequest(
            new QuerySpec(ws, Categories: new[] { EpistemicCategory.Evidence })), token);
        Assert.Equal(2, onlyEvidence.Items.Count);
        Assert.All(onlyEvidence.Items, i => Assert.Equal(EpistemicCategory.Evidence, i.Category));

        var onlyFact = await query.QueryAsync(new QueryRequest(
            new QuerySpec(ws, Categories: new[] { EpistemicCategory.Fact })), token);
        Assert.Single(onlyFact.Items);
        Assert.Equal("c-fact1", onlyFact.Items[0].EventId.Value);
    }

    [Fact]
    public async Task Query_returns_structured_results_workstream_scoped()
    {
        var (sp, agent, query, token) = BuildHost(workstream: "q-ws-1");
        using var _ = sp;
        await agent.IngestAsync(Evidence("q-001", "q-ws-1", "alpha"));
        await agent.IngestAsync(Evidence("q-002", "q-ws-1", "beta"));

        var result = await query.QueryAsync(new QueryRequest(
            new QuerySpec(new WorkstreamId("q-ws-1"))), token);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.TotalMatched);
        Assert.Null(result.Explain);
    }

    [Fact]
    public async Task Query_rejects_workstream_mismatch()
    {
        var (sp, _, query, token) = BuildHost(workstream: "q-ws-a");
        using var _2 = sp;
        await Assert.ThrowsAsync<CapabilityDeniedError>(async () =>
            await query.QueryAsync(new QueryRequest(new QuerySpec(new WorkstreamId("q-ws-b"))), token));
    }

    [Fact]
    public async Task Query_rejects_expired_token()
    {
        var (sp, _, query, _) = BuildHost(workstream: "q-ws-exp");
        using var _2 = sp;
        var expired = new CapabilityToken(
            Principal: new PrincipalId("p"),
            Workstream: new WorkstreamId("q-ws-exp"),
            NotBefore: DateTimeOffset.UtcNow.AddDays(-2),
            NotAfter: DateTimeOffset.UtcNow.AddDays(-1),
            AllowedCategories: Array.Empty<EpistemicCategory>());
        await Assert.ThrowsAsync<CapabilityDeniedError>(async () =>
            await query.QueryAsync(new QueryRequest(new QuerySpec(new WorkstreamId("q-ws-exp"))), expired));
    }

    [Fact]
    public async Task Query_rejects_technical_channel_without_grant()
    {
        var (sp, _, query, token) = BuildHost(workstream: "q-ws-t");
        using var _2 = sp;
        await Assert.ThrowsAsync<CapabilityDeniedError>(async () =>
            await query.QueryAsync(new QueryRequest(
                new QuerySpec(new WorkstreamId("q-ws-t"), Channel: EventChannel.Technical)), token));
    }

    [Fact]
    public async Task Query_rejects_category_outside_token_allow_set()
    {
        var (sp, _, query, _) = BuildHost(workstream: "q-ws-cat");
        using var _2 = sp;
        var token = new CapabilityToken(
            Principal: new PrincipalId("p"),
            Workstream: new WorkstreamId("q-ws-cat"),
            NotBefore: DateTimeOffset.UtcNow.AddDays(-1),
            NotAfter: DateTimeOffset.UtcNow.AddDays(1),
            AllowedCategories: new[] { EpistemicCategory.Fact }); // only Fact allowed
        await Assert.ThrowsAsync<CapabilityDeniedError>(async () =>
            await query.QueryAsync(new QueryRequest(
                new QuerySpec(new WorkstreamId("q-ws-cat"),
                    Categories: new[] { EpistemicCategory.Evidence })), token));
    }

    [Fact]
    public async Task Query_excludes_revoked_events()
    {
        var (sp, agent, query, token) = BuildHost(workstream: "q-rev");
        using var _ = sp;
        await agent.IngestAsync(Evidence("rev-001", "q-rev", "keep"));
        await agent.IngestAsync(Evidence("rev-002", "q-rev", "remove"));
        var rev = sp.GetRequiredService<Mneme.Revocation.IRevocationService>();
        await rev.RevokeAsync(new EventId("rev-002"), new WorkstreamId("q-rev"),
            new PrincipalId("alice"), "test");

        var result = await query.QueryAsync(new QueryRequest(new QuerySpec(new WorkstreamId("q-rev"))), token);
        Assert.Single(result.Items);
        Assert.Equal("rev-001", result.Items[0].EventId.Value);
    }

    [Fact]
    public async Task Query_free_text_uses_fts5_path()
    {
        var (sp, agent, query, token) = BuildHost(workstream: "q-fts");
        using var _ = sp;
        await agent.IngestAsync(Evidence("fts-001", "q-fts", "the quick brown fox"));
        await agent.IngestAsync(Evidence("fts-002", "q-fts", "the rain in spain"));

        var result = await query.QueryAsync(new QueryRequest(
            new QuerySpec(new WorkstreamId("q-fts"), FreeText: "fox"),
            Explain: true), token);
        Assert.Single(result.Items);
        Assert.Equal("fts-001", result.Items[0].EventId.Value);
        Assert.Equal("lexical-fts5", result.Explain!.DispatcherChoice);
        Assert.NotNull(result.Items[0].Details);
        Assert.True(result.Items[0].Details!.Bm25 > 0);
    }

    [Fact]
    public async Task ListRecent_returns_newest_first_workstream_scoped()
    {
        var (sp, agent, query, token) = BuildHost(workstream: "q-recent");
        using var _ = sp;
        for (var i = 0; i < 5; i++)
        {
            await agent.IngestAsync(Evidence($"r-{i:D3}", "q-recent", $"item {i}"));
            await Task.Delay(5);
        }
        var got = await query.ListRecentAsync(new WorkstreamId("q-recent"), 3, token);
        Assert.Equal(3, got.Count);
        Assert.Equal("r-004", got[0].EventId.Value);
        Assert.Equal("r-003", got[1].EventId.Value);
        Assert.Equal("r-002", got[2].EventId.Value);
    }

    [Fact]
    public async Task Distill_without_distiller_falls_back_to_heuristic_bundle()
    {
        var (sp, agent, query, token) = BuildHost(workstream: "q-dist");
        using var _ = sp;
        await agent.IngestAsync(Evidence("d-001", "q-dist", "hello"));

        var bundle = await query.DistillAsync(new WorkstreamId("q-dist"),
            new DistillOptions(), token);
        // No host IDistiller registered → SDK assembles the heuristic
        // fallback (still a complete ContextBundle, just no LLM prose).
        Assert.False(bundle.IsStale);
        Assert.Equal("q-dist", bundle.Workstream.Value);
        Assert.Single(bundle.Sections);
        Assert.Equal(EpistemicCategory.Evidence, bundle.Sections[0].Category);
        Assert.Contains("d-001", bundle.Sections[0].Content);
        Assert.Equal("d-001", bundle.EventsCoveredThrough.Value);
        Assert.Contains("Heuristic synthesis", bundle.Orientation.Paragraph);
    }

    [Fact]
    public async Task AsOf_temporal_filter_hides_events_recorded_after()
    {
        var (sp, agent, query, token) = BuildHost(workstream: "q-asof");
        using var _ = sp;
        await agent.IngestAsync(Evidence("t-001", "q-asof", "early"));
        await Task.Delay(50);
        var checkpoint = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        await agent.IngestAsync(Evidence("t-002", "q-asof", "late"));

        var all = await query.QueryAsync(new QueryRequest(new QuerySpec(new WorkstreamId("q-asof"))), token);
        Assert.Equal(2, all.Items.Count);

        var asOf = await query.QueryAsync(new QueryRequest(
            new QuerySpec(new WorkstreamId("q-asof"), AsOf: checkpoint)), token);
        Assert.Single(asOf.Items);
        Assert.Equal("t-001", asOf.Items[0].EventId.Value);
    }

    [Fact]
    public async Task Explain_populates_global_and_per_item_diagnostics()
    {
        var (sp, agent, query, token) = BuildHost(workstream: "q-exp");
        using var _ = sp;
        await agent.IngestAsync(Evidence("ex-001", "q-exp", "hello"));
        var result = await query.QueryAsync(new QueryRequest(
            new QuerySpec(new WorkstreamId("q-exp")), Explain: true), token);
        Assert.NotNull(result.Explain);
        Assert.Equal("structured", result.Explain!.DispatcherChoice);
        Assert.Contains("alice", result.Explain.CapabilityCheck);
        Assert.Single(result.Items);
        Assert.NotNull(result.Items[0].Details);
        Assert.Equal(1.0, result.Items[0].Details!.CurationMultiplier);
    }

    [Fact]
    public async Task Cross_workstream_token_can_query_any_workstream()
    {
        var (sp, agent, query, _) = BuildHost(workstream: "q-cross-a");
        using var _2 = sp;
        // Ingest into two workstreams via the cross-token path.
        var crossToken = new CapabilityToken(
            Principal: new PrincipalId("admin"),
            Workstream: null,
            NotBefore: DateTimeOffset.UtcNow.AddDays(-1),
            NotAfter: DateTimeOffset.UtcNow.AddDays(1),
            AllowedCategories: Array.Empty<EpistemicCategory>(),
            CrossWorkstream: true);
        await agent.IngestAsync(Evidence("x-a", "q-cross-a", "a-data"));
        await agent.IngestAsync(Evidence("x-b", "q-cross-b", "b-data"));

        var result = await query.QueryAsync(new QueryRequest(new QuerySpec(Workstream: null)), crossToken);
        Assert.Equal(2, result.Items.Count);
    }
}
