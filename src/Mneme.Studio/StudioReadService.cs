using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest;
using Mneme.Storage;

namespace Mneme.Studio;

/// <summary>
/// Lightweight read-only helpers Studio uses to render the event log
/// timeline and home-page metrics. The real <see cref="IMemoryQueryAPI"/>
/// arrives in Phase 4 with capability-token checks; Studio uses these
/// raw helpers in the meantime so the UI is usable for dogfooding
/// Phase 1+ surfaces before Phase 4 ships.
/// </summary>
public sealed class StudioReadService
{
    private readonly SqliteConnectionFactory _connections;

    public StudioReadService(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    public Task<StudioMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        using var c = _connections.Open();
        long events = ScalarLong(c, "SELECT COUNT(*) FROM memory_events;");
        long queued = ScalarLong(c, "SELECT COUNT(*) FROM distillation_queue;");
        long workstreams = ScalarLong(c, "SELECT COUNT(DISTINCT workstream_id) FROM memory_events;");
        var byCategory = new Dictionary<EpistemicCategory, long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT category, COUNT(*) FROM memory_events GROUP BY category;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                byCategory[(EpistemicCategory)r.GetInt32(0)] = r.GetInt64(1);
            }
        }
        return Task.FromResult(new StudioMetrics(events, queued, workstreams, byCategory));
    }

    public Task<IReadOnlyList<EventRow>> RecentEventsAsync(
        string? workstreamFilter, int limit, CancellationToken ct = default)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        if (string.IsNullOrWhiteSpace(workstreamFilter))
        {
            cmd.CommandText = """
                SELECT event_id, workstream_id, event_channel, category, valid_at, created_at, payload_json
                FROM memory_events
                ORDER BY created_at DESC
                LIMIT $limit;
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT event_id, workstream_id, event_channel, category, valid_at, created_at, payload_json
                FROM memory_events
                WHERE workstream_id = $ws
                ORDER BY created_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$ws", workstreamFilter);
        }
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<EventRow>(limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new EventRow(
                EventId: r.GetString(0),
                WorkstreamId: r.GetString(1),
                Channel: (EventChannel)r.GetInt32(2),
                Category: (EpistemicCategory)r.GetInt32(3),
                ValidAt: DateTimeOffset.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                CreatedAt: DateTimeOffset.Parse(r.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                PayloadJson: r.GetString(6)));
        }
        return Task.FromResult<IReadOnlyList<EventRow>>(rows);
    }

    public Task<IReadOnlyList<string>> WorkstreamsAsync(CancellationToken ct = default)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT workstream_id FROM memory_events ORDER BY workstream_id;";
        var rows = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(r.GetString(0));
        return Task.FromResult<IReadOnlyList<string>>(rows);
    }

    private static long ScalarLong(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }
}

public sealed record StudioMetrics(
    long TotalEvents,
    long QueuedForDistillation,
    long Workstreams,
    IReadOnlyDictionary<EpistemicCategory, long> ByCategory);

public sealed record EventRow(
    string EventId,
    string WorkstreamId,
    EventChannel Channel,
    EpistemicCategory Category,
    DateTimeOffset ValidAt,
    DateTimeOffset CreatedAt,
    string PayloadJson);
