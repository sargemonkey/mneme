using System.Globalization;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Projections;

namespace Mneme.Outcomes;

/// <summary>
/// Phase 7 — decision-chains projector. Walks
/// Decision → Action → Outcome cause chains from <c>memory_events</c>
/// into the <c>decision_chains</c> projection.
/// </summary>
/// <remarks>
/// Idempotent under replay: every write is an upsert keyed on
/// <c>(workstream_id, decision_event_id)</c>. Out-of-order events are
/// tolerated: when a Decision lands it sweeps for Actions/Outcomes that
/// referenced it before it existed; when an Outcome arrives before its
/// Action chain row exists, it's a no-op until the next replay.
/// </remarks>
public sealed class DecisionChainsProjector : IProjector
{
    /// <inheritdoc/>
    public string Name => "decision_chains";

    /// <inheritdoc/>
    public EpistemicCategory Category => EpistemicCategory.Decision;

    /// <inheritdoc/>
    public bool MatchesCategory(EpistemicCategory category) =>
        category is EpistemicCategory.Decision or EpistemicCategory.Action or EpistemicCategory.Outcome;

    /// <inheritdoc/>
    public void Apply(SqliteConnection c, SqliteTransaction tx, EventEnvelope e)
    {
        switch (e.Payload)
        {
            case DecisionPayload:
                UpsertDecision(c, tx, e);
                BackfillFromExistingEvents(c, tx, e);
                break;
            case ActionPayload action when action.DecisionEvent is { } d && d.HasValue:
                LinkAction(c, tx, e, d);
                break;
            case OutcomePayload outcome when outcome.ActionEvent.HasValue:
                LinkOutcome(c, tx, e, outcome);
                break;
        }
    }

    /// <inheritdoc/>
    public int Rebuild(SqliteConnection c, SqliteTransaction tx)
    {
        using (var del = c.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM decision_chains;"; del.ExecuteNonQuery(); }
        var n = 0;
        foreach (var e in EventEnvelopeReader.ReadAll(c, tx, EpistemicCategory.Decision)) { UpsertDecision(c, tx, e); n++; }
        foreach (var e in EventEnvelopeReader.ReadAll(c, tx, EpistemicCategory.Action))
        {
            if (e.Payload is ActionPayload a && a.DecisionEvent is { } d && d.HasValue) LinkAction(c, tx, e, d);
        }
        foreach (var e in EventEnvelopeReader.ReadAll(c, tx, EpistemicCategory.Outcome))
        {
            if (e.Payload is OutcomePayload o && o.ActionEvent.HasValue) LinkOutcome(c, tx, e, o);
        }
        return n;
    }

    private static void UpsertDecision(SqliteConnection c, SqliteTransaction tx, EventEnvelope e)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO decision_chains(workstream_id, decision_event_id, decision_at, closed)
            VALUES ($ws, $eid, $at, 0)
            ON CONFLICT(workstream_id, decision_event_id) DO UPDATE SET decision_at = excluded.decision_at;
            """;
        cmd.Parameters.AddWithValue("$ws", e.WorkstreamId.Value);
        cmd.Parameters.AddWithValue("$eid", e.EventId.Value);
        cmd.Parameters.AddWithValue("$at", Fmt(e.ValidAt));
        cmd.ExecuteNonQuery();
    }

    private static void LinkAction(SqliteConnection c, SqliteTransaction tx, EventEnvelope action, EventId decisionRef)
    {
        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO decision_chains(workstream_id, decision_event_id, decision_at, closed)
                VALUES ($ws, $did, $at, 0)
                ON CONFLICT(workstream_id, decision_event_id) DO NOTHING;
                """;
            ins.Parameters.AddWithValue("$ws", action.WorkstreamId.Value);
            ins.Parameters.AddWithValue("$did", decisionRef.Value);
            ins.Parameters.AddWithValue("$at", Fmt(action.ValidAt));
            ins.ExecuteNonQuery();
        }
        using var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = """
            UPDATE decision_chains SET action_event_id = $aid
             WHERE workstream_id = $ws AND decision_event_id = $did;
            """;
        upd.Parameters.AddWithValue("$aid", action.EventId.Value);
        upd.Parameters.AddWithValue("$ws", action.WorkstreamId.Value);
        upd.Parameters.AddWithValue("$did", decisionRef.Value);
        upd.ExecuteNonQuery();
    }

    private static void LinkOutcome(SqliteConnection c, SqliteTransaction tx, EventEnvelope outcome, OutcomePayload payload)
    {
        string? decisionId;
        using (var sel = c.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT decision_event_id FROM decision_chains
                WHERE workstream_id = $ws AND action_event_id = $aid;
                """;
            sel.Parameters.AddWithValue("$ws", outcome.WorkstreamId.Value);
            sel.Parameters.AddWithValue("$aid", payload.ActionEvent.Value);
            decisionId = sel.ExecuteScalar() as string;
        }
        if (decisionId is null) return;
        using var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = """
            UPDATE decision_chains
               SET outcome_event_id = $oid, outcome_polarity = $pol, outcome_at = $at, closed = 1
             WHERE workstream_id = $ws AND decision_event_id = $did;
            """;
        upd.Parameters.AddWithValue("$oid", outcome.EventId.Value);
        upd.Parameters.AddWithValue("$pol", (int)payload.Polarity);
        upd.Parameters.AddWithValue("$at", Fmt(outcome.ValidAt));
        upd.Parameters.AddWithValue("$ws", outcome.WorkstreamId.Value);
        upd.Parameters.AddWithValue("$did", decisionId);
        upd.ExecuteNonQuery();
    }

    private static void BackfillFromExistingEvents(SqliteConnection c, SqliteTransaction tx, EventEnvelope decision)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT event_id, workstream_id, event_channel, category, schema_version,
                   valid_at, invalid_at, created_at, expired_at, classification, payload_json, provenance_json
            FROM memory_events
            WHERE workstream_id = $ws AND category IN ($act, $out);
            """;
        cmd.Parameters.AddWithValue("$ws", decision.WorkstreamId.Value);
        cmd.Parameters.AddWithValue("$act", (int)EpistemicCategory.Action);
        cmd.Parameters.AddWithValue("$out", (int)EpistemicCategory.Outcome);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var env = new EventEnvelope(
                EventId: new EventId(r.GetString(0)),
                WorkstreamId: new WorkstreamId(r.GetString(1)),
                Channel: (EventChannel)r.GetInt32(2),
                Category: (EpistemicCategory)r.GetInt32(3),
                SchemaVersion: r.GetInt32(4),
                ValidAt: DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
                InvalidAt: r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
                CreatedAt: DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture),
                ExpiredAt: r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture),
                Classification: (Mneme.Contracts.Classification)r.GetInt32(9),
                RevokedAt: null,
                Payload: Mneme.Storage.EventSerialization.DeserializePayload(r.GetString(10)),
                Provenance: Mneme.Storage.EventSerialization.DeserializeProvenance(r.GetString(11)));
            if (env.Payload is ActionPayload a && a.DecisionEvent is { } d && d.Value == decision.EventId.Value)
            {
                LinkAction(c, tx, env, d);
            }
            else if (env.Payload is OutcomePayload o && o.ActionEvent.HasValue)
            {
                LinkOutcome(c, tx, env, o);
            }
        }
    }

    private static string Fmt(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
