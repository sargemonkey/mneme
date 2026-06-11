using Mneme.Contracts;
using Mneme.Ingest;
using Mneme.Projections;
using Mneme.Search;

namespace Mneme.Tests;

public sealed class TextSearchServiceTests
{
    [Fact]
    public async Task Search_returns_hits_for_indexed_content()
    {
        using var db = new TestDatabase();
        var search = new TextSearchService(db.Factory);
        var observer = new TextSearchIngestObserver(search);
        var agent = new MemoryAgent(
            db.Factory,
            new Mneme.Ingest.Redaction.RegexRedactor(),
            new AlwaysRedactedContent(),
            new Mneme.Classification.RuleBasedClassifier(),
            TimeProvider.System,
            new[] { (IIngestObserver)observer });

        await agent.IngestAsync(TestFixtures.NewEvidence(eventId: "s-001", content: "the quick brown fox jumps over the lazy dog"));
        await agent.IngestAsync(TestFixtures.NewEvidence(eventId: "s-002", content: "the rain in spain falls mainly on the plain"));
        await agent.IngestAsync(TestFixtures.NewEvidence(eventId: "s-003", content: "a fox is a small mammal"));

        var hits = search.Search("test-ws", "fox");
        Assert.NotEmpty(hits);
        var ids = hits.Select(h => h.EventId.Value).ToHashSet();
        Assert.Contains("s-001", ids);
        Assert.Contains("s-003", ids);
        Assert.DoesNotContain("s-002", ids);
        foreach (var h in hits)
        {
            Assert.InRange(h.NormalizedBm25, 0.0, 1.0);
            Assert.InRange(h.RecencyWeight, 0.0, 1.0);
            Assert.InRange(h.Score, 0.0, 1.0);
        }
    }

    [Fact]
    public async Task Search_is_workstream_scoped()
    {
        using var db = new TestDatabase();
        var search = new TextSearchService(db.Factory);
        var observer = new TextSearchIngestObserver(search);
        var agent = new MemoryAgent(
            db.Factory,
            new Mneme.Ingest.Redaction.RegexRedactor(),
            new AlwaysRedactedContent(),
            new Mneme.Classification.RuleBasedClassifier(),
            TimeProvider.System,
            new[] { (IIngestObserver)observer });

        await agent.IngestAsync(TestFixtures.NewEvidence(eventId: "scope-001", workstream: "ws-a", content: "the magic word is xyzzy"));
        await agent.IngestAsync(TestFixtures.NewEvidence(eventId: "scope-002", workstream: "ws-b", content: "xyzzy also lives over here"));

        var fromA = search.Search("ws-a", "xyzzy");
        var fromB = search.Search("ws-b", "xyzzy");

        Assert.Single(fromA);
        Assert.Single(fromB);
        Assert.Equal("scope-001", fromA[0].EventId.Value);
        Assert.Equal("scope-002", fromB[0].EventId.Value);
    }

    [Fact]
    public async Task Search_empty_query_returns_empty()
    {
        using var db = new TestDatabase();
        var search = new TextSearchService(db.Factory);
        var observer = new TextSearchIngestObserver(search);
        var agent = new MemoryAgent(
            db.Factory,
            new Mneme.Ingest.Redaction.RegexRedactor(),
            new AlwaysRedactedContent(),
            new Mneme.Classification.RuleBasedClassifier(),
            TimeProvider.System,
            new[] { (IIngestObserver)observer });
        await agent.IngestAsync(TestFixtures.NewEvidence(eventId: "empty-001", content: "hello"));
        Assert.Empty(search.Search("test-ws", ""));
        Assert.Empty(search.Search("test-ws", "   "));
    }
}
