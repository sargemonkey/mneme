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
        // The consolidating principal AUTHORS this Confidential source, so it is
        // legitimately visible to the dream run (author-only Private). The dreamer
        // asks for Global but the source-sensitivity cap must force the derived
        // skill to Private.
        await agent.IngestAsync(new CaptureEvent(
            new EventId("conf1"), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload("Confidential customer escalation playbook", "test"),
            new CaptureProvenance(new CaptureSourceId("t"), token.Principal)));

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

    // A dreamer whose OUTPUT text is itself sensitive ("Confidential …"), derived
    // from a benign source, requesting Global — to exercise the own-sensitivity cap.
    private sealed class ConfidentialOutputDreamer : IDreamer
    {
        public string Id => "test/confidential-output@1";
        public Task<DreamResult> DreamAsync(DreamRequest request, CancellationToken ct = default)
        {
            var src = request.Events.Select(e => e.EventId).ToArray();
            if (src.Length == 0) return Task.FromResult(new DreamResult(Array.Empty<DreamOutput>()));
            return Task.FromResult(new DreamResult(new[]
            {
                new DreamOutput(
                    new SkillPayload("Confidential escalation runbook", "handle the confidential incident", null, Array.Empty<EventId>()),
                    src, Visibility.Global),
            }));
        }
    }

    // A dreamer that records how many OpenContradictions it was handed.
    private sealed class ContradictionRecordingDreamer : IDreamer
    {
        public int LastContradictionCount { get; private set; } = -1;
        public string Id => "test/contradiction-recorder@1";
        public Task<DreamResult> DreamAsync(DreamRequest request, CancellationToken ct = default)
        {
            LastContradictionCount = request.OpenContradictions.Count;
            return Task.FromResult(new DreamResult(Array.Empty<DreamOutput>()));
        }
    }

    private static CaptureEvent FactWith(string id, string principal, string statement, FactTriple triple) =>
        new(new EventId(id), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new FactPayload(statement, Array.Empty<EventId>(), new[] { triple }),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId(principal)));

    private static CapabilityToken Token(string principal) => new(
        Principal: new PrincipalId(principal), Workstream: new WorkstreamId(Ws),
        NotBefore: DateTimeOffset.UtcNow.AddMinutes(-1), NotAfter: DateTimeOffset.UtcNow.AddDays(1),
        AllowedCategories: Array.Empty<EpistemicCategory>());

    [Fact]
    public async Task Derived_output_whose_own_text_is_sensitive_stays_private_despite_benign_sources()
    {
        // Second-order (finding #4): SetVisibility must never RAISE an output above
        // its own ingest-time visibility. The single source is Public, so the
        // source cap alone would allow Global — but the output's OWN text is
        // Confidential (stamped Private at ingest), and that stricter default wins.
        var (sp, agent, dream, token) = BuildHost(new ConfidentialOutputDreamer());
        using var _d = sp;
        // Benign source, authored by the consolidator so it is visible to the run.
        await agent.IngestAsync(new CaptureEvent(
            new EventId("pub1"), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload("the nightly build passed", "test"),
            new CaptureProvenance(new CaptureSourceId("t"), token.Principal)));

        var summary = await dream.ConsolidateAsync(new WorkstreamId(Ws), token);
        var producedId = summary.Produced.Single().Value;

        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT visibility FROM memory_visibility WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", producedId);
        Assert.Equal(Visibility.Private, (Visibility)Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public async Task Open_contradictions_with_a_non_author_private_side_are_hidden_from_the_dreamer()
    {
        // Second-order: LoadOpenContradictions gates BOTH sides. A contradiction
        // whose one side is another principal's Private fact is hidden from a
        // dreamer running as a different principal, but visible to the author.
        var recorder = new ContradictionRecordingDreamer();
        var (sp, agent, dream, _) = BuildHost(recorder);
        using var _d = sp;
        var ws = new WorkstreamId(Ws);

        // alice's Confidential (→Private) fact vs bob's Shared fact — same
        // subject+predicate, different object → a recorded contradiction.
        await agent.IngestAsync(FactWith("ct-alice", "alice",
            "Confidential: Alice lives_in Portland", new FactTriple("Alice", "lives_in", "Portland")));
        await agent.IngestAsync(FactWith("ct-bob", "bob",
            "Alice relocated recently", new FactTriple("Alice", "lives_in", "Seattle")));

        // Run as bob: the alice-Private side hides the whole contradiction.
        await dream.ConsolidateAsync(ws, Token("bob"));
        Assert.Equal(0, recorder.LastContradictionCount);

        // Run as alice (author of the Private side): both sides visible → shown.
        await dream.ConsolidateAsync(ws, Token("alice"));
        Assert.Equal(1, recorder.LastContradictionCount);
    }
}
