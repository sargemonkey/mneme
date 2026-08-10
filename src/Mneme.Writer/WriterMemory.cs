using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Writer;

/// <summary>
/// SQLite-backed <see cref="IWriterMemory"/>. Reads the shared
/// <c>memory_contradictions</c> projection that
/// <see cref="AuthoringCanonProjector"/> feeds, scoped and visibility-filtered
/// by the caller's capability token.
/// </summary>
public sealed class WriterMemory : IWriterMemory
{
    private readonly SqliteConnectionFactory _connections;
    private readonly TimeProvider _clock;

    /// <summary>Construct over the Mneme connection factory and clock.</summary>
    public WriterMemory(SqliteConnectionFactory connections, TimeProvider clock)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<WriterContinuityConflict>> GetContinuityConflictsAsync(
        CapabilityToken token, int limit = 100, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (!token.IsValidAt(_clock.GetUtcNow()))
        {
            throw new CapabilityDeniedError(
                $"token validity window [{token.NotBefore:O}..{token.NotAfter:O}] excludes now");
        }
        // Continuity is a workstream-scoped read; a null-workstream cross token
        // is not meaningful here (a manuscript's canon is one workstream).
        if (token.Workstream is not { } workstream)
        {
            throw new CapabilityDeniedError("continuity conflicts require a workstream-scoped capability token");
        }
        // Authoring claims ride under Fact; the token must be allowed to read it.
        if (!token.Allows(EpistemicCategory.Fact))
        {
            throw new CapabilityDeniedError("token does not allow the Fact category authoring claims ride under");
        }

        ct.ThrowIfCancellationRequested();

        var results = new List<WriterContinuityConflict>();
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        // A contradiction pairs two events; surface it only if the viewing
        // principal is authorized to see BOTH sides (neither is another author's
        // Private event) — the same both-sides visibility guard the dreamer uses.
        // The original subject surface form is recovered from the triple index so
        // the result reads "Helios", not the normalized key "helios".
        cmd.CommandText = """
            SELECT
                COALESCE((
                    SELECT ta.subject_text FROM projection_fact_triples ta
                    WHERE ta.workstream_id = ct.workstream_id
                      AND ta.event_id = ct.event_id_a
                      AND ta.subject_key = ct.subject_key
                      AND ta.predicate = ct.predicate
                    LIMIT 1), ct.subject_key) AS subject_text,
                ct.predicate, ct.event_id_a, ct.object_a, ct.event_id_b, ct.object_b
            FROM memory_contradictions ct
            JOIN memory_events ea ON ea.event_id = ct.event_id_a
            LEFT JOIN memory_visibility va ON va.event_id = ct.event_id_a
            JOIN memory_events eb ON eb.event_id = ct.event_id_b
            LEFT JOIN memory_visibility vb ON vb.event_id = ct.event_id_b
            WHERE ct.workstream_id = $ws AND ct.status = 0
              AND (COALESCE(va.visibility, 1) >= 1 OR ea.principal_id = $viewer)
              AND (COALESCE(vb.visibility, 1) >= 1 OR eb.principal_id = $viewer)
            ORDER BY ct.detected_at DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        cmd.Parameters.AddWithValue("$viewer", token.Principal.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            results.Add(new WriterContinuityConflict(
                Subject: rd.GetString(0),
                Attribute: rd.GetString(1),
                EventA: new EventId(rd.GetString(2)),
                ValueA: rd.GetString(3),
                EventB: new EventId(rd.GetString(4)),
                ValueB: rd.GetString(5)));
        }
        return Task.FromResult<IReadOnlyList<WriterContinuityConflict>>(results);
    }
}
