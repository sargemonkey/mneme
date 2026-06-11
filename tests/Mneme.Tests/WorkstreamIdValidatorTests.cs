using Mneme.Ingest.Validation;

namespace Mneme.Tests;

public sealed class WorkstreamIdValidatorTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("ws")]
    [InlineData("cust-acme-q3")]
    [InlineData("alpha.beta.gamma")]
    [InlineData("ws_2026_06")]
    [InlineData("abc-123-xyz")]
    public void Accepts_valid_ids(string id) =>
        Assert.True(WorkstreamIdValidator.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData(".leading")]
    [InlineData("trailing-")]
    [InlineData("trailing.")]
    [InlineData("double--dash")]
    [InlineData("a..b")]
    [InlineData("UPPER")]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("path/with/slash")]
    [InlineData("emoji-🚀")]
    [InlineData("nul\u0000byte")]
    public void Rejects_invalid_ids(string? id) =>
        Assert.False(WorkstreamIdValidator.IsValid(id));

    [Fact]
    public void Rejects_ids_longer_than_128()
    {
        var id = new string('a', 129);
        Assert.False(WorkstreamIdValidator.IsValid(id));
    }

    [Fact]
    public void EnsureValid_throws_on_invalid()
    {
        Assert.Throws<ArgumentException>(() => WorkstreamIdValidator.EnsureValid("UPPER"));
    }
}
