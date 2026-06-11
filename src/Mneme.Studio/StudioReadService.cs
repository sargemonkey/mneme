using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest;
using Mneme.Storage;

namespace Mneme.Studio;

/// <summary>
/// Lightweight read-only helpers Studio uses to render the event log
/// timeline, projection tables, and home-page metrics. The real
/// <see cref="IMemoryQueryAPI"/> arrives in Phase 4 with capability-token
/// checks; Studio uses these raw helpers in the meantime so the UI is
/// usable for dogfooding Phase 1+ surfaces before Phase 4 ships.
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
        long revoked = ScalarLong(c, "SELECT COUNT(*) FROM memory_revocations;");
        long projFacts = ScalarLong(c, "SELECT COUNT(*) FROM projection_facts;");
        long projDecisions = ScalarLong(c, "SELECT COUNT(*) FROM projection_decisions;");
        long projGoals = ScalarLong(c, "SELECT COUNT(*) FROM projection_goals;");
        long projHypotheses = ScalarLong(c, "SELECT COUNT(*) FROM projection_hypotheses;");
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
        var byClassification = new Dictionary<Mneme.Contracts.Classification, long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT classification, COUNT(*) FROM memory_events GROUP BY classification;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                byClassification[(Mneme.Contracts.Classification)r.GetInt32(0)] = r.GetInt64(1);
            }
        }
        return Task.FromResult(new StudioMetrics(
            events, queued, workstreams, revoked,
            projFacts, projDecisions, projGoals, projHypotheses,
            byCategory, byClassification));
    }

    public Task<IReadOnlyList<EventRow>> RecentEventsAsync(
        string? workstreamFilter, int limit, CancellationToken ct = default)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        if (string.IsNullOrWhiteSpace(workstreamFilter))
        {
            cmd.CommandText = """
                SELECT e.event_id, e.workstream_id, e.event_channel, e.category,
                       e.classification, e.valid_at, e.created_at, e.payload_json,
                       r.revoked_at IS NOT NULL AS is_revoked
                FROM memory_events e
                LEFT JOIN memory_revocations r ON r.event_id = e.event_id
                ORDER BY e.created_at DESC
                LIMIT $limit;
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT e.event_id, e.workstream_id, e.event_channel, e.category,
                       e.classification, e.valid_at, e.created_at, e.payload_json,
                       r.revoked_at IS NOT NULL AS is_revoked
                FROM memory_events e
                LEFT JOIN memory_revocations r ON r.event_id = e.event_id
                WHERE e.workstream_id = $ws
                ORDER BY e.created_at DESC
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
                Classification: (Mneme.Contracts.Classification)r.GetInt32(4),
                ValidAt: DateTimeOffset.Parse(r.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                CreatedAt: DateTimeOffset.Parse(r.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
                PayloadJson: r.GetString(7),
                IsRevoked: r.GetInt64(8) != 0));
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

    public Task<IReadOnlyList<ProjectionRow>> ProjectionRowsAsync(
        string table, string? workstreamFilter, int limit, CancellationToken ct = default)
    {
        // Whitelisted to prevent SQL injection via the table parameter.
        var allowed = new HashSet<string> { "projection_facts", "projection_decisions", "projection_goals", "projection_hypotheses" };
        if (!allowed.Contains(table))
        {
            throw new ArgumentException("Unknown projection table.", nameof(table));
        }
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        var where = string.IsNullOrWhiteSpace(workstreamFilter) ? "" : "WHERE workstream_id = $ws ";
        // For non-facts tables we still grab the same columns we render.
        cmd.CommandText = table switch
        {
            "projection_facts" => $"SELECT event_id, workstream_id, statement, '' AS rationale, '' AS approver, classification, valid_at, revoked_at FROM {table} {where} ORDER BY created_at DESC LIMIT $limit;",
            "projection_decisions" => $"SELECT event_id, workstream_id, statement, rationale, approver, classification, valid_at, revoked_at FROM {table} {where} ORDER BY created_at DESC LIMIT $limit;",
            "projection_goals" => $"SELECT event_id, workstream_id, statement, CAST(state AS TEXT), '' AS approver, classification, valid_at, revoked_at FROM {table} {where} ORDER BY created_at DESC LIMIT $limit;",
            "projection_hypotheses" => $"SELECT event_id, workstream_id, statement, CAST(state AS TEXT), '' AS approver, classification, valid_at, revoked_at FROM {table} {where} ORDER BY created_at DESC LIMIT $limit;",
            _ => throw new InvalidOperationException(),
        };
        if (!string.IsNullOrWhiteSpace(workstreamFilter))
        {
            cmd.Parameters.AddWithValue("$ws", workstreamFilter);
        }
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<ProjectionRow>(limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new ProjectionRow(
                EventId: r.GetString(0),
                WorkstreamId: r.GetString(1),
                Statement: r.GetString(2),
                SecondaryField: r.GetString(3),
                Approver: r.GetString(4),
                Classification: (Mneme.Contracts.Classification)r.GetInt32(5),
                ValidAt: DateTimeOffset.Parse(r.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
                IsRevoked: !r.IsDBNull(7)));
        }
        return Task.FromResult<IReadOnlyList<ProjectionRow>>(rows);
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
    long Revoked,
    long ProjectionFacts,
    long ProjectionDecisions,
    long ProjectionGoals,
    long ProjectionHypotheses,
    IReadOnlyDictionary<EpistemicCategory, long> ByCategory,
    IReadOnlyDictionary<Mneme.Contracts.Classification, long> ByClassification);

public sealed record EventRow(
    string EventId,
    string WorkstreamId,
    EventChannel Channel,
    EpistemicCategory Category,
    Mneme.Contracts.Classification Classification,
    DateTimeOffset ValidAt,
    DateTimeOffset CreatedAt,
    string PayloadJson,
    bool IsRevoked);

public sealed record ProjectionRow(
    string EventId,
    string WorkstreamId,
    string Statement,
    string SecondaryField,
    string Approver,
    Mneme.Contracts.Classification Classification,
    DateTimeOffset ValidAt,
    bool IsRevoked);
