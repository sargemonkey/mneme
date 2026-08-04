using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest;
using Mneme.Projections;
using Mneme.Revocation;
using Mneme.Storage;

namespace Mneme.Review;

/// <summary>
/// SQLite-backed <see cref="IReviewQueue"/> — the human-in-the-loop gate for
/// workstreams running in <see cref="WorkstreamMode.ReviewBeforeDistill"/>.
/// </summary>
/// <remarks>
/// <para>
/// The gate lives at projection time, honoring the append-only invariant: an
/// epistemic event captured into a review-mode workstream is still written to
/// <c>memory_events</c> (the source of truth), but the projector/index
/// observers are <em>skipped</em>, so the event is invisible to
/// <see cref="IMemoryQueryAPI"/> until a curator acts. A row is parked in
/// <c>review_queue</c> with <c>status = 'pending'</c>.
/// </para>
/// <list type="bullet">
///   <item><see cref="ApproveAsync"/> replays the ingest observers for the event
///     (projecting + indexing it, making it queryable) and appends an
///     <c>event.review_approved</c> technical event for the audit trail.</item>
///   <item><see cref="RejectAsync"/> tombstones the source via
///     <see cref="IRevocationService"/> and appends an
///     <c>event.review_rejected</c> technical event.</item>
///   <item><see cref="DeferAsync"/> hides the item from
///     <see cref="GetPendingAsync"/> until a chosen instant.</item>
/// </list>
/// <para>
/// Every mutating call requires a <see cref="CurationCapability"/> with
/// <see cref="CurationCapability.CanReview"/>; the privilege boundary is
/// enforced here, never by callers.
/// </para>
/// </remarks>
public sealed class SqliteReviewQueue : IReviewQueue
{
    private const string ReviewSource = "mneme-review-queue";

    private readonly SqliteConnectionFactory _connections;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<IIngestObserver> _observers;
    private readonly IRevocationService _revocation;

    /// <summary>Construct against the shared connection factory.</summary>
    /// <param name="connections">Shared SQLite connection factory.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="observers">The same ingest observers the agent runs on the auto-distill path; replayed on approve so the event becomes queryable.</param>
    /// <param name="revocation">Revocation service used to tombstone rejected events.</param>
    public SqliteReviewQueue(
        SqliteConnectionFactory connections,
        TimeProvider clock,
        IEnumerable<IIngestObserver> observers,
        IRevocationService revocation)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(revocation);
        _connections = connections;
        _clock = clock;
        _observers = observers.ToArray();
        _revocation = revocation;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PendingReviewItem>> GetPendingAsync(
        WorkstreamId workstream, CurationCapability cap, CancellationToken ct = default)
    {
        EnsureCanReview(cap, workstream);
        ct.ThrowIfCancellationRequested();

        var now = _clock.GetUtcNow();
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        // Pending items, plus deferred items whose defer window has elapsed.
        cmd.CommandText = """
            SELECT event_id, workstream_id, captured_at, summary
            FROM review_queue
            WHERE workstream_id = $ws
              AND (status = 'pending'
                   OR (status = 'deferred' AND (defer_until IS NULL OR defer_until <= $now)))
            ORDER BY captured_at ASC;
            """;
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        cmd.Parameters.AddWithValue("$now", FormatTimestamp(now));

        var items = new List<PendingReviewItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new PendingReviewItem(
                EventId: new EventId(reader.GetString(0)),
                Workstream: new WorkstreamId(reader.GetString(1)),
                CapturedAt: ParseTimestamp(reader.GetString(2)),
                Summary: reader.GetString(3)));
        }
        return Task.FromResult<IReadOnlyList<PendingReviewItem>>(items);
    }

    /// <inheritdoc/>
    public Task ApproveAsync(EventId pending, CurationCapability cap, CancellationToken ct = default)
    {
        var (workstream, status) = RequireQueueRow(pending, cap);
        ct.ThrowIfCancellationRequested();
        if (status == "approved")
        {
            return Task.CompletedTask; // idempotent
        }

        // Reconstruct the persisted event and replay the ingest observers so it
        // is projected + indexed exactly as an auto-distill event would have been.
        EventEnvelope envelope;
        using (var connection = _connections.Open())
        {
            envelope = EventEnvelopeReader.ReadOne(connection, null, pending)
                ?? throw new InvalidOperationException(
                    $"Review-queued event '{pending.Value}' not found in memory_events.");
        }
        foreach (var observer in _observers)
        {
            observer.OnIngested(envelope);
        }

        var now = _clock.GetUtcNow();
        AppendReviewEvent("event.review_approved", pending, workstream, cap.Principal,
            $"Review approved for event {pending.Value}", now);
        UpdateQueueStatus(pending, "approved", cap.Principal, now, deferUntil: null, rationale: null);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task RejectAsync(EventId pending, string reason, CurationCapability cap, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Reason is required.", nameof(reason));
        }
        var (workstream, status) = RequireQueueRow(pending, cap);
        ct.ThrowIfCancellationRequested();
        if (status == "rejected")
        {
            return; // idempotent
        }

        // Tombstone the source event's artifact body; the event row + metadata stay for audit.
        await _revocation.RevokeAsync(pending, workstream, cap.Principal, reason, ct).ConfigureAwait(false);

        var now = _clock.GetUtcNow();
        AppendReviewEvent("event.review_rejected", pending, workstream, cap.Principal, reason, now);
        UpdateQueueStatus(pending, "rejected", cap.Principal, now, deferUntil: null, rationale: reason);
    }

    /// <inheritdoc/>
    public Task DeferAsync(EventId pending, DateTimeOffset until, CurationCapability cap, CancellationToken ct = default)
    {
        var (_, status) = RequireQueueRow(pending, cap);
        ct.ThrowIfCancellationRequested();
        if (status is "approved" or "rejected")
        {
            throw new InvalidOperationException(
                $"Cannot defer event '{pending.Value}': it is already {status}.");
        }
        UpdateQueueStatus(pending, "deferred", cap.Principal, reviewedAt: null, deferUntil: until, rationale: null);
        return Task.CompletedTask;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void EnsureCanReview(CurationCapability cap, WorkstreamId workstream)
    {
        ArgumentNullException.ThrowIfNull(cap);
        if (!cap.CanReview)
        {
            throw new CapabilityDeniedError("CanReview not granted");
        }
        if (!cap.IsValidAt(DateTimeOffset.UtcNow))
        {
            throw new CapabilityDeniedError("capability token is not valid at the current time");
        }
        // null workstream == instance-wide review rights.
        if (cap.Workstream is { } scoped && scoped != workstream)
        {
            throw new CapabilityDeniedError(
                $"capability is scoped to workstream '{scoped.Value}', not '{workstream.Value}'");
        }
    }

    /// <summary>Load the queue row for <paramref name="eventId"/>, capability-check it, and return (workstream, status).</summary>
    private (WorkstreamId Workstream, string Status) RequireQueueRow(EventId eventId, CurationCapability cap)
    {
        if (!eventId.HasValue)
        {
            throw new ArgumentException("EventId is required.", nameof(eventId));
        }
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT workstream_id, status FROM review_queue WHERE event_id = $eventId;";
        cmd.Parameters.AddWithValue("$eventId", eventId.Value);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                $"No review-queue entry for event '{eventId.Value}'.");
        }
        var workstream = new WorkstreamId(reader.GetString(0));
        var status = reader.GetString(1);
        EnsureCanReview(cap, workstream);
        return (workstream, status);
    }

    private void UpdateQueueStatus(
        EventId eventId, string status, PrincipalId reviewer,
        DateTimeOffset? reviewedAt, DateTimeOffset? deferUntil, string? rationale)
    {
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE review_queue
               SET status = $status,
                   reviewer_id = $reviewer,
                   reviewed_at = $reviewedAt,
                   defer_until = $deferUntil,
                   rationale = COALESCE($rationale, rationale)
             WHERE event_id = $eventId;
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$reviewer", reviewer.Value);
        cmd.Parameters.AddWithValue("$reviewedAt",
            reviewedAt.HasValue ? FormatTimestamp(reviewedAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$deferUntil",
            deferUntil.HasValue ? FormatTimestamp(deferUntil.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$rationale", (object?)rationale ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$eventId", eventId.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Append a <see cref="EventChannel.Technical"/> audit event (<c>event.review_approved</c>
    /// / <c>event.review_rejected</c>) so the reviewer's action is part of the append-only log.
    /// Written directly (not via the ingest gate) so it is never itself queued for review.
    /// </summary>
    private void AppendReviewEvent(
        string kind, EventId target, WorkstreamId workstream, PrincipalId reviewer,
        string statement, DateTimeOffset now)
    {
        var payload = new EvidencePayload(
            Content: $"[{kind}] {statement}",
            Source: ReviewSource,
            Classification: Contracts.Classification.Internal);
        var provenance = new CaptureProvenance(
            Source: new CaptureSourceId(ReviewSource),
            Principal: reviewer,
            Context: target.Value,
            Citation: null);

        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_events(
                event_id, workstream_id, event_channel, category,
                schema_version, valid_at, invalid_at, created_at, expired_at,
                payload_json, provenance_json, content_shape, classification,
                principal_id, artifact_id)
            VALUES (
                $eventId, $workstreamId, $channel, $category,
                1, $now, NULL, $now, NULL,
                $payloadJson, $provenanceJson, 0, $classification,
                $principalId, NULL)
            ON CONFLICT(event_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$eventId",
            $"{kind}:{target.Value}:{now.UtcDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        cmd.Parameters.AddWithValue("$workstreamId", workstream.Value);
        cmd.Parameters.AddWithValue("$channel", (int)EventChannel.Technical);
        cmd.Parameters.AddWithValue("$category", (int)EpistemicCategory.Evidence);
        cmd.Parameters.AddWithValue("$now", FormatTimestamp(now));
        cmd.Parameters.AddWithValue("$payloadJson", EventSerialization.SerializePayload(payload));
        cmd.Parameters.AddWithValue("$provenanceJson", EventSerialization.SerializeProvenance(provenance));
        cmd.Parameters.AddWithValue("$classification", (int)Contracts.Classification.Internal);
        cmd.Parameters.AddWithValue("$principalId", provenance.Principal.Value);
        cmd.ExecuteNonQuery();
    }

    internal static string FormatTimestamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string v) =>
        DateTimeOffset.Parse(v, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
}
