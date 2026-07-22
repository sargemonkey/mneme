using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Review;
using Mneme.Storage;

namespace Mneme.Tests;

/// <summary>
/// Covers the pre-distillation review queue (<see cref="IReviewQueue"/>) and the
/// <see cref="WorkstreamMode.ReviewBeforeDistill"/> ingest gate: events captured
/// into a review-mode workstream are persisted but NOT projected until a curator
/// approves them; reject tombstones; defer hides; the default
/// <see cref="WorkstreamMode.AutoDistill"/> mode is unaffected.
/// </summary>
public sealed class ReviewQueueTests : IDisposable
{
    private readonly string _tmpDir;

    public ReviewQueueTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-reviewq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        SqliteConnection_ClearPools();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ReviewBeforeDistill_gates_projection_until_approved()
    {
        const string ws = "review-ws";
        using var sp = BuildServices(ws, out var factory);
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var queue = sp.GetRequiredService<IReviewQueue>();
        sp.GetRequiredService<WorkstreamConfigStore>().SetMode(new WorkstreamId(ws), WorkstreamMode.ReviewBeforeDistill);

        var evt = NewFact("review-fact-1", ws, "The Q3 launch date is October 15");
        await agent.IngestAsync(evt);

        // Gated: durable in memory_events, but NOT projected → invisible to queries.
        Assert.Equal(1, CountRows(factory, "memory_events", evt.EventId.Value));
        Assert.Equal(0, CountRows(factory, "projection_facts", evt.EventId.Value));

        var pending = await queue.GetPendingAsync(new WorkstreamId(ws), ReviewCap(ws));
        Assert.Single(pending);
        Assert.Equal(evt.EventId, pending[0].EventId);
        Assert.Equal("The Q3 launch date is October 15", pending[0].Summary);

        await queue.ApproveAsync(evt.EventId, ReviewCap(ws));

        // Approved: observers replayed → now projected/queryable, and gone from the queue.
        Assert.Equal(1, CountRows(factory, "projection_facts", evt.EventId.Value));
        Assert.Empty(await queue.GetPendingAsync(new WorkstreamId(ws), ReviewCap(ws)));
    }

    [Fact]
    public async Task Approve_appends_review_approved_audit_event()
    {
        const string ws = "review-ws";
        using var sp = BuildServices(ws, out var factory);
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var queue = sp.GetRequiredService<IReviewQueue>();
        sp.GetRequiredService<WorkstreamConfigStore>().SetMode(new WorkstreamId(ws), WorkstreamMode.ReviewBeforeDistill);

        var evt = NewFact("review-fact-audit", ws, "audit me");
        await agent.IngestAsync(evt);
        await queue.ApproveAsync(evt.EventId, ReviewCap(ws));

        // A technical event.review_approved audit row exists on the Technical channel.
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM memory_events
            WHERE workstream_id = $ws AND event_channel = $tech AND event_id LIKE 'event.review_approved:%';
            """;
        cmd.Parameters.AddWithValue("$ws", ws);
        cmd.Parameters.AddWithValue("$tech", (int)EventChannel.Technical);
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Reject_tombstones_source_and_clears_from_pending()
    {
        const string ws = "review-ws";
        using var sp = BuildServices(ws, out var factory);
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var queue = sp.GetRequiredService<IReviewQueue>();
        sp.GetRequiredService<WorkstreamConfigStore>().SetMode(new WorkstreamId(ws), WorkstreamMode.ReviewBeforeDistill);

        var evt = NewFact("review-fact-2", ws, "reject me please");
        await agent.IngestAsync(evt);

        await queue.RejectAsync(evt.EventId, "not durable", ReviewCap(ws));

        Assert.Equal(0, CountRows(factory, "projection_facts", evt.EventId.Value)); // never projected
        Assert.Equal(1, CountRows(factory, "memory_revocations", evt.EventId.Value)); // tombstoned
        Assert.Empty(await queue.GetPendingAsync(new WorkstreamId(ws), ReviewCap(ws)));
    }

    [Fact]
    public async Task Defer_hides_item_from_pending_until_its_window_elapses()
    {
        const string ws = "review-ws";
        using var sp = BuildServices(ws, out _);
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var queue = sp.GetRequiredService<IReviewQueue>();
        sp.GetRequiredService<WorkstreamConfigStore>().SetMode(new WorkstreamId(ws), WorkstreamMode.ReviewBeforeDistill);

        var evt = NewFact("review-fact-3", ws, "later");
        await agent.IngestAsync(evt);

        await queue.DeferAsync(evt.EventId, DateTimeOffset.UtcNow.AddDays(1), ReviewCap(ws));
        Assert.Empty(await queue.GetPendingAsync(new WorkstreamId(ws), ReviewCap(ws)));

        // A deferral whose window has already elapsed re-surfaces.
        await queue.DeferAsync(evt.EventId, DateTimeOffset.UtcNow.AddMinutes(-1), ReviewCap(ws));
        Assert.Single(await queue.GetPendingAsync(new WorkstreamId(ws), ReviewCap(ws)));
    }

    [Fact]
    public async Task AutoDistill_default_projects_immediately_and_queue_is_empty()
    {
        const string ws = "auto-ws";
        using var sp = BuildServices(ws, out var factory);
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var queue = sp.GetRequiredService<IReviewQueue>();

        var evt = NewFact("auto-fact-1", ws, "auto distilled fact");
        await agent.IngestAsync(evt);

        Assert.Equal(1, CountRows(factory, "projection_facts", evt.EventId.Value));
        Assert.Empty(await queue.GetPendingAsync(new WorkstreamId(ws), ReviewCap(ws)));
    }

    [Fact]
    public async Task Review_operations_require_CanReview_capability()
    {
        const string ws = "review-ws";
        using var sp = BuildServices(ws, out _);
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var queue = sp.GetRequiredService<IReviewQueue>();
        sp.GetRequiredService<WorkstreamConfigStore>().SetMode(new WorkstreamId(ws), WorkstreamMode.ReviewBeforeDistill);

        var evt = NewFact("review-fact-4", ws, "guarded");
        await agent.IngestAsync(evt);

        var noReview = new CurationCapability(
            new PrincipalId("mallory"), new WorkstreamId(ws),
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1),
            CanReview: false);

        await Assert.ThrowsAsync<CapabilityDeniedError>(
            async () => await queue.GetPendingAsync(new WorkstreamId(ws), noReview));
        await Assert.ThrowsAsync<CapabilityDeniedError>(
            async () => await queue.ApproveAsync(evt.EventId, noReview));

        // A CanReview token scoped to a different workstream is also denied.
        var wrongWs = new CurationCapability(
            new PrincipalId("alice"), new WorkstreamId("other-ws"),
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1),
            CanReview: true);
        await Assert.ThrowsAsync<CapabilityDeniedError>(
            async () => await queue.ApproveAsync(evt.EventId, wrongWs));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private ServiceProvider BuildServices(string workstream, out SqliteConnectionFactory factory)
    {
        var services = new ServiceCollection();
        services.AddMneme(opts =>
        {
            opts.WorkstreamId = workstream;
            opts.SqlitePath = Path.Combine(_tmpDir, workstream + "-" + Guid.NewGuid().ToString("N") + ".db");
            opts.UserId = "alice";
        });
        var sp = services.BuildServiceProvider();
        factory = sp.GetRequiredService<SqliteConnectionFactory>();
        return sp;
    }

    private static CurationCapability ReviewCap(string workstream) => new(
        new PrincipalId("alice"), new WorkstreamId(workstream),
        DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1),
        CanReview: true);

    private static CaptureEvent NewFact(string id, string workstream, string statement) => new(
        new EventId(id), new WorkstreamId(workstream), EventChannel.Epistemic,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        new FactPayload(statement, Array.Empty<EventId>()),
        new CaptureProvenance(new CaptureSourceId("unit-test"), new PrincipalId("test")));

    private static int CountRows(SqliteConnectionFactory factory, string table, string eventId)
    {
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", eventId);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SqliteConnection_ClearPools() =>
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
