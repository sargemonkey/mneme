using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Sync;

namespace Mneme.Tests;

public sealed class SyncEngineTests : IDisposable
{
    private readonly string _tmpDir;
    public SyncEngineTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private ServiceProvider Build(string ws, string dbName)
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = ws;
            o.SqlitePath = Path.Combine(_tmpDir, dbName);
            o.UserId = "alice";
        });
        return services.BuildServiceProvider();
    }

    private static CaptureEvent Evidence(string id, string ws, string content) =>
        new(new EventId(id), new WorkstreamId(ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload(content, "test"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")));

    [Fact]
    public async Task Push_then_pull_into_fresh_db_replicates_events()
    {
        var storeDir = Path.Combine(_tmpDir, "store");
        var store = new FileSystemSyncStore(storeDir);
        var ws = new WorkstreamId("sync-ws");

        // Source side: ingest a few events, push.
        using var src = Build("sync-ws", "src.db");
        var srcAgent = src.GetRequiredService<IMemoryAgent>();
        await srcAgent.IngestAsync(Evidence("sy-1", "sync-ws", "alpha"));
        await srcAgent.IngestAsync(Evidence("sy-2", "sync-ws", "beta"));
        await srcAgent.IngestAsync(Evidence("sy-3", "sync-ws", "gamma"));

        var srcEngine = new SyncEngine(src.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(), store);
        var push = await srcEngine.PushAsync(ws);
        Assert.Equal(3, push.EventCount);

        // Destination side: fresh empty db, pull.
        using var dst = Build("sync-ws", "dst.db");
        var dstEngine = new SyncEngine(dst.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(), store);
        var pull = await dstEngine.PullAsync(ws);
        Assert.Equal(1, pull.SnapshotsApplied);
        Assert.Equal(3, pull.NewEvents);

        var dstApi = dst.GetRequiredService<IMemoryQueryAPI>();
        var dstToken = dst.GetRequiredService<CapabilityToken>();
        var result = await dstApi.QueryAsync(new QueryRequest(new QuerySpec(ws)), dstToken);
        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public async Task Pull_is_idempotent_repeat_yields_zero_new_rows()
    {
        var store = new FileSystemSyncStore(Path.Combine(_tmpDir, "store2"));
        var ws = new WorkstreamId("sync-idem");
        using var src = Build("sync-idem", "src.db");
        await src.GetRequiredService<IMemoryAgent>().IngestAsync(Evidence("idem-1", "sync-idem", "x"));
        await new SyncEngine(src.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(), store).PushAsync(ws);

        using var dst = Build("sync-idem", "dst.db");
        var dstEngine = new SyncEngine(dst.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(), store);
        var first  = await dstEngine.PullAsync(ws);
        var second = await dstEngine.PullAsync(ws);
        Assert.Equal(1, first.NewEvents);
        Assert.Equal(0, second.NewEvents); // repeat pull adds nothing — idempotent merge
    }

    [Fact]
    public async Task Two_peers_with_disjoint_events_converge_to_union_via_round_trip()
    {
        var store = new FileSystemSyncStore(Path.Combine(_tmpDir, "store3"));
        var ws = new WorkstreamId("sync-converge");

        using var a = Build("sync-converge", "a.db");
        using var b = Build("sync-converge", "b.db");
        await a.GetRequiredService<IMemoryAgent>().IngestAsync(Evidence("a-1", "sync-converge", "from A"));
        await b.GetRequiredService<IMemoryAgent>().IngestAsync(Evidence("b-1", "sync-converge", "from B"));

        var aEng = new SyncEngine(a.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(), store);
        var bEng = new SyncEngine(b.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(), store);

        await aEng.PushAsync(ws);
        await bEng.PushAsync(ws);
        await aEng.PullAsync(ws);
        await bEng.PullAsync(ws);

        async Task<int> Count(ServiceProvider sp)
        {
            var api = sp.GetRequiredService<IMemoryQueryAPI>();
            var token = sp.GetRequiredService<CapabilityToken>();
            var result = await api.QueryAsync(new QueryRequest(new QuerySpec(ws)), token);
            return result.Items.Count;
        }
        Assert.Equal(2, await Count(a));
        Assert.Equal(2, await Count(b));
    }

    [Fact]
    public async Task Revocations_and_curations_replicate_in_snapshot()
    {
        var store = new FileSystemSyncStore(Path.Combine(_tmpDir, "store4"));
        var ws = new WorkstreamId("sync-rev");

        using var src = Build("sync-rev", "src.db");
        var srcAgent = src.GetRequiredService<IMemoryAgent>();
        var srcRev = src.GetRequiredService<Mneme.Revocation.IRevocationService>();
        var srcCurator = src.GetRequiredService<IMemoryCurator>();
        var cap = new CurationCapability(
            new PrincipalId("alice"), new WorkstreamId("sync-rev"),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1),
            CanAmend: true, CanAnnotate: true, CanPin: true, CanDemote: true,
            CanSplit: true, CanMerge: true, CanRevert: true, CanReview: true);

        await srcAgent.IngestAsync(Evidence("rv-1", "sync-rev", "to be revoked"));
        await srcAgent.IngestAsync(Evidence("rv-2", "sync-rev", "to be annotated"));
        await srcRev.RevokeAsync(new EventId("rv-1"), ws, new PrincipalId("alice"), "test");
        await srcCurator.AnnotateAsync(new EventId("rv-2"), "a note", cap);

        var srcEngine = new SyncEngine(src.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(), store);
        var push = await srcEngine.PushAsync(ws);
        Assert.Equal(1, push.RevocationCount);
        Assert.Equal(1, push.CurationCount);

        using var dst = Build("sync-rev", "dst.db");
        var pull = await new SyncEngine(dst.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(), store).PullAsync(ws);
        Assert.Equal(2, pull.NewEvents);
        Assert.Equal(1, pull.NewRevocations);
        Assert.Equal(1, pull.NewCurations);

        // Verify revoked event is filtered out on the destination too.
        var dstApi = dst.GetRequiredService<IMemoryQueryAPI>();
        var dstToken = dst.GetRequiredService<CapabilityToken>();
        var qr = await dstApi.QueryAsync(new QueryRequest(new QuerySpec(ws)), dstToken);
        Assert.Single(qr.Items);
        Assert.Equal("rv-2", qr.Items[0].EventId.Value);
    }

    [Fact]
    public async Task FileSystemSyncStore_rejects_overwrite_with_different_content()
    {
        var store = new FileSystemSyncStore(Path.Combine(_tmpDir, "store5"));
        var a = System.Text.Encoding.UTF8.GetBytes("alpha");
        var b = System.Text.Encoding.UTF8.GetBytes("beta");
        var aHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a));
        var bHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));

        await store.WriteAsync("x/y/z.bin", a, aHash);
        // Same content + same hash = no-op.
        await store.WriteAsync("x/y/z.bin", a, aHash);
        // Different content under same key = idempotency violation.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.WriteAsync("x/y/z.bin", b, bHash));
    }
}
