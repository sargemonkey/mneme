using Microsoft.Extensions.DependencyInjection;
using Mneme.Capture;
using Mneme.Contracts;
using Mneme.Hosting;

namespace Mneme.Tests;

public sealed class CaptureSessionTests : IDisposable
{
    private readonly string _tmpDir;
    public CaptureSessionTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private ServiceProvider Build(ICapturePolicy policy, params ICaptureFilter[] filters)
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = "cap-ws";
            o.SqlitePath = Path.Combine(_tmpDir, "cap.db");
            o.UserId = "alice";
        });
        services.AddSingleton(policy);
        foreach (var f in filters)
        {
            services.AddSingleton<ICaptureFilter>(f);
        }
        return services.BuildServiceProvider();
    }

    private static ConversationTurn Turn(string content, string speaker = "alice") =>
        new(new PrincipalId(speaker), content, DateTimeOffset.UtcNow, SessionId: "session-1");

    [Fact]
    public async Task Empty_policy_skips_ingest_entirely()
    {
        using var sp = Build(new InlinePolicy("test/empty", _ => Array.Empty<CaptureCandidate>()));
        var session = sp.GetRequiredService<CaptureSession>();
        var results = await session.ProcessTurnAsync(Turn("anything"), new WorkstreamId("cap-ws"));
        Assert.Empty(results);
    }

    [Fact]
    public async Task Policy_candidates_become_events_with_capture_provenance()
    {
        using var sp = Build(new InlinePolicy("test/regex@1", turn =>
            new[] { new CaptureCandidate(turn.Content, EpistemicCategory.Fact, Rationale: "user said it") }));
        var session = sp.GetRequiredService<CaptureSession>();
        var results = await session.ProcessTurnAsync(Turn("the sky is blue"), new WorkstreamId("cap-ws"));
        Assert.Single(results);
        Assert.False(results[0].WasDuplicate);

        var api = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var qr = await api.QueryAsync(new QueryRequest(new QuerySpec(new WorkstreamId("cap-ws"))), token);
        Assert.Single(qr.Items);
        Assert.Equal(EpistemicCategory.Fact, qr.Items[0].Category);
        Assert.Equal("the sky is blue", qr.Items[0].Summary);

        // Provenance source should be 'capture/<policy id>' — check via raw read.
        var factory = sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT provenance_json FROM memory_events;";
        var prov = cmd.ExecuteScalar() as string;
        Assert.NotNull(prov);
        Assert.Contains("capture/test/regex@1", prov);
    }

    [Fact]
    public async Task Filter_chain_drops_candidates()
    {
        using var sp = Build(
            new InlinePolicy("p", _ => new[]
            {
                new CaptureCandidate("low", EpistemicCategory.Evidence, Confidence: 0.2),
                new CaptureCandidate("high", EpistemicCategory.Evidence, Confidence: 0.9),
            }),
            new ConfidenceFilter(0.5));
        var session = sp.GetRequiredService<CaptureSession>();
        var results = await session.ProcessTurnAsync(Turn("ignored"), new WorkstreamId("cap-ws"));
        Assert.Single(results);
        var api = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var qr = await api.QueryAsync(new QueryRequest(new QuerySpec(new WorkstreamId("cap-ws"))), token);
        Assert.Single(qr.Items);
        Assert.Equal("high", qr.Items[0].Summary);
    }

    [Fact]
    public async Task RecentDuplicateFilter_skips_already_stored_content()
    {
        using var sp = Build(
            new InlinePolicy("p", turn => new[] { new CaptureCandidate(turn.Content, EpistemicCategory.Evidence) }));
        // Wire the dedupe filter after the initial container so it sees the factory.
        var factory = sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>();
        var dedupe = new RecentDuplicateFilter(factory);
        var sessionA = new CaptureSession(
            sp.GetRequiredService<IMemoryAgent>(),
            sp.GetRequiredService<ICapturePolicy>(),
            new ICaptureFilter[] { dedupe });

        var ws = new WorkstreamId("cap-ws");
        var r1 = await sessionA.ProcessTurnAsync(Turn("Remember: meet at 10am"), ws);
        var r2 = await sessionA.ProcessTurnAsync(Turn("Remember: meet at 10am"), ws);
        var r3 = await sessionA.ProcessTurnAsync(Turn("REMEMBER:    Meet at 10am"), ws); // whitespace + case normalised
        Assert.Single(r1);
        Assert.Empty(r2);
        Assert.Empty(r3);

        var api = sp.GetRequiredService<IMemoryQueryAPI>();
        var token = sp.GetRequiredService<CapabilityToken>();
        var qr = await api.QueryAsync(new QueryRequest(new QuerySpec(ws)), token);
        Assert.Single(qr.Items);
    }

    [Fact]
    public async Task CaptureSession_throws_when_no_policy_registered()
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = "cap-no-policy";
            o.SqlitePath = Path.Combine(_tmpDir, "no-policy.db");
            o.UserId = "alice";
        });
        using var sp = services.BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<CaptureSession>());
        Assert.Contains("ICapturePolicy", ex.Message);
    }

    private sealed class InlinePolicy : ICapturePolicy
    {
        private readonly Func<ConversationTurn, IReadOnlyList<CaptureCandidate>> _fn;
        public string Id { get; }
        public InlinePolicy(string id, Func<ConversationTurn, IReadOnlyList<CaptureCandidate>> fn) { Id = id; _fn = fn; }
        public Task<IReadOnlyList<CaptureCandidate>> EvaluateAsync(ConversationTurn turn, WorkstreamId ws, CancellationToken ct = default)
            => Task.FromResult(_fn(turn));
    }

    private sealed class ConfidenceFilter : ICaptureFilter
    {
        private readonly double _threshold;
        public ConfidenceFilter(double threshold) { _threshold = threshold; }
        public Task<IReadOnlyList<CaptureCandidate>> FilterAsync(IReadOnlyList<CaptureCandidate> candidates, WorkstreamId ws, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CaptureCandidate>>(candidates.Where(c => c.Confidence >= _threshold).ToArray());
    }
}
