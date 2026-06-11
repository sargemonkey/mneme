using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Ingest.Distillation;

/// <summary>
/// SQLite-backed <see cref="IDistillationQueue"/> using the
/// <c>distillation_queue</c> outbox table. The enqueue is part of the
/// same WAL commit that persists the event itself, so the queue can
/// never get out of sync with the log.
/// </summary>
public sealed class SqliteDistillationQueue : IDistillationQueue
{
    private readonly SqliteConnectionFactory _connections;

    /// <summary>Construct against the shared connection factory.</summary>
    public SqliteDistillationQueue(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    /// <inheritdoc/>
    public void Enqueue(EventId eventId, WorkstreamId workstreamId, DateTimeOffset enqueuedAt)
    {
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO distillation_queue(event_id, workstream_id, enqueued_at)
            VALUES ($eventId, $workstreamId, $enqueuedAt)
            ON CONFLICT(event_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$eventId", eventId.Value);
        cmd.Parameters.AddWithValue("$workstreamId", workstreamId.Value);
        cmd.Parameters.AddWithValue("$enqueuedAt", FormatTimestamp(enqueuedAt));
        cmd.ExecuteNonQuery();
    }

    internal static string FormatTimestamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
