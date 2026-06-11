using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Curation;

/// <summary>
/// Computes the canonical pre-state hash of an event for the curator
/// stale-state guard. Pattern from Letta <c>core_memory_replace</c>
/// (<c>base.py:262-280</c>) — every curator mutation cites the pre-
/// mutation state; mismatch fails the call rather than silently
/// overwriting a concurrent change. See
/// <c>research-design-lessons.md §3.4</c>.
/// </summary>
/// <remarks>
/// The canonical state of an event is composed of:
/// <list type="bullet">
///   <item>The latest non-reverted amended content for the target, or
///         the original payload_json if no amend has landed.</item>
///   <item>The currently-effective pin/demote multiplier (the latest
///         non-reverted <see cref="CurationType.Pinned"/> or
///         <see cref="CurationType.Demoted"/>, or 1.0).</item>
///   <item>The count of non-reverted annotations attached to the target.</item>
/// </list>
/// The hash is a hex-encoded SHA256 over that triple. Curators must
/// re-read state before retrying after a <see cref="StaleProposalError"/>.
/// </remarks>
public static class PreStateHasher
{
    /// <summary>Compute the canonical pre-state hash of an event.</summary>
    public static string ComputeHash(SqliteConnectionFactory connections, EventId target)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (!target.HasValue) throw new ArgumentException("Target id required.", nameof(target));
        using var connection = connections.Open();
        var state = ReadCanonical(connection, target);
        return Encode(state);
    }

    /// <summary>Compute the canonical pre-state hash on an open connection (used inside a transaction).</summary>
    public static string ComputeHash(SqliteConnection connection, SqliteTransaction? tx, EventId target)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!target.HasValue) throw new ArgumentException("Target id required.", nameof(target));
        var state = ReadCanonical(connection, target, tx);
        return Encode(state);
    }

    private static (string Content, double Multiplier, int Annotations) ReadCanonical(
        SqliteConnection connection, EventId target, SqliteTransaction? tx = null)
    {
        string? content = null;

        // Latest non-reverted amend wins for the content slot.
        using (var amend = connection.CreateCommand())
        {
            amend.Transaction = tx;
            amend.CommandText = """
                SELECT payload_json FROM curation_events
                WHERE target_event_id = $tid
                  AND curation_type = $amended
                  AND reverted_by IS NULL
                ORDER BY occurred_at DESC LIMIT 1;
                """;
            amend.Parameters.AddWithValue("$tid", target.Value);
            amend.Parameters.AddWithValue("$amended", (int)CurationType.Amended);
            var raw = amend.ExecuteScalar() as string;
            if (raw is not null)
            {
                // amend payload is a FactAmendment JSON; pull NewContent.
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("newContent", out var nc))
                    {
                        content = nc.GetString();
                    }
                }
                catch { /* fall through to original */ }
            }
        }

        if (content is null)
        {
            using var original = connection.CreateCommand();
            original.Transaction = tx;
            original.CommandText = "SELECT payload_json FROM memory_events WHERE event_id = $id;";
            original.Parameters.AddWithValue("$id", target.Value);
            content = (original.ExecuteScalar() as string) ?? string.Empty;
        }

        double multiplier = 1.0;
        using (var pin = connection.CreateCommand())
        {
            pin.Transaction = tx;
            pin.CommandText = """
                SELECT payload_json FROM curation_events
                WHERE target_event_id = $tid
                  AND curation_type IN ($pinned, $demoted)
                  AND reverted_by IS NULL
                ORDER BY occurred_at DESC LIMIT 1;
                """;
            pin.Parameters.AddWithValue("$tid", target.Value);
            pin.Parameters.AddWithValue("$pinned", (int)CurationType.Pinned);
            pin.Parameters.AddWithValue("$demoted", (int)CurationType.Demoted);
            var raw = pin.ExecuteScalar() as string;
            if (raw is not null)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("multiplier", out var m))
                    {
                        multiplier = m.GetDouble();
                    }
                }
                catch { /* keep default */ }
            }
        }

        int annotations;
        using (var ann = connection.CreateCommand())
        {
            ann.Transaction = tx;
            ann.CommandText = """
                SELECT COUNT(*) FROM curation_events
                WHERE target_event_id = $tid
                  AND curation_type = $annotated
                  AND reverted_by IS NULL;
                """;
            ann.Parameters.AddWithValue("$tid", target.Value);
            ann.Parameters.AddWithValue("$annotated", (int)CurationType.Annotated);
            annotations = Convert.ToInt32(ann.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        return (content ?? string.Empty, multiplier, annotations);
    }

    private static string Encode((string Content, double Multiplier, int Annotations) state)
    {
        var canonical = $"v1|content={state.Content}|mult={state.Multiplier.ToString("R", CultureInfo.InvariantCulture)}|ann={state.Annotations}";
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
