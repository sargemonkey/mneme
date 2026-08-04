using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Ingest;
using Mneme.Storage;

namespace Mneme.Tests;

/// <summary>
/// Covers procedural memory (Phase 14, ADR-0004): <see cref="SkillPayload"/>
/// events ride under <see cref="EpistemicCategory.Evidence"/> (the seven
/// categories stay locked) and are projected into <c>projection_skills</c> by a
/// dedicated <c>SkillsProjector</c> that filters by payload type. Ordinary
/// evidence must not land in the skills table; skill text must be redacted and
/// searchable like any other content.
/// </summary>
public sealed class SkillProjectionTests : IDisposable
{
    private readonly string _tmpDir;
    public SkillProjectionTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private const string Ws = "skill-ws";

    private (ServiceProvider sp, IMemoryAgent agent, IMemoryQueryAPI query, CapabilityToken token) BuildHost()
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = Ws;
            o.SqlitePath = Path.Combine(_tmpDir, "skill.db");
            o.UserId = "dreamer";
        });
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IMemoryAgent>(),
                sp.GetRequiredService<IMemoryQueryAPI>(),
                sp.GetRequiredService<CapabilityToken>());
    }

    private CaptureEvent Skill(string id, string name, string procedure, string? trigger) =>
        new(new EventId(id), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new SkillPayload(name, procedure, trigger, Array.Empty<EventId>()),
            new CaptureProvenance(new CaptureSourceId("dreamer"), new PrincipalId("dreamer"),
                Citation: new Citation.Derived(new[] { new EventId("src-1") }, "dreamer@1")));

    private (string? name, string? proc, string? trig, int count) ReadSkill(ServiceProvider sp, string eventId)
    {
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, procedure, trigger FROM projection_skills WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", eventId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null, null, 0);
        return (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), 1);
    }

    private int SkillRowCount(ServiceProvider sp)
    {
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projection_skills;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Skill_event_is_projected_into_projection_skills()
    {
        var (sp, agent, _, _) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(Skill("sk1", "resolve gateway double-charge",
            "check the idempotency key at the gateway on retry", "when the gateway double-charges"));

        var (name, proc, trig, count) = ReadSkill(sp, "sk1");
        Assert.Equal(1, count);
        Assert.Equal("resolve gateway double-charge", name);
        Assert.Equal("check the idempotency key at the gateway on retry", proc);
        Assert.Equal("when the gateway double-charges", trig);
    }

    [Fact]
    public async Task Ordinary_evidence_does_not_land_in_the_skills_table()
    {
        var (sp, agent, _, _) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(new CaptureEvent(
            new EventId("ev1"), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload("just a plain observation", "test"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("u"))));

        Assert.Equal(0, SkillRowCount(sp));
    }

    [Fact]
    public async Task Skill_is_searchable_by_free_text()
    {
        var (sp, agent, query, token) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(Skill("sk2", "rotate the signing key",
            "run the key-rotation runbook and redeploy the auth service", null));

        var result = await query.QueryAsync(new QueryRequest(
            new QuerySpec(new WorkstreamId(Ws), FreeText: "key rotation runbook")), token);
        Assert.Contains(result.Items, i => i.EventId.Value == "sk2");
    }

    [Fact]
    public async Task Skill_secret_content_is_redacted_before_persist()
    {
        var (sp, agent, _, _) = BuildHost();
        using var _d = sp;
        // A password-style secret in the procedure text must be redacted at ingest.
        await agent.IngestAsync(Skill("sk3", "deploy the service",
            "set password = supersecretvalue123 before deploying", null));

        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using (var c = factory.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT payload_json FROM memory_events WHERE event_id = 'sk3';";
            var rawPayload = (string)cmd.ExecuteScalar()!;
            Assert.False(rawPayload.Contains("supersecretvalue123"),
                $"raw payload not redacted: {rawPayload}");
        }

        var (_, proc, _, _) = ReadSkill(sp, "sk3");
        Assert.NotNull(proc);
        Assert.DoesNotContain("supersecretvalue123", proc!);
    }
}
