using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Curation;
using Mneme.Distillation;
using Mneme.Hosting;
using Mneme.Ingest;

namespace Mneme.Tests;

public sealed class DistillationTests : IDisposable
{
    private readonly string _tmpDir;
    public DistillationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-dist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private ServiceProvider Build(IDistiller? distiller = null, string workstream = "dist-ws")
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = workstream;
            o.SqlitePath = Path.Combine(_tmpDir, workstream + ".db");
            o.UserId = "alice";
        });
        if (distiller is not null)
        {
            services.AddSingleton(distiller);
        }
        services.AddSingleton(sp => new CurationCapability(
            Principal: new PrincipalId("alice"),
            Workstream: new WorkstreamId(workstream),
            NotBefore: DateTimeOffset.UtcNow.AddDays(-1),
            NotAfter: DateTimeOffset.UtcNow.AddDays(1),
            CanAmend: true, CanAnnotate: true, CanPin: true, CanDemote: true,
            CanSplit: true, CanMerge: true, CanRevert: true, CanReview: true));
        return services.BuildServiceProvider();
    }

    private static CaptureEvent Evidence(string id, string ws, string content) =>
        new(new EventId(id), new WorkstreamId(ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload(content, "test"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")));

    [Fact]
    public async Task RequestBuilder_includes_all_events_with_recency_scores()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        await agent.IngestAsync(Evidence("rb-001", "dist-ws", "alpha"));
        await agent.IngestAsync(Evidence("rb-002", "dist-ws", "beta"));

        var builder = new DistillationRequestBuilder(sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>());
        var req = builder.Build(new WorkstreamId("dist-ws"), 2048, priorBundle: null, DateTimeOffset.UtcNow);
        Assert.Equal(2, req.Events.Count);
        Assert.All(req.Events, e => Assert.InRange(e.Score, 0.0, 1.0));
        Assert.Equal("rb-002", req.EventsCoveredThrough.Value);
    }

    [Fact]
    public async Task RequestBuilder_applies_pin_multiplier_to_score()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var curator = sp.GetRequiredService<IMemoryCurator>();
        var cap = sp.GetRequiredService<CurationCapability>();
        await agent.IngestAsync(Evidence("pin-001", "dist-ws", "ordinary"));
        await agent.IngestAsync(Evidence("pin-002", "dist-ws", "pinned"));
        await curator.PinAsync(new EventId("pin-002"), PinScope.Workstream, 2.5f, cap);

        var builder = new DistillationRequestBuilder(sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>());
        var req = builder.Build(new WorkstreamId("dist-ws"), 2048, null, DateTimeOffset.UtcNow);
        var pinned = req.Events.First(e => e.EventId.Value == "pin-002");
        var ordinary = req.Events.First(e => e.EventId.Value == "pin-001");
        Assert.True(pinned.Score > ordinary.Score,
            $"pinned score {pinned.Score} should beat ordinary {ordinary.Score}");
        Assert.Contains("pin-002", req.Curations.Keys.Select(k => k.Value));
    }

    [Fact]
    public async Task RequestBuilder_excludes_revoked_events()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var rev = sp.GetRequiredService<Mneme.Revocation.IRevocationService>();
        await agent.IngestAsync(Evidence("rv-001", "dist-ws", "stays"));
        await agent.IngestAsync(Evidence("rv-002", "dist-ws", "goes"));
        await rev.RevokeAsync(new EventId("rv-002"), new WorkstreamId("dist-ws"),
            new PrincipalId("alice"), "test");

        var builder = new DistillationRequestBuilder(sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>());
        var req = builder.Build(new WorkstreamId("dist-ws"), 2048, null, DateTimeOffset.UtcNow);
        Assert.Single(req.Events);
        Assert.Equal("rv-001", req.Events[0].EventId.Value);
    }

    [Fact]
    public async Task RequestBuilder_substitutes_amended_content_into_payload()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var curator = sp.GetRequiredService<IMemoryCurator>();
        var cap = sp.GetRequiredService<CurationCapability>();
        var factory = sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>();
        var evt = new CaptureEvent(new EventId("am-001"), new WorkstreamId("dist-ws"), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new FactPayload("original statement", Array.Empty<EventId>()),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")));
        await agent.IngestAsync(evt);
        var hash = PreStateHasher.ComputeHash(factory, new EventId("am-001"));
        await curator.AmendFactAsync(new FactId("am-001"), hash,
            new FactAmendment("corrected statement", "fix typo"), cap);

        var builder = new DistillationRequestBuilder(factory);
        var req = builder.Build(new WorkstreamId("dist-ws"), 2048, null, DateTimeOffset.UtcNow);
        var amended = req.Events.Single();
        Assert.IsType<FactPayload>(amended.Payload);
        Assert.Equal("corrected statement", ((FactPayload)amended.Payload).Statement);
    }

    [Fact]
    public async Task HeuristicBundle_groups_by_category_in_canonical_order()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        await agent.IngestAsync(new CaptureEvent(new EventId("hb-fact"), new WorkstreamId("dist-ws"), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new FactPayload("a fact", Array.Empty<EventId>()),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p"))));
        await agent.IngestAsync(new CaptureEvent(new EventId("hb-dec"), new WorkstreamId("dist-ws"), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new DecisionPayload("a decision", "because", Array.Empty<EventId>(), new PrincipalId("p")),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p"))));
        await agent.IngestAsync(Evidence("hb-ev", "dist-ws", "raw evidence"));

        var builder = new DistillationRequestBuilder(sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>());
        var req = builder.Build(new WorkstreamId("dist-ws"), 2048, null, DateTimeOffset.UtcNow);
        var bundle = DistillationPromptBuilder.BuildHeuristicBundle(req);
        Assert.Equal(3, bundle.Sections.Count);
        // Canonical order: Decision, then Fact, then Evidence.
        Assert.Equal(EpistemicCategory.Decision, bundle.Sections[0].Category);
        Assert.Equal(EpistemicCategory.Fact, bundle.Sections[1].Category);
        Assert.Equal(EpistemicCategory.Evidence, bundle.Sections[2].Category);
    }

    [Fact]
    public async Task Cache_serves_repeat_call_when_no_new_events_landed()
    {
        var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var api = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        await agent.IngestAsync(Evidence("c-001", "dist-ws", "first"));
        var b1 = await api.DistillAsync(new WorkstreamId("dist-ws"), new DistillOptions(), token);
        var b2 = await api.DistillAsync(new WorkstreamId("dist-ws"), new DistillOptions(), token);
        // Same GeneratedAt -> served from cache.
        Assert.Equal(b1.GeneratedAt, b2.GeneratedAt);
        sp.Dispose();
    }

    [Fact]
    public async Task Cache_invalidates_after_new_event_lands()
    {
        var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var api = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        await agent.IngestAsync(Evidence("c2-001", "dist-ws", "first"));
        var b1 = await api.DistillAsync(new WorkstreamId("dist-ws"), new DistillOptions(), token);
        await Task.Delay(10);
        await agent.IngestAsync(Evidence("c2-002", "dist-ws", "second"));
        var b2 = await api.DistillAsync(new WorkstreamId("dist-ws"), new DistillOptions(), token);
        Assert.NotEqual(b1.EventsCoveredThrough.Value, b2.EventsCoveredThrough.Value);
        Assert.Equal("c2-002", b2.EventsCoveredThrough.Value);
        sp.Dispose();
    }

    [Fact]
    public async Task Cache_invalidates_after_curation()
    {
        var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var api = sp.GetRequiredService<IMemoryQueryAPI>();
        var curator = sp.GetRequiredService<IMemoryCurator>();
        var cap = sp.GetRequiredService<CurationCapability>();
        var token = sp.GetRequiredService<CapabilityToken>();
        await agent.IngestAsync(Evidence("c3-001", "dist-ws", "to be pinned"));
        var b1 = await api.DistillAsync(new WorkstreamId("dist-ws"), new DistillOptions(), token);
        await Task.Delay(10);
        await curator.PinAsync(new EventId("c3-001"), PinScope.Workstream, 2.0f, cap);
        var b2 = await api.DistillAsync(new WorkstreamId("dist-ws"), new DistillOptions(), token);
        // GeneratedAt must advance — curation invalidated the cache.
        Assert.True(b2.GeneratedAt > b1.GeneratedAt);
        sp.Dispose();
    }

    [Fact]
    public async Task Custom_IDistiller_is_called_and_id_is_stamped_on_bundle()
    {
        var calls = 0;
        var custom = new InlineDistiller("test/inline@1", req =>
        {
            calls++;
            // Trivial bundle that echoes the request — proves the SDK
            // delegates and that the resulting bundle survives caching.
            return new ContextBundle(
                Workstream: req.Workstream,
                Orientation: new OrientationSummary($"got {req.Events.Count} events", "wrong-id-should-be-overwritten", req.GeneratedAt, req.EventsCoveredThrough),
                Index: new BundleIndex("wrong-id-should-be-overwritten", req.TokenBudget, 10, req.GeneratedAt, req.EventsCoveredThrough, Array.Empty<BundleSectionRef>()),
                Sections: Array.Empty<BundleSection>(),
                Hints: new LookupHints(Array.Empty<LookupHint>()),
                GeneratedAt: req.GeneratedAt,
                EventsCoveredThrough: req.EventsCoveredThrough,
                IsStale: false);
        });
        using var sp = Build(distiller: custom);
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var api = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        await agent.IngestAsync(Evidence("cd-001", "dist-ws", "x"));
        var b = await api.DistillAsync(new WorkstreamId("dist-ws"), new DistillOptions(ForceRefresh: true), token);
        Assert.Equal(1, calls);
        // SDK overrides the distiller id on the returned bundle so a host
        // that forgot to set it still gets the right one stamped.
        Assert.Equal("test/inline@1", b.Orientation.Distiller);
        Assert.Equal("test/inline@1", b.Index.Distiller);
        Assert.Contains("got 1 events", b.Orientation.Paragraph);
    }

    [Fact]
    public async Task PromptBuilder_user_prompt_lists_every_event_grouped_by_category()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        await agent.IngestAsync(Evidence("pb-001", "dist-ws", "evidence one"));
        await agent.IngestAsync(new CaptureEvent(new EventId("pb-002"), new WorkstreamId("dist-ws"), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new DecisionPayload("the choice", "reason", Array.Empty<EventId>(), new PrincipalId("p")),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p"))));

        var builder = new DistillationRequestBuilder(sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>());
        var req = builder.Build(new WorkstreamId("dist-ws"), 2048, null, DateTimeOffset.UtcNow);
        var prompt = DistillationPromptBuilder.BuildUserPrompt(req);
        Assert.Contains("Workstream: dist-ws", prompt);
        Assert.Contains("== Decision (1)", prompt);
        Assert.Contains("== Evidence (1)", prompt);
        Assert.Contains("[pb-001]", prompt);
        Assert.Contains("[pb-002]", prompt);
    }

    private sealed class InlineDistiller : IDistiller
    {
        private readonly Func<DistillationRequest, ContextBundle> _fn;
        public string Id { get; }
        public InlineDistiller(string id, Func<DistillationRequest, ContextBundle> fn) { Id = id; _fn = fn; }
        public Task<ContextBundle> DistillAsync(DistillationRequest request, CancellationToken ct = default)
            => Task.FromResult(_fn(request));
    }
}
