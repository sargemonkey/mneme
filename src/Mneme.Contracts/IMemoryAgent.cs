namespace Mneme.Contracts;

/// <summary>
/// The ingest side of Mneme. A single subscriber per workstream consumes the
/// capture stream and persists events into the bi-temporal event log.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Sync-stage contract:</strong> <see cref="IngestAsync"/> returns
/// after the synchronous stages complete — validate → redact → cheap
/// classify → WAL commit. Target latency &lt; 50ms. The expensive stages
/// (LLM-based extraction, entity resolution, projection update,
/// reconciliation, synthesis, indexing) run in a separate
/// <c>DistillationJob</c> worker after this call returns.
/// </para>
/// <para>
/// This split is a locked design decision: Mem0 v2 → v3 dropped sync
/// invalidation and gained +20 LoCoMo points. See
/// <c>plans/research-design-lessons.md</c> §3.2 + §4.2.
/// </para>
/// <para>
/// Ingest is idempotent on <see cref="CaptureEvent.EventId"/>: ingesting the
/// same event twice is a no-op and returns
/// <see cref="IngestResult.WasDuplicate"/> = <c>true</c>.
/// </para>
/// </remarks>
public interface IMemoryAgent
{
    /// <summary>
    /// Persist the event into the WAL after sync-stage processing. Returns
    /// quickly; distillation runs asynchronously in the worker.
    /// </summary>
    /// <param name="evt">The event to ingest. Must have a non-empty <see cref="CaptureEvent.EventId"/>.</param>
    /// <param name="ct">Cancellation token. If signalled before WAL commit, the call may complete or throw <see cref="OperationCanceledException"/>; behavior after WAL commit is undefined and the event is durable regardless.</param>
    /// <returns>The result of the sync stages.</returns>
    /// <exception cref="ArgumentException">If <paramref name="evt"/> fails validation (e.g., empty workstream or event id, missing payload).</exception>
    Task<IngestResult> IngestAsync(CaptureEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Distill the slice of an agent session's context that hasn't yet been
    /// distilled. Routes the entries strictly after the prior watermark
    /// through the host-supplied <see cref="ISessionDistiller"/>, ingests
    /// any events it produces with a <see cref="Citation.SessionRange"/>
    /// stamp, and atomically advances the watermark.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Idempotent on (session, from-entry-id, to-entry-id).</strong>
    /// Re-calling with the same range is a no-op and returns the existing
    /// watermark with <see cref="DistillSessionResult.WasNoOp"/> = <c>true</c>.
    /// </para>
    /// <para>
    /// The host is responsible for retaining its own chat history; Mneme
    /// stores no copy. The <see cref="Citation.SessionRange"/> stamped on
    /// each event lets the host re-resolve the source entries on demand.
    /// </para>
    /// <para>
    /// Throws <see cref="InvalidOperationException"/> if no
    /// <see cref="ISessionDistiller"/> is registered with the host.
    /// </para>
    /// </remarks>
    /// <param name="session">Session whose context is being distilled.</param>
    /// <param name="entries">Entries to consider — typically everything in the session strictly after the last watermark. The SDK additionally filters out anything at-or-before the persisted watermark.</param>
    /// <param name="capability">Capability token authorising the workstream the events will land in.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The events produced, the new watermark, and an optional audit list of dropped entries.</returns>
    Task<DistillSessionResult> DistillSessionAsync(
        SessionId session,
        IReadOnlyList<ContextEntry> entries,
        CapabilityToken capability,
        CancellationToken ct = default);

    /// <summary>
    /// Return the current distillation watermark for <paramref name="session"/>,
    /// or <c>null</c> if the session has never been distilled.
    /// </summary>
    Task<ContextWatermark?> GetWatermarkAsync(SessionId session, CancellationToken ct = default);
}
