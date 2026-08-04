using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Ingest;

namespace Mneme.Tests;

/// <summary>
/// Covers the agent/role scope axis (Phase 13, ADR-0004): the nullable
/// <see cref="QuerySpec.Principal"/> filter that scopes reads to a single
/// author (agent/user) within a workstream, backed by the indexed
/// <c>memory_events.principal_id</c> column. This same column is the O(index)
/// primitive for data-subject access / erasure.
/// </summary>
public sealed class AgentScopeTests : IDisposable
{
    private readonly string _tmpDir;
    public AgentScopeTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private (ServiceProvider sp, IMemoryAgent agent, IMemoryQueryAPI query, CapabilityToken token)
        BuildHost(string workstream = "scope-ws")
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = workstream;
            o.SqlitePath = Path.Combine(_tmpDir, workstream + ".db");
            o.UserId = "host";
        });
        var sp = services.BuildServiceProvider();
        return (sp,
                sp.GetRequiredService<IMemoryAgent>(),
                sp.GetRequiredService<IMemoryQueryAPI>(),
                sp.GetRequiredService<CapabilityToken>());
    }

    private static CaptureEvent Evidence(string id, string ws, string principal, string content) =>
        new(new EventId(id), new WorkstreamId(ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload(content, "test"),
            new CaptureProvenance(new CaptureSourceId("agent"), new PrincipalId(principal)));

    [Fact]
    public async Task Query_filtered_by_principal_returns_only_that_principals_events()
    {
        var (sp, agent, query, token) = BuildHost("scope-a");
        using var _ = sp;
        var ws = new WorkstreamId("scope-a");
        await agent.IngestAsync(Evidence("a1", "scope-a", "agent-planner", "planner note one"));
        await agent.IngestAsync(Evidence("a2", "scope-a", "agent-planner", "planner note two"));
        await agent.IngestAsync(Evidence("a3", "scope-a", "agent-coder", "coder note"));

        var result = await query.QueryAsync(
            new QueryRequest(new QuerySpec(ws, Principal: new PrincipalId("agent-planner"))), token);

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, i => Assert.Contains(i.EventId.Value, new[] { "a1", "a2" }));
    }

    [Fact]
    public async Task Query_without_principal_returns_all_authors()
    {
        var (sp, agent, query, token) = BuildHost("scope-b");
        using var _ = sp;
        var ws = new WorkstreamId("scope-b");
        await agent.IngestAsync(Evidence("b1", "scope-b", "agent-planner", "one"));
        await agent.IngestAsync(Evidence("b2", "scope-b", "agent-coder", "two"));

        var result = await query.QueryAsync(new QueryRequest(new QuerySpec(ws)), token);

        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task Free_text_query_respects_principal_filter()
    {
        var (sp, agent, query, token) = BuildHost("scope-c");
        using var _ = sp;
        var ws = new WorkstreamId("scope-c");
        await agent.IngestAsync(Evidence("c1", "scope-c", "agent-planner", "the deployment pipeline is green"));
        await agent.IngestAsync(Evidence("c2", "scope-c", "agent-coder", "the deployment pipeline is red"));

        var scoped = await query.QueryAsync(new QueryRequest(
            new QuerySpec(ws, FreeText: "deployment pipeline", Principal: new PrincipalId("agent-coder"))), token);

        Assert.All(scoped.Items, i => Assert.Equal("c2", i.EventId.Value));
        Assert.Contains(scoped.Items, i => i.EventId.Value == "c2");

        var unscoped = await query.QueryAsync(new QueryRequest(
            new QuerySpec(ws, FreeText: "deployment pipeline")), token);
        Assert.True(unscoped.Items.Count >= scoped.Items.Count);
    }

    [Fact]
    public async Task Principal_scoped_query_is_the_subject_access_primitive()
    {
        // "Everything principal X authored" is a single indexed query — the
        // basis for data-subject access and erasure (revoke each returned id).
        var (sp, agent, query, token) = BuildHost("scope-d");
        using var _ = sp;
        var ws = new WorkstreamId("scope-d");
        await agent.IngestAsync(Evidence("d1", "scope-d", "user-erasable", "personal note A"));
        await agent.IngestAsync(Evidence("d2", "scope-d", "user-erasable", "personal note B"));
        await agent.IngestAsync(Evidence("d3", "scope-d", "user-other", "unrelated"));

        var owned = await query.QueryAsync(
            new QueryRequest(new QuerySpec(ws, Principal: new PrincipalId("user-erasable"), Limit: 1000)), token);

        var ids = owned.Items.Select(i => i.EventId.Value).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "d1", "d2" }, ids);
    }
}
