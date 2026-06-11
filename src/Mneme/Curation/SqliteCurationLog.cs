using System.Globalization;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Query;
using Mneme.Storage;

namespace Mneme.Curation;

/// <summary>SQLite-backed <see cref="ICurationLog"/>.</summary>
public sealed class SqliteCurationLog : ICurationLog
{
    private readonly SqliteConnectionFactory _connections;
    private readonly TimeProvider _clock;

    public SqliteCurationLog(SqliteConnectionFactory connections)
        : this(connections, TimeProvider.System) { }

    public SqliteCurationLog(SqliteConnectionFactory connections, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _clock = clock;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CurationEntry>> GetCurationHistoryAsync(WorkstreamId? workstream, DateTimeOffset since,
        CapabilityToken token, CancellationToken ct = default)
    {
        _ = CapabilityEnforcement.Enforce(token, workstream, null, EventChannel.Epistemic, _clock.GetUtcNow());
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        if (workstream is null)
        {
            cmd.CommandText = """
                SELECT event_id, target_event_id, workstream_id, curation_type,
                       curator, rationale, occurred_at, pre_state_hash
                FROM curation_events WHERE occurred_at >= $since
                ORDER BY occurred_at DESC;
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT event_id, target_event_id, workstream_id, curation_type,
                       curator, rationale, occurred_at, pre_state_hash
                FROM curation_events
                WHERE workstream_id = $ws AND occurred_at >= $since
                ORDER BY occurred_at DESC;
                """;
            cmd.Parameters.AddWithValue("$ws", workstream.Value.Value);
        }
        cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return Task.FromResult(Read(cmd));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CurationEntry>> GetCurationsByPrincipalAsync(PrincipalId curator, DateTimeOffset since,
        CapabilityToken token, CancellationToken ct = default)
    {
        _ = CapabilityEnforcement.Enforce(token, token.Workstream, null, EventChannel.Epistemic, _clock.GetUtcNow());
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, target_event_id, workstream_id, curation_type,
                   curator, rationale, occurred_at, pre_state_hash
            FROM curation_events
            WHERE curator = $curator AND occurred_at >= $since
            ORDER BY occurred_at DESC;
            """;
        cmd.Parameters.AddWithValue("$curator", curator.Value);
        cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        var rows = Read(cmd);
        if (token.Workstream is { } ws && !token.CrossWorkstream)
        {
            rows = rows.Where(r => r.Workstream.Value == ws.Value).ToArray();
        }
        return Task.FromResult(rows);
    }

    private static IReadOnlyList<CurationEntry> Read(SqliteCommand cmd)
    {
        var results = new List<CurationEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            results.Add(new CurationEntry(
                CurationEventId: new EventId(r.GetString(0)),
                Curator: new PrincipalId(r.GetString(4)),
                TargetEventId: new EventId(r.GetString(1)),
                Type: (CurationType)r.GetInt32(3),
                Rationale: r.GetString(5),
                OccurredAt: DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
                PreStateHash: r.GetString(7),
                Workstream: new WorkstreamId(r.GetString(2))));
        }
        return results;
    }
}
