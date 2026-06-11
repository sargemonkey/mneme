namespace Mneme.Contracts.Tests;

public sealed class IdentifierTests
{
    [Fact]
    public void EventId_None_HasValue_False()
    {
        Assert.False(EventId.None.HasValue);
        Assert.Equal(string.Empty, EventId.None.Value);
    }

    [Fact]
    public void EventId_NonEmpty_HasValue_True()
    {
        var id = new EventId("01HJABC");
        Assert.True(id.HasValue);
        Assert.Equal("01HJABC", id.Value);
        Assert.Equal("01HJABC", id.ToString());
    }

    [Fact]
    public void EventId_Equality_IsCaseSensitive()
    {
        Assert.Equal(new EventId("abc"), new EventId("abc"));
        Assert.NotEqual(new EventId("abc"), new EventId("ABC"));
    }

    [Fact]
    public void WorkstreamId_RoundTripsValue()
    {
        var id = new WorkstreamId("cust-acme-q3");
        Assert.Equal("cust-acme-q3", id.Value);
        Assert.Equal("cust-acme-q3", id.ToString());
    }

    [Fact]
    public void FactId_EqualityByValue()
    {
        Assert.Equal(new FactId("fact-1"), new FactId("fact-1"));
        Assert.NotEqual(new FactId("fact-1"), new FactId("fact-2"));
    }

    [Fact]
    public void EntityId_EqualityByValue()
    {
        Assert.Equal(new EntityId("e1"), new EntityId("e1"));
    }

    [Fact]
    public void PrincipalId_EqualityByValue()
    {
        Assert.Equal(new PrincipalId("u@x"), new PrincipalId("u@x"));
        Assert.NotEqual(new PrincipalId("u@x"), new PrincipalId("v@x"));
    }
}
