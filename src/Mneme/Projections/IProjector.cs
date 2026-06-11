using Microsoft.Data.Sqlite;
using Mneme.Contracts;

namespace Mneme.Projections;

/// <summary>
/// A projector consumes events from <c>memory_events</c> and writes derived
/// rows into one projection table (<c>projection_facts</c>,
/// <c>projection_decisions</c>, etc.). Two surfaces:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><see cref="Apply"/> is called per event by the projector
///         worker after ingest. It must be idempotent — replaying the
///         same event must produce the same row (typical pattern:
///         <c>INSERT … ON CONFLICT … DO UPDATE</c>).</item>
///   <item><see cref="Rebuild"/> wipes the projection's table and
///         re-applies every relevant event from genesis. Used after a
///         schema change or to recover a corrupt projection.</item>
/// </list>
/// <para>
/// Both methods get a live <see cref="SqliteConnection"/> + an open
/// <see cref="SqliteTransaction"/> so the projector worker can batch
/// many events into one commit.
/// </para>
/// </remarks>
public interface IProjector
{
    /// <summary>Stable projection name — used as the key in <c>event_processing_log.projection_name</c>.</summary>
    string Name { get; }

    /// <summary>Categories this projector cares about. Events with other categories are skipped without log entries.</summary>
    EpistemicCategory Category { get; }

    /// <summary>Apply a single event. Idempotent.</summary>
    void Apply(SqliteConnection c, SqliteTransaction tx, EventEnvelope envelope);

    /// <summary>Wipe and rebuild the projection table from genesis.</summary>
    int Rebuild(SqliteConnection c, SqliteTransaction tx);
}

/// <summary>
/// The decoded form of a <c>memory_events</c> row as it flows through the
/// projector pipeline. Contains everything a projector needs without
/// having to round-trip through the API surface.
/// </summary>
public sealed record EventEnvelope(
    EventId EventId,
    WorkstreamId WorkstreamId,
    EventChannel Channel,
    EpistemicCategory Category,
    int SchemaVersion,
    DateTimeOffset ValidAt,
    DateTimeOffset? InvalidAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiredAt,
    Mneme.Contracts.Classification Classification,
    DateTimeOffset? RevokedAt,
    EventPayload Payload,
    CaptureProvenance Provenance);
