using System.Globalization;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Outcomes;

/// <summary>
/// Phase 7 — feedback learner. When an Outcome closes a Decision, this
/// service nudges the <c>feedback_weight</c> on the events that
/// supported the decision: positive outcomes raise the weight, negative
/// outcomes lower it. Pattern adapted from Cognee's <c>improve()</c> —
/// see <c>research-design-lessons.md §3.3</c>.
/// </summary>
/// <remarks>
/// <para>
/// Update rule: <c>w_new = clamp(w_old + alpha * (score - 0.5), 0.1, 5.0)</c>
/// where <c>score = 1.0</c> for positive, <c>0.5</c> for neutral,
/// <c>0.0</c> for negative. Default <c>alpha = 0.1</c>. Bounds prevent
/// runaway weights from a long streak of identical-polarity outcomes.
/// </para>
/// <para>
/// Phase 4 retrieval scoring will multiply this weight in next to
/// curation multipliers once Phase 7.5's pin/demote table joins the
/// score pipeline; for v1 the weight is computed and stored so the
/// retrieval side has something to read.
/// </para>
/// </remarks>
public sealed class FeedbackLearner
{
    /// <summary>Learning rate. Higher = faster adjustment, more noise.</summary>
    public double Alpha { get; init; } = 0.1;
    /// <summary>Minimum feedback weight (prevents zero-or-negative weights).</summary>
    public double MinWeight { get; init; } = 0.1;
    /// <summary>Maximum feedback weight (prevents runaway boosts).</summary>
    public double MaxWeight { get; init; } = 5.0;

    private readonly SqliteConnectionFactory _connections;
    private readonly TimeProvider _clock;

    public FeedbackLearner(SqliteConnectionFactory connections)
        : this(connections, TimeProvider.System) { }

    public FeedbackLearner(SqliteConnectionFactory connections, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _clock = clock;
    }

    /// <summary>
    /// Apply feedback from an outcome closure. Reads the closed decision
    /// chain, walks every supporting-evidence event linked from the
    /// decision payload, and nudges each one's feedback weight.
    /// </summary>
    public int ApplyFromOutcome(EventId decisionEventId, OutcomePolarity polarity)
    {
        if (!decisionEventId.HasValue) throw new ArgumentException("decisionEventId required", nameof(decisionEventId));
        var delta = Alpha * (Score(polarity) - 0.5);
        var nowUtc = _clock.GetUtcNow();

        using var c = _connections.Open();
        using var tx = c.BeginTransaction();

        // Read the decision payload to find supporting events.
        EventId[] supporters;
        using (var sel = c.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT payload_json FROM memory_events WHERE event_id = $id;";
            sel.Parameters.AddWithValue("$id", decisionEventId.Value);
            var json = sel.ExecuteScalar() as string;
            if (json is null)
            {
                return 0;
            }
            var payload = EventSerialization.DeserializePayload(json);
            if (payload is not DecisionPayload d)
            {
                return 0;
            }
            supporters = d.SupportingEvents.ToArray();
        }
        // Always nudge the decision itself, plus every supporting event.
        var targets = new HashSet<string>(StringComparer.Ordinal) { decisionEventId.Value };
        foreach (var s in supporters) targets.Add(s.Value);

        var updated = 0;
        foreach (var targetId in targets)
        {
            using var upsert = c.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO event_feedback(event_id, feedback_weight, updated_at, update_count)
                VALUES ($id, $w, $at, 1)
                ON CONFLICT(event_id) DO UPDATE SET
                    feedback_weight = MIN($max, MAX($min, feedback_weight + $delta)),
                    updated_at      = excluded.updated_at,
                    update_count    = update_count + 1;
                """;
            upsert.Parameters.AddWithValue("$id", targetId);
            upsert.Parameters.AddWithValue("$w", Math.Clamp(1.0 + delta, MinWeight, MaxWeight));
            upsert.Parameters.AddWithValue("$at", nowUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            upsert.Parameters.AddWithValue("$delta", delta);
            upsert.Parameters.AddWithValue("$min", MinWeight);
            upsert.Parameters.AddWithValue("$max", MaxWeight);
            upsert.ExecuteNonQuery();
            updated++;
        }
        tx.Commit();
        return updated;
    }

    /// <summary>Current feedback weight for an event (1.0 if untouched).</summary>
    public double GetWeight(EventId eventId)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT feedback_weight FROM event_feedback WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", eventId.Value);
        return cmd.ExecuteScalar() is double d ? d : 1.0;
    }

    private static double Score(OutcomePolarity polarity) => polarity switch
    {
        OutcomePolarity.Positive => 1.0,
        OutcomePolarity.Neutral  => 0.5,
        OutcomePolarity.Negative => 0.0,
        _ => 0.5,
    };
}
