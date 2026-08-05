using Microsoft.Data.Sqlite;
using Mneme.Contracts;

namespace Mneme.Projections.Projectors;

/// <summary>
/// Cross-session fact de-duplication (Phase 14, ADR-0004). When a newly-projected
/// fact shares its normalized statement with an existing non-revoked fact in the
/// same workstream — the signature of two concurrent sessions/agents asserting
/// the same thing — this projector records the pair in <c>memory_duplicates</c>
/// as an open review candidate. It is <strong>propose-only</strong>: it never
/// revokes or merges, honouring the conservative-resolution locked decision. A
/// curator confirms (revokes the duplicate) or dismisses.
/// </summary>
/// <remarks>
/// Deterministic and narrow (low false positives): duplicate = same
/// <c>LOWER(TRIM(statement))</c>. The <em>canonical</em> is the earlier fact
/// (by <c>created_at</c>, tie-broken by event id); the later one is the
/// duplicate. Must run after <see cref="FactsProjector"/> so the current fact is
/// already in <c>projection_facts</c>.
/// </remarks>
public sealed class DuplicateFactsProjector : IProjector
{
    /// <inheritdoc/>
    public string Name => "duplicates";
    /// <inheritdoc/>
    public EpistemicCategory Category => EpistemicCategory.Fact;

    /// <inheritdoc/>
    public void Apply(SqliteConnection c, SqliteTransaction tx, EventEnvelope e)
    {
        if (e.Payload is not FactPayload p) return;
        if (string.IsNullOrWhiteSpace(p.Statement)) return;

        // Other non-revoked facts in this workstream with the same normalized
        // statement, with their created_at so we can pick the canonical.
        var matches = new List<(string EventId, string CreatedAt)>();
        using (var find = c.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = """
                SELECT event_id, created_at
                FROM projection_facts
                WHERE workstream_id = $ws
                  AND event_id <> $eid
                  AND revoked_at IS NULL
                  AND LOWER(TRIM(statement)) = LOWER(TRIM($stmt));
                """;
            find.Parameters.AddWithValue("$ws", e.WorkstreamId.Value);
            find.Parameters.AddWithValue("$eid", e.EventId.Value);
            find.Parameters.AddWithValue("$stmt", p.Statement);
            using var r = find.ExecuteReader();
            while (r.Read()) matches.Add((r.GetString(0), r.GetString(1)));
        }
        if (matches.Count == 0) return;

        var thisCreatedAt = FactsProjector.Fmt(e.CreatedAt);
        var statementKey = p.Statement.Trim().ToLowerInvariant();
        foreach (var (otherId, otherCreatedAt) in matches)
        {
            // Canonical = earlier created_at; tie-break by ordinal event id.
            var cmp = string.CompareOrdinal(otherCreatedAt, thisCreatedAt);
            var thisIsCanonical = cmp > 0
                || (cmp == 0 && string.CompareOrdinal(e.EventId.Value, otherId) < 0);
            var (canonicalId, duplicateId) = thisIsCanonical
                ? (e.EventId.Value, otherId)
                : (otherId, e.EventId.Value);
            Insert(c, tx, e.WorkstreamId.Value, canonicalId, duplicateId, statementKey, thisCreatedAt);
        }
    }

    private static void Insert(SqliteConnection c, SqliteTransaction tx, string ws,
        string canonicalId, string duplicateId, string statementKey, string detectedAt)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO memory_duplicates(workstream_id, canonical_event_id, duplicate_event_id,
                statement_key, detected_at, status)
            VALUES ($ws, $canon, $dup, $key, $at, 0)
            ON CONFLICT(workstream_id, canonical_event_id, duplicate_event_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$ws", ws);
        cmd.Parameters.AddWithValue("$canon", canonicalId);
        cmd.Parameters.AddWithValue("$dup", duplicateId);
        cmd.Parameters.AddWithValue("$key", statementKey);
        cmd.Parameters.AddWithValue("$at", detectedAt);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public int Rebuild(SqliteConnection c, SqliteTransaction tx)
    {
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM memory_duplicates;";
            del.ExecuteNonQuery();
        }
        using var ins = c.CreateCommand();
        ins.Transaction = tx;
        // Self-join: each duplicate pair once, canonical = earlier (created_at,
        // then event_id). Compare normalized statements.
        ins.CommandText = """
            INSERT INTO memory_duplicates(workstream_id, canonical_event_id, duplicate_event_id,
                statement_key, detected_at, status)
            SELECT a.workstream_id, a.event_id, b.event_id,
                   LOWER(TRIM(a.statement)), MAX(a.created_at, b.created_at), 0
            FROM projection_facts a
            JOIN projection_facts b
              ON a.workstream_id = b.workstream_id
             AND LOWER(TRIM(a.statement)) = LOWER(TRIM(b.statement))
             AND (a.created_at < b.created_at
                  OR (a.created_at = b.created_at AND a.event_id < b.event_id))
            WHERE a.revoked_at IS NULL AND b.revoked_at IS NULL
            ON CONFLICT(workstream_id, canonical_event_id, duplicate_event_id) DO NOTHING;
            """;
        return ins.ExecuteNonQuery();
    }
}
