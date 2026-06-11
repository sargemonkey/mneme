using Mneme.Contracts;
using Mneme.Ingest;
using Mneme.Revocation;

namespace Mneme.Tests;

public sealed class SqliteRevocationServiceTests
{
    [Fact]
    public async Task Revoke_inserts_revocation_row_and_is_idempotent()
    {
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);
        var rev = new SqliteRevocationService(db.Factory);

        var evt = TestFixtures.NewEvidence(eventId: "01H0REVOK00000000000000001");
        await agent.IngestAsync(evt);

        var first = await rev.RevokeAsync(
            evt.EventId, evt.WorkstreamId,
            new PrincipalId("alice"), "user request");
        var second = await rev.RevokeAsync(
            evt.EventId, evt.WorkstreamId,
            new PrincipalId("bob"), "second try");

        Assert.False(first.AlreadyRevoked);
        Assert.True(second.AlreadyRevoked);
        Assert.Equal(first.RevokedAt, second.RevokedAt);

        using var c = db.Factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT revoked_by, reason FROM memory_revocations WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", evt.EventId.Value);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("alice", r.GetString(0));    // first principal wins
        Assert.Equal("user request", r.GetString(1));
    }

    [Fact]
    public async Task Revoke_leaves_memory_events_row_intact()
    {
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);
        var rev = new SqliteRevocationService(db.Factory);

        var evt = TestFixtures.NewEvidence(
            eventId: "01H0REVOK00000000000000002",
            content: "this content stays in payload_json");
        await agent.IngestAsync(evt);
        await rev.RevokeAsync(evt.EventId, evt.WorkstreamId, new PrincipalId("alice"), "legal");

        using var c = db.Factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM memory_events WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", evt.EventId.Value);
        var payload = cmd.ExecuteScalar() as string;
        Assert.NotNull(payload);
        Assert.Contains("this content stays in payload_json", payload);
    }

    [Fact]
    public async Task Revoke_rejects_workstream_mismatch()
    {
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);
        var rev = new SqliteRevocationService(db.Factory);
        var evt = TestFixtures.NewEvidence(eventId: "01H0REVOK00000000000000003");
        await agent.IngestAsync(evt);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rev.RevokeAsync(
                evt.EventId, new WorkstreamId("other-ws"),
                new PrincipalId("alice"), "wrong scope"));
    }

    [Fact]
    public async Task IsRevoked_reflects_revocation_state()
    {
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);
        var rev = new SqliteRevocationService(db.Factory);
        var evt = TestFixtures.NewEvidence(eventId: "01H0REVOK00000000000000004");
        await agent.IngestAsync(evt);

        Assert.False(await rev.IsRevokedAsync(evt.EventId));
        await rev.RevokeAsync(evt.EventId, evt.WorkstreamId, new PrincipalId("alice"), "test");
        Assert.True(await rev.IsRevokedAsync(evt.EventId));
    }

    [Fact]
    public async Task Revoke_validates_arguments()
    {
        using var db = new TestDatabase();
        var rev = new SqliteRevocationService(db.Factory);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await rev.RevokeAsync(EventId.None, new WorkstreamId("ws"), new PrincipalId("p"), "r"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await rev.RevokeAsync(new EventId("x"), new WorkstreamId("../escape"), new PrincipalId("p"), "r"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await rev.RevokeAsync(new EventId("x"), new WorkstreamId("ws"), new PrincipalId(""), "r"));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await rev.RevokeAsync(new EventId("x"), new WorkstreamId("ws"), new PrincipalId("p"), "   "));
    }
}
