using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest.Validation;
using Mneme.Storage;

namespace Mneme.Curation;

/// <summary>
/// SQLite-backed <see cref="IMemoryCurator"/>. Every operation appends a
/// row to <c>curation_events</c> — the underlying <c>memory_events</c>
/// table is never mutated, preserving the append-only invariant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stale-state guard:</strong> amend / split / merge re-compute
/// the target's canonical hash inside the same transaction that writes
/// the curation event, and throw <see cref="StaleProposalError"/> on
/// mismatch. Pattern from Letta <c>core_memory_replace</c>.
/// </para>
/// <para>
/// Split / merge are out of scope for this initial Phase 7.5 commit and
/// throw <see cref="NotImplementedException"/>; the contract surface is
/// still present so a follow-up can land them without touching callers.
/// </para>
/// </remarks>
public sealed class SqliteMemoryCurator : IMemoryCurator
{
    private readonly SqliteConnectionFactory _connections;
    private readonly TimeProvider _clock;

    /// <summary>Construct against the shared connection factory.</summary>
    public SqliteMemoryCurator(SqliteConnectionFactory connections)
        : this(connections, TimeProvider.System) { }

    /// <summary>Construct with a custom clock (tests).</summary>
    public SqliteMemoryCurator(SqliteConnectionFactory connections, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _clock = clock;
    }

    /// <inheritdoc/>
    public Task<CurationResult> AmendFactAsync(FactId target, string preStateHash, FactAmendment amendment,
        CurationCapability cap, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(amendment);
        if (string.IsNullOrWhiteSpace(amendment.NewContent))
            throw new ArgumentException("amendment.NewContent is required.", nameof(amendment));
        Authorize(cap, c => c.CanAmend, nameof(CurationCapability.CanAmend));
        var eventId = new EventId(target.Value);
        return AppendCuration(eventId, cap, CurationType.Amended,
            amendment.Rationale, preStateHash, JsonSerializer.Serialize(amendment, JsonOptions));
    }

    /// <inheritdoc/>
    public Task<CurationResult> AnnotateAsync(EventId target, string commentary,
        CurationCapability cap, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commentary))
            throw new ArgumentException("commentary is required.", nameof(commentary));
        Authorize(cap, c => c.CanAnnotate, nameof(CurationCapability.CanAnnotate));
        return AppendCuration(target, cap, CurationType.Annotated,
            commentary, preStateHash: null,
            JsonSerializer.Serialize(new { commentary }, JsonOptions));
    }

    /// <inheritdoc/>
    public Task<CurationResult> PinAsync(EventId target, PinScope scope, float weightMultiplier,
        CurationCapability cap, CancellationToken ct = default)
    {
        if (weightMultiplier <= 1.0f)
            throw new ArgumentException("Pin multiplier must be > 1.0 to be meaningful.", nameof(weightMultiplier));
        Authorize(cap, c => c.CanPin, nameof(CurationCapability.CanPin));
        return AppendCuration(target, cap, CurationType.Pinned,
            $"pin scope={scope}", preStateHash: null,
            JsonSerializer.Serialize(new { multiplier = (double)weightMultiplier, scope = scope.ToString() }, JsonOptions));
    }

    /// <inheritdoc/>
    public Task<CurationResult> DemoteAsync(EventId target, float weightMultiplier,
        CurationCapability cap, CancellationToken ct = default)
    {
        if (weightMultiplier <= 0.0f || weightMultiplier >= 1.0f)
            throw new ArgumentException("Demote multiplier must be in (0.0, 1.0) to be meaningful.", nameof(weightMultiplier));
        Authorize(cap, c => c.CanDemote, nameof(CurationCapability.CanDemote));
        return AppendCuration(target, cap, CurationType.Demoted,
            "demote", preStateHash: null,
            JsonSerializer.Serialize(new { multiplier = (double)weightMultiplier }, JsonOptions));
    }

    /// <inheritdoc/>
    public Task<CurationResult> SplitFactAsync(FactId source, IReadOnlyList<FactSplitPart> parts, string preStateHash,
        CurationCapability cap, CancellationToken ct = default) =>
        throw new NotImplementedException("SplitFactAsync arrives in a Phase 7.5 follow-up commit.");

    /// <inheritdoc/>
    public Task<CurationResult> MergeFactsAsync(IReadOnlyList<FactId> sources, FactMerged target, string preStateHash,
        CurationCapability cap, CancellationToken ct = default) =>
        throw new NotImplementedException("MergeFactsAsync arrives in a Phase 7.5 follow-up commit.");

    /// <inheritdoc/>
    public Task<CurationResult> RevertCurationAsync(EventId curationEventId, string reason,
        CurationCapability cap, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required.", nameof(reason));
        Authorize(cap, c => c.CanRevert, nameof(CurationCapability.CanRevert));
        if (!curationEventId.HasValue)
            throw new ArgumentException("curationEventId is required.", nameof(curationEventId));

        using var connection = _connections.Open();
        using var tx = connection.BeginTransaction();

        // The curation being reverted must exist, not already be a
        // curation.reverted, and not already be reverted by something else.
        string targetEventId;
        int curationTypeInt;
        string? alreadyRevertedBy;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = tx;
            lookup.CommandText = """
                SELECT target_event_id, curation_type, reverted_by
                FROM curation_events WHERE event_id = $id;
                """;
            lookup.Parameters.AddWithValue("$id", curationEventId.Value);
            using var rd = lookup.ExecuteReader();
            if (!rd.Read())
            {
                throw new InvalidOperationException($"No curation event with id '{curationEventId.Value}'.");
            }
            targetEventId = rd.GetString(0);
            curationTypeInt = rd.GetInt32(1);
            alreadyRevertedBy = rd.IsDBNull(2) ? null : rd.GetString(2);
        }
        if ((CurationType)curationTypeInt == CurationType.Reverted)
        {
            throw new InvalidOperationException("Cannot revert a curation.reverted event. Issue a fresh curation instead.");
        }
        if (alreadyRevertedBy is not null)
        {
            throw new InvalidOperationException($"Curation '{curationEventId.Value}' is already reverted by '{alreadyRevertedBy}'.");
        }

        var newEventId = new EventId(NewCurationId());
        var nowUtc = _clock.GetUtcNow();

        // Workstream is inferred from the target event.
        string workstreamId;
        using (var wsLookup = connection.CreateCommand())
        {
            wsLookup.Transaction = tx;
            wsLookup.CommandText = "SELECT workstream_id FROM curation_events WHERE event_id = $id;";
            wsLookup.Parameters.AddWithValue("$id", curationEventId.Value);
            workstreamId = (wsLookup.ExecuteScalar() as string) ?? throw new InvalidOperationException("workstream lookup failed");
        }

        InsertCurationEvent(connection, tx,
            eventId: newEventId,
            targetEventId: new EventId(curationEventId.Value),
            workstreamId: workstreamId,
            curationType: CurationType.Reverted,
            curator: cap.Principal,
            rationale: reason,
            occurredAt: nowUtc,
            preStateHash: string.Empty,
            payloadJson: JsonSerializer.Serialize(new { reason, reverts = curationEventId.Value }, JsonOptions));

        using (var markRev = connection.CreateCommand())
        {
            markRev.Transaction = tx;
            markRev.CommandText = "UPDATE curation_events SET reverted_by = $newId WHERE event_id = $id;";
            markRev.Parameters.AddWithValue("$newId", newEventId.Value);
            markRev.Parameters.AddWithValue("$id", curationEventId.Value);
            markRev.ExecuteNonQuery();
        }

        tx.Commit();
        return Task.FromResult(new CurationResult(newEventId, nowUtc, string.Empty));
    }

    private Task<CurationResult> AppendCuration(EventId target, CurationCapability cap,
        CurationType type, string rationale, string? preStateHash, string payloadJson)
    {
        if (!target.HasValue) throw new ArgumentException("Target id required.", nameof(target));
        if (cap.Workstream is { } scope) WorkstreamIdValidator.EnsureValid(scope.Value, "cap.Workstream");

        using var connection = _connections.Open();
        using var tx = connection.BeginTransaction();

        // Verify the target exists + capture its workstream.
        string workstreamId;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = tx;
            lookup.CommandText = "SELECT workstream_id FROM memory_events WHERE event_id = $id;";
            lookup.Parameters.AddWithValue("$id", target.Value);
            workstreamId = (lookup.ExecuteScalar() as string)
                ?? throw new InvalidOperationException($"No event with id '{target.Value}'.");
        }

        // Capability workstream scope check.
        if (cap.Workstream is { } cws && cws.Value != workstreamId)
        {
            throw new CapabilityDeniedError(
                $"curation capability scoped to '{cws.Value}'; target event is in '{workstreamId}'");
        }

        // Stale-state guard for amend / split / merge.
        if (preStateHash is not null)
        {
            var actual = PreStateHasher.ComputeHash(connection, tx, target);
            if (!string.Equals(actual, preStateHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new StaleProposalError(target, preStateHash, actual);
            }
        }

        var newEventId = new EventId(NewCurationId());
        var nowUtc = _clock.GetUtcNow();
        InsertCurationEvent(connection, tx,
            eventId: newEventId,
            targetEventId: target,
            workstreamId: workstreamId,
            curationType: type,
            curator: cap.Principal,
            rationale: rationale,
            occurredAt: nowUtc,
            preStateHash: preStateHash ?? string.Empty,
            payloadJson: payloadJson);

        tx.Commit();
        return Task.FromResult(new CurationResult(newEventId, nowUtc, preStateHash ?? string.Empty));
    }

    private static void InsertCurationEvent(SqliteConnection connection, SqliteTransaction tx,
        EventId eventId, EventId targetEventId, string workstreamId, CurationType curationType,
        PrincipalId curator, string rationale, DateTimeOffset occurredAt,
        string preStateHash, string payloadJson)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO curation_events(
                event_id, target_event_id, workstream_id, curation_type,
                curator, rationale, occurred_at, pre_state_hash, payload_json, reverted_by)
            VALUES ($id, $tid, $ws, $type, $curator, $rationale, $at, $hash, $payload, NULL);
            """;
        cmd.Parameters.AddWithValue("$id", eventId.Value);
        cmd.Parameters.AddWithValue("$tid", targetEventId.Value);
        cmd.Parameters.AddWithValue("$ws", workstreamId);
        cmd.Parameters.AddWithValue("$type", (int)curationType);
        cmd.Parameters.AddWithValue("$curator", curator.Value);
        cmd.Parameters.AddWithValue("$rationale", rationale);
        cmd.Parameters.AddWithValue("$at", occurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$hash", preStateHash);
        cmd.Parameters.AddWithValue("$payload", payloadJson);
        cmd.ExecuteNonQuery();
    }

    private static void Authorize(CurationCapability cap, Func<CurationCapability, bool> predicate, string flagName)
    {
        ArgumentNullException.ThrowIfNull(cap);
        if (!cap.IsValidAt(DateTimeOffset.UtcNow))
        {
            throw new CapabilityDeniedError(
                $"curation capability validity window [{cap.NotBefore:O}..{cap.NotAfter:O}] is closed");
        }
        if (!predicate(cap))
        {
            throw new CapabilityDeniedError($"{flagName} not granted on curation capability");
        }
    }

    private static string NewCurationId() => "cur-" + Guid.NewGuid().ToString("N");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
