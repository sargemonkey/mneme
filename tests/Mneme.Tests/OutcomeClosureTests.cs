using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Outcomes;

namespace Mneme.Tests;

public sealed class OutcomeClosureTests : IDisposable
{
    private readonly string _tmpDir;
    public OutcomeClosureTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-oc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private ServiceProvider Build(string ws = "oc-ws")
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = ws;
            o.SqlitePath = Path.Combine(_tmpDir, ws + ".db");
            o.UserId = "alice";
        });
        return services.BuildServiceProvider();
    }

    private static CaptureEvent Decision(string id, string ws, string stmt, params EventId[] supporters) =>
        new(new EventId(id), new WorkstreamId(ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new DecisionPayload(stmt, "rationale", supporters, new PrincipalId("p")),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")));

    private static CaptureEvent Action(string id, string ws, string stmt, EventId? decisionId, DateTimeOffset? at = null) =>
        new(new EventId(id), new WorkstreamId(ws), EventChannel.Epistemic,
            at ?? DateTimeOffset.UtcNow, at ?? DateTimeOffset.UtcNow,
            new ActionPayload(stmt, decisionId, null),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")));

    private static CaptureEvent Outcome(string id, string ws, string stmt, EventId actionId, OutcomePolarity polarity, DateTimeOffset? at = null) =>
        new(new EventId(id), new WorkstreamId(ws), EventChannel.Epistemic,
            at ?? DateTimeOffset.UtcNow, at ?? DateTimeOffset.UtcNow,
            new OutcomePayload(stmt, actionId, polarity),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")));

    private static CaptureEvent Evidence(string id, string ws, string content) =>
        new(new EventId(id), new WorkstreamId(ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload(content, "t"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")));

    private static long Scalar(Mneme.Storage.SqliteConnectionFactory f, string sql, params (string k, object v)[] args)
    {
        using var c = f.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        var r = cmd.ExecuteScalar();
        return r is long l ? l : Convert.ToInt64(r ?? 0L);
    }

    [Fact]
    public async Task DecisionChainsProjector_links_decision_action_outcome_in_order()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var factory = sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>();

        await agent.IngestAsync(Decision("d-1", "oc-ws", "ship v2 in october"));
        await agent.IngestAsync(Action("a-1", "oc-ws", "merge PR", new EventId("d-1")));
        await agent.IngestAsync(Outcome("o-1", "oc-ws", "shipped on time", new EventId("a-1"), OutcomePolarity.Positive));

        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT decision_event_id, action_event_id, outcome_event_id, outcome_polarity, closed FROM decision_chains;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("d-1", r.GetString(0));
        Assert.Equal("a-1", r.GetString(1));
        Assert.Equal("o-1", r.GetString(2));
        Assert.Equal((int)OutcomePolarity.Positive, r.GetInt32(3));
        Assert.Equal(1L, r.GetInt64(4));
    }

    [Fact]
    public async Task DecisionChainsProjector_handles_out_of_order_decision_after_action()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var factory = sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>();

        // Action and Outcome arrive BEFORE the Decision they reference.
        await agent.IngestAsync(Action("a-2", "oc-ws", "premature action", new EventId("d-2")));
        await agent.IngestAsync(Outcome("o-2", "oc-ws", "premature outcome", new EventId("a-2"), OutcomePolarity.Negative));
        // Decision arrives later — the projector should backfill links.
        await agent.IngestAsync(Decision("d-2", "oc-ws", "the original decision"));

        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT action_event_id, outcome_event_id, outcome_polarity, closed FROM decision_chains WHERE decision_event_id='d-2';";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("a-2", r.GetString(0));
        Assert.Equal("o-2", r.GetString(1));
        Assert.Equal((int)OutcomePolarity.Negative, r.GetInt32(2));
        Assert.Equal(1L, r.GetInt64(3));
    }

    [Fact]
    public async Task DecisionChainsProjector_open_chain_until_outcome_arrives()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var factory = sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>();
        await agent.IngestAsync(Decision("d-3", "oc-ws", "we'll see"));
        await agent.IngestAsync(Action("a-3", "oc-ws", "started doing it", new EventId("d-3")));

        var closed = Scalar(factory, "SELECT closed FROM decision_chains WHERE decision_event_id='d-3';");
        Assert.Equal(0L, closed);
    }

    [Fact]
    public async Task FeedbackLearner_raises_weight_on_positive_outcome()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var learner = sp.GetRequiredService<FeedbackLearner>();
        await agent.IngestAsync(Evidence("e-1", "oc-ws", "supporting evidence"));
        await agent.IngestAsync(Decision("d-pos", "oc-ws", "based on e-1", new EventId("e-1")));
        await agent.IngestAsync(Action("a-pos", "oc-ws", "did it", new EventId("d-pos")));
        await agent.IngestAsync(Outcome("o-pos", "oc-ws", "great", new EventId("a-pos"), OutcomePolarity.Positive));

        // FeedbackIngestObserver runs synchronously on Outcome ingest.
        var wDecision = learner.GetWeight(new EventId("d-pos"));
        var wEvidence = learner.GetWeight(new EventId("e-1"));
        Assert.True(wDecision > 1.0, $"decision weight should be > 1.0, got {wDecision}");
        Assert.True(wEvidence > 1.0, $"evidence weight should be > 1.0, got {wEvidence}");
    }

    [Fact]
    public async Task FeedbackLearner_lowers_weight_on_negative_outcome()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var learner = sp.GetRequiredService<FeedbackLearner>();
        await agent.IngestAsync(Evidence("e-neg", "oc-ws", "bad call"));
        await agent.IngestAsync(Decision("d-neg", "oc-ws", "based on e-neg", new EventId("e-neg")));
        await agent.IngestAsync(Action("a-neg", "oc-ws", "did it", new EventId("d-neg")));
        await agent.IngestAsync(Outcome("o-neg", "oc-ws", "bad", new EventId("a-neg"), OutcomePolarity.Negative));

        var wEvidence = learner.GetWeight(new EventId("e-neg"));
        Assert.True(wEvidence < 1.0, $"evidence weight should be < 1.0, got {wEvidence}");
    }

    [Fact]
    public async Task FeedbackLearner_neutral_outcome_leaves_weight_unchanged()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var learner = sp.GetRequiredService<FeedbackLearner>();
        await agent.IngestAsync(Evidence("e-neut", "oc-ws", "x"));
        await agent.IngestAsync(Decision("d-neut", "oc-ws", "based on e-neut", new EventId("e-neut")));
        await agent.IngestAsync(Action("a-neut", "oc-ws", "did it", new EventId("d-neut")));
        await agent.IngestAsync(Outcome("o-neut", "oc-ws", "meh", new EventId("a-neut"), OutcomePolarity.Neutral));

        Assert.Equal(1.0, learner.GetWeight(new EventId("e-neut")), precision: 6);
    }

    [Fact]
    public async Task FeedbackLearner_clamps_after_streak_of_positives()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var learner = sp.GetRequiredService<FeedbackLearner>();
        await agent.IngestAsync(Evidence("e-clamp", "oc-ws", "long-running supporter"));
        await agent.IngestAsync(Decision("d-clamp", "oc-ws", "based on e-clamp", new EventId("e-clamp")));
        await agent.IngestAsync(Action("a-clamp", "oc-ws", "did it", new EventId("d-clamp")));

        // 200 positive outcomes -> would explode if not clamped.
        for (var i = 0; i < 200; i++)
        {
            await agent.IngestAsync(Outcome($"o-clamp-{i:D3}", "oc-ws", "good", new EventId("a-clamp"), OutcomePolarity.Positive));
        }
        var w = learner.GetWeight(new EventId("e-clamp"));
        Assert.True(w <= 5.0, $"weight {w} exceeded MaxWeight clamp");
    }

    [Fact]
    public async Task ProjectorPipeline_RebuildAll_includes_decision_chains()
    {
        using var sp = Build();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var pipeline = sp.GetRequiredService<Mneme.Projections.ProjectorPipeline>();
        await agent.IngestAsync(Decision("d-rb", "oc-ws", "rb"));
        await agent.IngestAsync(Action("a-rb", "oc-ws", "rb action", new EventId("d-rb")));
        await agent.IngestAsync(Outcome("o-rb", "oc-ws", "rb outcome", new EventId("a-rb"), OutcomePolarity.Positive));

        var results = pipeline.RebuildAll();
        Assert.True(results.ContainsKey("decision_chains"));
        Assert.True(results["decision_chains"] >= 1, $"expected ≥1 decision chain row, got {results["decision_chains"]}");
    }
}
