namespace Mneme.Contracts.Tests;

public sealed class CapabilityTokenTests
{
    [Fact]
    public void IsValidAt_TrueInsideWindow()
    {
        var t = new CapabilityToken(
            new PrincipalId("u"),
            new WorkstreamId("w"),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-12-31T23:59:59Z"),
            Array.Empty<EpistemicCategory>());

        Assert.True(t.IsValidAt(DateTimeOffset.Parse("2026-06-15T12:00:00Z")));
    }

    [Fact]
    public void IsValidAt_FalseBeforeNotBefore()
    {
        var t = new CapabilityToken(
            new PrincipalId("u"),
            new WorkstreamId("w"),
            DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-12-31T23:59:59Z"),
            Array.Empty<EpistemicCategory>());

        Assert.False(t.IsValidAt(DateTimeOffset.Parse("2026-05-31T23:59:59Z")));
    }

    [Fact]
    public void IsValidAt_FalseAfterNotAfter()
    {
        var t = new CapabilityToken(
            new PrincipalId("u"),
            new WorkstreamId("w"),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-06-30T00:00:00Z"),
            Array.Empty<EpistemicCategory>());

        Assert.False(t.IsValidAt(DateTimeOffset.Parse("2026-07-01T00:00:01Z")));
    }

    [Fact]
    public void Allows_EmptyAllowedCategories_AllowsEverything()
    {
        // Empty-set convention means "no restriction". This is the
        // common case for full-trust tokens.
        var t = new CapabilityToken(
            new PrincipalId("u"),
            new WorkstreamId("w"),
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            Array.Empty<EpistemicCategory>());

        foreach (var c in Enum.GetValues<EpistemicCategory>())
        {
            Assert.True(t.Allows(c));
        }
    }

    [Fact]
    public void Allows_NonEmptyAllowedCategories_RestrictsToSet()
    {
        var t = new CapabilityToken(
            new PrincipalId("u"),
            new WorkstreamId("w"),
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            new[] { EpistemicCategory.Decision, EpistemicCategory.Outcome });

        Assert.True(t.Allows(EpistemicCategory.Decision));
        Assert.True(t.Allows(EpistemicCategory.Outcome));
        Assert.False(t.Allows(EpistemicCategory.Evidence));
        Assert.False(t.Allows(EpistemicCategory.Fact));
    }

    [Fact]
    public void Defaults_CrossWorkstreamAndIncludeTechnical_AreFalse()
    {
        // Locked privilege defaults — must require explicit opt-in.
        var t = new CapabilityToken(
            new PrincipalId("u"),
            new WorkstreamId("w"),
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            Array.Empty<EpistemicCategory>());

        Assert.False(t.CrossWorkstream);
        Assert.False(t.IncludeTechnical);
        Assert.Null(t.Signature);
    }
}

public sealed class CurationCapabilityTests
{
    [Fact]
    public void Defaults_AllFlagsFalse()
    {
        // Principle of least authority: every flag must be explicit.
        // A fresh CurationCapability grants NO operations.
        var c = new CurationCapability(
            new PrincipalId("u"),
            new WorkstreamId("w"),
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue);

        Assert.False(c.CanAmend);
        Assert.False(c.CanAnnotate);
        Assert.False(c.CanPin);
        Assert.False(c.CanDemote);
        Assert.False(c.CanSplit);
        Assert.False(c.CanMerge);
        Assert.False(c.CanRevert);
        Assert.False(c.CanReview);
        Assert.Null(c.Signature);
    }

    [Fact]
    public void NamedArguments_SetIndividualFlags()
    {
        var c = new CurationCapability(
            new PrincipalId("u"),
            new WorkstreamId("w"),
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            CanPin: true,
            CanDemote: true);

        Assert.True(c.CanPin);
        Assert.True(c.CanDemote);
        Assert.False(c.CanAmend);
        Assert.False(c.CanSplit);
    }

    [Fact]
    public void IsValidAt_RespectsWindow()
    {
        var c = new CurationCapability(
            new PrincipalId("u"),
            new WorkstreamId("w"),
            DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-06-30T23:59:59Z"));

        Assert.True(c.IsValidAt(DateTimeOffset.Parse("2026-06-15T12:00:00Z")));
        Assert.False(c.IsValidAt(DateTimeOffset.Parse("2026-05-30T12:00:00Z")));
        Assert.False(c.IsValidAt(DateTimeOffset.Parse("2026-07-01T12:00:00Z")));
    }
}
