using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Dreaming;
using Mneme.Hosting;
using Mneme.Ingest;
using Mneme.Review;
using Mneme.Storage;

namespace Mneme.Tests;

/// <summary>
/// Covers cross-workstream ("fleet") consolidation (Phase 14, ADR-0004): mining
/// opted-in workstreams' skills into a global skill library, fenced by the opt-in
/// flag, the cross-workstream token requirement, and the classification floor.
/// </summary>
public sealed class FleetConsolidatorTests : IDisposable
{
    private readonly string _tmpDir;
    public FleetConsolidatorTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-fleet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    // A dreamer that fuses every input skill into one global skill citing them all.
    private sealed class FusingDreamer : IDreamer
    {
        public string Id => "test/fusing-dreamer@1";
        public Task<DreamResult> DreamAsync(DreamRequest request, CancellationToken ct = default)
        {
            if (request.Events.Count == 0)
                return Task.FromResult(new DreamResult(Array.Empty<DreamOutput>()));
            var sources = request.Events.Select(e => e.EventId).ToArray();
            var output = new DreamOutput(
                new SkillPayload("global: idempotency guard", "guard retries with an idempotency key", null, Array.Empty<EventId>()),
                sources, Visibility.Global);
            return Task.FromResult(new DreamResult(new[] { output }));
        }
    }

    private ServiceProvider BuildHost(IDreamer? dreamer)
    {
        var services = new ServiceCollection();
        // The AddMneme workstream is just the DB owner; the fleet job works across
        // whatever workstreams have skill events + opt-in.
        services.AddMneme(o =>
        {
            o.WorkstreamId = "fleet-owner";
            o.SqlitePath = Path.Combine(_tmpDir, "fleet.db");
            o.UserId = "host";
        });
        if (dreamer is not null) services.AddSingleton(dreamer);
        return services.BuildServiceProvider();
    }

    private static CaptureEvent Skill(string id, string ws, string name, string procedure) =>
        new(new EventId(id), new WorkstreamId(ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new SkillPayload(name, procedure, null, Array.Empty<EventId>()),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("author"),
                Citation: new Citation.Derived(new[] { new EventId("upstream") }, "src@1")));

    private static CapabilityToken CrossToken() => new(
        Principal: new PrincipalId("fleet-operator"),
        Workstream: null,
        NotBefore: DateTimeOffset.UtcNow.AddMinutes(-1),
        NotAfter: DateTimeOffset.UtcNow.AddDays(1),
        AllowedCategories: Array.Empty<EpistemicCategory>(),
        CrossWorkstream: true);

    private int GlobalSkillCount(ServiceProvider sp, string globalWs)
    {
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projection_skills WHERE workstream_id = $ws;";
        cmd.Parameters.AddWithValue("$ws", globalWs);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Promotes_a_global_skill_from_opted_in_workstreams()
    {
        using var sp = BuildHost(new FusingDreamer());
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var config = sp.GetRequiredService<WorkstreamConfigStore>();
        var fleet = sp.GetRequiredService<FleetConsolidator>();

        // Two teams independently learned a similar skill; both opt in.
        await agent.IngestAsync(Skill("sk-a", "team-a", "check idempotency key on retry", "verify the key at the gateway"));
        await agent.IngestAsync(Skill("sk-b", "team-b", "guard charge retries", "ensure idempotency before retrying"));
        config.SetParticipatesInCrossWorkstreamConsolidation(new WorkstreamId("team-a"), true);
        config.SetParticipatesInCrossWorkstreamConsolidation(new WorkstreamId("team-b"), true);

        var summary = await fleet.ConsolidateFleetAsync(CrossToken());

        Assert.Equal(2, summary.WorkstreamsMined);
        Assert.Equal(2, summary.SkillsConsidered);
        Assert.Single(summary.Promoted);
        Assert.Equal(1, GlobalSkillCount(sp, FleetConsolidator.DefaultGlobalWorkstream));
    }

    [Fact]
    public async Task Global_skill_is_visible_globally_and_cites_its_sources()
    {
        using var sp = BuildHost(new FusingDreamer());
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var config = sp.GetRequiredService<WorkstreamConfigStore>();
        var fleet = sp.GetRequiredService<FleetConsolidator>();

        await agent.IngestAsync(Skill("g-a", "team-a", "rotate keys", "run rotation runbook"));
        config.SetParticipatesInCrossWorkstreamConsolidation(new WorkstreamId("team-a"), true);

        var summary = await fleet.ConsolidateFleetAsync(CrossToken());
        var promotedId = summary.Promoted.Single().Value;

        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT visibility FROM memory_visibility WHERE event_id = $id;";
            cmd.Parameters.AddWithValue("$id", promotedId);
            Assert.Equal(Visibility.Global, (Visibility)Convert.ToInt32(cmd.ExecuteScalar()));
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT provenance_json FROM memory_events WHERE event_id = $id;";
            cmd.Parameters.AddWithValue("$id", promotedId);
            var prov = EventSerialization.DeserializeProvenance((string)cmd.ExecuteScalar()!);
            var derived = Assert.IsType<Citation.Derived>(prov.Citation);
            Assert.Contains(new EventId("g-a"), derived.From);
        }
    }

    [Fact]
    public async Task Workstreams_that_did_not_opt_in_are_not_mined()
    {
        using var sp = BuildHost(new FusingDreamer());
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var fleet = sp.GetRequiredService<FleetConsolidator>();

        // Skill exists but the workstream never opted in.
        await agent.IngestAsync(Skill("no-a", "team-private", "secret sauce step", "do the private thing"));

        var summary = await fleet.ConsolidateFleetAsync(CrossToken());

        Assert.Equal(0, summary.WorkstreamsMined);
        Assert.Equal(0, summary.SkillsConsidered);
        Assert.Empty(summary.Promoted);
        Assert.Equal(0, GlobalSkillCount(sp, FleetConsolidator.DefaultGlobalWorkstream));
    }

    [Fact]
    public async Task Sensitive_source_skill_is_not_promoted_to_global()
    {
        using var sp = BuildHost(new FusingDreamer());
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var config = sp.GetRequiredService<WorkstreamConfigStore>();
        var fleet = sp.GetRequiredService<FleetConsolidator>();

        // A skill whose own text is Confidential is stamped Private at ingest. The
        // fleet miner must NOT load a non-shareable (Private) skill across the
        // isolation boundary at all (F2 guardrail) — it is never fed to the
        // dreamer, so it cannot be laundered into a global skill.
        await agent.IngestAsync(Skill("cf-a", "team-a", "Confidential handling of the escalation", "do the confidential thing"));
        config.SetParticipatesInCrossWorkstreamConsolidation(new WorkstreamId("team-a"), true);

        var summary = await fleet.ConsolidateFleetAsync(CrossToken());

        Assert.Equal(0, summary.SkillsConsidered); // Private skill excluded at mining, never seen by the dreamer
        Assert.Empty(summary.Promoted);
        Assert.Equal(0, summary.SkippedIneligible);
        Assert.Equal(0, GlobalSkillCount(sp, FleetConsolidator.DefaultGlobalWorkstream));
    }

    [Fact]
    public async Task Requires_a_cross_workstream_token()
    {
        using var sp = BuildHost(new FusingDreamer());
        var fleet = sp.GetRequiredService<FleetConsolidator>();
        // The default AddMneme token is workstream-scoped, not cross-workstream.
        var scoped = sp.GetRequiredService<CapabilityToken>();

        await Assert.ThrowsAsync<CapabilityDeniedError>(
            () => fleet.ConsolidateFleetAsync(scoped));
    }
}
