namespace Mneme.Contracts.Tests;

public sealed class RerankTests
{
    [Fact]
    public void RerankCandidate_carries_event_and_text()
    {
        var c = new RerankCandidate(new EventId("e1"), "some text");
        Assert.Equal("e1", c.EventId.Value);
        Assert.Equal("some text", c.Text);
    }

    [Fact]
    public void RerankResult_carries_event_and_score()
    {
        var r = new RerankResult(new EventId("e2"), 3.5);
        Assert.Equal("e2", r.EventId.Value);
        Assert.Equal(3.5, r.Score);
    }

    [Fact]
    public async Task IReranker_can_be_implemented_and_invoked()
    {
        IReranker reranker = new ReverseReranker();
        var result = await reranker.RerankAsync("q", new[]
        {
            new RerankCandidate(new EventId("a"), "a"),
            new RerankCandidate(new EventId("b"), "b"),
        }, topK: 2);
        Assert.Equal("b", result[0].EventId.Value); // reversed
        Assert.Equal("a", result[1].EventId.Value);
        Assert.Equal("test/reverse", reranker.Id);
    }

    private sealed class ReverseReranker : IReranker
    {
        public string Id => "test/reverse";
        public Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, int topK, CancellationToken ct = default)
        {
            var ranked = candidates.Reverse().Take(topK)
                .Select((c, i) => new RerankResult(c.EventId, candidates.Count - i)).ToArray();
            return Task.FromResult<IReadOnlyList<RerankResult>>(ranked);
        }
    }
}
