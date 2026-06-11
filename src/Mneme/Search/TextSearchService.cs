using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest.Validation;
using Mneme.Storage;

namespace Mneme.Search;

/// <summary>
/// Maintains the FTS5 <c>event_text_index</c> virtual table and serves
/// recency-weighted full-text search against it. Workstream-scoped at
/// the query layer — never returns a row from a workstream the caller
/// did not name. (The Phase 4 query API will additionally check a
/// <see cref="CapabilityToken"/> before calling in.)
/// </summary>
/// <remarks>
/// <para>
/// Recency weighting is applied multiplicatively on top of the
/// normalized BM25 score: <c>score = bm25_norm * exp(-ageDays / halfLife)</c>
/// with a default half-life of 30 days. The half-life is configurable;
/// pass <c>TimeSpan.Zero</c> to disable recency weighting entirely.
/// </para>
/// </remarks>
public sealed class TextSearchService
{
    private readonly SqliteConnectionFactory _connections;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _recencyHalfLife;

    /// <summary>Construct with the default 30-day recency half-life.</summary>
    public TextSearchService(SqliteConnectionFactory connections)
        : this(connections, TimeProvider.System, TimeSpan.FromDays(30)) { }

    /// <summary>Construct with a custom clock + recency half-life.</summary>
    public TextSearchService(SqliteConnectionFactory connections, TimeProvider clock, TimeSpan recencyHalfLife)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        if (recencyHalfLife < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(recencyHalfLife), "Recency half-life must be non-negative.");
        }
        _connections = connections;
        _clock = clock;
        _recencyHalfLife = recencyHalfLife;
    }

    /// <summary>Index a single (already-redacted) event's content.</summary>
    public void Index(EventId eventId, WorkstreamId workstreamId, EpistemicCategory category, DateTimeOffset createdAt, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var c = _connections.Open();
        using var tx = c.BeginTransaction();
        // FTS5 has no "insert or replace" semantics on a content-less
        // virtual table — emulate by deleting any previous row for this
        // event id first.
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM event_text_index WHERE event_id = $eid;";
            del.Parameters.AddWithValue("$eid", eventId.Value);
            del.ExecuteNonQuery();
        }
        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO event_text_index(content, workstream_id, event_id, category, created_at)
                VALUES ($content, $ws, $eid, $cat, $ca);
                """;
            ins.Parameters.AddWithValue("$content", content);
            ins.Parameters.AddWithValue("$ws", workstreamId.Value);
            ins.Parameters.AddWithValue("$eid", eventId.Value);
            ins.Parameters.AddWithValue("$cat", (int)category);
            ins.Parameters.AddWithValue("$ca", createdAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Search the index. Returns up to <paramref name="limit"/> hits
    /// ranked by recency-weighted normalized BM25 (highest first).
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string workstreamId, string query, int limit = 25)
    {
        WorkstreamIdValidator.EnsureValid(workstreamId, nameof(workstreamId));
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return Array.Empty<SearchHit>();
        }
        var tokenCount = AdaptiveBm25.CountTokens(query);

        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, category, created_at, bm25(event_text_index) AS raw
            FROM event_text_index
            WHERE event_text_index MATCH $q AND workstream_id = $ws
            ORDER BY raw ASC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$ws", workstreamId);
        cmd.Parameters.AddWithValue("$lim", limit);

        var results = new List<SearchHit>(limit);
        using var r = cmd.ExecuteReader();
        var now = _clock.GetUtcNow();
        while (r.Read())
        {
            var rawBm25 = r.IsDBNull(3) ? 0.0 : r.GetDouble(3);
            var normalized = AdaptiveBm25.Normalize(rawBm25, tokenCount);
            var createdAt = DateTimeOffset.Parse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
            var recency = _recencyHalfLife <= TimeSpan.Zero
                ? 1.0
                : Math.Exp(-(now - createdAt).TotalDays / Math.Max(0.001, _recencyHalfLife.TotalDays));
            results.Add(new SearchHit(
                EventId: new EventId(r.GetString(0)),
                Category: (EpistemicCategory)r.GetInt32(1),
                CreatedAt: createdAt,
                RawBm25: rawBm25,
                NormalizedBm25: normalized,
                RecencyWeight: recency,
                Score: normalized * recency));
        }
        // bm25() ascending sort hit the storage; re-sort by combined score for the caller.
        results.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return results;
    }
}

/// <summary>A single ranked text-search hit.</summary>
/// <param name="EventId">The event the indexed content belonged to.</param>
/// <param name="Category">The event's epistemic category.</param>
/// <param name="CreatedAt">Ingest time (UTC) of the event.</param>
/// <param name="RawBm25">FTS5's raw <c>bm25()</c> score (negative; lower == more relevant).</param>
/// <param name="NormalizedBm25">Adaptive-sigmoid mapping of <see cref="RawBm25"/> to [0,1].</param>
/// <param name="RecencyWeight">Exponential decay applied for age, in [0,1].</param>
/// <param name="Score">Final combined ranking score (<see cref="NormalizedBm25"/> × <see cref="RecencyWeight"/>).</param>
public sealed record SearchHit(
    EventId EventId,
    EpistemicCategory Category,
    DateTimeOffset CreatedAt,
    double RawBm25,
    double NormalizedBm25,
    double RecencyWeight,
    double Score);
