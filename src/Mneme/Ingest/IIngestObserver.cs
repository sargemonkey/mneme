namespace Mneme.Ingest;

/// <summary>
/// Called by <see cref="MemoryAgent"/> after every <em>new</em> event has
/// been committed to the WAL. Observers run synchronously inside
/// <c>IngestAsync</c> — keep them fast.
/// </summary>
/// <remarks>
/// Default Mneme observers (Phase 3+) include projection updates and
/// text-index maintenance. A no-observer agent preserves the &lt;50ms
/// p99 ingest invariant from Phase 1.
/// </remarks>
public interface IIngestObserver
{
    /// <summary>Invoked once per newly-committed event (skipped for duplicates).</summary>
    void OnIngested(Mneme.Projections.EventEnvelope envelope);
}
