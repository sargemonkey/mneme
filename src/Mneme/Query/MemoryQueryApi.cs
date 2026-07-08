using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest.Validation;
using Mneme.Observability;
using Mneme.Resolution;
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
    private const int RerankPool = 150;

    private readonly SqliteConnectionFactory _connections;
    private readonly TextSearchService _search;
    private readonly TimeProvider _clock;
    private readonly IDistiller? _distiller;
    private readonly VectorIndex? _vectors;
    private readonly IReranker? _reranker;
    private readonly bool _subjectBoost;
    private readonly Mneme.Distillation.DistillationRequestBuilder _requestBuilder;
    private readonly Mneme.Distillation.DistillationCache _cache;

    /// <summary>Construct against the shared connection factory + text search service.</summary>
    public MemoryQueryApi(SqliteConnectionFactory connections, TextSearchService search)
        : this(connections, search, TimeProvider.System, distiller: null, vectors: null, reranker: null) { }

    /// <summary>Construct with a custom clock (tests).</summary>
    public MemoryQueryApi(SqliteConnectionFactory connections, TextSearchService search, TimeProvider clock)
        : this(connections, search, clock, distiller: null, vectors: null, reranker: null) { }

    /// <summary>Construct with an optional <see cref="IDistiller"/> (no vector index).</summary>
    public MemoryQueryApi(SqliteConnectionFactory connections, TextSearchService search, TimeProvider clock, IDistiller? distiller)
        : this(connections, search, clock, distiller, vectors: null, reranker: null) { }

    /// <summary>Construct with an optional <see cref="IDistiller"/> and <see cref="VectorIndex"/>.</summary>
    public MemoryQueryApi(SqliteConnectionFactory connections, TextSearchService search, TimeProvider clock, IDistiller? distiller, VectorIndex? vectors)
        : this(connections, search, clock, distiller, vectors, reranker: null) { }

    /// <summary>Construct with everything including an optional <see cref="IReranker"/>.</summary>
    public MemoryQueryApi(SqliteConnectionFactory connections, TextSearchService search, TimeProvider clock, IDistiller? distiller, VectorIndex? vectors, IReranker? reranker)
        : this(connections, search, clock, distiller, vectors, reranker, subjectBoost: true) { }

    /// <summary>Construct with everything, controlling the subject-attribution boost.</summary>
    public MemoryQueryApi(SqliteConnectionFactory connections, TextSearchService search, TimeProvider clock, IDistiller? distiller, VectorIndex? vectors, IReranker? reranker, bool subjectBoost)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _search = search;
        _clock = clock;
        _distiller = distiller;
        _vectors = vectors;
        _reranker = reranker;
        _subjectBoost = subjectBoost;
        _requestBuilder = new Mneme.Distillation.DistillationRequestBuilder(connections);
        _cache = new Mneme.Distillation.DistillationCache(connections);
    }

    /// <inheritdoc/>
    public async Task<QueryResult> QueryAsync(QueryRequest request, CapabilityToken token, CancellationToken ct = default)
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

        // When a reranker is registered and the query has free text, retrieve a
        // wider candidate pool first, then let the cross-encoder pick the final
        // top-k (two-stage retrieve-then-rerank).
        var hasFreeText = !string.IsNullOrWhiteSpace(spec.FreeText);
        var willRerank = _reranker is not null && hasFreeText;
        var retrieveLimit = willRerank ? Math.Min(HardLimit, Math.Max(limit, RerankPool)) : limit;

        using var activity = MnemeActivitySource.Source.StartActivity(
            MnemeActivitySource.QueryExecute, ActivityKind.Internal);
        activity?.SetTag("mneme.query.cross_workstream", resolved.CrossWorkstream);
        activity?.SetTag("mneme.query.has_free_text", hasFreeText);
        activity?.SetTag("mneme.query.as_of", spec.AsOf?.ToString("O"));

        (IReadOnlyList<QueryResultItem> Items, int TotalMatched, string Dispatcher, int Considered, int Gated) outcome;
        if (string.IsNullOrWhiteSpace(spec.FreeText))
        {
            outcome = StructuredScan(spec, resolved, retrieveLimit, request.Explain);
        }
        else if (_vectors is { IsEnabled: true } && !resolved.CrossWorkstream)
        {
            outcome = await HybridSearch(spec, resolved, retrieveLimit, request.Explain, ct).ConfigureAwait(false);
        }
        else
        {
            outcome = FreeTextSearch(spec, resolved, retrieveLimit, request.Explain);
        }
        var (items, totalMatched, dispatcher, candidatesConsidered, gatedOut) = outcome;

        if (willRerank && items.Count > 0)
        {
            items = await RerankAsync(spec.FreeText!, items, limit, ct).ConfigureAwait(false);
            dispatcher += "+rerank";
            totalMatched = items.Count;
        }

        QueryExplain? explain = null;
        if (request.Explain)
        {
            explain = new QueryExplain(
                DispatcherChoice: dispatcher,
                CapabilityCheck: FormatCapability(token, resolved),
                CandidatesConsidered: candidatesConsidered,
                CandidatesGatedOut: gatedOut);
        }

        // Answer-context supplement (the validated win): surface subject-scoped
        // triples for the entities named in the query as an APPEND-ONLY list the
        // consumer can add alongside the ranked items. The semantic result above
        // is untouched — nothing is displaced. Only for free-text queries within
        // a single workstream (subject scoping needs the query text + the
        // workstream's triple projection).
        IReadOnlyList<SubjectTripleHit>? subjectTriples = null;
        if (request.SupplementSubjectTriples && hasFreeText && spec.Workstream is not null && !resolved.CrossWorkstream)
        {
            subjectTriples = LoadSubjectTripleSupplement(spec.Workstream.Value, spec.FreeText!, limit);
        }

        return new QueryResult(items, totalMatched, explain, subjectTriples);
    }

    /// <inheritdoc/>
    public async Task<ContextBundle> DistillAsync(WorkstreamId workstream, DistillOptions options, CapabilityToken token, CancellationToken ct = default)
    {
        WorkstreamIdValidator.EnsureValid(workstream.Value, nameof(workstream));
        _ = CapabilityEnforcement.Enforce(token, workstream, null, EventChannel.Epistemic, _clock.GetUtcNow());

        var now = _clock.GetUtcNow();
        var budget = options.TokenBudget ?? 4096;

        // Cache hit?
        if (!options.ForceRefresh)
        {
            var cached = _cache.TryLoad(workstream);
            if (cached is not null && _cache.IsFresh(workstream, cached))
            {
                return cached;
            }
        }

        var request = _requestBuilder.Build(workstream, budget, _cache.TryLoad(workstream), now);

        if (request.Events.Count == 0)
        {
            return EmptyBundle(workstream, request.EventsCoveredThrough, now, budget);
        }

        ContextBundle bundle;
        if (_distiller is null)
        {
            // No host distiller registered — surface the degraded fallback
            // so consumers see a working (if mechanical) bundle and a
            // clear hint about how to upgrade.
            bundle = Mneme.Distillation.DistillationPromptBuilder.BuildHeuristicBundle(request);
        }
        else
        {
            bundle = await _distiller.DistillAsync(request, ct).ConfigureAwait(false);
            // Stamp the distiller id on the bundle even if the host's
            // distiller forgot to.
            bundle = bundle with
            {
                Orientation = bundle.Orientation with { Distiller = _distiller.Id },
                Index = bundle.Index with { Distiller = _distiller.Id },
            };
        }

        _cache.Save(workstream, bundle);
        return bundle;
    }

    private static ContextBundle EmptyBundle(WorkstreamId ws, EventId covered, DateTimeOffset now, int budget)
    {
        var distillerId = "mneme/empty";
        var orientation = new OrientationSummary(
            Paragraph: "Workstream is empty — nothing to distill yet.",
            Distiller: distillerId,
            GeneratedAt: now,
            EventsCoveredThrough: covered);
        return new ContextBundle(
            Workstream: ws,
            Orientation: orientation,
            Index: new BundleIndex(distillerId, budget, 0, now, covered, Array.Empty<BundleSectionRef>()),
            Sections: Array.Empty<BundleSection>(),
            Hints: new LookupHints(Array.Empty<LookupHint>()),
            GeneratedAt: now,
            EventsCoveredThrough: covered,
            IsStale: false);
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
        // Single-category queries use an equality predicate so SQLite can
        // serve the ORDER BY valid_at directly from
        // idx_memory_events_category (workstream_id, category, valid_at)
        // and stop at LIMIT — avoiding a full temp-b-tree sort of every
        // matching row. The IN-subquery form (needed for multi-category)
        // forces that sort because the planner can't merge index-ordered
        // streams across the IN list. Benchmarked: ~17ms → ~sub-ms at 10k
        // events for the common single-category case.
        var effective = resolved.EffectiveCategories;
        var singleCategory = effective.Count == 1;
        var categoryPredicate = singleCategory
            ? "e.category = $cat"
            : "e.category IN (SELECT value FROM json_each($cats))";
        var sb = new StringBuilder($"""
            SELECT e.event_id, e.workstream_id, e.category, e.valid_at, e.created_at, e.payload_json,
                   r.revoked_at IS NOT NULL AS is_revoked
            FROM memory_events e
            LEFT JOIN memory_revocations r ON r.event_id = e.event_id
            WHERE e.event_channel = $channel
              AND {categoryPredicate}
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
        if (singleCategory)
        {
            cmd.Parameters.AddWithValue("$cat", (int)effective.First());
        }
        else
        {
            cmd.Parameters.AddWithValue("$cats", System.Text.Json.JsonSerializer.Serialize(
                effective.Select(x => (int)x).ToArray()));
        }
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

    // Semantic + lexical hybrid retrieval. Runs when a host IEmbeddingProvider
    // is registered (VectorIndex.IsEnabled). Fuses cosine semantic score with
    // normalized BM25 and recency — the multi-signal approach LoCoMo-grade
    // retrieval needs (paraphrases that share no keywords still match).
    private async Task<(IReadOnlyList<QueryResultItem> Items, int TotalMatched, string Dispatcher, int Considered, int Gated)>
        HybridSearch(QuerySpec spec, ResolvedCapability resolved, int limit, bool explain, CancellationToken ct)
    {
        const double wSemantic = 0.65;
        const double wBm25 = 0.35;
        const double wSubject = 0.20; // additive boost for subject-attributed matches
        var workstream = spec.Workstream!.Value;
        var pool = Math.Min(200, Math.Max(limit * 8, 64));

        var semantic = await _vectors!.SearchAsync(workstream, spec.FreeText!, pool, ct).ConfigureAwait(false);
        var lexical = _search.Search(workstream.Value, spec.FreeText!, pool);

        var semMap = semantic.ToDictionary(h => h.EventId.Value, h => h);
        var lexMap = lexical.ToDictionary(h => h.EventId.Value, h => h);

        // Subject-scoped attribution: events carrying a fact triple whose subject
        // matches an entity named in the query. These are structurally ABOUT the
        // asked-about person, so they get an additive boost that floats the right
        // sub-graph above distractor facts that merely mention the same names.
        // Events already retrieved semantically/lexically are always boosted;
        // subject-ONLY events (no semantic/lexical hit) are injected but capped so
        // a prolific entity's facts can't flood a small result window — mirroring
        // the validated "supplement, don't replace" benchmark result.
        var subjectKeys = SubjectKey.ExtractSubjects(spec.FreeText);
        var subjectEvents = _subjectBoost ? LoadSubjectScopedEvents(workstream, subjectKeys) : new HashSet<string>(StringComparer.Ordinal);
        var subjectInjectCap = Math.Max(6, limit / 3);
        var subjectOnly = subjectEvents.Where(id => !semMap.ContainsKey(id) && !lexMap.ContainsKey(id));
        var admittedSubjectOnly = new HashSet<string>(subjectOnly.Take(subjectInjectCap), StringComparer.Ordinal);

        var fused = new List<(string EventId, double Semantic, double Bm25, double SubjectBoost, double Fused, double Recency, double Final)>();
        foreach (var id in semMap.Keys.Union(lexMap.Keys).Union(admittedSubjectOnly))
        {
            var sem = semMap.TryGetValue(id, out var sh) ? sh.Semantic : 0.0;
            var bm25 = lexMap.TryGetValue(id, out var lh) ? lh.NormalizedBm25 : 0.0;
            var recency = semMap.TryGetValue(id, out var s2) ? s2.RecencyWeight
                        : lexMap.TryGetValue(id, out var l2) ? l2.RecencyWeight
                        : 1.0;
            var subjectBoost = subjectEvents.Contains(id) ? wSubject : 0.0;
            var fusedScore = (wSemantic * sem) + (wBm25 * bm25) + subjectBoost;
            fused.Add((id, sem, bm25, subjectBoost, fusedScore, recency, fusedScore * recency));
        }
        fused.Sort(static (a, b) => b.Final.CompareTo(a.Final));

        using var c = _connections.Open();
        var items = new List<QueryResultItem>(limit);
        var gated = 0;
        foreach (var cand in fused)
        {
            if (items.Count >= limit) break;
            if (cand.Fused < SemanticThreshold) { gated++; continue; }

            using var lookup = c.CreateCommand();
            lookup.CommandText = """
                SELECT e.category, e.valid_at, e.created_at, e.payload_json,
                       e.event_channel, e.invalid_at, r.revoked_at
                FROM memory_events e
                LEFT JOIN memory_revocations r ON r.event_id = e.event_id
                WHERE e.event_id = $id AND e.workstream_id = $ws;
                """;
            lookup.Parameters.AddWithValue("$id", cand.EventId);
            lookup.Parameters.AddWithValue("$ws", workstream.Value);
            using var rd = lookup.ExecuteReader();
            if (!rd.Read()) { gated++; continue; }
            var category = (EpistemicCategory)rd.GetInt32(0);
            if (!resolved.EffectiveCategories.Contains(category)) { gated++; continue; }
            var channel = (EventChannel)rd.GetInt32(4);
            if (channel != spec.Channel) { gated++; continue; }
            if (!rd.IsDBNull(6)) { gated++; continue; } // revoked
            var validAt = DateTimeOffset.Parse(rd.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
            var recordedAt = DateTimeOffset.Parse(rd.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
            if (spec.From is { } fromBound && validAt < fromBound) { gated++; continue; }
            if (spec.To   is { } toBound   && validAt > toBound)   { gated++; continue; }
            if (spec.AsOf is { } asOf)
            {
                if (recordedAt > asOf) { gated++; continue; }
                if (!rd.IsDBNull(5))
                {
                    var inv = DateTimeOffset.Parse(rd.GetString(5), System.Globalization.CultureInfo.InvariantCulture);
                    if (inv <= asOf) { gated++; continue; }
                }
            }
            var payload = Storage.EventSerialization.DeserializePayload(rd.GetString(3));
            const double curationMult = 1.0;
            var details = explain
                ? new ScoreDetails(
                    Semantic: cand.Semantic,
                    Bm25: cand.Bm25,
                    EntityBoost: cand.SubjectBoost,
                    CurationMultiplier: curationMult,
                    Fused: cand.Fused,
                    Final: cand.Final * curationMult,
                    PassedSemanticThreshold: true,
                    GateReason: null)
                : null;
            items.Add(new QueryResultItem(
                EventId: new EventId(cand.EventId),
                Category: category,
                ValidAt: validAt,
                RecordedAt: recordedAt,
                Summary: SummariseShort(payload),
                Score: cand.Final * curationMult,
                Annotations: Array.Empty<string>(),
                Details: details));
        }
        return (items, items.Count, "hybrid-semantic-bm25", fused.Count, gated);
    }

    // Subject-scoped triple supplement for the answer context: structured triples
    // whose subject matches an entity named in the query, capped and de-duplicated.
    // Returned as an append-only list (QueryResult.SubjectTriples) so a consumer
    // can add the queried person's attributed facts alongside the ranked items
    // without displacing them. Ordered most-recent-first; excludes revoked triples.
    private IReadOnlyList<SubjectTripleHit> LoadSubjectTripleSupplement(WorkstreamId workstream, string freeText, int limit)
    {
        var subjectKeys = SubjectKey.ExtractSubjects(freeText);
        if (subjectKeys.Count == 0) return Array.Empty<SubjectTripleHit>();

        var cap = Math.Max(6, limit / 3);
        var hits = new List<SubjectTripleHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var c = _connections.Open();
        foreach (var key in subjectKeys)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT subject_text, predicate, object, valid_at, event_id
                FROM projection_fact_triples
                WHERE workstream_id = $ws
                  AND revoked_at IS NULL
                  AND (subject_key LIKE '%' || $k || '%' OR $k LIKE '%' || subject_key || '%')
                ORDER BY valid_at DESC;
                """;
            cmd.Parameters.AddWithValue("$ws", workstream.Value);
            cmd.Parameters.AddWithValue("$k", key);
            using var rd = cmd.ExecuteReader();
            while (rd.Read() && hits.Count < cap)
            {
                var subject = rd.GetString(0);
                var predicate = rd.GetString(1);
                var obj = rd.GetString(2);
                var dedup = $"{subject}\u0001{predicate}\u0001{obj}";
                if (!seen.Add(dedup)) continue;
                var validAt = DateTimeOffset.Parse(rd.GetString(3), System.Globalization.CultureInfo.InvariantCulture);
                hits.Add(new SubjectTripleHit(
                    new FactTriple(subject, predicate, obj), validAt, new EventId(rd.GetString(4))));
            }
            if (hits.Count >= cap) break;
        }
        return hits;
    }

    // Event ids in the workstream that carry a non-revoked fact triple whose
    // subject matches any of the query subject keys. Matching is bidirectional
    // substring ("melanie" matches subject "melanie grandma" and vice versa) so
    // a possessive-chain question still reaches the base entity's facts. The
    // triple table is per-workstream and small; a LIKE scan is fine at our scale.
    private HashSet<string> LoadSubjectScopedEvents(WorkstreamId workstream, IReadOnlyList<string> subjectKeys)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (subjectKeys.Count == 0) return ids;

        using var c = _connections.Open();
        foreach (var key in subjectKeys)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT event_id FROM projection_fact_triples
                WHERE workstream_id = $ws
                  AND revoked_at IS NULL
                  AND (subject_key LIKE '%' || $k || '%' OR $k LIKE '%' || subject_key || '%');
                """;
            cmd.Parameters.AddWithValue("$ws", workstream.Value);
            cmd.Parameters.AddWithValue("$k", key);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) ids.Add(rd.GetString(0));
        }
        return ids;
    }

    // Two-stage reranking: hand the retrieved candidate pool to the host
    // reranker (cross-encoder / hosted rerank API / LLM) and keep its top-k
    // ordering. The rerank score replaces the retrieval score; if the reranker
    // returns an id the retriever didn't (it shouldn't), that id is ignored.
    private async Task<IReadOnlyList<QueryResultItem>> RerankAsync(
        string query, IReadOnlyList<QueryResultItem> items, int limit, CancellationToken ct)
    {
        var candidates = items.Select(i => new RerankCandidate(i.EventId, i.Summary)).ToList();
        var ranked = await _reranker!.RerankAsync(query, candidates, limit, ct).ConfigureAwait(false);

        var byId = items.ToDictionary(i => i.EventId.Value);
        var result = new List<QueryResultItem>(Math.Min(limit, ranked.Count));
        foreach (var r in ranked)
        {
            if (!byId.TryGetValue(r.EventId.Value, out var item)) continue;
            var details = item.Details is { } d
                ? d with { Final = r.Score, GateReason = "reranked" }
                : null;
            result.Add(item with { Score = r.Score, Details = details });
            if (result.Count >= limit) break;
        }
        // Defensive: if the reranker dropped everything, fall back to the
        // retrieval order trimmed to limit.
        return result.Count > 0 ? result : items.Take(limit).ToList();
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
