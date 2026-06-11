using Mneme.Contracts;

namespace Mneme.Ingest.Distillation;

/// <summary>
/// The hand-off between the sync ingest stage and the async distillation
/// worker. The Phase 1 implementation is a SQLite-backed outbox table
/// (<c>distillation_queue</c>); the Phase 5 worker drains it. Keeping
/// this interface explicit means later phases can swap in a different
/// queue (in-memory channel, Service Bus, etc.) without changing
/// <see cref="MemoryAgent"/>.
/// </summary>
public interface IDistillationQueue
{
    /// <summary>
    /// Enqueue an event for downstream distillation. Idempotent on
    /// <see cref="CaptureEvent.EventId"/>: a re-ingested duplicate
    /// event does not re-enqueue.
    /// </summary>
    void Enqueue(EventId eventId, WorkstreamId workstreamId, DateTimeOffset enqueuedAt);
}
