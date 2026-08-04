using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Dreaming;
using Mneme.Hosting;
using Mneme.Ingest;
using Mneme.Storage;

namespace Mneme.Tests;

/// <summary>
/// Covers the offline consolidation worker (Phase 14, ADR-0004): a host
/// <see cref="IDreamer"/> produces derived events, which the
/// <see cref="DreamCoordinator"/> direct-ingests with a <see cref="Citation.Derived"/>
/// provenance, caps by source sensitivity, and audits. Uses a deterministic
/// test dreamer (no LLM).
/// </summary>
public sealed class DreamCoordinatorTests : IDisposable
{
    private readonly string _tmpDir;
    public DreamCoordinatorTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-dream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private const string Ws = "dream-ws";

    // A test dreamer: for each Evidence event in scope, emit one skill derived
    // from it, requesting Global visibility (so the cap guardrail is exercised).
    private sealed class SkillFromEvidenceDreamer : IDreamer
    {
        public string Id => "test/skill-dreamer@1";
        public Task<DreamResult> DreamAsync(DreamRequest request, CancellationToken ct = default)
        {
            var outputs = new List<DreamOutput>();
            foreach (var e in request.Events)
            {
                if (e.Payload is not EvidencePayload ev) continue;
                outputs.Add(new DreamOutput(
                    new SkillPayload("skill: " + ev.Content, "do " + ev.Content, null, Array.Empty<EventId>()),
                    new[] { e.EventId },
                    Visibility.Global));
            }
            return Task.FromResult(new DreamResult(outputs));
        }
    }

    private (ServiceProvider sp, IMemoryAgent agent, DreamCoordinator dream, CapabilityToken token) BuildHost(IDreamer? dreamer)
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = Ws;
            o.SqlitePath = Path.Combine(_tmpDir, "dream.db");
            o.UserId = "consolidator";
        });
        if (dreamer is not null) services.AddSingleton(dreamer);
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IMemoryAgent>(),
                sp.GetRequiredService<DreamCoordinator>(),
                sp.GetRequiredService<CapabilityToken>());
    }

    private static CaptureEvent Evidence(string id, string content) =>
        new(new EventId(id), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload(content, "test"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("author")));

    private int Count(ServiceProvider sp, string sql)
    {
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public void Coordinator_is_inert_when_no_dreamer_is_registered()
    {
        var (sp, _, dream, _) = BuildHost(dreamer: null);
        using var _d = sp;
        Assert.False(dream.IsEnabled);
    }

    [Fact]
    public async Task Consolidate_without_dreamer_throws()
    {
        var (sp, _, dream, token) = BuildHost(dreamer: null);
        using var _d = sp;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dream.ConsolidateAsync(new WorkstreamId(Ws), token));
    }

    [Fact]
    public async Task Consolidate_produces_derived_skill_events_and_audits()
    {
        var (sp, agent, dream, token) = BuildHost(new SkillFromEvidenceDreamer());
        using var _d = sp;
        await agent.IngestAsync(Evidence("e1", "restart the pod when memory spikes"));
        await agent.IngestAsync(Evidence("e2", "flush the cache before a deploy"));

        var summary = await dream.ConsolidateAsync(new WorkstreamId(Ws), token);

        Assert.Equal("test/skill-dreamer@1", summary.DreamerId);
        Assert.Equal(2, summary.Produced.Count);
        // The two derived skills landed in the skills projection.
        Assert.Equal(2, Count(sp, "SELECT COUNT(*) FROM projection_skills;"));
        // The run was audited.
        Assert.Equal(1, Count(sp, "SELECT COUNT(*) FROM dream_runs WHERE outputs_out = 2;"));
    }

    [Fact]
    public async Task Derived_event_carries_a_derived_citation_naming_its_source()
    {
        var (sp, agent, dream, token) = BuildHost(new SkillFromEvidenceDreamer());
        using var _d = sp;
        await agent.IngestAsync(Evidence("src1", "rotate certs quarterly"));

        var summary = await dream.ConsolidateAsync(new WorkstreamId(Ws), token);
        var producedId = summary.Produced.Single().Value;

        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT provenance_json FROM memory_events WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", producedId);
        var provJson = (string)cmd.ExecuteScalar()!;
        var prov = EventSerialization.DeserializeProvenance(provJson);
        var derived = Assert.IsType<Citation.Derived>(prov.Citation);
        Assert.Contains(new EventId("src1"), derived.From);
        Assert.Equal("test/skill-dreamer@1", derived.ConsolidatorId);
    }

    [Fact]
    public async Task Visibility_is_capped_to_private_when_a_source_is_sensitive()
    {
        var (sp, agent, dream, token) = BuildHost(new SkillFromEvidenceDreamer());
        using var _d = sp;
        // "Confidential …" → Classification.Confidential source; the dreamer asks
        // for Global but the cap must force the derived skill to Private.
        await agent.IngestAsync(Evidence("conf1", "Confidential customer escalation playbook"));

        var summary = await dream.ConsolidateAsync(new WorkstreamId(Ws), token);
        var producedId = summary.Produced.Single().Value;

        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT visibility FROM memory_visibility WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", producedId);
        var vis = (Visibility)Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(Visibility.Private, vis);
    }

    [Fact]
    public async Task Visibility_honours_global_when_all_sources_are_public()
    {
        var (sp, agent, dream, token) = BuildHost(new SkillFromEvidenceDreamer());
        using var _d = sp;
        await agent.IngestAsync(Evidence("pub1", "plain public runbook step"));

        var summary = await dream.ConsolidateAsync(new WorkstreamId(Ws), token);
        var producedId = summary.Produced.Single().Value;

        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT visibility FROM memory_visibility WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", producedId);
        var vis = (Visibility)Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(Visibility.Global, vis);
    }
}
