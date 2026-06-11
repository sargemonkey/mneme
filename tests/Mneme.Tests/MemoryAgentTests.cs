using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest;
using Mneme.Storage;

namespace Mneme.Tests;

public sealed class MemoryAgentTests
{
    [Fact]
    public async Task Ingest_persists_event_and_enqueues_distillation()
    {
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);

        var evt = TestFixtures.NewEvidence(eventId: "01H0EVID000000000000000001");
        var result = await agent.IngestAsync(evt);

        Assert.Equal(evt.EventId, result.EventId);
        Assert.False(result.WasDuplicate);

        using var c = db.Factory.Open();
        Assert.Equal(1L, Count(c, "memory_events"));
        Assert.Equal(1L, Count(c, "distillation_queue"));

        // Bi-temporal columns populated as expected.
        using var read = c.CreateCommand();
        read.CommandText = "SELECT workstream_id, event_channel, category, valid_at, invalid_at, created_at, expired_at, content_shape FROM memory_events;";
        using var r = read.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("test-ws", r.GetString(0));
        Assert.Equal((int)EventChannel.Epistemic, r.GetInt32(1));
        Assert.Equal((int)EpistemicCategory.Evidence, r.GetInt32(2));
        Assert.False(string.IsNullOrEmpty(r.GetString(3)));      // valid_at
        Assert.True(r.IsDBNull(4));                              // invalid_at null on fresh ingest
        Assert.False(string.IsNullOrEmpty(r.GetString(5)));      // created_at
        Assert.True(r.IsDBNull(6));                              // expired_at null on fresh ingest
        Assert.Equal((int)ContentShape.RedactedContent, r.GetInt32(7));
    }

    [Fact]
    public async Task Ingest_is_idempotent_on_event_id()
    {
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);

        var evt = TestFixtures.NewEvidence(eventId: "01H0EVID000000000000000002");
        var first = await agent.IngestAsync(evt);
        var second = await agent.IngestAsync(evt);

        Assert.False(first.WasDuplicate);
        Assert.True(second.WasDuplicate);

        using var c = db.Factory.Open();
        Assert.Equal(1L, Count(c, "memory_events"));
        Assert.Equal(1L, Count(c, "distillation_queue"));
    }

    [Fact]
    public async Task Ingest_redacts_secret_content_in_evidence()
    {
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);

        const string secret = "sk-abcdefghijklmnopqrstuvwxyz1234567890";
        var evt = TestFixtures.NewEvidence(
            eventId: "01H0EVID000000000000000003",
            content: $"customer pasted their key: {secret}");
        await agent.IngestAsync(evt);

        using var c = db.Factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM memory_events;";
        var payloadJson = (string?)cmd.ExecuteScalar();
        Assert.NotNull(payloadJson);
        Assert.DoesNotContain(secret, payloadJson);
        Assert.Contains("<REDACTED:openai-key>", payloadJson);
    }

    [Fact]
    public async Task Ingest_rejects_invalid_workstream_id()
    {
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);

        var evt = TestFixtures.NewEvidence(workstream: "../escape");
        await Assert.ThrowsAsync<ArgumentException>(async () => await agent.IngestAsync(evt));
    }

    [Fact]
    public async Task Ingest_rejects_empty_event_id()
    {
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);

        var bad = TestFixtures.NewEvidence(eventId: "");
        await Assert.ThrowsAsync<ArgumentException>(async () => await agent.IngestAsync(bad));
    }

    [Fact]
    public async Task Ingest_default_channel_is_epistemic()
    {
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);
        var evt = TestFixtures.NewEvidence(eventId: "01H0EVID000000000000000004");
        await agent.IngestAsync(evt);

        using var c = db.Factory.Open();
        var ch = Scalar(c, "SELECT event_channel FROM memory_events;");
        Assert.Equal(((int)EventChannel.Epistemic).ToString(), ch);
    }

    [Fact]
    public async Task Ingest_p99_latency_under_50ms_warm()
    {
        // Invariant test for the sync-stage contract: after a warm-up
        // pass, the 99th percentile of 200 ingests must be < 50ms.
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);

        // warm-up
        for (var i = 0; i < 20; i++)
        {
            await agent.IngestAsync(TestFixtures.NewEvidence(eventId: $"warm-{i:D24}"));
        }

        var samples = new long[200];
        var sw = new System.Diagnostics.Stopwatch();
        for (var i = 0; i < samples.Length; i++)
        {
            var evt = TestFixtures.NewEvidence(eventId: $"measured-{i:D22}");
            sw.Restart();
            await agent.IngestAsync(evt);
            sw.Stop();
            samples[i] = sw.ElapsedMilliseconds;
        }

        Array.Sort(samples);
        var p99 = samples[(int)(samples.Length * 0.99) - 1];
        Assert.True(p99 < 50, $"p99 ingest latency was {p99}ms (must be < 50ms). " +
                              $"min={samples[0]}ms median={samples[samples.Length / 2]}ms max={samples[^1]}ms");
    }

    private static long Count(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)cmd.ExecuteScalar()!;
    }

    private static string? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }
}
