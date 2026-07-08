using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest.Validation;
using Mneme.Storage;

namespace Mneme.Resolution;

/// <summary>
/// Post-projection pass that binds fact-triple subjects to canonical entity ids.
/// Reads every <c>projection_fact_triples</c> row whose <c>subject_entity_id</c>
/// is still null, resolves each distinct subject surface form through the Phase-6
/// <see cref="EntityResolver"/> (so aliases and re-mentions unify — e.g. "Mel"
/// folds into "Melanie" via Tier-2 cosine when an embedding provider is wired),
/// and stamps the resolved id back onto the rows.
/// </summary>
/// <remarks>
/// Kept as a separate pass rather than folded into <c>FactTriplesProjector</c>
/// because <see cref="EntityResolver.ResolveAsync"/> is asynchronous and manages
/// its own write transactions, which cannot compose inside the synchronous,
/// single-transaction projector. Idempotent: only null rows are processed and
/// each distinct subject is resolved once per pass. Subjects are treated as
/// <see cref="EntityKind.Name"/> (people/possessive chains); Tier 1 never
/// auto-merges names, so unification only happens when an embedding provider is
/// registered on the resolver.
/// </remarks>
public sealed class SubjectTripleResolver
{
    private readonly SqliteConnectionFactory _connections;
    private readonly EntityResolver _resolver;

    public SubjectTripleResolver(SqliteConnectionFactory connections, EntityResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(resolver);
        _connections = connections;
        _resolver = resolver;
    }

    /// <summary>
    /// Resolve and stamp <c>subject_entity_id</c> for all unresolved triples in
    /// the workstream. Returns the number of distinct subjects resolved.
    /// </summary>
    public async Task<int> ResolveWorkstreamAsync(WorkstreamId workstream, CancellationToken ct = default)
    {
        WorkstreamIdValidator.EnsureValid(workstream.Value, nameof(workstream));

        // Collect each distinct unresolved subject with a representative event id
        // to cite as the mention source.
        var subjects = new List<(string SubjectText, string EventId)>();
        using (var c = _connections.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT subject_text, MIN(event_id) AS event_id
                FROM projection_fact_triples
                WHERE workstream_id = $ws AND subject_entity_id IS NULL
                GROUP BY subject_text;
                """;
            cmd.Parameters.AddWithValue("$ws", workstream.Value);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                subjects.Add((rd.GetString(0), rd.GetString(1)));
            }
        }

        var resolved = 0;
        foreach (var (subjectText, eventId) in subjects)
        {
            ct.ThrowIfCancellationRequested();
            var resolution = await _resolver.ResolveAsync(
                workstream, EntityKind.Name, rawIdentifier: subjectText, displayName: subjectText,
                mentionedIn: new EventId(eventId), ct).ConfigureAwait(false);

            using var c = _connections.Open();
            using var upd = c.CreateCommand();
            upd.CommandText = """
                UPDATE projection_fact_triples
                SET subject_entity_id = $eid
                WHERE workstream_id = $ws AND subject_text = $stext AND subject_entity_id IS NULL;
                """;
            upd.Parameters.AddWithValue("$eid", resolution.Entity.EntityId.Value);
            upd.Parameters.AddWithValue("$ws", workstream.Value);
            upd.Parameters.AddWithValue("$stext", subjectText);
            upd.ExecuteNonQuery();
            resolved++;
        }
        return resolved;
    }
}
