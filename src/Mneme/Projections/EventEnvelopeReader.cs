using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Projections;

/// <summary>
/// Reads <c>memory_events</c> rows (with the LEFT JOIN onto
/// <c>memory_revocations</c>) into typed <see cref="EventEnvelope"/>
/// values for the projector pipeline.
/// </summary>
internal static class EventEnvelopeReader
{
    public static IEnumerable<EventEnvelope> ReadAll(SqliteConnection c, SqliteTransaction? tx, EpistemicCategory? categoryFilter)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        var where = categoryFilter is null
            ? string.Empty
            : "WHERE e.category = $cat";
        cmd.CommandText = $"""
            SELECT e.event_id, e.workstream_id, e.event_channel, e.category, e.schema_version,
                   e.valid_at, e.invalid_at, e.created_at, e.expired_at,
                   e.classification, r.revoked_at,
                   e.payload_json, e.provenance_json
            FROM memory_events e
            LEFT JOIN memory_revocations r ON r.event_id = e.event_id
            {where}
            ORDER BY e.created_at ASC;
            """;
        if (categoryFilter is not null)
        {
            cmd.Parameters.AddWithValue("$cat", (int)categoryFilter.Value);
        }
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return Map(r);
        }
    }

    public static EventEnvelope? ReadOne(SqliteConnection c, SqliteTransaction? tx, EventId id)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT e.event_id, e.workstream_id, e.event_channel, e.category, e.schema_version,
                   e.valid_at, e.invalid_at, e.created_at, e.expired_at,
                   e.classification, r.revoked_at,
                   e.payload_json, e.provenance_json
            FROM memory_events e
            LEFT JOIN memory_revocations r ON r.event_id = e.event_id
            WHERE e.event_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.Value);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? Map(rd) : null;
    }

    private static EventEnvelope Map(SqliteDataReader r)
    {
        var payload = EventSerialization.DeserializePayload(r.GetString(11));
        var provenance = EventSerialization.DeserializeProvenance(r.GetString(12));
        return new EventEnvelope(
            EventId: new EventId(r.GetString(0)),
            WorkstreamId: new WorkstreamId(r.GetString(1)),
            Channel: (EventChannel)r.GetInt32(2),
            Category: (EpistemicCategory)r.GetInt32(3),
            SchemaVersion: r.GetInt32(4),
            ValidAt: Parse(r.GetString(5))!.Value,
            InvalidAt: Parse(r.IsDBNull(6) ? null : r.GetString(6)),
            CreatedAt: Parse(r.GetString(7))!.Value,
            ExpiredAt: Parse(r.IsDBNull(8) ? null : r.GetString(8)),
            Classification: (Mneme.Contracts.Classification)r.GetInt32(9),
            RevokedAt: Parse(r.IsDBNull(10) ? null : r.GetString(10)),
            Payload: payload,
            Provenance: provenance);
    }

    private static DateTimeOffset? Parse(string? v) => v is null
        ? null
        : DateTimeOffset.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
}
