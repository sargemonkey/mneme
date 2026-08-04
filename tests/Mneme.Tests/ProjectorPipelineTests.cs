using Mneme.Contracts;
using Mneme.Ingest;
using Mneme.Projections;
using Mneme.Search;

namespace Mneme.Tests;

public sealed class ProjectorPipelineTests
{
    [Fact]
    public async Task Ingest_with_projector_observer_populates_projection_facts()
    {
        using var db = new TestDatabase();
        var pipeline = new ProjectorPipeline(db.Factory);
        var observer = new ProjectorIngestObserver(pipeline);
        var agent = new MemoryAgent(
            db.Factory,
            new Mneme.Ingest.Redaction.RegexRedactor(),
            new AlwaysRedactedContent(),
            new Mneme.Classification.RuleBasedClassifier(),
            TimeProvider.System,
            new[] { (IIngestObserver)observer });

        var evt = new CaptureEvent(
            new EventId("proj-fact-001"),
            new WorkstreamId("proj-ws"),
            EventChannel.Epistemic,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            new FactPayload("the sky is blue", Array.Empty<EventId>()),
            new CaptureProvenance(new CaptureSourceId("test"), new PrincipalId("alice")));
        await agent.IngestAsync(evt);

        using var c = db.Factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT statement FROM projection_facts WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", evt.EventId.Value);
        var statement = cmd.ExecuteScalar() as string;
        Assert.Equal("the sky is blue", statement);

        using var logCmd = c.CreateCommand();
        logCmd.CommandText = "SELECT status FROM event_processing_log WHERE event_id = $id AND projection_name = 'facts';";
        logCmd.Parameters.AddWithValue("$id", evt.EventId.Value);
        var status = logCmd.ExecuteScalar();
        Assert.NotNull(status);
        Assert.Equal((long)(int)ProcessingStatus.Applied, Convert.ToInt64(status));
    }

    [Fact]
    public async Task Projector_skips_events_of_other_categories()
    {
        using var db = new TestDatabase();
        var pipeline = new ProjectorPipeline(db.Factory);
        var observer = new ProjectorIngestObserver(pipeline);
        var agent = new MemoryAgent(
            db.Factory,
            new Mneme.Ingest.Redaction.RegexRedactor(),
            new AlwaysRedactedContent(),
            new Mneme.Classification.RuleBasedClassifier(),
            TimeProvider.System,
            new[] { (IIngestObserver)observer });

        // Evidence event — the fact/decision/goal/hypothesis projectors skip it
        // (category mismatch). The skills projector matches the Evidence category
        // but type-filters the non-skill payload, so it writes no row.
        await agent.IngestAsync(TestFixtures.NewEvidence(eventId: "proj-evid-001"));

        using var c = db.Factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projection_facts;";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM projection_skills;";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        // Only the skills projector matches the Evidence category (as a no-op here);
        // the other default projectors don't match and log nothing.
        cmd.CommandText = "SELECT COUNT(*) FROM event_processing_log WHERE projection_name <> 'skills';";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task RebuildAll_reproduces_projections_from_genesis()
    {
        using var db = new TestDatabase();
        var pipeline = new ProjectorPipeline(db.Factory);
        var observer = new ProjectorIngestObserver(pipeline);
        var agent = new MemoryAgent(
            db.Factory,
            new Mneme.Ingest.Redaction.RegexRedactor(),
            new AlwaysRedactedContent(),
            new Mneme.Classification.RuleBasedClassifier(),
            TimeProvider.System,
            new[] { (IIngestObserver)observer });

        // Seed: 3 facts + 2 decisions + 1 goal + 1 hypothesis + 1 evidence
        await agent.IngestAsync(MakeFact("f1", "fact one"));
        await agent.IngestAsync(MakeFact("f2", "fact two"));
        await agent.IngestAsync(MakeFact("f3", "fact three"));
        await agent.IngestAsync(MakeDecision("d1", "decide A", "because X"));
        await agent.IngestAsync(MakeDecision("d2", "decide B", "because Y"));
        await agent.IngestAsync(MakeGoal("g1", "ship it", GoalState.Active));
        await agent.IngestAsync(MakeHypothesis("h1", "cache helps"));
        await agent.IngestAsync(TestFixtures.NewEvidence(eventId: "e1"));

        long Count(string table)
        {
            using var c = db.Factory.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
            return (long)cmd.ExecuteScalar()!;
        }

        Assert.Equal(3L, Count("projection_facts"));
        Assert.Equal(2L, Count("projection_decisions"));
        Assert.Equal(1L, Count("projection_goals"));
        Assert.Equal(1L, Count("projection_hypotheses"));

        // Wipe + rebuild.
        using (var c = db.Factory.Open())
        using (var wipe = c.CreateCommand())
        {
            wipe.CommandText = "DELETE FROM projection_facts; DELETE FROM projection_decisions; DELETE FROM projection_goals; DELETE FROM projection_hypotheses;";
            wipe.ExecuteNonQuery();
        }
        Assert.Equal(0L, Count("projection_facts"));

        var results = pipeline.RebuildAll();
        Assert.Equal(3, results["facts"]);
        Assert.Equal(2, results["decisions"]);
        Assert.Equal(1, results["goals"]);
        Assert.Equal(1, results["hypotheses"]);
        Assert.Equal(3L, Count("projection_facts"));
        Assert.Equal(2L, Count("projection_decisions"));
        Assert.Equal(1L, Count("projection_goals"));
        Assert.Equal(1L, Count("projection_hypotheses"));
    }

    private static CaptureEvent MakeFact(string id, string statement) =>
        new(new EventId(id), new WorkstreamId("rebuild-ws"), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new FactPayload(statement, Array.Empty<EventId>()),
            new CaptureProvenance(new CaptureSourceId("test"), new PrincipalId("p")));

    private static CaptureEvent MakeDecision(string id, string stmt, string rat) =>
        new(new EventId(id), new WorkstreamId("rebuild-ws"), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new DecisionPayload(stmt, rat, Array.Empty<EventId>(), new PrincipalId("approver")),
            new CaptureProvenance(new CaptureSourceId("test"), new PrincipalId("p")));

    private static CaptureEvent MakeGoal(string id, string stmt, GoalState state) =>
        new(new EventId(id), new WorkstreamId("rebuild-ws"), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new GoalPayload(stmt, state),
            new CaptureProvenance(new CaptureSourceId("test"), new PrincipalId("p")));

    private static CaptureEvent MakeHypothesis(string id, string stmt) =>
        new(new EventId(id), new WorkstreamId("rebuild-ws"), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new HypothesisPayload(stmt, HypothesisState.Open),
            new CaptureProvenance(new CaptureSourceId("test"), new PrincipalId("p")));
}
