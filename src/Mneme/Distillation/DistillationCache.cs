using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Distillation;

/// <summary>
/// Caches the latest <see cref="ContextBundle"/> per workstream in the
/// <c>distillation_cache</c> table. A bundle is stale if its
/// <see cref="ContextBundle.EventsCoveredThrough"/> doesn't equal the newest
/// event id in the workstream (or if any curation has landed after its
/// <see cref="ContextBundle.GeneratedAt"/>).
/// </summary>
public sealed class DistillationCache
{
    private readonly SqliteConnectionFactory _connections;

    public DistillationCache(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    public ContextBundle? TryLoad(WorkstreamId workstream)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT bundle_json FROM distillation_cache WHERE workstream_id = $ws;";
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        var json = cmd.ExecuteScalar() as string;
        if (json is null) return null;
        return JsonSerializer.Deserialize<ContextBundle>(json, Options);
    }

    public void Save(WorkstreamId workstream, ContextBundle bundle)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO distillation_cache(workstream_id, bundle_json, events_covered_through,
                generated_at, distiller, token_count)
            VALUES ($ws, $json, $ev, $at, $d, $tc)
            ON CONFLICT(workstream_id) DO UPDATE SET
                bundle_json = excluded.bundle_json,
                events_covered_through = excluded.events_covered_through,
                generated_at = excluded.generated_at,
                distiller = excluded.distiller,
                token_count = excluded.token_count;
            """;
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(bundle, Options));
        cmd.Parameters.AddWithValue("$ev", bundle.EventsCoveredThrough.Value);
        cmd.Parameters.AddWithValue("$at", bundle.GeneratedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$d", bundle.Index.Distiller);
        cmd.Parameters.AddWithValue("$tc", bundle.Index.TokenCount);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// True if the cached bundle is up-to-date with the underlying log:
    /// the newest event id matches <paramref name="cached"/>'s
    /// <see cref="ContextBundle.EventsCoveredThrough"/> AND no curation has
    /// landed after the bundle's <see cref="ContextBundle.GeneratedAt"/>.
    /// </summary>
    public bool IsFresh(WorkstreamId workstream, ContextBundle cached)
    {
        using var c = _connections.Open();
        EventId latest;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT event_id FROM memory_events WHERE workstream_id = $ws ORDER BY created_at DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("$ws", workstream.Value);
            latest = cmd.ExecuteScalar() is string s ? new EventId(s) : EventId.None;
        }
        if (latest.Value != cached.EventsCoveredThrough.Value) return false;

        using (var cur = c.CreateCommand())
        {
            cur.CommandText = """
                SELECT 1 FROM curation_events
                WHERE workstream_id = $ws AND occurred_at > $at
                LIMIT 1;
                """;
            cur.Parameters.AddWithValue("$ws", workstream.Value);
            cur.Parameters.AddWithValue("$at", cached.GeneratedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            if (cur.ExecuteScalar() is not null) return false;
        }
        return true;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
