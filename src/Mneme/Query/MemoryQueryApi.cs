using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest.Validation;
using Mneme.Observability;
using Mneme.Search;
using Mneme.Storage;

namespace Mneme.Query;

/// <summary>
/// Phase 4 implementation of <see cref="IMemoryQueryAPI"/>. Capability-
/// checked at every entry point. No raw-SQL escape on the public surface.
/// Bi-temporal: an explicit <c>QuerySpec.AsOf</c> trims results to the
/// state Mneme knew at that instant.
/// </summary>
/// <remarks>
/// <para>
/// Two retrieval paths:
/// <list type="bullet">
///   <item>If <c>QuerySpec.FreeText</c> is set, the dispatcher routes to
///         the FTS5 <see cref="TextSearchService"/>; capability + bi-
///         temporal filters are then applied to the candidates.</item>
///   <item>Otherwise the dispatcher does a projection scan over
///         <c>memory_events</c> with WHERE clauses on workstream,
///         channel, category, bi-temporal window, and revocation.</item>
/// </list>
/// </para>
/// <para>
/// Curation multiplier currently always 1.0 — Phase 7.5 will populate
/// it from the curation projection (mem-query-curation-weight-hook).
/// </para>
/// <para>
/// <see cref="DistillAsync"/> runs in degraded mode (returns a stub
/// bundle with <see cref="OrientationSummary.Paragraph"/> explaining
/// that the distillation worker has not yet shipped) until Phase 5
/// lands. The shape of the response is final.
/// </para>
/// </remarks>
public sealed class MemoryQueryApi : IMemoryQueryAPI
{
    private const int HardLimit = 500;
    private const string Distiller = "phase4-degraded";
    private const double SemanticThreshold = 0.1;

    private readonly SqliteConnectionFactory _connections;
    private readonly TextSearchService _search;
    private readonly TimeProvider _clock;

    /// <summary>Construct against the shared connection factory + text search service.</summary>
    public MemoryQueryApi(SqliteConnectionFactory connections, TextSearchService search)
        : this(connections, search, TimeProvider.System) { }

    /// <summary>Construct with a custom clock (tests).</summary>
    public MemoryQueryApi(SqliteConnectionFactory connections, TextSearchService search, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _search = search;
        _clock = clock;
    }

    /// <inheritdoc/>
    public Task<QueryResult> QueryAsync(QueryRequest request, CapabilityToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var spec = request.Spec;
        var now = _clock.GetUtcNow();
        var resolved = CapabilityEnforcement.Enforce(token, spec.Workstream, spec.Categories, spec.Channel, now);

        if (spec.Workstream is not null)
        {
            WorkstreamIdValidator.EnsureValid(spec.Workstream.Value.Value, "spec.Workstream");
        }
        var limit = Math.Clamp(spec.Limit, 1, HardLimit);

        using var activity = MnemeActivitySource.Source.StartActivity(
            MnemeActivitySource.QueryExecute, ActivityKind.Internal);
        activity?.SetTag("mneme.query.cross_workstream", resolved.CrossWorkstream);
        activity?.SetTag("mneme.query.has_free_text", !string.IsNullOrWhiteSpace(spec.FreeText));
        activity?.SetTag("mneme.query.as_of", spec.AsOf?.ToString("O"));

        var (items, totalMatched, dispatcher, candidatesConsidered, gatedOut) =
            string.IsNullOrWhiteSpace(spec.FreeText)
                ? StructuredScan(spec, resolved, limit, request.Explain)
                : FreeTextSearch(spec, resolved, limit, request.Explain);

        QueryExplain? explain = null;
        if (request.Explain)
        {
            explain = new QueryExplain(
                DispatcherChoice: dispatcher,
                CapabilityCheck: FormatCapability(token, resolved),
                CandidatesConsidered: candidatesConsidered,
                CandidatesGatedOut: gatedOut);
        }

        return Task.FromResult(new QueryResult(items, totalMatched, explain));
    }

    /// <inheritdoc/>
    public Task<ContextBundle> DistillAsync(WorkstreamId workstream, DistillOptions options, CapabilityToken token, CancellationToken ct = default)
    {
        WorkstreamIdValidator.EnsureValid(workstream.Value, nameof(workstream));
        _ = CapabilityEnforcement.Enforce(token, workstream, null, EventChannel.Epistemic, _clock.GetUtcNow());

        // Phase 4 ships a degraded bundle: the worker that produces real
        // synthesis lands in Phase 5. Consumers see a clear "no synthesis
        // available" Orientation; the shape is final so callers can
        // start integrating today.
        var now = _clock.GetUtcNow();
        var lastEventId = GetLastEventId(workstream) ?? EventId.None;
        return Task.FromResult(new ContextBundle(
            Workstream: workstream,
            Orientation: new OrientationSummary(
                Paragraph: "No synthesis available — the distillation worker (Phase 5) is not yet running. " +
                           "Use QueryAsync / ListRecentAsync for raw access in the meantime.",
                Distiller: Distiller,
                GeneratedAt: now,
                EventsCoveredThrough: lastEventId),
            Index: new BundleIndex(
                Distiller: Distiller,
                TokenBudget: options.TokenBudget ?? 0,
                TokenCount: 0,
                GeneratedAt: now,
                EventsCoveredThrough: lastEventId,
                SectionRefs: Array.Empty<BundleSectionRef>()),
            Sections: Array.Empty<BundleSection>(),
            Hints: new LookupHints(Array.Empty<LookupHint>()),
            GeneratedAt: now,
            EventsCoveredThrough: lastEventId,
            IsStale: true));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<QueryResultItem>> ListRecentAsync(WorkstreamId workstream, int limit, CapabilityToken token, CancellationToken ct = default)
    {
        WorkstreamIdValidator.EnsureValid(workstream.Value, nameof(workstream));
        var now = _clock.GetUtcNow();
        var resolved = CapabilityEnforcement.Enforce(token, workstream, null, EventChannel.Epistemic, now);
        limit = Math.Clamp(limit, 1, HardLimit);

        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT e.event_id, e.category, e.valid_at, e.created_at, e.payload_json,
                   r.revoked_at IS NOT NULL AS is_revoked
            FROM memory_events e
            LEFT JOIN memory_revocations r ON r.event_id = e.event_id
            WHERE e.workstream_id = $ws
              AND e.event_channel = 0
              AND e.category IN (SELECT value FROM json_each($cats))
            ORDER BY e.created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        cmd.Parameters.AddWithValue("$cats", System.Text.Json.JsonSerializer.Serialize(
            resolved.EffectiveCategories.Select(x => (int)x).ToArray()));
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<QueryResultItem>(limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.GetInt64(5) != 0) continue; // skip revoked
            var category = (EpistemicCategory)r.GetInt32(1);
            var payload = Storage.EventSerialization.DeserializePayload(r.GetString(4));
            results.Add(new QueryResultItem(
                EventId: new EventId(r.GetString(0)),
                Category: category,
                ValidAt: DateTimeOffset.Parse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                RecordedAt: DateTimeOffset.Parse(r.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                Summary: SummariseShort(payload),
                Score: 1.0,
                Annotations: Array.Empty<string>(),
                Details: null));
        }
        return Task.FromResult<IReadOnlyList<QueryResultItem>>(results);
    }

    private (IReadOnlyList<QueryResultItem> Items, int TotalMatched, string Dispatcher,
             int CandidatesConsidered, int GatedOut)
        StructuredScan(QuerySpec spec, ResolvedCapability resolved, int limit, bool explain)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        var sb = new StringBuilder("""
            SELECT e.event_id, e.workstream_id, e.category, e.valid_at, e.created_at, e.payload_json,
                   r.revoked_at IS NOT NULL AS is_revoked
            FROM memory_events e
            LEFT JOIN memory_revocations r ON r.event_id = e.event_id
            WHERE e.event_channel = $channel
              AND e.category IN (SELECT value FROM json_each($cats))
              AND (e.valid_at >= $from OR $from IS NULL)
              AND (e.valid_at <= $to   OR $to   IS NULL)
              AND ($asOf IS NULL OR e.created_at <= $asOf)
              AND ($asOf IS NULL OR e.invalid_at IS NULL OR e.invalid_at >  $asOf)
            """);
        if (!resolved.CrossWorkstream)
        {
            sb.AppendLine(" AND e.workstream_id = $ws");
        }
        sb.AppendLine(" ORDER BY e.valid_at DESC LIMIT $limit;");
        cmd.CommandText = sb.ToString();

        cmd.Parameters.AddWithValue("$channel", (int)spec.Channel);
        cmd.Parameters.AddWithValue("$cats", System.Text.Json.JsonSerializer.Serialize(
            resolved.EffectiveCategories.Select(x => (int)x).ToArray()));
        cmd.Parameters.AddWithValue("$from", (object?)spec.From?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)spec.To?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$asOf", (object?)spec.AsOf?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? DBNull.Value);
        if (!resolved.CrossWorkstream)
        {
            cmd.Parameters.AddWithValue("$ws", spec.Workstream!.Value.Value);
        }
        cmd.Parameters.AddWithValue("$limit", limit);

        var hits = new List<QueryResultItem>(limit);
        var considered = 0;
        var gated = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            considered++;
            if (r.GetInt64(6) != 0) { gated++; continue; }
            var payload = Storage.EventSerialization.DeserializePayload(r.GetString(5));
            const double score = 1.0; // structured scan: no relevance ranking
            const double multiplier = 1.0; // Phase 7.5 will populate from curation
            var details = explain
                ? new ScoreDetails(
                    Semantic: 0.0, Bm25: 0.0, EntityBoost: 0.0,
                    CurationMultiplier: multiplier,
                    Fused: score, Final: score * multiplier,
                    PassedSemanticThreshold: true,
                    GateReason: "structured scan: no semantic gate")
                : null;
            hits.Add(new QueryResultItem(
                EventId: new EventId(r.GetString(0)),
                Category: (EpistemicCategory)r.GetInt32(2),
                ValidAt: DateTimeOffset.Parse(r.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                RecordedAt: DateTimeOffset.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                Summary: SummariseShort(payload),
                Score: score * multiplier,
                Annotations: Array.Empty<string>(),
                Details: details));
        }
        return (hits, hits.Count, "structured", considered, gated);
    }

    private (IReadOnlyList<QueryResultItem> Items, int TotalMatched, string Dispatcher,
             int CandidatesConsidered, int GatedOut)
        FreeTextSearch(QuerySpec spec, ResolvedCapability resolved, int limit, bool explain)
    {
        if (resolved.CrossWorkstream)
        {
            // Cross-workstream free-text is intentionally unsupported in
            // v0 — the index is scoped per-workstream. Fall back to a
            // structured scan that respects the resolved capability.
            return StructuredScan(spec, resolved, limit, explain);
        }
        var workstream = spec.Workstream!.Value.Value;
        var raw = _search.Search(workstream, spec.FreeText!, limit);

        // Enrich each FTS hit with bi-temporal + revocation + capability checks.
        using var c = _connections.Open();
        var items = new List<QueryResultItem>(raw.Count);
        var gated = 0;
        foreach (var hit in raw)
        {
            using var lookup = c.CreateCommand();
            lookup.CommandText = """
                SELECT e.workstream_id, e.category, e.valid_at, e.created_at, e.payload_json,
                       e.event_channel, e.invalid_at, r.revoked_at
                FROM memory_events e
                LEFT JOIN memory_revocations r ON r.event_id = e.event_id
                WHERE e.event_id = $id;
                """;
            lookup.Parameters.AddWithValue("$id", hit.EventId.Value);
            using var rd = lookup.ExecuteReader();
            if (!rd.Read()) { gated++; continue; }
            var ws = rd.GetString(0);
            if (ws != workstream) { gated++; continue; }
            var category = (EpistemicCategory)rd.GetInt32(1);
            if (!resolved.EffectiveCategories.Contains(category)) { gated++; continue; }
            var channel = (EventChannel)rd.GetInt32(5);
            if (channel != spec.Channel) { gated++; continue; }
            if (!rd.IsDBNull(7)) { gated++; continue; } // revoked
            var validAt = DateTimeOffset.Parse(rd.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
            var recordedAt = DateTimeOffset.Parse(rd.GetString(3), System.Globalization.CultureInfo.InvariantCulture);
            if (spec.From is { } fromBound && validAt < fromBound) { gated++; continue; }
            if (spec.To   is { } toBound   && validAt > toBound)   { gated++; continue; }
            if (spec.AsOf is { } asOf)
            {
                if (recordedAt > asOf) { gated++; continue; }
                if (!rd.IsDBNull(6))
                {
                    var inv = DateTimeOffset.Parse(rd.GetString(6), System.Globalization.CultureInfo.InvariantCulture);
                    if (inv <= asOf) { gated++; continue; }
                }
            }
            var payload = Storage.EventSerialization.DeserializePayload(rd.GetString(4));

            const double curationMult = 1.0;
            var passedGate = hit.NormalizedBm25 >= SemanticThreshold;
            var details = explain
                ? new ScoreDetails(
                    Semantic: 0.0,
                    Bm25: hit.NormalizedBm25,
                    EntityBoost: 0.0,
                    CurationMultiplier: curationMult,
                    Fused: hit.Score,
                    Final: hit.Score * curationMult,
                    PassedSemanticThreshold: passedGate,
                    GateReason: passedGate ? null : "below normalized BM25 threshold")
                : null;
            items.Add(new QueryResultItem(
                EventId: hit.EventId,
                Category: category,
                ValidAt: validAt,
                RecordedAt: recordedAt,
                Summary: SummariseShort(payload),
                Score: hit.Score * curationMult,
                Annotations: Array.Empty<string>(),
                Details: details));
        }
        return (items, items.Count, "lexical-fts5", raw.Count, gated);
    }

    private EventId? GetLastEventId(WorkstreamId workstream)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT event_id FROM memory_events WHERE workstream_id = $ws ORDER BY created_at DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        var v = cmd.ExecuteScalar() as string;
        return v is null ? null : new EventId(v);
    }

    private static string SummariseShort(EventPayload p) => p switch
    {
        EvidencePayload e   => Trim(e.Content, 200),
        FactPayload f       => Trim(f.Statement, 200),
        DecisionPayload d   => Trim(d.Statement, 200),
        HypothesisPayload h => Trim(h.Statement, 200),
        GoalPayload g       => Trim(g.Statement, 200),
        ActionPayload a     => Trim(a.Statement, 200),
        OutcomePayload o    => Trim(o.Statement, 200),
        _                   => "(unknown payload)",
    };

    private static string Trim(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";

    private static string FormatCapability(CapabilityToken token, ResolvedCapability resolved)
    {
        var sb = new StringBuilder();
        sb.Append("principal=").Append(token.Principal.Value);
        sb.Append("; workstream=");
        sb.Append(resolved.ScopeWorkstream?.Value ?? (resolved.CrossWorkstream ? "<cross>" : "<unspecified>"));
        sb.Append("; categories=").Append(string.Join(',', resolved.EffectiveCategories));
        sb.Append("; includeTechnical=").Append(token.IncludeTechnical);
        return sb.ToString();
    }
}
