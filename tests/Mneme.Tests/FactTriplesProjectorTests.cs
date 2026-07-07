using Mneme.Contracts;
using Mneme.Ingest;
using Mneme.Projections;

namespace Mneme.Tests;

public sealed class FactTriplesProjectorTests
{
    private static MemoryAgent NewAgent(TestDatabase db)
    {
        var pipeline = new ProjectorPipeline(db.Factory);
        var observer = new ProjectorIngestObserver(pipeline);
        return new MemoryAgent(
            db.Factory,
            new Mneme.Ingest.Redaction.RegexRedactor(),
            new AlwaysRedactedContent(),
            new Mneme.Classification.RuleBasedClassifier(),
            TimeProvider.System,
            new[] { (IIngestObserver)observer });
    }

    private static CaptureEvent FactWithTriples(string id, string statement, params FactTriple[] triples) =>
        new(new EventId(id), new WorkstreamId("kg-ws"), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new FactPayload(statement, Array.Empty<EventId>(), triples),
            new CaptureProvenance(new CaptureSourceId("test"), new PrincipalId("p")));

    [Fact]
    public async Task Fact_with_triples_projects_subject_scoped_rows()
    {
        using var db = new TestDatabase();
        var agent = NewAgent(db);

        await agent.IngestAsync(FactWithTriples("kg-1",
            "Melanie's grandma is from Sweden",
            new FactTriple("Melanie's grandma", "nationality", "Swedish")));

        using var c = db.Factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT subject_text, subject_key, predicate, object FROM projection_fact_triples WHERE event_id = 'kg-1';";
        using var rd = cmd.ExecuteReader();
        Assert.True(rd.Read());
        Assert.Equal("Melanie's grandma", rd.GetString(0));
        Assert.Equal("melanie grandma", rd.GetString(1)); // possessive stripped, lowercased
        Assert.Equal("nationality", rd.GetString(2));
        Assert.Equal("Swedish", rd.GetString(3));
    }

    [Fact]
    public async Task Fact_without_triples_projects_no_rows()
    {
        using var db = new TestDatabase();
        var agent = NewAgent(db);

        await agent.IngestAsync(FactWithTriples("kg-plain", "just a statement"));

        using var c = db.Factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projection_fact_triples;";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);

        // The statement itself still lands in projection_facts.
        cmd.CommandText = "SELECT COUNT(*) FROM projection_facts WHERE event_id = 'kg-plain';";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Multiple_triples_get_stable_ordinals()
    {
        using var db = new TestDatabase();
        var agent = NewAgent(db);

        await agent.IngestAsync(FactWithTriples("kg-multi",
            "Caroline likes Bach and plays piano",
            new FactTriple("Caroline", "likes", "Bach"),
            new FactTriple("Caroline", "plays", "piano")));

        using var c = db.Factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ordinal, object FROM projection_fact_triples WHERE event_id = 'kg-multi' ORDER BY ordinal;";
        using var rd = cmd.ExecuteReader();
        Assert.True(rd.Read());
        Assert.Equal(0, rd.GetInt32(0));
        Assert.Equal("Bach", rd.GetString(1));
        Assert.True(rd.Read());
        Assert.Equal(1, rd.GetInt32(0));
        Assert.Equal("piano", rd.GetString(1));
    }

    [Fact]
    public async Task Rebuild_reproduces_triples_from_event_log()
    {
        using var db = new TestDatabase();
        var agent = NewAgent(db);
        var pipeline = new ProjectorPipeline(db.Factory);

        await agent.IngestAsync(FactWithTriples("kg-r1", "a", new FactTriple("Mel", "likes", "tea")));
        await agent.IngestAsync(FactWithTriples("kg-r2", "b", new FactTriple("Cara", "likes", "coffee")));

        long Count()
        {
            using var c = db.Factory.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM projection_fact_triples;";
            return (long)cmd.ExecuteScalar()!;
        }
        Assert.Equal(2L, Count());

        using (var c = db.Factory.Open())
        using (var wipe = c.CreateCommand())
        {
            wipe.CommandText = "DELETE FROM projection_fact_triples;";
            wipe.ExecuteNonQuery();
        }
        Assert.Equal(0L, Count());

        var results = pipeline.RebuildAll();
        Assert.Equal(2, results["fact-triples"]);
        Assert.Equal(2L, Count());
    }
}
