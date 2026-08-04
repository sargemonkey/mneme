using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Dreaming;

/// <summary>
/// Orchestrates one offline consolidation ("dream") pass over a workstream
/// (Phase 14, ADR-0004). Loads the in-scope epistemic events, prior skills, and
/// open contradiction candidates; runs the host <see cref="IDreamer"/>; then
/// direct-ingests each output as a <see cref="Citation.Derived"/> event through
/// the normal <see cref="IMemoryAgent.IngestAsync"/> pipeline (so redaction runs
/// again on the derived text), applying the visibility-cap guardrail, and
/// records a <c>dream_runs</c> audit row.
/// </summary>
/// <remarks>
/// Guardrails enforced here (the safety-of-a-single-output ones): (1) outputs
/// flow through <see cref="IMemoryAgent.IngestAsync"/>, so the ingest redactor
/// re-scrubs LLM-synthesized text before the WAL; (2) each output's requested
/// visibility is <em>capped</em> by the sensitivity of the events it was derived
/// from — if any source is Confidential/Secret/Pii the output is forced
/// <see cref="Visibility.Private"/>, and only all-Public/Internal sources may be
/// promoted to <see cref="Visibility.Global"/>. Operational guardrails (per-
/// workstream opt-in for cross-workstream mining, richer audit queries) land in
/// the dependent guardrails/fleet tasks.
/// </remarks>
public sealed class DreamCoordinator
{
    private readonly SqliteConnectionFactory _connections;
    private readonly IMemoryAgent _agent;
    private readonly IDreamer? _dreamer;
    private readonly TimeProvider _clock;

    public DreamCoordinator(
        SqliteConnectionFactory connections,
        IMemoryAgent agent,
        IDreamer? dreamer,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _agent = agent;
        _dreamer = dreamer;
        _clock = clock;
    }

    /// <summary>True if a host <see cref="IDreamer"/> is wired.</summary>
    public bool IsEnabled => _dreamer is not null;

    /// <summary>
    /// Run one consolidation pass over <paramref name="workstream"/>.
    /// </summary>
    /// <param name="workstream">Workstream to consolidate.</param>
    /// <param name="capability">Token authorizing the workstream; its principal authors the derived events.</param>
    /// <param name="maxEvents">Cap on how many recent events to feed the dreamer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A summary of the run.</returns>
    /// <exception cref="InvalidOperationException">If no <see cref="IDreamer"/> is registered.</exception>
    public async Task<DreamRunSummary> ConsolidateAsync(
        WorkstreamId workstream,
        CapabilityToken capability,
        int maxEvents = 500,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (_dreamer is null)
        {
            throw new InvalidOperationException(
                "Consolidation requires an IDreamer. Register one with " +
                "services.AddSingleton<IDreamer>(sp => new YourDreamer(...));");
        }

        var events = LoadEvents(workstream, maxEvents);
        var request = new DreamRequest(
            Workstream: workstream,
            GeneratedAt: _clock.GetUtcNow(),
            Events: events,
            PriorSkills: LoadPriorSkills(workstream, 100),
            OpenContradictions: LoadOpenContradictions(workstream, 100),
            TokenBudget: 8192);

        var result = await _dreamer.DreamAsync(request, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // Source classifications, to cap each output's visibility.
        var classes = LoadClassifications(
            result.Outputs.SelectMany(o => o.DerivedFrom).Select(e => e.Value));

        var produced = new List<EventId>(result.Outputs.Count);
        foreach (var output in result.Outputs)
        {
            if (output.DerivedFrom is null || output.DerivedFrom.Count == 0)
            {
                continue; // a derived event must cite its sources
            }

            var envelope = new CaptureEvent(
                EventId: new EventId("dream-" + Guid.NewGuid().ToString("N")),
                WorkstreamId: workstream,
                Channel: EventChannel.Epistemic,
                ValidAt: _clock.GetUtcNow(),
                RecordedAt: _clock.GetUtcNow(),
                Payload: output.Payload,
                Provenance: new CaptureProvenance(
                    Source: new CaptureSourceId("dream/" + _dreamer.Id),
                    Principal: capability.Principal,
                    Context: "consolidation",
                    Citation: new Citation.Derived(output.DerivedFrom, _dreamer.Id)));

            // Re-redaction happens inside IngestAsync (validate → redact → …).
            var ingest = await _agent.IngestAsync(envelope, ct).ConfigureAwait(false);
            produced.Add(ingest.EventId);

            var capped = CapVisibility(output.ProposedVisibility, output.DerivedFrom, classes);
            SetVisibility(ingest.EventId, workstream, capped);
        }

        var runId = "dream-run-" + Guid.NewGuid().ToString("N");
        RecordRun(runId, workstream, _dreamer.Id, request.GeneratedAt, events.Count, produced.Count);

        return new DreamRunSummary(runId, _dreamer.Id, events.Count, produced);
    }

    /// <summary>
    /// The highest visibility an output may receive: forced Private if any source
    /// is sensitive (Confidential/Secret/Pii), else the dreamer's request honoured
    /// (up to Global). Missing source classifications are treated conservatively.
    /// </summary>
    private static Visibility CapVisibility(
        Visibility proposed, IReadOnlyList<EventId> derivedFrom, IReadOnlyDictionary<string, Mneme.Contracts.Classification> classes)
    {
        foreach (var src in derivedFrom)
        {
            var cls = classes.TryGetValue(src.Value, out var c) ? c : Mneme.Contracts.Classification.Confidential;
            if (cls is Mneme.Contracts.Classification.Confidential or Mneme.Contracts.Classification.Secret or Mneme.Contracts.Classification.Pii)
            {
                return Visibility.Private; // one sensitive source floors the whole output
            }
        }
        return (Visibility)Math.Min((int)proposed, (int)Visibility.Global);
    }

    private IReadOnlyList<DistillationEvent> LoadEvents(WorkstreamId ws, int limit)
    {
        var list = new List<DistillationEvent>();
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT e.event_id, e.category, e.classification, e.valid_at, e.created_at,
                   e.payload_json, e.provenance_json
            FROM memory_events e
            LEFT JOIN memory_revocations r ON r.event_id = e.event_id
            WHERE e.workstream_id = $ws AND r.event_id IS NULL AND e.event_channel = 0
            ORDER BY e.created_at DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$ws", ws.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new DistillationEvent(
                EventId: new EventId(rd.GetString(0)),
                Category: (EpistemicCategory)rd.GetInt32(1),
                Classification: (Mneme.Contracts.Classification)rd.GetInt32(2),
                ValidAt: DateTimeOffset.Parse(rd.GetString(3), CultureInfo.InvariantCulture),
                RecordedAt: DateTimeOffset.Parse(rd.GetString(4), CultureInfo.InvariantCulture),
                Score: 1.0,
                Payload: EventSerialization.DeserializePayload(rd.GetString(5)),
                Provenance: EventSerialization.DeserializeProvenance(rd.GetString(6))));
        }
        return list;
    }

    private IReadOnlyList<PriorSkill> LoadPriorSkills(WorkstreamId ws, int limit)
    {
        var list = new List<PriorSkill>();
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.event_id, s.name, s.procedure, s.trigger
            FROM projection_skills s
            LEFT JOIN memory_revocations r ON r.event_id = s.event_id
            WHERE s.workstream_id = $ws AND s.revoked_at IS NULL AND r.event_id IS NULL
            ORDER BY s.valid_at DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$ws", ws.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new PriorSkill(
                new EventId(rd.GetString(0)), rd.GetString(1), rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3)));
        }
        return list;
    }

    private IReadOnlyList<ContradictionCandidate> LoadOpenContradictions(WorkstreamId ws, int limit)
    {
        var list = new List<ContradictionCandidate>();
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT subject_key, predicate, event_id_a, object_a, event_id_b, object_b
            FROM memory_contradictions
            WHERE workstream_id = $ws AND status = 0
            ORDER BY detected_at DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$ws", ws.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new ContradictionCandidate(
                rd.GetString(0), rd.GetString(1),
                new EventId(rd.GetString(2)), rd.GetString(3),
                new EventId(rd.GetString(4)), rd.GetString(5)));
        }
        return list;
    }

    private IReadOnlyDictionary<string, Mneme.Contracts.Classification> LoadClassifications(IEnumerable<string> eventIds)
    {
        var ids = eventIds.Distinct(StringComparer.Ordinal).ToArray();
        var map = new Dictionary<string, Mneme.Contracts.Classification>(ids.Length, StringComparer.Ordinal);
        if (ids.Length == 0) return map;
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, classification FROM memory_events
            WHERE event_id IN (SELECT value FROM json_each($ids));
            """;
        cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) map[rd.GetString(0)] = (Mneme.Contracts.Classification)rd.GetInt32(1);
        return map;
    }

    private void SetVisibility(EventId eventId, WorkstreamId ws, Visibility visibility)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_visibility(event_id, workstream_id, visibility, set_at)
            VALUES ($eid, $ws, $vis, $at)
            ON CONFLICT(event_id) DO UPDATE SET visibility = excluded.visibility, set_at = excluded.set_at;
            """;
        cmd.Parameters.AddWithValue("$eid", eventId.Value);
        cmd.Parameters.AddWithValue("$ws", ws.Value);
        cmd.Parameters.AddWithValue("$vis", (int)visibility);
        cmd.Parameters.AddWithValue("$at", _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private void RecordRun(string runId, WorkstreamId ws, string dreamerId,
        DateTimeOffset startedAt, int eventsIn, int outputsOut)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dream_runs(run_id, workstream_id, dreamer_id, started_at, events_in, outputs_out)
            VALUES ($id, $ws, $did, $at, $in, $out);
            """;
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.Parameters.AddWithValue("$ws", ws.Value);
        cmd.Parameters.AddWithValue("$did", dreamerId);
        cmd.Parameters.AddWithValue("$at", startedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$in", eventsIn);
        cmd.Parameters.AddWithValue("$out", outputsOut);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Summary of one <see cref="DreamCoordinator.ConsolidateAsync"/> pass.</summary>
/// <param name="RunId">The audit run id recorded in <c>dream_runs</c>.</param>
/// <param name="DreamerId">The dreamer that produced the outputs.</param>
/// <param name="EventsConsidered">How many events were fed to the dreamer.</param>
/// <param name="Produced">Event ids of the derived events ingested this run.</param>
public sealed record DreamRunSummary(
    string RunId,
    string DreamerId,
    int EventsConsidered,
    IReadOnlyList<EventId> Produced);
