using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Ingest;
using Mneme.Resolution;

namespace Mneme.Tests;

public sealed class SubjectTripleResolverTests : IDisposable
{
    private readonly string _tmpDir;
    public SubjectTripleResolverTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-str-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private ServiceProvider Build(string ws)
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

    private static CaptureEvent Fact(string id, string ws, string statement, params FactTriple[] triples) =>
        new(new EventId(id), new WorkstreamId(ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new FactPayload(statement, Array.Empty<EventId>(), triples),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")));

    private static long NullEntityCount(ServiceProvider sp)
    {
        var factory = sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projection_fact_triples WHERE subject_entity_id IS NULL;";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task Resolve_stamps_subject_entity_id_on_all_triples()
    {
        using var sp = Build("str-ws");
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var resolver = new SubjectTripleResolver(
            sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(),
            sp.GetRequiredService<EntityResolver>());
        var ws = new WorkstreamId("str-ws");

        await agent.IngestAsync(Fact("str-1", "str-ws", "Melanie likes tea",
            new FactTriple("Melanie", "likes", "tea")));
        await agent.IngestAsync(Fact("str-2", "str-ws", "Melanie's grandma is from Sweden",
            new FactTriple("Melanie's grandma", "nationality", "Swedish")));

        Assert.Equal(2L, NullEntityCount(sp));

        var resolved = await resolver.ResolveWorkstreamAsync(ws);
        Assert.Equal(2, resolved); // two distinct subjects
        Assert.Equal(0L, NullEntityCount(sp));
    }

    [Fact]
    public async Task Same_subject_across_events_gets_one_entity_id()
    {
        using var sp = Build("str-ws2");
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var resolver = new SubjectTripleResolver(
            sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(),
            sp.GetRequiredService<EntityResolver>());
        var ws = new WorkstreamId("str-ws2");

        await agent.IngestAsync(Fact("str2-1", "str-ws2", "Melanie likes tea",
            new FactTriple("Melanie", "likes", "tea")));
        await agent.IngestAsync(Fact("str2-2", "str-ws2", "Melanie plays piano",
            new FactTriple("Melanie", "plays", "piano")));

        var resolved = await resolver.ResolveWorkstreamAsync(ws);
        Assert.Equal(1, resolved); // one distinct subject ("Melanie")

        var factory = sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT subject_entity_id) FROM projection_fact_triples WHERE subject_key = 'melanie';";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Resolve_is_idempotent()
    {
        using var sp = Build("str-ws3");
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var resolver = new SubjectTripleResolver(
            sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(),
            sp.GetRequiredService<EntityResolver>());
        var ws = new WorkstreamId("str-ws3");

        await agent.IngestAsync(Fact("str3-1", "str-ws3", "Caroline sings",
            new FactTriple("Caroline", "hobby", "singing")));

        Assert.Equal(1, await resolver.ResolveWorkstreamAsync(ws));
        // Second pass: nothing left unresolved.
        Assert.Equal(0, await resolver.ResolveWorkstreamAsync(ws));
        Assert.Equal(0L, NullEntityCount(sp));
    }
}
