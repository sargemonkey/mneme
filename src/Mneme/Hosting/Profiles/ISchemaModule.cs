using Microsoft.Data.Sqlite;

namespace Mneme.Hosting.Profiles;

/// <summary>
/// A domain profile's schema contribution: idempotent DDL that creates the
/// profile's own <c>projection_*</c> / domain tables and indexes. Run once at
/// composition time (after the core schema), and safe to re-run on every
/// startup (use <c>CREATE TABLE/INDEX IF NOT EXISTS</c>).
/// </summary>
/// <remarks>
/// A module may only create and write its <em>own</em> tables — it must never
/// touch <c>memory_events</c> or another profile's tables (ADR-0005: the
/// append-only log stays the single source of truth; profile tables are
/// derived projections rebuildable from it).
/// </remarks>
public interface ISchemaModule
{
    /// <summary>Stable module name (used to namespace its version in <c>schema_meta</c>).</summary>
    string Name { get; }

    /// <summary>Monotonic schema version for this module's tables.</summary>
    int Version { get; }

    /// <summary>Apply the module's DDL against an open connection, idempotently.</summary>
    void ApplyDdl(SqliteConnection connection);
}
