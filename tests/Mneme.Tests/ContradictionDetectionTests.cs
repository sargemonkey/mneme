using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Ingest;
using Mneme.Storage;

namespace Mneme.Tests;

/// <summary>
/// Covers cross-agent contradiction detection (Phase 13, ADR-0004): two
/// currently-valid triples with the same subject_key + predicate but a different
/// object are recorded as an open candidate in <c>memory_contradictions</c>
/// instead of silently superseding one another. Deterministic, structured-triple
/// only.
/// </summary>
public sealed class ContradictionDetectionTests : IDisposable
{
    private readonly string _tmpDir;
    public ContradictionDetectionTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-contra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private const string Ws = "contra-ws";

    private (ServiceProvider sp, IMemoryAgent agent) BuildHost()
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = Ws;
            o.SqlitePath = Path.Combine(_tmpDir, "contra.db");
            o.UserId = "host";
        });
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IMemoryAgent>());
    }

    // A fact carrying one subject-attributed triple, authored by `agent`.
    private static CaptureEvent Fact(string id, string agent, string subject, string predicate, string obj) =>
        new(new EventId(id), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new FactPayload($"{subject} {predicate} {obj}", Array.Empty<EventId>(),
                new[] { new FactTriple(subject, predicate, obj) }),
            new CaptureProvenance(new CaptureSourceId("agent"), new PrincipalId(agent)));

    private List<(string a, string b, string oa, string ob)> ReadContradictions(ServiceProvider sp)
    {
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT event_id_a, event_id_b, object_a, object_b FROM memory_contradictions;";
        var rows = new List<(string, string, string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return rows;
    }

    [Fact]
    public async Task Conflicting_objects_from_two_agents_are_recorded()
    {
        var (sp, agent) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(Fact("c-a", "agent-planner", "Alice", "lives_in", "Portland"));
        await agent.IngestAsync(Fact("c-b", "agent-coder", "Alice", "lives_in", "Seattle"));

        var rows = ReadContradictions(sp);
        Assert.Single(rows);
        var (a, b, oa, ob) = rows[0];
        Assert.Equal("c-a", a); // deterministic ordering by event id
        Assert.Equal("c-b", b);
        Assert.Equal("Portland", oa);
        Assert.Equal("Seattle", ob);
    }

    [Fact]
    public async Task Same_object_is_not_a_contradiction()
    {
        var (sp, agent) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(Fact("s-a", "agent-1", "Alice", "lives_in", "Portland"));
        await agent.IngestAsync(Fact("s-b", "agent-2", "Alice", "lives_in", "portland ")); // trim/case-insensitive

        Assert.Empty(ReadContradictions(sp));
    }

    [Fact]
    public async Task Different_predicate_is_not_a_contradiction()
    {
        var (sp, agent) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(Fact("p-a", "agent-1", "Alice", "lives_in", "Portland"));
        await agent.IngestAsync(Fact("p-b", "agent-2", "Alice", "works_at", "Acme"));

        Assert.Empty(ReadContradictions(sp));
    }

    [Fact]
    public async Task Different_subject_is_not_a_contradiction()
    {
        var (sp, agent) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(Fact("d-a", "agent-1", "Alice", "lives_in", "Portland"));
        await agent.IngestAsync(Fact("d-b", "agent-2", "Bob", "lives_in", "Seattle"));

        Assert.Empty(ReadContradictions(sp));
    }

    [Fact]
    public async Task Detection_is_idempotent_on_reingest()
    {
        var (sp, agent) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(Fact("i-a", "agent-1", "Alice", "lives_in", "Portland"));
        await agent.IngestAsync(Fact("i-b", "agent-2", "Alice", "lives_in", "Seattle"));
        // Re-ingest the same events (idempotent) — must not duplicate the candidate.
        await agent.IngestAsync(Fact("i-b", "agent-2", "Alice", "lives_in", "Seattle"));

        Assert.Single(ReadContradictions(sp));
    }
}
