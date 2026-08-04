using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Dreaming;

/// <summary>
/// Capability-gated traversal of <see cref="Citation.Derived"/> provenance
/// (ADR-0004 guardrail #3). A consolidated event names the events it was derived
/// from; following that chain must <em>not</em> let a caller read a source event
/// in a workstream their token doesn't authorize. This resolver reads a derived
/// event's sources and returns only the ones the supplied
/// <see cref="CapabilityToken"/> is allowed to see — closing the back-channel
/// where a global skill's citation could otherwise leak cross-workstream source
/// material.
/// </summary>
public sealed class DerivedCitationResolver
{
    private readonly SqliteConnectionFactory _connections;
    private readonly TimeProvider _clock;

    public DerivedCitationResolver(SqliteConnectionFactory connections, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _clock = clock;
    }

    /// <summary>
    /// Resolve the derived sources of <paramref name="eventId"/> that
    /// <paramref name="token"/> may read. Returns an empty list when the event is
    /// not a <see cref="Citation.Derived"/> event, and filters out any source in a
    /// workstream the token doesn't authorize (and any revoked source).
    /// </summary>
    /// <exception cref="CapabilityDeniedError">If the token is invalid at the current time.</exception>
    public IReadOnlyList<EventId> ResolveAuthorizedSources(EventId eventId, CapabilityToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!token.IsValidAt(_clock.GetUtcNow()))
        {
            throw new CapabilityDeniedError(
                $"token validity window [{token.NotBefore:O}..{token.NotAfter:O}] excludes now");
        }

        var from = ReadDerivedSources(eventId);
        if (from.Count == 0) return Array.Empty<EventId>();

        var crossOk = token.CrossWorkstream && token.Workstream is null;
        var allowed = new List<EventId>(from.Count);
        foreach (var (sourceId, workstream, revoked) in ReadSourceWorkstreams(from))
        {
            if (revoked) continue; // never surface a revoked source through a citation
            if (crossOk || (token.Workstream is { } w && w.Value == workstream))
            {
                allowed.Add(new EventId(sourceId));
            }
        }
        return allowed;
    }

    private IReadOnlyList<string> ReadDerivedSources(EventId eventId)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT provenance_json FROM memory_events WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", eventId.Value);
        var json = cmd.ExecuteScalar() as string;
        if (json is null) return Array.Empty<string>();

        var provenance = EventSerialization.DeserializeProvenance(json);
        return provenance.Citation is Citation.Derived derived
            ? derived.From.Select(e => e.Value).ToArray()
            : Array.Empty<string>();
    }

    private IEnumerable<(string EventId, string Workstream, bool Revoked)> ReadSourceWorkstreams(IReadOnlyList<string> ids)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT e.event_id, e.workstream_id, (r.event_id IS NOT NULL) AS revoked
            FROM memory_events e
            LEFT JOIN memory_revocations r ON r.event_id = e.event_id
            WHERE e.event_id IN (SELECT value FROM json_each($ids));
            """;
        cmd.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(ids));
        using var rd = cmd.ExecuteReader();
        var rows = new List<(string, string, bool)>();
        while (rd.Read())
        {
            rows.Add((rd.GetString(0), rd.GetString(1), rd.GetInt64(2) != 0));
        }
        return rows;
    }
}
