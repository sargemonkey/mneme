using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Ingest;
using Mneme.Revocation;
using Mneme.Storage;

namespace Mneme.Tests;

/// <summary>
/// Covers cross-session fact de-duplication (Phase 14, ADR-0004): two non-revoked
/// facts in a workstream sharing a normalized statement (concurrent sessions/
/// agents asserting the same thing) are recorded in <c>memory_duplicates</c> as
/// an open review candidate — propose-only, never auto-revoked. The earlier fact
/// is canonical.
/// </summary>
public sealed class DuplicateFactsTests : IDisposable
{
    private readonly string _tmpDir;
    public DuplicateFactsTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private const string Ws = "dup-ws";

    private (ServiceProvider sp, IMemoryAgent agent, IRevocationService revoke) BuildHost()
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = Ws;
            o.SqlitePath = Path.Combine(_tmpDir, "dup.db");
            o.UserId = "host";
        });
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IMemoryAgent>(),
                sp.GetRequiredService<IRevocationService>());
    }

    // A fact authored by `agent`, ingested at a controllable event-time order via id.
    private static CaptureEvent Fact(string id, string agent, string statement) =>
        new(new EventId(id), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new FactPayload(statement, Array.Empty<EventId>()),
            new CaptureProvenance(new CaptureSourceId("agent"), new PrincipalId(agent)));

    private List<(string canon, string dup, int status)> ReadDuplicates(ServiceProvider sp)
    {
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT canonical_event_id, duplicate_event_id, status FROM memory_duplicates;";
        var rows = new List<(string, string, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetInt32(2)));
        return rows;
    }

    [Fact]
    public async Task Same_statement_from_two_agents_is_recorded_as_duplicate()
    {
        var (sp, agent, _) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(Fact("d-a", "agent-1", "The API rate limit is 100 requests per minute."));
        await agent.IngestAsync(Fact("d-b", "agent-2", "the api rate limit is 100 requests per minute.")); // case-insensitive

        var rows = ReadDuplicates(sp);
        Assert.Single(rows);
        var (canon, dup, status) = rows[0];
        Assert.Equal("d-a", canon); // earlier fact is canonical
        Assert.Equal("d-b", dup);
        Assert.Equal(0, status);    // open for review
    }

    [Fact]
    public async Task Distinct_statements_are_not_duplicates()
    {
        var (sp, agent, _) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(Fact("x-a", "agent-1", "The API rate limit is 100 requests per minute."));
        await agent.IngestAsync(Fact("x-b", "agent-2", "The API rate limit is 500 requests per minute."));

        Assert.Empty(ReadDuplicates(sp));
    }

    [Fact]
    public async Task Duplicate_detection_is_propose_only_and_does_not_revoke()
    {
        var (sp, agent, revoke) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(Fact("p-a", "agent-1", "Deploys happen on Fridays."));
        await agent.IngestAsync(Fact("p-b", "agent-2", "Deploys happen on Fridays."));

        // A candidate exists, but neither fact was revoked.
        Assert.Single(ReadDuplicates(sp));
        Assert.False(await revoke.IsRevokedAsync(new EventId("p-a")));
        Assert.False(await revoke.IsRevokedAsync(new EventId("p-b")));
    }

    [Fact]
    public async Task Detection_is_idempotent_on_reingest()
    {
        var (sp, agent, _) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(Fact("i-a", "agent-1", "Cache TTL is five minutes."));
        await agent.IngestAsync(Fact("i-b", "agent-2", "Cache TTL is five minutes."));
        await agent.IngestAsync(Fact("i-b", "agent-2", "Cache TTL is five minutes.")); // idempotent

        Assert.Single(ReadDuplicates(sp));
    }
}
