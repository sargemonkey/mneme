using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Ingest.Redaction;
using Mneme.Storage;

namespace Mneme.Tests;

/// <summary>
/// Regression guard for the ingest secret-redaction path (locked decision #11).
/// A DI mis-registration (<c>TryAddSingleton&lt;IRedactor, RegexRedactor&gt;()</c>)
/// let the container pick <see cref="RegexRedactor"/>'s greedy
/// <c>IEnumerable&lt;RedactionRule&gt;</c> constructor and resolve it to an
/// <em>empty</em> rule set — so inline redaction silently stripped nothing in
/// every DI-wired host. These tests fail closed if that ever regresses.
/// </summary>
public sealed class RedactorRegistrationTests : IDisposable
{
    private readonly string _tmpDir;
    public RedactorRegistrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-red-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private const string Ws = "red-ws";

    private (ServiceProvider sp, IMemoryAgent agent) BuildHost()
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = Ws;
            o.SqlitePath = Path.Combine(_tmpDir, "red.db");
            o.UserId = "host";
        });
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IMemoryAgent>());
    }

    private const string Secret = "set password = supersecretvalue123 before deploying";

    [Fact]
    public void RegexRedactor_default_ctor_redacts()
    {
        var r = new RegexRedactor().Redact(Secret);
        Assert.True(r.HadHits, "expected a redaction hit from the default rule set");
        Assert.DoesNotContain("supersecretvalue123", r.RedactedContent);
    }

    [Fact]
    public void Host_resolved_redactor_actually_redacts()
    {
        var (sp, _) = BuildHost();
        using var _d = sp;
        var red = sp.GetRequiredService<IRedactor>();
        var r = red.Redact(Secret);
        Assert.True(r.HadHits, $"host redactor ({red.GetType().Name}) resolved with an empty rule set");
        Assert.DoesNotContain("supersecretvalue123", r.RedactedContent);
    }

    [Fact]
    public async Task Ingest_redacts_secrets_before_they_reach_the_wal()
    {
        var (sp, agent) = BuildHost();
        using var _d = sp;
        await agent.IngestAsync(new CaptureEvent(
            new EventId("evsec"), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload(Secret, "test"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("u"))));

        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM memory_events WHERE event_id = 'evsec';";
        var rawPayload = (string)cmd.ExecuteScalar()!;
        Assert.False(rawPayload.Contains("supersecretvalue123"), $"secret reached the WAL: {rawPayload}");
    }
}
