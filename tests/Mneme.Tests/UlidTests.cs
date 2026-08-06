using Mneme.Util;

namespace Mneme.Tests;

/// <summary>
/// Covers the ULID generator used for library-generated event ids (e.g. the MCP
/// <c>remember</c> tool when the caller omits an id). ULIDs are lexicographically
/// sortable by creation time, which is what makes them a good ordering +
/// idempotency key for an append-only event log.
/// </summary>
public sealed class UlidTests
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    [Fact]
    public void NewUlid_is_26_crockford_chars()
    {
        var id = Ulid.NewUlid();
        Assert.Equal(26, id.Length);
        Assert.All(id, ch => Assert.Contains(ch, Crockford));
    }

    [Fact]
    public void NewUlid_is_lexicographically_time_sortable()
    {
        var early = Ulid.NewUlid(DateTimeOffset.UnixEpoch.AddMilliseconds(1_000));
        var late = Ulid.NewUlid(DateTimeOffset.UnixEpoch.AddMilliseconds(2_000));
        Assert.True(string.CompareOrdinal(early, late) < 0,
            "a later timestamp must produce a lexicographically greater ULID");
    }

    [Fact]
    public void NewUlid_is_unique_across_many_calls()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 2000; i++)
        {
            Assert.True(set.Add(Ulid.NewUlid()), "ULID collision");
        }
    }
}
