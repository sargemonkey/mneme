using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Ingest;

namespace Mneme.Tests;

/// <summary>
/// Covers the read-side visibility tier (Phase 13, ADR-0004): sensitive classes
/// default to <see cref="Visibility.Private"/> (author-only), non-sensitive to
/// <see cref="Visibility.Shared"/>. Private events are readable only by the
/// principal that authored them — the PII-containment boundary in a shared
/// workstream. Enforced across all read paths (ListRecent, structured, free-text).
/// </summary>
public sealed class VisibilityTierTests : IDisposable
{
    private readonly string _tmpDir;
    public VisibilityTierTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-vis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private const string Ws = "vis-ws";

    private (ServiceProvider sp, IMemoryAgent agent, IMemoryQueryAPI query) BuildHost()
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = Ws;
            o.SqlitePath = Path.Combine(_tmpDir, "vis.db");
            o.UserId = "host";
        });
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IMemoryAgent>(), sp.GetRequiredService<IMemoryQueryAPI>());
    }

    // A token whose viewer-principal is set — visibility keys "own Private" off this.
    private static CapabilityToken TokenFor(string principal) => new(
        Principal: new PrincipalId(principal),
        Workstream: new WorkstreamId(Ws),
        NotBefore: DateTimeOffset.UtcNow.AddMinutes(-1),
        NotAfter: DateTimeOffset.UtcNow.AddDays(1),
        AllowedCategories: Array.Empty<EpistemicCategory>());

    private static CaptureEvent Event(string id, string author, string content) =>
        new(new EventId(id), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload(content, "test"),
            new CaptureProvenance(new CaptureSourceId("agent"), new PrincipalId(author)));

    [Fact]
    public void DefaultVisibility_maps_sensitive_classes_to_private()
    {
        Assert.Equal(Visibility.Private, MemoryAgent.DefaultVisibility(Contracts.Classification.Pii));
        Assert.Equal(Visibility.Private, MemoryAgent.DefaultVisibility(Contracts.Classification.Confidential));
        Assert.Equal(Visibility.Private, MemoryAgent.DefaultVisibility(Contracts.Classification.Secret));
        Assert.Equal(Visibility.Shared, MemoryAgent.DefaultVisibility(Contracts.Classification.Public));
        Assert.Equal(Visibility.Shared, MemoryAgent.DefaultVisibility(Contracts.Classification.Internal));
    }

    [Fact]
    public async Task Private_event_is_visible_to_its_author()
    {
        var (sp, agent, query) = BuildHost();
        using var _ = sp;
        // "Confidential …" → Classification.Confidential → Visibility.Private.
        await agent.IngestAsync(Event("p1", "author-x", "Confidential: the Q3 pricing model is changing"));

        var seen = await query.ListRecentAsync(new WorkstreamId(Ws), 50, TokenFor("author-x"));
        Assert.Contains(seen, i => i.EventId.Value == "p1");
    }

    [Fact]
    public async Task Private_event_is_hidden_from_a_different_principal()
    {
        var (sp, agent, query) = BuildHost();
        using var _ = sp;
        await agent.IngestAsync(Event("p1", "author-x", "Confidential: the Q3 pricing model is changing"));

        var seen = await query.ListRecentAsync(new WorkstreamId(Ws), 50, TokenFor("viewer-y"));
        Assert.DoesNotContain(seen, i => i.EventId.Value == "p1");
    }

    [Fact]
    public async Task Shared_event_is_visible_to_any_principal()
    {
        var (sp, agent, query) = BuildHost();
        using var _ = sp;
        // Plain content → Classification.Public → Visibility.Shared.
        await agent.IngestAsync(Event("s1", "author-x", "the deployment finished successfully"));

        var asAuthor = await query.ListRecentAsync(new WorkstreamId(Ws), 50, TokenFor("author-x"));
        var asOther = await query.ListRecentAsync(new WorkstreamId(Ws), 50, TokenFor("viewer-y"));
        Assert.Contains(asAuthor, i => i.EventId.Value == "s1");
        Assert.Contains(asOther, i => i.EventId.Value == "s1");
    }

    [Fact]
    public async Task Free_text_path_enforces_author_only_private()
    {
        var (sp, agent, query) = BuildHost();
        using var _ = sp;
        await agent.IngestAsync(Event("f1", "author-x", "Confidential migration runbook for the billing service"));

        var asOther = await query.QueryAsync(new QueryRequest(
            new QuerySpec(new WorkstreamId(Ws), FreeText: "billing service migration")), TokenFor("viewer-y"));
        Assert.DoesNotContain(asOther.Items, i => i.EventId.Value == "f1");

        var asAuthor = await query.QueryAsync(new QueryRequest(
            new QuerySpec(new WorkstreamId(Ws), FreeText: "billing service migration")), TokenFor("author-x"));
        Assert.Contains(asAuthor.Items, i => i.EventId.Value == "f1");
    }

    [Fact]
    public async Task Structured_query_enforces_author_only_private()
    {
        var (sp, agent, query) = BuildHost();
        using var _ = sp;
        await agent.IngestAsync(Event("st1", "author-x", "Confidential: acquisition target shortlist"));

        var asOther = await query.QueryAsync(new QueryRequest(
            new QuerySpec(new WorkstreamId(Ws), Categories: new[] { EpistemicCategory.Evidence })),
            TokenFor("viewer-y"));
        Assert.DoesNotContain(asOther.Items, i => i.EventId.Value == "st1");

        var asAuthor = await query.QueryAsync(new QueryRequest(
            new QuerySpec(new WorkstreamId(Ws), Categories: new[] { EpistemicCategory.Evidence })),
            TokenFor("author-x"));
        Assert.Contains(asAuthor.Items, i => i.EventId.Value == "st1");
    }
}
