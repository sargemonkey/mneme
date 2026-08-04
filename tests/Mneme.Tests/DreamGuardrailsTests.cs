using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Dreaming;
using Mneme.Hosting;
using Mneme.Ingest;
using Mneme.Review;

namespace Mneme.Tests;

/// <summary>
/// Covers the operational dreamer guardrails (Phase 14, ADR-0004): the
/// cross-workstream opt-in flag, the classification floor for global promotion,
/// capability-gated Citation.Derived traversal, and the consolidation audit
/// trail.
/// </summary>
public sealed class DreamGuardrailsTests : IDisposable
{
    private readonly string _tmpDir;
    public DreamGuardrailsTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private ServiceProvider BuildHost(string workstream)
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = workstream;
            o.SqlitePath = Path.Combine(_tmpDir, workstream + ".db");
            o.UserId = "host";
        });
        return services.BuildServiceProvider();
    }

    // --- classification floor (pure) ---------------------------------------

    [Fact]
    public void Global_promotion_requires_all_public_or_internal_sources()
    {
        var a = new EventId("a");
        var b = new EventId("b");
        var allPublic = new Dictionary<string, Mneme.Contracts.Classification>
        {
            ["a"] = Mneme.Contracts.Classification.Public, ["b"] = Mneme.Contracts.Classification.Internal,
        };
        Assert.True(DreamGuardrails.IsGlobalPromotionEligible(new[] { a, b }, allPublic));

        var oneSensitive = new Dictionary<string, Mneme.Contracts.Classification>
        {
            ["a"] = Mneme.Contracts.Classification.Public, ["b"] = Mneme.Contracts.Classification.Confidential,
        };
        Assert.False(DreamGuardrails.IsGlobalPromotionEligible(new[] { a, b }, oneSensitive));

        // Unknown source classification is treated as ineligible.
        var missing = new Dictionary<string, Mneme.Contracts.Classification> { ["a"] = Mneme.Contracts.Classification.Public };
        Assert.False(DreamGuardrails.IsGlobalPromotionEligible(new[] { a, b }, missing));

        // No sources at all is ineligible.
        Assert.False(DreamGuardrails.IsGlobalPromotionEligible(Array.Empty<EventId>(), allPublic));
    }

    [Fact]
    public void CapVisibility_floors_to_private_on_sensitive_source()
    {
        var src = new[] { new EventId("s") };
        var sensitive = new Dictionary<string, Mneme.Contracts.Classification> { ["s"] = Mneme.Contracts.Classification.Pii };
        Assert.Equal(Visibility.Private,
            DreamGuardrails.CapVisibility(Visibility.Global, src, sensitive));

        var ok = new Dictionary<string, Mneme.Contracts.Classification> { ["s"] = Mneme.Contracts.Classification.Internal };
        Assert.Equal(Visibility.Global,
            DreamGuardrails.CapVisibility(Visibility.Global, src, ok));
        // Proposed lower than the ceiling is preserved.
        Assert.Equal(Visibility.Shared,
            DreamGuardrails.CapVisibility(Visibility.Shared, src, ok));
    }

    // --- opt-in flag -------------------------------------------------------

    [Fact]
    public void Cross_workstream_participation_defaults_to_false_and_toggles()
    {
        using var sp = BuildHost("guard-optin");
        var store = sp.GetRequiredService<WorkstreamConfigStore>();
        var ws = new WorkstreamId("guard-optin");

        Assert.False(store.GetParticipatesInCrossWorkstreamConsolidation(ws));

        store.SetParticipatesInCrossWorkstreamConsolidation(ws, true);
        Assert.True(store.GetParticipatesInCrossWorkstreamConsolidation(ws));

        store.SetParticipatesInCrossWorkstreamConsolidation(ws, false);
        Assert.False(store.GetParticipatesInCrossWorkstreamConsolidation(ws));
    }

    [Fact]
    public void Opt_in_does_not_disturb_the_workstream_mode()
    {
        using var sp = BuildHost("guard-mode");
        var store = sp.GetRequiredService<WorkstreamConfigStore>();
        var ws = new WorkstreamId("guard-mode");

        store.SetMode(ws, WorkstreamMode.ReviewBeforeDistill);
        store.SetParticipatesInCrossWorkstreamConsolidation(ws, true);

        Assert.Equal(WorkstreamMode.ReviewBeforeDistill, store.GetMode(ws));
        Assert.True(store.GetParticipatesInCrossWorkstreamConsolidation(ws));
    }

    // --- capability-gated Citation.Derived traversal -----------------------

    private sealed class SkillFromEvidenceDreamer : IDreamer
    {
        public string Id => "test/guard-dreamer@1";
        public Task<DreamResult> DreamAsync(DreamRequest request, CancellationToken ct = default)
        {
            var outputs = request.Events
                .Where(e => e.Payload is EvidencePayload)
                .Select(e => new DreamOutput(
                    new SkillPayload("s", "do it", null, Array.Empty<EventId>()),
                    new[] { e.EventId }, Visibility.Shared))
                .ToArray();
            return Task.FromResult(new DreamResult(outputs));
        }
    }

    [Fact]
    public async Task Derived_traversal_returns_sources_the_token_may_read()
    {
        var services = new ServiceCollection();
        services.AddMneme(o => { o.WorkstreamId = "guard-trav2"; o.SqlitePath = Path.Combine(_tmpDir, "trav2.db"); o.UserId = "host"; });
        services.AddSingleton<IDreamer>(new SkillFromEvidenceDreamer());
        using var sp2 = services.BuildServiceProvider();
        var ws2 = new WorkstreamId("guard-trav2");

        var agent = sp2.GetRequiredService<IMemoryAgent>();
        var dream = sp2.GetRequiredService<DreamCoordinator>();
        var resolver = sp2.GetRequiredService<DerivedCitationResolver>();
        var token = sp2.GetRequiredService<CapabilityToken>();

        await agent.IngestAsync(new CaptureEvent(new EventId("src-1"), ws2, EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new EvidencePayload("a fact", "t"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("u"))));

        var summary = await dream.ConsolidateAsync(ws2, token);
        var derivedId = summary.Produced.Single();

        // The workstream-scoped token authorizes the source in its own workstream.
        var allowed = resolver.ResolveAuthorizedSources(derivedId, token);
        Assert.Contains(new EventId("src-1"), allowed);
    }

    [Fact]
    public async Task Derived_traversal_hides_sources_outside_the_token_workstream()
    {
        var services = new ServiceCollection();
        services.AddMneme(o => { o.WorkstreamId = "guard-hide"; o.SqlitePath = Path.Combine(_tmpDir, "hide.db"); o.UserId = "host"; });
        services.AddSingleton<IDreamer>(new SkillFromEvidenceDreamer());
        using var sp = services.BuildServiceProvider();
        var ws = new WorkstreamId("guard-hide");

        var agent = sp.GetRequiredService<IMemoryAgent>();
        var dream = sp.GetRequiredService<DreamCoordinator>();
        var resolver = sp.GetRequiredService<DerivedCitationResolver>();
        var token = sp.GetRequiredService<CapabilityToken>();

        await agent.IngestAsync(new CaptureEvent(new EventId("src-1"), ws, EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new EvidencePayload("a fact", "t"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("u"))));
        var summary = await dream.ConsolidateAsync(ws, token);
        var derivedId = summary.Produced.Single();

        // A token scoped to a DIFFERENT workstream may not traverse into this one.
        var foreignToken = new CapabilityToken(
            Principal: new PrincipalId("intruder"),
            Workstream: new WorkstreamId("some-other-ws"),
            NotBefore: DateTimeOffset.UtcNow.AddMinutes(-1),
            NotAfter: DateTimeOffset.UtcNow.AddDays(1),
            AllowedCategories: Array.Empty<EpistemicCategory>());

        var allowed = resolver.ResolveAuthorizedSources(derivedId, foreignToken);
        Assert.Empty(allowed);
    }

    [Fact]
    public async Task Consolidation_run_is_audited()
    {
        var services = new ServiceCollection();
        services.AddMneme(o => { o.WorkstreamId = "guard-audit"; o.SqlitePath = Path.Combine(_tmpDir, "audit.db"); o.UserId = "host"; });
        services.AddSingleton<IDreamer>(new SkillFromEvidenceDreamer());
        using var sp = services.BuildServiceProvider();
        var ws = new WorkstreamId("guard-audit");

        var agent = sp.GetRequiredService<IMemoryAgent>();
        var dream = sp.GetRequiredService<DreamCoordinator>();
        var token = sp.GetRequiredService<CapabilityToken>();

        await agent.IngestAsync(new CaptureEvent(new EventId("e1"), ws, EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new EvidencePayload("obs", "t"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("u"))));
        await dream.ConsolidateAsync(ws, token);

        var audit = dream.GetAuditTrail(ws);
        Assert.Single(audit);
        Assert.Equal("test/guard-dreamer@1", audit[0].DreamerId);
        Assert.Equal(1, audit[0].OutputsOut);
    }
}

