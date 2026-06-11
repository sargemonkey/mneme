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
}
