using System.Text.Json;

namespace Mneme.Contracts.Tests;

public sealed class QueryTests
{
    [Fact]
    public void QuerySpec_DefaultChannelIsEpistemic()
    {
        var spec = new QuerySpec(new WorkstreamId("w"));
        Assert.Equal(EventChannel.Epistemic, spec.Channel);
    }

    [Fact]
    public void QuerySpec_DefaultLimitIs50()
    {
        var spec = new QuerySpec(new WorkstreamId("w"));
        Assert.Equal(50, spec.Limit);
    }

    [Fact]
    public void QueryRequest_DefaultExplainIsFalse()
    {
        var req = new QueryRequest(new QuerySpec(new WorkstreamId("w")));
        Assert.False(req.Explain);
    }

    [Fact]
    public void DistillOptions_DefaultsAreNotForce_NoBudget()
    {
        var o = new DistillOptions();
        Assert.False(o.ForceRefresh);
        Assert.Null(o.TokenBudget);
    }

    [Fact]
    public void QuerySpec_Roundtrips()
    {
        var spec = new QuerySpec(
            new WorkstreamId("cust-acme"),
            new[] { EpistemicCategory.Decision, EpistemicCategory.Outcome },
            EventChannel.Epistemic,
            FreeText: "release plan",
            Entity: new EntityId("john"),
            From: DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
            To: DateTimeOffset.Parse("2026-06-30T00:00:00Z"),
            AsOf: DateTimeOffset.Parse("2026-06-15T00:00:00Z"),
            Limit: 25);
        var json = JsonSerializer.Serialize(spec, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<QuerySpec>(json, Fixtures.JsonOptions);

        // Record-equality on collection-typed properties uses reference
        // equality (System.Object), so two records that are JSON-equivalent
        // can still compare unequal. Compare field-by-field instead and
        // verify the JSON output round-trips byte-for-byte.
        Assert.NotNull(back);
        Assert.Equal(spec.Workstream, back!.Workstream);
        Assert.Equal(spec.Categories, back.Categories);
        Assert.Equal(spec.Channel, back.Channel);
        Assert.Equal(spec.FreeText, back.FreeText);
        Assert.Equal(spec.Entity, back.Entity);
        Assert.Equal(spec.From, back.From);
        Assert.Equal(spec.To, back.To);
        Assert.Equal(spec.AsOf, back.AsOf);
        Assert.Equal(spec.Limit, back.Limit);

        var json2 = JsonSerializer.Serialize(back, Fixtures.JsonOptions);
        Assert.Equal(json, json2);
    }

    [Fact]
    public void ScoreDetails_Roundtrips()
    {
        var d = new ScoreDetails(0.8, 0.6, 0.2, 1.5, 0.7, 1.05, true);
        var json = JsonSerializer.Serialize(d, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<ScoreDetails>(json, Fixtures.JsonOptions);
        Assert.Equal(d, back);
    }

    [Fact]
    public void QueryResultItem_RoundtripsWithAnnotations()
    {
        var item = new QueryResultItem(
            new EventId("e1"),
            EpistemicCategory.Decision,
            DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-06-01T00:00:01Z"),
            "ship q3 release",
            0.83,
            new[] { "high-confidence", "approved by jane" });
        var json = JsonSerializer.Serialize(item, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<QueryResultItem>(json, Fixtures.JsonOptions);
        Assert.NotNull(back);
        Assert.Equal(item.EventId, back!.EventId);
        Assert.Equal(item.Category, back.Category);
        Assert.Equal(item.Score, back.Score);
        Assert.Equal(item.Annotations, back.Annotations);
        Assert.Null(back.Details);
    }

    [Fact]
    public void QueryResult_RoundtripsWithExplain()
    {
        var item = new QueryResultItem(
            new EventId("e1"),
            EpistemicCategory.Fact,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "x",
            0.5,
            Array.Empty<string>());
        var result = new QueryResult(
            new[] { item },
            42,
            new QueryExplain("lexical", "allowed: all", 100, 58));
        var json = JsonSerializer.Serialize(result, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<QueryResult>(json, Fixtures.JsonOptions);
        Assert.NotNull(back);
        Assert.Equal(42, back!.TotalMatched);
        Assert.Single(back.Items);
        Assert.NotNull(back.Explain);
        Assert.Equal("lexical", back.Explain!.DispatcherChoice);
        Assert.Equal(100, back.Explain.CandidatesConsidered);
        Assert.Equal(58, back.Explain.CandidatesGatedOut);
    }
}
