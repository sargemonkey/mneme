using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest.Validation;
using Mneme.Storage;

namespace Mneme.Revocation;

/// <summary>SQLite-backed <see cref="IRevocationService"/>.</summary>
public sealed class SqliteRevocationService : IRevocationService
{
    private readonly SqliteConnectionFactory _connections;
    private readonly TimeProvider _clock;

    /// <summary>Construct against the shared connection factory.</summary>
    public SqliteRevocationService(SqliteConnectionFactory connections)
        : this(connections, TimeProvider.System) { }

    /// <summary>Construct against the shared connection factory with a custom clock (tests).</summary>
    public SqliteRevocationService(SqliteConnectionFactory connections, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _clock = clock;
    }

    /// <inheritdoc/>
    public Task<RevocationResult> RevokeAsync(
        EventId eventId,
        WorkstreamId workstreamId,
        PrincipalId revokedBy,
        string reason,
        CancellationToken ct = default)
    {
        if (!eventId.HasValue)
        {
            throw new ArgumentException("EventId is required.", nameof(eventId));
        }
        WorkstreamIdValidator.EnsureValid(workstreamId.Value, nameof(workstreamId));
        if (string.IsNullOrEmpty(revokedBy.Value))
        {
            throw new ArgumentException("RevokedBy is required.", nameof(revokedBy));
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Reason is required.", nameof(reason));
        }
        ct.ThrowIfCancellationRequested();

        using var connection = _connections.Open();
        using var tx = connection.BeginTransaction();

        // 1. Verify the event exists and belongs to the named workstream.
        string? existingArtifact;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = tx;
            lookup.CommandText = """
                SELECT artifact_id FROM memory_events
                WHERE event_id = $eventId AND workstream_id = $ws;
                """;
            lookup.Parameters.AddWithValue("$eventId", eventId.Value);
            lookup.Parameters.AddWithValue("$ws", workstreamId.Value);
            var result = lookup.ExecuteScalar();
            if (result is null)
            {
                throw new InvalidOperationException(
                    $"No event '{eventId.Value}' in workstream '{workstreamId.Value}'.");
            }
            existingArtifact = result is DBNull ? null : (string?)result;
        }

        var nowUtc = _clock.GetUtcNow();

        // 2. Insert into memory_revocations. ON CONFLICT DO NOTHING gives
        //    idempotency: a second revocation of the same event is a no-op
        //    and the original principal/reason/timestamp win.
        int inserted;
        using (var rev = connection.CreateCommand())
        {
            rev.Transaction = tx;
            rev.CommandText = """
                INSERT INTO memory_revocations(event_id, workstream_id, revoked_at, revoked_by, reason)
                VALUES ($eventId, $ws, $revokedAt, $revokedBy, $reason)
                ON CONFLICT(event_id) DO NOTHING;
                """;
            rev.Parameters.AddWithValue("$eventId", eventId.Value);
            rev.Parameters.AddWithValue("$ws", workstreamId.Value);
            rev.Parameters.AddWithValue("$revokedAt", FormatTimestamp(nowUtc));
            rev.Parameters.AddWithValue("$revokedBy", revokedBy.Value);
            rev.Parameters.AddWithValue("$reason", reason);
            inserted = rev.ExecuteNonQuery();
        }

        bool bodyZeroed = false;
        DateTimeOffset effectiveRevokedAt = nowUtc;
        if (inserted == 0)
        {
            // Already revoked — return the original timestamp.
            using var read = connection.CreateCommand();
            read.Transaction = tx;
            read.CommandText = "SELECT revoked_at FROM memory_revocations WHERE event_id = $eventId;";
            read.Parameters.AddWithValue("$eventId", eventId.Value);
            var raw = read.ExecuteScalar() as string;
            if (raw is not null)
            {
                effectiveRevokedAt = DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        else if (existingArtifact is not null)
        {
            using var nullify = connection.CreateCommand();
            nullify.Transaction = tx;
            nullify.CommandText = """
                UPDATE memory_artifacts
                   SET body = NULL,
                       body_hash = NULL,
                       revoked_at = $revokedAt,
                       revocation_reason = $reason
                 WHERE artifact_id = $artifactId AND revoked_at IS NULL;
                """;
            nullify.Parameters.AddWithValue("$artifactId", existingArtifact);
            nullify.Parameters.AddWithValue("$revokedAt", FormatTimestamp(nowUtc));
            nullify.Parameters.AddWithValue("$reason", reason);
            bodyZeroed = nullify.ExecuteNonQuery() > 0;
        }

        tx.Commit();
        return Task.FromResult(new RevocationResult(
            EventId: eventId,
            RevokedAt: effectiveRevokedAt,
            AlreadyRevoked: inserted == 0,
            BodyZeroed: bodyZeroed));
    }

    /// <inheritdoc/>
    public Task<bool> IsRevokedAsync(EventId eventId, CancellationToken ct = default)
    {
        if (!eventId.HasValue) return Task.FromResult(false);
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM memory_revocations WHERE event_id = $eventId;";
        cmd.Parameters.AddWithValue("$eventId", eventId.Value);
        return Task.FromResult(cmd.ExecuteScalar() is not null);
    }

    internal static string FormatTimestamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
