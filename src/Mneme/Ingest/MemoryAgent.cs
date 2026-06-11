using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest.Distillation;
using Mneme.Ingest.Redaction;
using Mneme.Ingest.Validation;
using Mneme.Observability;
using Mneme.Storage;

namespace Mneme.Ingest;

/// <summary>
/// Phase 1 implementation of <see cref="IMemoryAgent"/>. Runs the sync
/// stages of ingest — validate → redact → classify (stub) → persist —
/// inside a single SQLite transaction. Returns after the WAL commit;
/// the LLM-driven distillation work happens asynchronously when a
/// future-phase worker drains <c>distillation_queue</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ingest is idempotent on <see cref="CaptureEvent.EventId"/>. A second
/// ingest of the same id is a no-op: the original row stays, the
/// distillation queue is not re-enqueued, and the call returns with
/// <see cref="IngestResult.WasDuplicate"/> = <c>true</c>.
/// </para>
/// <para>
/// Classification is a placeholder in Phase 1 — every ingest goes
/// through the <see cref="MnemeActivitySource.ClassifyRun"/> span but
/// the synchronous classifier just labels by category. The Phase 2
/// classifier replaces this surface without changing the call site.
/// </para>
/// </remarks>
public sealed class MemoryAgent : IMemoryAgent
{
    private readonly SqliteConnectionFactory _connections;
    private readonly IRedactor _redactor;
    private readonly IContentShapeSelector _shapeSelector;
    private readonly Classification.IClassifier _classifier;
    private readonly TimeProvider _clock;

    /// <summary>Construct against the storage layer with default helpers.</summary>
    public MemoryAgent(SqliteConnectionFactory connections)
        : this(connections, new RegexRedactor(), new AlwaysRedactedContent(),
               new Classification.RuleBasedClassifier(), TimeProvider.System)
    { }

    /// <summary>Construct against the storage layer with custom helpers (used by tests / DI).</summary>
    public MemoryAgent(
        SqliteConnectionFactory connections,
        IRedactor redactor,
        IContentShapeSelector shapeSelector,
        Classification.IClassifier classifier,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(shapeSelector);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _redactor = redactor;
        _shapeSelector = shapeSelector;
        _classifier = classifier;
        _clock = clock;
    }

    /// <inheritdoc/>
    public async Task<IngestResult> IngestAsync(CaptureEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ct.ThrowIfCancellationRequested();

        ValidateEnvelope(evt);

        using var activity = MnemeActivitySource.Source.StartActivity(
            MnemeActivitySource.IngestEvent, ActivityKind.Internal);
        activity?.SetTag("mneme.workstream_id", evt.WorkstreamId.Value);
        activity?.SetTag("mneme.event_id", evt.EventId.Value);
        activity?.SetTag("mneme.event_channel", (int)evt.Channel);
        activity?.SetTag("mneme.category", (int)evt.Payload.Category);

        // Redact in its own span so the cost is attributable.
        EventPayload redactedPayload;
        bool hadHits;
        using (var redactSpan = MnemeActivitySource.Source.StartActivity(
            MnemeActivitySource.RedactorRun, ActivityKind.Internal))
        {
            var (p, h, count) = PayloadRedactor.Redact(evt.Payload, _redactor);
            redactedPayload = p;
            hadHits = h;
            redactSpan?.SetTag("mneme.redactor.hit_count", count);
        }
        activity?.SetTag("mneme.redactor.had_hits", hadHits);

        Mneme.Contracts.Classification label;
        using (var classifySpan = MnemeActivitySource.Source.StartActivity(
            MnemeActivitySource.ClassifyRun, ActivityKind.Internal))
        {
            label = await _classifier.ClassifyAsync(
                PayloadText(redactedPayload), hadHits, redactedPayload.Category, ct)
                .ConfigureAwait(false);
            classifySpan?.SetTag("mneme.classify.label", label.ToString());
        }
        activity?.SetTag("mneme.classification", (int)label);

        var shape = _shapeSelector.Select(evt);
        activity?.SetTag("mneme.content_shape", (int)shape);

        var nowUtc = _clock.GetUtcNow();
        var record = new EventRecord(
            EventId: evt.EventId,
            WorkstreamId: evt.WorkstreamId,
            Channel: evt.Channel,
            Category: redactedPayload.Category,
            SchemaVersion: evt.SchemaVersion,
            ValidAt: evt.ValidAt,
            InvalidAt: null,
            CreatedAt: nowUtc,
            ExpiredAt: null,
            PayloadJson: EventSerialization.SerializePayload(redactedPayload),
            ProvenanceJson: EventSerialization.SerializeProvenance(evt.Provenance),
            Shape: shape,
            Classification: label,
            ArtifactId: null);

        var wasDuplicate = Persist(record);
        activity?.SetTag("mneme.ingest.duplicate", wasDuplicate);

        return new IngestResult(evt.EventId, nowUtc, wasDuplicate);
    }

    private static string PayloadText(EventPayload p) => p switch
    {
        EvidencePayload e   => e.Content,
        FactPayload f       => f.Statement,
        DecisionPayload d   => d.Statement + "\n" + d.Rationale,
        HypothesisPayload h => h.Statement,
        GoalPayload g       => g.Statement,
        ActionPayload a     => a.Statement,
        OutcomePayload o    => o.Statement,
        _                   => string.Empty,
    };

    private static void ValidateEnvelope(CaptureEvent evt)
    {
        if (!evt.EventId.HasValue)
        {
            throw new ArgumentException("EventId is required.", nameof(evt));
        }
        WorkstreamIdValidator.EnsureValid(evt.WorkstreamId.Value, "evt.WorkstreamId");
        if (evt.Payload is null)
        {
            throw new ArgumentException("Payload is required.", nameof(evt));
        }
        if (evt.SchemaVersion < 1)
        {
            throw new ArgumentException("SchemaVersion must be >= 1.", nameof(evt));
        }
    }

    private bool Persist(EventRecord r)
    {
        using var connection = _connections.Open();
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO memory_events(
                event_id, workstream_id, event_channel, category,
                schema_version, valid_at, invalid_at, created_at, expired_at,
                payload_json, provenance_json, content_shape, classification, artifact_id)
            VALUES (
                $eventId, $workstreamId, $channel, $category,
                $schemaVersion, $validAt, $invalidAt, $createdAt, $expiredAt,
                $payloadJson, $provenanceJson, $contentShape, $classification, $artifactId)
            ON CONFLICT(event_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$eventId", r.EventId.Value);
        cmd.Parameters.AddWithValue("$workstreamId", r.WorkstreamId.Value);
        cmd.Parameters.AddWithValue("$channel", (int)r.Channel);
        cmd.Parameters.AddWithValue("$category", (int)r.Category);
        cmd.Parameters.AddWithValue("$schemaVersion", r.SchemaVersion);
        cmd.Parameters.AddWithValue("$validAt", FormatTimestamp(r.ValidAt));
        cmd.Parameters.AddWithValue("$invalidAt", r.InvalidAt.HasValue
            ? FormatTimestamp(r.InvalidAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", FormatTimestamp(r.CreatedAt));
        cmd.Parameters.AddWithValue("$expiredAt", r.ExpiredAt.HasValue
            ? FormatTimestamp(r.ExpiredAt.Value) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$payloadJson", r.PayloadJson);
        cmd.Parameters.AddWithValue("$provenanceJson", r.ProvenanceJson);
        cmd.Parameters.AddWithValue("$contentShape", (int)r.Shape);
        cmd.Parameters.AddWithValue("$classification", (int)r.Classification);
        cmd.Parameters.AddWithValue("$artifactId", (object?)r.ArtifactId ?? DBNull.Value);
        var inserted = cmd.ExecuteNonQuery();

        if (inserted > 0)
        {
            using var enq = connection.CreateCommand();
            enq.Transaction = tx;
            enq.CommandText = """
                INSERT INTO distillation_queue(event_id, workstream_id, enqueued_at)
                VALUES ($eventId, $workstreamId, $enqueuedAt)
                ON CONFLICT(event_id) DO NOTHING;
                """;
            enq.Parameters.AddWithValue("$eventId", r.EventId.Value);
            enq.Parameters.AddWithValue("$workstreamId", r.WorkstreamId.Value);
            enq.Parameters.AddWithValue("$enqueuedAt", FormatTimestamp(r.CreatedAt));
            enq.ExecuteNonQuery();
        }

        tx.Commit();
        return inserted == 0;
    }

    internal static string FormatTimestamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record EventRecord(
        EventId EventId,
        WorkstreamId WorkstreamId,
        EventChannel Channel,
        EpistemicCategory Category,
        int SchemaVersion,
        DateTimeOffset ValidAt,
        DateTimeOffset? InvalidAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ExpiredAt,
        string PayloadJson,
        string ProvenanceJson,
        ContentShape Shape,
        Mneme.Contracts.Classification Classification,
        string? ArtifactId);
}
