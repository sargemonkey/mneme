using System.Globalization;
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
        var workstream = RequireWorkstreamScoped(token, EpistemicCategory.Fact,
            "continuity conflicts", "authoring claims");
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

    /// <inheritdoc/>
    public Task<IReadOnlyList<NarrativeCommitment>> GetOpenCommitmentsAsync(
        CapabilityToken token, int limit = 100, CancellationToken ct = default)
    {
        var workstream = RequireWorkstreamScoped(token, EpistemicCategory.Goal,
            "open commitments", "narrative commitments");
        ct.ThrowIfCancellationRequested();

        var results = new List<NarrativeCommitment>();
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        // An open commitment = a viewer-visible SETUP with no viewer-visible
        // PAYOFF sharing its commitment_id. Both the setup and the NOT-EXISTS
        // payoff probe are visibility-filtered against the same viewer, so a
        // payoff the viewer can't see never silently closes a promise (and its
        // existence is never leaked). Revoked rows drop out on both sides.
        cmd.CommandText = """
            SELECT s.commitment_id, s.kind, s.description, s.event_id,
                   s.scene_path, s.quote, s.story_time, s.reveal_time
            FROM projection_narrative_commitments s
            JOIN memory_events es ON es.event_id = s.event_id
            LEFT JOIN memory_visibility vs ON vs.event_id = s.event_id
            WHERE s.workstream_id = $ws
              AND s.role = 0
              AND s.revoked_at IS NULL
              AND (COALESCE(vs.visibility, 1) >= 1 OR es.principal_id = $viewer)
              AND NOT EXISTS (
                  SELECT 1
                  FROM projection_narrative_commitments p
                  JOIN memory_events ep ON ep.event_id = p.event_id
                  LEFT JOIN memory_visibility vp ON vp.event_id = p.event_id
                  WHERE p.workstream_id = s.workstream_id
                    AND p.commitment_id = s.commitment_id
                    AND p.role = 1
                    AND p.revoked_at IS NULL
                    AND (COALESCE(vp.visibility, 1) >= 1 OR ep.principal_id = $viewer)
              )
            ORDER BY s.story_time ASC, s.event_id ASC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        cmd.Parameters.AddWithValue("$viewer", token.Principal.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            results.Add(new NarrativeCommitment(
                CommitmentId: rd.GetString(0),
                Kind: rd.GetString(1),
                Description: rd.GetString(2),
                SetupEvent: new EventId(rd.GetString(3)),
                ScenePath: rd.IsDBNull(4) ? null : rd.GetString(4),
                Quote: rd.IsDBNull(5) ? null : rd.GetString(5),
                StoryTime: ParseTs(rd.GetString(6)),
                RevealTime: ParseTs(rd.GetString(7))));
        }
        return Task.FromResult<IReadOnlyList<NarrativeCommitment>>(results);
    }

    // Shared capability guard for the writer read surface: validity window +
    // workstream scope + category allow-check. Returns the scoped workstream.
    private WorkstreamId RequireWorkstreamScoped(
        CapabilityToken token, EpistemicCategory category, string operation, string rideUnder)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!token.IsValidAt(_clock.GetUtcNow()))
        {
            throw new CapabilityDeniedError(
                $"token validity window [{token.NotBefore:O}..{token.NotAfter:O}] excludes now");
        }
        // These are workstream-scoped reads; a null-workstream cross token is not
        // meaningful (a manuscript's canon / ledger is one workstream).
        if (token.Workstream is not { } workstream)
        {
            throw new CapabilityDeniedError($"{operation} require a workstream-scoped capability token");
        }
        if (!token.Allows(category))
        {
            throw new CapabilityDeniedError($"token does not allow the {category} category {rideUnder} ride under");
        }
        return workstream;
    }

    private static DateTimeOffset ParseTs(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
