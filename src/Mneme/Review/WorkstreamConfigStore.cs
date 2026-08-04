using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest.Validation;
using Mneme.Storage;

namespace Mneme.Review;

/// <summary>
/// Reads and writes per-workstream configuration — currently just the
/// <see cref="WorkstreamMode"/> that decides whether ingested epistemic events
/// are projected immediately (<see cref="WorkstreamMode.AutoDistill"/>, the
/// default) or held in the pre-distillation <c>review_queue</c> until a curator
/// approves them (<see cref="WorkstreamMode.ReviewBeforeDistill"/>).
/// </summary>
/// <remarks>
/// The mode lives on workstream metadata (the <c>workstream_config</c> table),
/// not on the event log — it is configuration, not history. A workstream with
/// no row reads as <see cref="WorkstreamMode.AutoDistill"/>, so existing
/// deployments keep today's behavior with zero migration work.
/// </remarks>
public sealed class WorkstreamConfigStore
{
    private readonly SqliteConnectionFactory _connections;
    private readonly TimeProvider _clock;

    /// <summary>Construct against the shared connection factory.</summary>
    public WorkstreamConfigStore(SqliteConnectionFactory connections)
        : this(connections, TimeProvider.System) { }

    /// <summary>Construct against the shared connection factory with a custom clock (tests).</summary>
    public WorkstreamConfigStore(SqliteConnectionFactory connections, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _clock = clock;
    }

    /// <summary>
    /// The configured <see cref="WorkstreamMode"/> for <paramref name="workstream"/>.
    /// Returns <see cref="WorkstreamMode.AutoDistill"/> when the workstream has no
    /// explicit configuration row.
    /// </summary>
    public WorkstreamMode GetMode(WorkstreamId workstream)
    {
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT mode FROM workstream_config WHERE workstream_id = $ws;";
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull
            ? WorkstreamMode.AutoDistill
            : (WorkstreamMode)Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Set (upsert) the <see cref="WorkstreamMode"/> for <paramref name="workstream"/>.</summary>
    public void SetMode(WorkstreamId workstream, WorkstreamMode mode)
    {
        WorkstreamIdValidator.EnsureValid(workstream.Value, nameof(workstream));
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workstream_config(workstream_id, mode, updated_at)
            VALUES ($ws, $mode, $updatedAt)
            ON CONFLICT(workstream_id) DO UPDATE SET
                mode = excluded.mode,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        cmd.Parameters.AddWithValue("$mode", (int)mode);
        cmd.Parameters.AddWithValue("$updatedAt",
            _clock.GetUtcNow().UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Whether <paramref name="workstream"/> has opted in to being mined by the
    /// cross-workstream consolidation ("fleet dreaming") pass (ADR-0004). Defaults
    /// to <c>false</c> — a workstream is never mined for the global skill library
    /// unless it explicitly opts in.
    /// </summary>
    public bool GetParticipatesInCrossWorkstreamConsolidation(WorkstreamId workstream)
    {
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT participates_in_cross_workstream_consolidation
            FROM workstream_config WHERE workstream_id = $ws;
            """;
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        var result = cmd.ExecuteScalar();
        return result is not (null or DBNull)
            && Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Set (upsert) the cross-workstream-consolidation opt-in for <paramref name="workstream"/>.</summary>
    public void SetParticipatesInCrossWorkstreamConsolidation(WorkstreamId workstream, bool participates)
    {
        WorkstreamIdValidator.EnsureValid(workstream.Value, nameof(workstream));
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workstream_config(workstream_id, mode, updated_at,
                participates_in_cross_workstream_consolidation)
            VALUES ($ws, 0, $updatedAt, $participates)
            ON CONFLICT(workstream_id) DO UPDATE SET
                participates_in_cross_workstream_consolidation = excluded.participates_in_cross_workstream_consolidation,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        cmd.Parameters.AddWithValue("$participates", participates ? 1 : 0);
        cmd.Parameters.AddWithValue("$updatedAt",
            _clock.GetUtcNow().UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
