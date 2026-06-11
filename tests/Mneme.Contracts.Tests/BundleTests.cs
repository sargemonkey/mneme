using System.Text.Json;

namespace Mneme.Contracts.Tests;

public sealed class BundleTests
{
    [Fact]
    public void ContextBundle_Roundtrips()
    {
        var bundle = NewBundle();
        var json = JsonSerializer.Serialize(bundle, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<ContextBundle>(json, Fixtures.JsonOptions);
        Assert.NotNull(back);
        Assert.Equal(bundle.Workstream, back!.Workstream);
        Assert.Equal(bundle.IsStale, back.IsStale);
        Assert.Equal(bundle.GeneratedAt, back.GeneratedAt);
        Assert.Equal(bundle.EventsCoveredThrough, back.EventsCoveredThrough);
        Assert.Equal(bundle.Index.TokenCount, back.Index.TokenCount);
        Assert.Single(back.Sections);
        Assert.Equal(2, back.Hints.Hints.Count);
    }

    [Fact]
    public void BundleSection_ProvenanceListIsPreserved()
    {
        var section = new BundleSection(
            "sec-1",
            "Decisions",
            EpistemicCategory.Decision,
            "We decided X because Y",
            "gpt-4o-2026-06-01:prompt-hash-abc",
            DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
            new EventId("e-100"),
            TokenBudget: 4000,
            TokenCount: 3120,
            Provenance: new[] { new EventId("e-50"), new EventId("e-75") });

        var json = JsonSerializer.Serialize(section, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<BundleSection>(json, Fixtures.JsonOptions);
        Assert.NotNull(back);
        Assert.Equal(2, back!.Provenance.Count);
        Assert.Equal(new EventId("e-50"), back.Provenance[0]);
        Assert.Equal(new EventId("e-75"), back.Provenance[1]);
    }

    [Fact]
    public void BundleIndex_TokenBudgetIsCarriedSeparately()
    {
        // Budget vs. actual count is the contract that lets consumers
        // detect over-budget bundles. The two fields are distinct.
        var idx = new BundleIndex(
            "distiller-x",
            TokenBudget: 1000,
            TokenCount: 1247,
            DateTimeOffset.UtcNow,
            new EventId("e-1"),
            Array.Empty<BundleSectionRef>());
        Assert.Equal(1000, idx.TokenBudget);
        Assert.Equal(1247, idx.TokenCount);
    }

    [Fact]
    public void OrientationSummary_RoundtripsViaJson()
    {
        var o = new OrientationSummary(
            "We are in week 4 of the Q3 release; main risk is auth-rollback.",
            "gpt-4o-2026-06-01",
            DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
            new EventId("e-100"));
        var json = JsonSerializer.Serialize(o, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<OrientationSummary>(json, Fixtures.JsonOptions);
        Assert.Equal(o, back);
    }

    [Fact]
    public void LookupHints_EmptyListIsValid()
    {
        var h = new LookupHints(Array.Empty<LookupHint>());
        Assert.Empty(h.Hints);
    }

    private static ContextBundle NewBundle() => new(
        new WorkstreamId("cust-acme"),
        new OrientationSummary(
            "Para.",
            "distiller-x",
            DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
            new EventId("e-100")),
        new BundleIndex(
            "distiller-x",
            1000,
            750,
            DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
            new EventId("e-100"),
            new[] { new BundleSectionRef("sec-1", "Decisions", EpistemicCategory.Decision, 3120) }),
        new[]
        {
            new BundleSection(
                "sec-1", "Decisions", EpistemicCategory.Decision,
                "## Decisions\n- decided X",
                "distiller-x",
                DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
                new EventId("e-100"),
                4000, 3120,
                new[] { new EventId("e-50") }),
        },
        new LookupHints(new[]
        {
            new LookupHint("auth rollback", new EventId("e-30"), "from rfc-12"),
            new LookupHint("ci flake", new EventId("e-31"), "from incident-7"),
        }),
        DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
        new EventId("e-100"),
        IsStale: false);
}
