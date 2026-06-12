using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Distillation;

/// <summary>
/// Pulls events + active curations out of SQLite and assembles them into a
/// <see cref="DistillationRequest"/>. Honors the same capability + bi-temporal
/// + revocation gates the query API uses, and applies pin/demote multipliers
/// to each event's score so a host distiller can rank confidently.
/// </summary>
public sealed class DistillationRequestBuilder
{
    private readonly SqliteConnectionFactory _connections;

    public DistillationRequestBuilder(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
    }

    public DistillationRequest Build(
        WorkstreamId workstream,
        int tokenBudget,
        ContextBundle? priorBundle,
        DateTimeOffset now)
    {
        using var c = _connections.Open();

        // Latest event id in the workstream (or None when empty).
        EventId latest;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT event_id FROM memory_events WHERE workstream_id = $ws ORDER BY created_at DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("$ws", workstream.Value);
            latest = cmd.ExecuteScalar() is string s ? new EventId(s) : EventId.None;
        }

        // Pull active (non-reverted) curations grouped by target so we can
        // (a) substitute amended content, (b) compute pin/demote multipliers,
        // (c) surface annotations.
        var curationsByTarget = new Dictionary<EventId, List<DistillationCuration>>();
        var multiplierByTarget = new Dictionary<EventId, double>();
        var amendedByTarget = new Dictionary<EventId, string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT event_id, target_event_id, curation_type, curator,
                       rationale, occurred_at, payload_json
                FROM curation_events
                WHERE workstream_id = $ws AND reverted_by IS NULL
                ORDER BY occurred_at ASC;
                """;
            cmd.Parameters.AddWithValue("$ws", workstream.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var target = new EventId(r.GetString(1));
                var type = (CurationType)r.GetInt32(2);
                var payload = r.GetString(6);
                double mult = 1.0;
                string? amended = null;
                if (type is CurationType.Pinned or CurationType.Demoted)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("multiplier", out var m))
                    {
                        mult = m.GetDouble();
                        multiplierByTarget[target] = mult;
                    }
                }
                else if (type == CurationType.Amended)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("newContent", out var nc))
                    {
                        amended = nc.GetString();
                        if (amended is not null) amendedByTarget[target] = amended;
                    }
                }
                if (!curationsByTarget.TryGetValue(target, out var list))
                {
                    list = new List<DistillationCuration>();
                    curationsByTarget[target] = list;
                }
                list.Add(new DistillationCuration(
                    CurationEventId: new EventId(r.GetString(0)),
                    Type: type,
                    Curator: new PrincipalId(r.GetString(3)),
                    Rationale: r.GetString(4),
                    OccurredAt: DateTimeOffset.Parse(r.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                    Multiplier: mult,
                    AmendedContent: amended));
            }
        }

        // Pull non-revoked events. The score is recency * curation multiplier;
        // distillers can override scoring entirely if they want.
        var events = new List<DistillationEvent>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT e.event_id, e.category, e.classification, e.valid_at, e.created_at,
                       e.payload_json, e.provenance_json
                FROM memory_events e
                LEFT JOIN memory_revocations r ON r.event_id = e.event_id
                WHERE e.workstream_id = $ws AND r.event_id IS NULL
                  AND e.event_channel = 0
                ORDER BY e.created_at DESC;
                """;
            cmd.Parameters.AddWithValue("$ws", workstream.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = new EventId(r.GetString(0));
                var category = (EpistemicCategory)r.GetInt32(1);
                var classification = (Mneme.Contracts.Classification)r.GetInt32(2);
                var validAt = DateTimeOffset.Parse(r.GetString(3), System.Globalization.CultureInfo.InvariantCulture);
                var createdAt = DateTimeOffset.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture);
                var payload = EventSerialization.DeserializePayload(r.GetString(5));
                if (amendedByTarget.TryGetValue(id, out var amended))
                {
                    payload = ApplyAmendment(payload, amended);
                }
                var provenance = EventSerialization.DeserializeProvenance(r.GetString(6));
                var ageDays = Math.Max(0, (now - createdAt).TotalDays);
                var recency = Math.Exp(-ageDays / 30.0); // 30-day half-life
                var multiplier = multiplierByTarget.TryGetValue(id, out var m) ? m : 1.0;
                events.Add(new DistillationEvent(
                    EventId: id,
                    Category: category,
                    Classification: classification,
                    ValidAt: validAt,
                    RecordedAt: createdAt,
                    Score: Math.Clamp(recency * multiplier, 0.0, 1.0),
                    Payload: payload,
                    Provenance: provenance));
            }
        }

        return new DistillationRequest(
            Workstream: workstream,
            GeneratedAt: now,
            EventsCoveredThrough: latest,
            TokenBudget: tokenBudget,
            Events: events,
            Curations: curationsByTarget.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<DistillationCuration>)kvp.Value),
            PriorBundle: priorBundle);
    }

    private static EventPayload ApplyAmendment(EventPayload payload, string amendedContent) => payload switch
    {
        EvidencePayload e   => e with { Content = amendedContent },
        FactPayload f       => f with { Statement = amendedContent },
        DecisionPayload d   => d with { Statement = amendedContent },
        HypothesisPayload h => h with { Statement = amendedContent },
        GoalPayload g       => g with { Statement = amendedContent },
        ActionPayload a     => a with { Statement = amendedContent },
        OutcomePayload o    => o with { Statement = amendedContent },
        _ => payload,
    };
}
