using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Mcp;
using Mneme.Storage;

namespace Mneme.Tests;

public sealed class MnemeMcpToolsTests : IDisposable
{
    private readonly string _tmpDir;
    public MnemeMcpToolsTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private ServiceProvider Build(string workstream = "mcp-test")
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = workstream;
            o.SqlitePath = Path.Combine(_tmpDir, workstream + ".db");
            o.UserId = "alice";
        });
        services.AddSingleton(sp => new CurationCapability(
            Principal: new PrincipalId("alice"),
            Workstream: new WorkstreamId(workstream),
            NotBefore: DateTimeOffset.UtcNow.AddDays(-1),
            NotAfter: DateTimeOffset.UtcNow.AddDays(1),
            CanAmend: true, CanAnnotate: true, CanPin: true, CanDemote: true,
            CanSplit: true, CanMerge: true, CanRevert: true, CanReview: true));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Remember_then_query_round_trip()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var api = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();

        var rememberJson = await MnemeMcpTools.Remember(agent, token,
            eventId: "mcp-rt-001", content: "the mcp tool round-trip works", source: "unit-test");
        Assert.Contains("mcp-rt-001", rememberJson);
        Assert.Contains("\"was_duplicate\": false", rememberJson);

        var listJson = await MnemeMcpTools.ListRecent(api, token, limit: 10);
        Assert.Contains("mcp-rt-001", listJson);
        Assert.Contains("the mcp tool round-trip works", listJson);

        var queryJson = await MnemeMcpTools.Query(api, token, freeText: "mcp tool", limit: 5, explain: true);
        Assert.Contains("mcp-rt-001", queryJson);
        Assert.Contains("\"explain\"", queryJson);
    }

    [Fact]
    public async Task Remember_is_idempotent_on_event_id()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var token = sp.GetRequiredService<CapabilityToken>();

        await MnemeMcpTools.Remember(agent, token, eventId: "mcp-idem-1", content: "first");
        var second = await MnemeMcpTools.Remember(agent, token, eventId: "mcp-idem-1", content: "second");
        Assert.Contains("\"was_duplicate\": true", second);
    }

    [Fact]
    public async Task Forget_revokes_event()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var revocation = sp.GetRequiredService<Mneme.Revocation.IRevocationService>();
        var token = sp.GetRequiredService<CapabilityToken>();

        await MnemeMcpTools.Remember(agent, token, eventId: "mcp-forget-1", content: "to be forgotten");
        var json = await MnemeMcpTools.Forget(revocation, token, eventId: "mcp-forget-1", reason: "test");
        Assert.Contains("\"already_revoked\": false", json);

        Assert.True(await revocation.IsRevokedAsync(new EventId("mcp-forget-1")));
    }

    [Fact]
    public async Task Improve_annotate_dispatches_to_curator()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var curator = sp.GetRequiredService<IMemoryCurator>();
        var cap = sp.GetRequiredService<CurationCapability>();
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        var token = sp.GetRequiredService<CapabilityToken>();

        await MnemeMcpTools.Remember(agent, token, eventId: "mcp-imp-1", content: "improve target");
        var json = await MnemeMcpTools.Improve(curator, cap, factory,
            operation: "annotate", targetId: "mcp-imp-1", rationale: "needs review");
        Assert.Contains("curation_event_id", json);
    }

    [Fact]
    public async Task Improve_unknown_operation_throws()
    {
        using var sp = Build();
        var curator = sp.GetRequiredService<IMemoryCurator>();
        var cap = sp.GetRequiredService<CurationCapability>();
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await MnemeMcpTools.Improve(curator, cap, factory, operation: "destroy", targetId: "x"));
    }

    [Fact]
    public async Task Distill_returns_degraded_bundle_marker()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var api = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        await MnemeMcpTools.Remember(agent, token, eventId: "mcp-dist-1", content: "x");
        var json = await MnemeMcpTools.Distill(api, token);
        Assert.Contains("\"is_stale\": true", json);
        Assert.Contains("not yet running", json);
    }

    [Fact]
    public void Every_tool_method_has_explicit_McpServerToolAttribute_with_safe_defaults()
    {
        // Catch the "SDK defaults are wrong" footgun: ensure ReadOnly and
        // Destructive are explicitly set on every tool method, and that
        // Query/ListRecent/Distill are ReadOnly=true.
        var tools = typeof(MnemeMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null)
            .ToArray();
        Assert.True(tools.Length >= 6, $"expected >=6 tools, got {tools.Length}");

        foreach (var m in tools)
        {
            var attr = m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()!;
            Assert.False(string.IsNullOrEmpty(attr.Name), $"{m.Name} missing Name");
            Assert.False(string.IsNullOrEmpty(attr.Title), $"{m.Name} missing Title");
            Assert.True(m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is not null,
                $"{m.Name} missing [Description]");
        }

        var readOnly = new[] { "query", "list_recent", "distill" };
        foreach (var name in readOnly)
        {
            var m = tools.First(x => x.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()!.Name == name);
            var attr = m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()!;
            Assert.True(attr.ReadOnly, $"{name} should be ReadOnly=true");
            Assert.False(attr.Destructive, $"{name} should be Destructive=false");
            Assert.False(attr.OpenWorld, $"{name} should be OpenWorld=false");
        }
    }
}
