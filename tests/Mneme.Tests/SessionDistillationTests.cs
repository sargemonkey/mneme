using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;

namespace Mneme.Tests;

public sealed class SessionDistillationTests : IDisposable
{
    private readonly string _tmpDir;
    public SessionDistillationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-sess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private ServiceProvider Build(ISessionDistiller distiller)
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = "sess-ws";
            o.SqlitePath = Path.Combine(_tmpDir, "sess.db");
            o.UserId = "alice";
        });
        services.AddSingleton(distiller);
        return services.BuildServiceProvider();
    }

    private static ContextEntry Entry(string id, string text, ContextEntryKind kind = ContextEntryKind.UserMessage)
        => new(id, DateTimeOffset.UtcNow, kind, text);

    [Fact]
    public async Task DistillSessionAsync_ingests_events_and_advances_watermark()
    {
        using var sp = Build(new InlineDistiller("test/d@1", req => new SessionDistillationResult(
            new[]
            {
                new DistilledEvent(
                    new FactPayload("the team picked October", Array.Empty<EventId>()),
                    new[] { "0001", "0002" }),
            },
            new[] { new DroppedEntry("0003", "small talk") })));
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var session = new SessionId("s1");

        var entries = new[]
        {
            Entry("0001", "We picked October."),
            Entry("0002", "Two engineers out in September."),
            Entry("0003", "Nice weather today."),
        };

        Assert.Null(await agent.GetWatermarkAsync(session));
        var result = await agent.DistillSessionAsync(session, entries, token);
        Assert.False(result.WasNoOp);
        Assert.Single(result.NewEvents);
        Assert.Equal("0003", result.NewWatermark.LastDistilledEntryId);
        Assert.Single(result.Dropped!);

        var watermark = await agent.GetWatermarkAsync(session);
        Assert.NotNull(watermark);
        Assert.Equal("0003", watermark!.LastDistilledEntryId);
        Assert.Equal("test/d@1", watermark.DistillerVersion);

        // Events should be queryable through the normal API and carry a SessionRange citation.
        var api = sp.GetRequiredService<IMemoryQueryAPI>();
        var qr = await api.QueryAsync(new QueryRequest(new QuerySpec(new WorkstreamId("sess-ws"))), token);
        Assert.Single(qr.Items);
        Assert.Equal("the team picked October", qr.Items[0].Summary);

        // Provenance JSON must include the SessionRange.
        var factory = sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT provenance_json FROM memory_events;";
        var prov = (string)cmd.ExecuteScalar()!;
        Assert.Contains("SessionRange", prov);
        Assert.Contains("0001", prov);
        Assert.Contains("0002", prov);
    }

    [Fact]
    public async Task Replaying_same_range_is_idempotent_no_op()
    {
        using var sp = Build(new InlineDistiller("test/d@1", _ => new SessionDistillationResult(
            new[] { new DistilledEvent(new FactPayload("only once", Array.Empty<EventId>()), new[] { "0001" }) })));
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var session = new SessionId("s2");
        var entries = new[] { Entry("0001", "X"), Entry("0002", "Y") };

        var first = await agent.DistillSessionAsync(session, entries, token);
        Assert.False(first.WasNoOp);
        Assert.Single(first.NewEvents);

        var replay = await agent.DistillSessionAsync(session, entries, token);
        Assert.True(replay.WasNoOp);
        Assert.Empty(replay.NewEvents);

        var api = sp.GetRequiredService<IMemoryQueryAPI>();
        var qr = await api.QueryAsync(new QueryRequest(new QuerySpec(new WorkstreamId("sess-ws"))), token);
        Assert.Single(qr.Items); // not duplicated
    }

    [Fact]
    public async Task Growing_session_only_distills_new_tail()
    {
        var seenEntries = new List<int>();
        using var sp = Build(new InlineDistiller("test/d@1", req =>
        {
            seenEntries.Add(req.Entries.Count);
            return new SessionDistillationResult(
                new[] { new DistilledEvent(new EvidencePayload(string.Join("|", req.Entries.Select(e => e.EntryId)), null), req.Entries.Select(e => e.EntryId).ToArray()) });
        }));
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var session = new SessionId("s3");

        var first = new[] { Entry("0001", "a"), Entry("0002", "b") };
        await agent.DistillSessionAsync(session, first, token);

        var second = new[] { Entry("0001", "a"), Entry("0002", "b"), Entry("0003", "c") };
        await agent.DistillSessionAsync(session, second, token);

        // First call saw 2 entries; second call saw only the new tail (1).
        Assert.Equal(new[] { 2, 1 }, seenEntries);
    }

    [Fact]
    public async Task DistillSessionAsync_throws_when_no_distiller_registered()
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = "sess-no-distiller";
            o.SqlitePath = Path.Combine(_tmpDir, "no-d.db");
            o.UserId = "alice";
        });
        using var sp = services.BuildServiceProvider();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.DistillSessionAsync(new SessionId("s4"), new[] { Entry("0001", "x") }, token));
        Assert.Contains("ISessionDistiller", ex.Message);
    }

    [Fact]
    public async Task Empty_tail_is_a_no_op_without_calling_distiller()
    {
        var called = 0;
        using var sp = Build(new InlineDistiller("test/d@1", req =>
        {
            called++;
            return new SessionDistillationResult(Array.Empty<DistilledEvent>());
        }));
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var session = new SessionId("s5");

        // Ingest once.
        var first = await agent.DistillSessionAsync(session, new[] { Entry("0001", "x") }, token);
        Assert.False(first.WasNoOp);
        Assert.Equal(1, called);

        // Re-pass the same entries; tail filtering should drop them all and short-circuit.
        var second = await agent.DistillSessionAsync(session, new[] { Entry("0001", "x") }, token);
        Assert.True(second.WasNoOp);
        Assert.Equal(1, called); // distiller not called again
    }

    private sealed class InlineDistiller : ISessionDistiller
    {
        private readonly Func<SessionDistillationRequest, SessionDistillationResult> _fn;
        public string Id { get; }
        public InlineDistiller(string id, Func<SessionDistillationRequest, SessionDistillationResult> fn) { Id = id; _fn = fn; }
        public Task<SessionDistillationResult> DistillAsync(SessionDistillationRequest req, CancellationToken ct = default)
            => Task.FromResult(_fn(req));
    }
}
