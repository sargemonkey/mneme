using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Observability;
using Mneme.Projections.Projectors;
using Mneme.Storage;

namespace Mneme.Projections;

/// <summary>
/// Drives a set of <see cref="IProjector"/>s against a Mneme database.
/// Two entry points: <see cref="ProcessEvent(EventId)"/> for the
/// incremental post-ingest path, and <see cref="RebuildAll"/> for the
/// disaster-recovery path that replays the entire event log. Both
/// paths write to <c>event_processing_log</c> so a single projection
/// can be re-run without rebuilding the others.
/// </summary>
public sealed class ProjectorPipeline
{
    private readonly SqliteConnectionFactory _connections;
    private readonly IReadOnlyList<IProjector> _projectors;

    /// <summary>Construct with the default Phase 3 projector set.</summary>
    public ProjectorPipeline(SqliteConnectionFactory connections)
        : this(connections, DefaultProjectors) { }

    /// <summary>Construct with a custom projector list (tests / extensions).</summary>
    public ProjectorPipeline(SqliteConnectionFactory connections, IEnumerable<IProjector> projectors)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(projectors);
        _connections = connections;
        _projectors = projectors.ToArray();
    }

    /// <summary>The default Phase 3 projectors: Facts, Decisions, Goals, Hypotheses.</summary>
    public static IReadOnlyList<IProjector> DefaultProjectors { get; } = new IProjector[]
    {
        new FactsProjector(),
        new FactTriplesProjector(),
        new ContradictionsProjector(),
        new DuplicateFactsProjector(),
        new DecisionsProjector(),
        new GoalsProjector(),
        new HypothesesProjector(),
        new SkillsProjector(),
    };

    /// <summary>Apply every projector that matches the event's category. Idempotent.</summary>
    public void ProcessEvent(EventId eventId)
    {
        using var connection = _connections.Open();
        using var tx = connection.BeginTransaction();
        var envelope = EventEnvelopeReader.ReadOne(connection, tx, eventId)
            ?? throw new InvalidOperationException($"Event '{eventId.Value}' not found in memory_events.");
        ApplyMatching(connection, tx, envelope);
        tx.Commit();
    }

    /// <summary>Apply every projector that matches the envelope's category. Idempotent.</summary>
    public void ProcessEvent(EventEnvelope envelope)
    {
        using var connection = _connections.Open();
        using var tx = connection.BeginTransaction();
        ApplyMatching(connection, tx, envelope);
        tx.Commit();
    }

    private void ApplyMatching(SqliteConnection c, SqliteTransaction tx, EventEnvelope envelope)
    {
        foreach (var p in _projectors)
        {
            if (!p.MatchesCategory(envelope.Category)) continue;
            using var activity = MnemeActivitySource.Source.StartActivity(
                MnemeActivitySource.ProjectionRebuild, ActivityKind.Internal);
            activity?.SetTag("mneme.projection.name", p.Name);
            activity?.SetTag("mneme.projection.event_id", envelope.EventId.Value);
            try
            {
                p.Apply(c, tx, envelope);
                LogStatus(c, tx, envelope.EventId, p.Name, ProcessingStatus.Applied, null);
            }
            catch (Exception ex)
            {
                LogStatus(c, tx, envelope.EventId, p.Name, ProcessingStatus.Failed, ex.Message);
                throw;
            }
        }
    }

    /// <summary>Rebuild every projection from scratch. Returns row counts per projection.</summary>
    public IDictionary<string, int> RebuildAll()
    {
        var results = new Dictionary<string, int>();
        using var connection = _connections.Open();
        using var tx = connection.BeginTransaction();
        foreach (var p in _projectors)
        {
            using var activity = MnemeActivitySource.Source.StartActivity(
                MnemeActivitySource.ProjectionRebuild, ActivityKind.Internal);
            activity?.SetTag("mneme.projection.name", p.Name);
            activity?.SetTag("mneme.projection.mode", "rebuild");
            results[p.Name] = p.Rebuild(connection, tx);
        }
        // Reset processing log entries for the rebuilt projections.
        using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM event_processing_log WHERE projection_name IN ({string.Join(",", _projectors.Select((_, i) => $"$n{i}"))});";
            for (var i = 0; i < _projectors.Count; i++)
            {
                del.Parameters.AddWithValue($"$n{i}", _projectors[i].Name);
            }
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return results;
    }

    private static void LogStatus(SqliteConnection c, SqliteTransaction tx, EventId eventId,
        string projection, ProcessingStatus status, string? error)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO event_processing_log(event_id, projection_name, status, processed_at, error)
            VALUES ($eid, $name, $status, $at, $err)
            ON CONFLICT(event_id, projection_name) DO UPDATE SET
                status = excluded.status,
                processed_at = excluded.processed_at,
                error = excluded.error;
            """;
        cmd.Parameters.AddWithValue("$eid", eventId.Value);
        cmd.Parameters.AddWithValue("$name", projection);
        cmd.Parameters.AddWithValue("$status", (int)status);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Result of an <see cref="IProjector"/> applying a single event.</summary>
public enum ProcessingStatus
{
    /// <summary>Projector applied successfully.</summary>
    Applied = 0,
    /// <summary>Projector failed; <c>error</c> column carries the message.</summary>
    Failed = 1,
    /// <summary>Projector skipped this event (category mismatch).</summary>
    Skipped = 2,
}
