using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Hosting.Profiles;
using Mneme.Ingest.Redaction;
using Mneme.Projections;
using Mneme.Storage;

namespace Mneme.Tests;

/// <summary>
/// End-to-end proof of the Profile SDK composition point (ADR-0005 / Phase
/// 15.A): a complete <see cref="IMnemeProfile"/> — a satellite payload +
/// descriptor + schema module + projector — registered via
/// <c>AddMnemeProfile</c> must (1) create its own projection table, (2) have
/// its projector run on the public ingest path, and (3) survive
/// <c>RebuildAll</c> (i.e. the projection is rebuildable from the log, honouring
/// the "event log is the single source of truth" invariant).
/// </summary>
public sealed class ProfileSdkTests : IDisposable
{
    private readonly string _tmpDir;
    public ProfileSdkTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-prof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private const string Ws = "prof-ws";

    // --- A minimal but complete domain profile -----------------------------

    private sealed record NotePayload(string Title, string Body) : EventPayload
    {
        public override EpistemicCategory Category => EpistemicCategory.Evidence;
    }

    private sealed class NoteDescriptor : IPayloadDescriptor
    {
        public Type PayloadType => typeof(NotePayload);
        public string Discriminator => "NotePayload";
        public (EventPayload Payload, bool HadHits, int HitCount) Redact(EventPayload payload, IRedactor redactor)
        {
            var p = (NotePayload)payload;
            var t = redactor.Redact(p.Title);
            var b = redactor.Redact(p.Body);
            return (p with { Title = t.RedactedContent, Body = b.RedactedContent },
                    t.HadHits || b.HadHits, t.Hits.Count + b.Hits.Count);
        }
        public string ExtractText(EventPayload payload) { var p = (NotePayload)payload; return p.Title + " " + p.Body; }
        public string Summarize(EventPayload payload) => ((NotePayload)payload).Title;
    }

    private sealed class NoteSchemaModule : ISchemaModule
    {
        public string Name => "notes";
        public int Version => 1;
        public void ApplyDdl(SqliteConnection c)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS projection_notes (
                    workstream_id TEXT NOT NULL,
                    event_id      TEXT NOT NULL PRIMARY KEY,
                    title         TEXT NOT NULL,
                    body          TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
    }

    private sealed class NoteProjector : IProjector
    {
        public string Name => "notes";
        public EpistemicCategory Category => EpistemicCategory.Evidence;
        public void Apply(SqliteConnection c, SqliteTransaction tx, EventEnvelope e)
        {
            if (e.Payload is not NotePayload p) return; // ignore non-note Evidence
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO projection_notes(workstream_id, event_id, title, body)
                VALUES ($ws, $eid, $t, $b)
                ON CONFLICT(event_id) DO UPDATE SET title = excluded.title, body = excluded.body;
                """;
            cmd.Parameters.AddWithValue("$ws", e.WorkstreamId.Value);
            cmd.Parameters.AddWithValue("$eid", e.EventId.Value);
            cmd.Parameters.AddWithValue("$t", p.Title);
            cmd.Parameters.AddWithValue("$b", p.Body);
            cmd.ExecuteNonQuery();
        }
        public int Rebuild(SqliteConnection c, SqliteTransaction tx)
        {
            using (var del = c.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM projection_notes;"; del.ExecuteNonQuery(); }
            var n = 0;
            foreach (var e in EventEnvelopeReader.ReadAll(c, tx, Category))
            {
                if (e.Payload is not NotePayload) continue;
                Apply(c, tx, e); n++;
            }
            return n;
        }
    }

    private sealed class NoteProfile : IMnemeProfile
    {
        public string Name => "notes";
        public IReadOnlyList<IPayloadDescriptor> PayloadDescriptors { get; } = new IPayloadDescriptor[] { new NoteDescriptor() };
        public IReadOnlyList<ISchemaModule> SchemaModules { get; } = new ISchemaModule[] { new NoteSchemaModule() };
        public IReadOnlyList<IProjector> Projectors { get; } = new IProjector[] { new NoteProjector() };
    }

    // -----------------------------------------------------------------------

    private (ServiceProvider sp, IMemoryAgent agent) BuildHost()
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = Ws;
            o.SqlitePath = Path.Combine(_tmpDir, "prof.db");
            o.UserId = "host";
        }).AddMnemeProfile<NoteProfile>();
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IMemoryAgent>());
    }

    private static long CountNotes(ServiceProvider sp)
    {
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projection_notes;";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Schema_module_creates_the_profiles_table()
    {
        var (sp, _) = BuildHost();
        using var _d = sp;
        // Table exists (query does not throw) and the module version was recorded.
        Assert.Equal(0, CountNotes(sp));
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_meta WHERE key = 'module:notes';";
        Assert.Equal("1", cmd.ExecuteScalar() as string);
    }

    [Fact]
    public async Task Profile_projector_runs_on_the_public_ingest_path()
    {
        var (sp, agent) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(new CaptureEvent(
            new EventId("note-1"), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new NotePayload("Chapter 1", "Alice adopts a dog."),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("u"))));

        Assert.Equal(1, CountNotes(sp));
    }

    [Fact]
    public async Task Profile_projection_is_rebuildable_from_the_log()
    {
        var (sp, agent) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(new CaptureEvent(
            new EventId("note-r1"), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new NotePayload("Chapter 2", "The plot thickens."),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("u"))));
        Assert.Equal(1, CountNotes(sp));

        // Nuke the derived table, then RebuildAll must reconstruct it from the log.
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using (var c = factory.Open())
        using (var del = c.CreateCommand())
        {
            del.CommandText = "DELETE FROM projection_notes;";
            del.ExecuteNonQuery();
        }
        Assert.Equal(0, CountNotes(sp));

        var pipeline = sp.GetRequiredService<ProjectorPipeline>();
        var counts = pipeline.RebuildAll();

        Assert.True(counts.ContainsKey("notes"), "profile projector did not participate in RebuildAll");
        Assert.Equal(1, counts["notes"]);
        Assert.Equal(1, CountNotes(sp));
    }
}
