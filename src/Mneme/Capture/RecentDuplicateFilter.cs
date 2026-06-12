using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Capture;

/// <summary>
/// Suppresses candidates whose content already appears verbatim in a recent
/// event in the same workstream. Default window: last <see cref="RecentRows"/>
/// events. Useful guard against agents that call capture twice on the same
/// turn, or against transcript watchers replaying a session.
/// </summary>
/// <remarks>
/// Comparison is case-insensitive, whitespace-normalised, and against the
/// canonical content slot of each payload. Hosts wanting embedding-based
/// near-duplicate detection should implement their own
/// <see cref="ICaptureFilter"/>.
/// </remarks>
public sealed class RecentDuplicateFilter : ICaptureFilter
{
    /// <summary>How many recent events to compare against (default 100).</summary>
    public int RecentRows { get; init; } = 100;

    private readonly SqliteConnectionFactory _connections;

    public RecentDuplicateFilter(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CaptureCandidate>> FilterAsync(
        IReadOnlyList<CaptureCandidate> candidates,
        WorkstreamId workstream,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) return Task.FromResult(candidates);
        var recent = LoadRecentContent(workstream);
        var result = new List<CaptureCandidate>(candidates.Count);
        foreach (var c in candidates)
        {
            if (recent.Contains(Normalise(c.Content))) continue;
            result.Add(c);
        }
        return Task.FromResult<IReadOnlyList<CaptureCandidate>>(result);
    }

    private HashSet<string> LoadRecentContent(WorkstreamId ws)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT payload_json FROM memory_events
            WHERE workstream_id = $ws
            ORDER BY created_at DESC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$ws", ws.Value);
        cmd.Parameters.AddWithValue("$n", RecentRows);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var json = r.GetString(0);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var field in new[] { "content", "statement" })
                {
                    if (doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (s is not null) set.Add(Normalise(s));
                    }
                }
            }
            catch { /* ignore unparseable payloads */ }
        }
        return set;
    }

    private static string Normalise(string s) =>
        string.Join(' ', s.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
}
