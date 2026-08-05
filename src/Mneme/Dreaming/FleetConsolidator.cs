using System.Globalization;
using Mneme.Contracts;
using Mneme.Review;
using Mneme.Storage;

namespace Mneme.Dreaming;

/// <summary>
/// Cross-workstream ("fleet") consolidation (Phase 14, ADR-0004): mines the
/// skills of every <em>opted-in</em> workstream for recurring patterns and
/// promotes the results into a shared <see cref="Visibility.Global"/> skill
/// library. This is the one job that reads across the isolation boundary, so it
/// carries the strongest guardrails:
/// <list type="number">
///   <item><strong>Opt-in only</strong> — a workstream is mined only when it has
///         set <c>participates_in_cross_workstream_consolidation</c>.</item>
///   <item><strong>Cross-workstream token</strong> — the caller must supply a
///         token with <see cref="CapabilityToken.CrossWorkstream"/> and a null
///         workstream scope.</item>
///   <item><strong>Classification floor</strong> — an output is promoted to
///         global only when <em>every</em> source event is Public/Internal
///         (<see cref="DreamGuardrails.IsGlobalPromotionEligible"/>); ineligible
///         outputs are skipped entirely (never written as a sensitive global
///         skill).</item>
///   <item><strong>Re-redaction + audit</strong> — outputs are ingested through
///         the normal pipeline (redactor re-runs) and every run is recorded in
///         <c>dream_runs</c> against the global workstream.</item>
/// </list>
/// </summary>
public sealed class FleetConsolidator
{
    /// <summary>Default workstream id the global skill library is written to.</summary>
    public const string DefaultGlobalWorkstream = "mneme-global-skills";

    private readonly SqliteConnectionFactory _connections;
    private readonly IMemoryAgent _agent;
    private readonly IDreamer? _dreamer;
    private readonly WorkstreamConfigStore _config;
    private readonly TimeProvider _clock;

    public FleetConsolidator(
        SqliteConnectionFactory connections,
        IMemoryAgent agent,
        IDreamer? dreamer,
        WorkstreamConfigStore config,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _agent = agent;
        _dreamer = dreamer;
        _config = config;
        _clock = clock;
    }

    /// <summary>True if a host <see cref="IDreamer"/> is wired.</summary>
    public bool IsEnabled => _dreamer is not null;

    /// <summary>
    /// Run one fleet consolidation pass: mine opted-in workstreams' skills and
    /// promote eligible patterns into the global skill library.
    /// </summary>
    /// <param name="crossWorkstreamToken">A token with <see cref="CapabilityToken.CrossWorkstream"/> = true and a null workstream scope.</param>
    /// <param name="globalWorkstream">Workstream the global skills are written to. Defaults to <see cref="DefaultGlobalWorkstream"/>.</param>
    /// <param name="maxSkillsPerWorkstream">Cap on skills loaded per source workstream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="CapabilityDeniedError">If the token does not grant cross-workstream access.</exception>
    /// <exception cref="InvalidOperationException">If no <see cref="IDreamer"/> is registered.</exception>
    public async Task<FleetConsolidationSummary> ConsolidateFleetAsync(
        CapabilityToken crossWorkstreamToken,
        WorkstreamId? globalWorkstream = null,
        int maxSkillsPerWorkstream = 200,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(crossWorkstreamToken);
        if (_dreamer is null)
        {
            throw new InvalidOperationException(
                "Fleet consolidation requires an IDreamer. Register one with " +
                "services.AddSingleton<IDreamer>(sp => new YourDreamer(...));");
        }
        if (!(crossWorkstreamToken.CrossWorkstream && crossWorkstreamToken.Workstream is null))
        {
            throw new CapabilityDeniedError(
                "fleet consolidation requires a cross-workstream token (CrossWorkstream=true, Workstream=null)");
        }

        var globalWs = globalWorkstream ?? new WorkstreamId(DefaultGlobalWorkstream);
        var participating = _config.ListParticipatingWorkstreams()
            .Where(w => w.Value != globalWs.Value)
            .ToList();

        // Pool skills from every opted-in workstream (skills ride under Evidence).
        var events = new List<DistillationEvent>();
        foreach (var ws in participating)
        {
            events.AddRange(LoadSkillEvents(ws, maxSkillsPerWorkstream));
        }

        var produced = new List<EventId>();
        var skipped = 0;
        if (events.Count > 0)
        {
            var request = new DreamRequest(
                Workstream: globalWs,
                GeneratedAt: _clock.GetUtcNow(),
                Events: events,
                PriorSkills: LoadPriorGlobalSkills(globalWs, 200),
                OpenContradictions: Array.Empty<ContradictionCandidate>(),
                TokenBudget: 8192);

            var result = await _dreamer.DreamAsync(request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var classes = LoadClassifications(
                result.Outputs.SelectMany(o => o.DerivedFrom).Select(e => e.Value));

            foreach (var output in result.Outputs)
            {
                if (output.DerivedFrom is null || output.DerivedFrom.Count == 0) { skipped++; continue; }

                // Hard classification floor: a global skill may only come from
                // all-Public/Internal sources. Ineligible outputs are dropped
                // (never written as a sensitive "global" skill).
                if (!DreamGuardrails.IsGlobalPromotionEligible(output.DerivedFrom, classes))
                {
                    skipped++;
                    continue;
                }

                var envelope = new CaptureEvent(
                    EventId: new EventId("fleet-" + Guid.NewGuid().ToString("N")),
                    WorkstreamId: globalWs,
                    Channel: EventChannel.Epistemic,
                    ValidAt: _clock.GetUtcNow(),
                    RecordedAt: _clock.GetUtcNow(),
                    Payload: output.Payload,
                    Provenance: new CaptureProvenance(
                        Source: new CaptureSourceId("fleet/" + _dreamer.Id),
                        Principal: crossWorkstreamToken.Principal,
                        Context: "fleet-consolidation",
                        Citation: new Citation.Derived(output.DerivedFrom, _dreamer.Id)));

                var ingest = await _agent.IngestAsync(envelope, ct).ConfigureAwait(false);
                produced.Add(ingest.EventId);
                SetVisibility(ingest.EventId, globalWs, Visibility.Global);
            }
        }

        var runId = "fleet-run-" + Guid.NewGuid().ToString("N");
        RecordRun(runId, globalWs, _dreamer.Id, _clock.GetUtcNow(), events.Count, produced.Count);

        return new FleetConsolidationSummary(
            runId, _dreamer.Id, participating.Count, events.Count, produced, skipped);
    }

    private IReadOnlyList<DistillationEvent> LoadSkillEvents(WorkstreamId ws, int limit)
    {
        var list = new List<DistillationEvent>();
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        // Skill events (SkillPayload) that are non-revoked. Their events carry
        // the source classification we gate promotion on.
        cmd.CommandText = """
            SELECT e.event_id, e.category, e.classification, e.valid_at, e.created_at,
                   e.payload_json, e.provenance_json
            FROM projection_skills s
            JOIN memory_events e ON e.event_id = s.event_id
            LEFT JOIN memory_revocations r ON r.event_id = e.event_id
            WHERE s.workstream_id = $ws AND s.revoked_at IS NULL AND r.event_id IS NULL
            ORDER BY s.valid_at DESC
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

    private IReadOnlyList<PriorSkill> LoadPriorGlobalSkills(WorkstreamId globalWs, int limit)
    {
        var list = new List<PriorSkill>();
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, name, procedure, trigger
            FROM projection_skills
            WHERE workstream_id = $ws AND revoked_at IS NULL
            ORDER BY valid_at DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$ws", globalWs.Value);
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
        cmd.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(ids));
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

/// <summary>Summary of one <see cref="FleetConsolidator.ConsolidateFleetAsync"/> pass.</summary>
/// <param name="RunId">The audit run id.</param>
/// <param name="DreamerId">The dreamer that ran.</param>
/// <param name="WorkstreamsMined">How many opted-in workstreams were mined.</param>
/// <param name="SkillsConsidered">How many source skills were fed to the dreamer.</param>
/// <param name="Promoted">Event ids of the global skills produced.</param>
/// <param name="SkippedIneligible">Outputs dropped by the classification floor (or with no sources).</param>
public sealed record FleetConsolidationSummary(
    string RunId,
    string DreamerId,
    int WorkstreamsMined,
    int SkillsConsidered,
    IReadOnlyList<EventId> Promoted,
    int SkippedIneligible);
