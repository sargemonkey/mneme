using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Revocation;
using Mneme.Storage;

namespace Mneme.Tests;

public sealed class AddMnemeTests : IDisposable
{
    private readonly string _tmpDir;

    public AddMnemeTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-addmneme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task AddMneme_registers_full_stack_and_ingest_works_end_to_end()
    {
        var services = new ServiceCollection();
        services.AddMneme(opts =>
        {
            opts.WorkstreamId = "addmneme-test";
            opts.SqlitePath = Path.Combine(_tmpDir, "stack.db");
            opts.UserId = "alice";
        });
        using var sp = services.BuildServiceProvider();

        var agent = sp.GetRequiredService<IMemoryAgent>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var rev = sp.GetRequiredService<IRevocationService>();
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();

        // Capability token auto-built with sensible defaults.
        Assert.Equal("alice", token.Principal.Value);
        Assert.Equal("addmneme-test", token.Workstream!.Value.Value);
        Assert.False(token.CrossWorkstream);
        Assert.False(token.IncludeTechnical);
        Assert.True(token.IsValidAt(DateTimeOffset.UtcNow));

        // End-to-end: ingest, then revoke through the resolved services.
        var evt = TestFixtures.NewEvidence(
            eventId: "addmneme-evt-1",
            workstream: "addmneme-test",
            content: "hello from AddMneme");
        var ingest = await agent.IngestAsync(evt);
        Assert.False(ingest.WasDuplicate);

        var revoked = await rev.RevokeAsync(
            evt.EventId, evt.WorkstreamId, new PrincipalId("alice"), "cleanup");
        Assert.False(revoked.AlreadyRevoked);

        // Schema was initialized.
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memory_events;";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void AddMneme_requires_workstream_user_and_path()
    {
        var s1 = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => s1.AddMneme(o => { o.SqlitePath = "x"; o.UserId = "y"; }));
        var s2 = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => s2.AddMneme(o => { o.WorkstreamId = "ws"; o.UserId = "y"; }));
        var s3 = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => s3.AddMneme(o => { o.WorkstreamId = "ws"; o.SqlitePath = "x"; }));
    }
}
