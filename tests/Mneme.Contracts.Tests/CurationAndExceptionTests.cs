using System.Text.Json;

namespace Mneme.Contracts.Tests;

public sealed class CurationRecordTests
{
    [Fact]
    public void CurationResult_Roundtrips()
    {
        var r = new CurationResult(
            new EventId("cur-1"),
            DateTimeOffset.Parse("2026-06-05T12:00:00Z"),
            "sha256:abc");
        var json = JsonSerializer.Serialize(r, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<CurationResult>(json, Fixtures.JsonOptions);
        Assert.Equal(r, back);
    }

    [Fact]
    public void FactAmendment_DefaultsValidAtToNull()
    {
        var a = new FactAmendment("new text", "typo fix");
        Assert.Null(a.ValidAt);
    }

    [Fact]
    public void FactSplitPart_DefaultsValidAtToNull()
    {
        var p = new FactSplitPart("part 1", EpistemicCategory.Fact);
        Assert.Null(p.ValidAt);
    }

    [Fact]
    public void FactMerged_RequiresValidAt()
    {
        // FactMerged's valid_at is required by convention (earliest source).
        var m = new FactMerged(
            "merged content",
            EpistemicCategory.Fact,
            DateTimeOffset.Parse("2026-06-01T00:00:00Z"));
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T00:00:00Z"), m.ValidAt);
    }

    [Fact]
    public void CurationEntry_Roundtrips()
    {
        var e = new CurationEntry(
            new EventId("cur-1"),
            new PrincipalId("alice"),
            new EventId("fact-42"),
            CurationType.Amended,
            "fixed off-by-one in step count",
            DateTimeOffset.Parse("2026-06-05T12:00:00Z"),
            "sha256:abc",
            new WorkstreamId("cust-acme"));
        var json = JsonSerializer.Serialize(e, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<CurationEntry>(json, Fixtures.JsonOptions);
        Assert.Equal(e, back);
    }

    [Fact]
    public void PendingReviewItem_Roundtrips()
    {
        var p = new PendingReviewItem(
            new EventId("evt-1"),
            new WorkstreamId("sensitive"),
            DateTimeOffset.Parse("2026-06-05T12:00:00Z"),
            "user uploaded password in chat");
        var json = JsonSerializer.Serialize(p, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<PendingReviewItem>(json, Fixtures.JsonOptions);
        Assert.Equal(p, back);
    }
}

public sealed class ExceptionTests
{
    [Fact]
    public void StaleProposalError_CarriesTargetAndHashes()
    {
        var err = new StaleProposalError(new EventId("fact-1"), "expected", "actual");
        Assert.Equal(new EventId("fact-1"), err.Target);
        Assert.Equal("expected", err.ExpectedHash);
        Assert.Equal("actual", err.ActualHash);
        Assert.Contains("expected", err.Message);
        Assert.Contains("actual", err.Message);
    }

    [Fact]
    public void StaleProposalError_RejectsNullHashes()
    {
        Assert.Throws<ArgumentNullException>(
            () => new StaleProposalError(new EventId("f"), null!, "actual"));
        Assert.Throws<ArgumentNullException>(
            () => new StaleProposalError(new EventId("f"), "expected", null!));
    }

    [Fact]
    public void StaleProposalError_IsInvalidOperationException()
    {
        // Promise that callers can catch InvalidOperationException to handle
        // stale-state races at a coarser granularity.
        Assert.IsAssignableFrom<InvalidOperationException>(
            new StaleProposalError(new EventId("f"), "e", "a"));
    }

    [Fact]
    public void CapabilityDeniedError_CarriesReason()
    {
        var err = new CapabilityDeniedError("CanAmend not granted");
        Assert.Equal("CanAmend not granted", err.Reason);
        Assert.Contains("CanAmend not granted", err.Message);
    }

    [Fact]
    public void CapabilityDeniedError_IsUnauthorizedAccessException()
    {
        // Promise that ASP.NET / HTTP layers can map this to 403.
        Assert.IsAssignableFrom<UnauthorizedAccessException>(
            new CapabilityDeniedError("nope"));
    }

    [Fact]
    public void CapabilityDeniedError_PreservesInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var err = new CapabilityDeniedError("nope", inner);
        Assert.Same(inner, err.InnerException);
    }
}
