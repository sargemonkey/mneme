namespace Mneme.Contracts;

/// <summary>
/// The wire envelope for an event being ingested into Mneme. Designed so the
/// consumer side (cockpit, plugin, third-party host) can construct one without
/// knowing anything about Mneme's storage layer.
/// </summary>
/// <param name="EventId">Globally unique id. Idempotent insertion key — repeated
/// ingest with the same id is a no-op. Producers should use ULIDs.</param>
/// <param name="WorkstreamId">Workstream this event belongs to. Required.</param>
/// <param name="Channel">Whether this is a first-class epistemic event or technical bookkeeping.</param>
/// <param name="ValidAt">When the claim was true / observed in the world. Half of the bi-temporal pair.</param>
/// <param name="RecordedAt">When the producer captured the claim. Half of the bi-temporal pair. May differ from <paramref name="ValidAt"/> for backfilled or delayed observations.</param>
/// <param name="Payload">Typed payload — one of the seven <see cref="EventPayload"/> subtypes.</param>
/// <param name="Provenance">Where this event came from: source, principal, capture context.</param>
/// <param name="SchemaVersion">Version of the event schema. Defaults to <c>1</c>. Bumped only when the wire shape changes incompatibly.</param>
public sealed record CaptureEvent(
    EventId EventId,
    WorkstreamId WorkstreamId,
    EventChannel Channel,
    DateTimeOffset ValidAt,
    DateTimeOffset RecordedAt,
    EventPayload Payload,
    CaptureProvenance Provenance,
    int SchemaVersion = 1);

/// <summary>
/// Records who produced an event and through what capture source. Survives
/// for the lifetime of the event; never revoked even if content is redacted.
/// </summary>
/// <param name="Source">The capture source identifier (e.g., plugin name, agent name).</param>
/// <param name="Principal">The principal acting at the source. May be a human or an agent.</param>
/// <param name="Context">Free-text context (e.g., session id, request id). Useful for grouping and debugging.</param>
/// <param name="Citation">Optional polymorphic pointer back to the source signal so "why does memory say X?" stays answerable. <see cref="Citation.SessionRange"/> for events produced by session distillation; <see cref="Citation.Manual"/> / <see cref="Citation.Workflow"/> / <see cref="Citation.External"/> for direct-ingest events. <c>null</c> is permitted for un-cited events.</param>
public sealed record CaptureProvenance(
    CaptureSourceId Source,
    PrincipalId Principal,
    string? Context = null,
    Citation? Citation = null);

/// <summary>
/// Identifies a capture source (plugin, agent, manual entry). Opaque string;
/// shape is consumer-defined.
/// </summary>
/// <param name="Value">The source identifier. Recommended format: lowercase kebab-case.</param>
public readonly record struct CaptureSourceId(string Value)
{
    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// Returned by <see cref="IMemoryAgent.IngestAsync"/> after sync ingest stages
/// (validate → redact → classify → WAL commit) complete. Async distillation
/// runs after the call returns; observers should subscribe to bundle updates
/// via the appropriate MCP resource or projection query.
/// </summary>
/// <param name="EventId">The id of the persisted event (echoes back the input).</param>
/// <param name="RecordedAt">When Mneme's WAL committed the event. May differ from the producer's <see cref="CaptureEvent.RecordedAt"/>.</param>
/// <param name="WasDuplicate">True if an event with this id was already in the log (idempotent insert).</param>
public sealed record IngestResult(
    EventId EventId,
    DateTimeOffset RecordedAt,
    bool WasDuplicate = false);
