using Mneme.Search;

namespace Mneme.Tests;

public sealed class AdaptiveBm25Tests
{
    [Theory]
    [InlineData(0, 15, -7)]
    [InlineData(1, 15, -7)]
    [InlineData(3, 15, -7)]
    [InlineData(4, 10, -10)]
    [InlineData(6, 10, -10)]
    [InlineData(7, 8, -12)]
    [InlineData(9, 8, -12)]
    [InlineData(10, 6, -14)]
    [InlineData(15, 6, -14)]
    [InlineData(16, 4, -16)]
    [InlineData(50, 4, -16)]
    public void Parameters_match_mem0_regimes(int tokens, double k, double x0)
    {
        var (gotK, gotX0) = AdaptiveBm25.Parameters(tokens);
        Assert.Equal(k, gotK);
        Assert.Equal(x0, gotX0);
    }

    [Fact]
    public void Normalize_maps_to_unit_interval()
    {
        // A more-negative raw bm25 (better match) should yield a higher score.
        var weaker = AdaptiveBm25.Normalize(-3.0, 5);
        var stronger = AdaptiveBm25.Normalize(-12.0, 5);
        Assert.InRange(weaker, 0.0, 1.0);
        Assert.InRange(stronger, 0.0, 1.0);
        Assert.True(stronger > weaker, $"stronger={stronger}, weaker={weaker}");
    }

    [Theory]
    [InlineData("",                  0)]
    [InlineData("hello",             1)]
    [InlineData("hello world",       2)]
    [InlineData("  a  b   c\n d  ",  4)]
    public void Token_count_splits_on_whitespace(string q, int expected) =>
        Assert.Equal(expected, AdaptiveBm25.CountTokens(q));
}
