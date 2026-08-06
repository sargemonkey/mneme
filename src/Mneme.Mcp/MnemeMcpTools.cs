using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Mneme.Contracts;
using Mneme.Curation;

namespace Mneme.Mcp;

/// <summary>
/// MCP tool surface for Mneme. Names follow the community vocabulary
/// (see <c>research-design-lessons.md §2.8 + §4.5</c>) so the tools
/// are immediately discoverable by LLM clients trained on the
/// ecosystem: <c>remember</c>, <c>query</c>, <c>distill</c>,
/// <c>forget</c>, <c>list_recent</c>, <c>improve</c>.
/// </summary>
/// <remarks>
/// <para>
/// Annotations are explicit on every tool. The SDK defaults
/// (<c>DestructiveDefault=true</c>, <c>OpenWorldDefault=true</c>) are
/// wrong for read-only paths like <c>query</c>, so every method sets
/// all four annotation properties to the value it actually wants.
/// </para>
/// <para>
/// Capability tokens flow through DI. In stdio deployments the host
/// reads the token from <c>MNEME_CAPABILITY_TOKEN</c> at startup and
/// registers it as a singleton; tool methods receive it via parameter
/// injection. The HTTP / multi-client transport with per-request
/// tokens is a Phase 8 follow-up.
/// </para>
/// </remarks>
[McpServerToolType]
public static class MnemeMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [McpServerTool(
        Name = "remember",
        Title = "Remember an event in Mneme memory",
        Destructive = true, OpenWorld = false, ReadOnly = false, Idempotent = true)]
    [Description("""
        Persist a single event (Evidence by default) into Mneme's append-only memory log
        for the configured workstream. Call BEFORE the conversation moves on whenever the
        user has shared a fact, made a decision, or noted an outcome that future turns
        might benefit from. Idempotent ONLY on a caller-supplied event_id — re-calling with
        the same id is a no-op. Leaving event_id blank auto-generates a fresh ULID each
        call, so a blank-id retry is NOT idempotent (it appends a new event); supply a
        stable id if you need retry-safety.
        """)]
    public static async Task<string> Remember(
        IMemoryAgent agent,
        CapabilityToken token,
        [Description("Stable id for the event (idempotency key). Leave blank to auto-generate a ULID; note a blank id is not retry-idempotent.")] string? eventId,
        [Description("Free-text content to remember.")] string content,
        [Description("Optional source descriptor (URL, file path, plugin name).")] string? source = null,
        CancellationToken ct = default)
    {
        if (token.Workstream is null)
        {
            throw new InvalidOperationException("Server is not configured for a single workstream; pass one explicitly.");
        }
        var id = string.IsNullOrWhiteSpace(eventId) ? Mneme.Util.Ulid.NewUlid() : eventId!;
        var now = DateTimeOffset.UtcNow;
        var result = await agent.IngestAsync(new CaptureEvent(
            new EventId(id),
            token.Workstream.Value,
            EventChannel.Epistemic,
            ValidAt: now,
            RecordedAt: now,
            Payload: new EvidencePayload(content, source ?? "mcp"),
            Provenance: new CaptureProvenance(
                new CaptureSourceId("mcp"),
                token.Principal,
                Context: source)),
            ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            event_id = result.EventId.Value,
            recorded_at = result.RecordedAt,
            was_duplicate = result.WasDuplicate,
        }, JsonOptions);
    }

    [McpServerTool(
        Name = "query",
        Title = "Query Mneme memory (alias: recall)",
        Destructive = false, OpenWorld = false, ReadOnly = true, Idempotent = true)]
    [Description("""
        Call BEFORE responding to questions that may benefit from prior context. Returns
        ranked memory hits with score, summary, and event_id. Use the `freeText` parameter
        for natural-language search (routes through FTS5 with adaptive BM25 + recency
        weighting). Use the `asOf` parameter ("as of this instant") to retrieve the state
        Mneme knew at that point in time — useful for explaining past decisions.
        """)]
    public static async Task<string> Query(
        IMemoryQueryAPI api,
        CapabilityToken token,
        [Description("Optional free-text natural-language query. Routes through FTS5.")] string? freeText = null,
        [Description("Optional ISO 8601 timestamp — return state as Mneme knew it at this instant.")] string? asOf = null,
        [Description("Maximum results to return. Default 25, max 500.")] int limit = 25,
        [Description("Set true to include score-decomposition diagnostics.")] bool explain = false,
        CancellationToken ct = default)
    {
        DateTimeOffset? asOfParsed = null;
        if (!string.IsNullOrWhiteSpace(asOf))
        {
            if (!DateTimeOffset.TryParse(asOf, out var t)) throw new ArgumentException("asOf must be ISO 8601.", nameof(asOf));
            asOfParsed = t;
        }
        var spec = new QuerySpec(
            Workstream: token.Workstream,
            FreeText: freeText,
            AsOf: asOfParsed,
            Limit: limit);
        var result = await api.QueryAsync(new QueryRequest(spec, Explain: explain), token, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            total_matched = result.TotalMatched,
            explain = result.Explain,
            items = result.Items.Select(i => new
            {
                event_id = i.EventId.Value,
                category = i.Category.ToString(),
                summary = i.Summary,
                score = i.Score,
                valid_at = i.ValidAt,
                recorded_at = i.RecordedAt,
                details = i.Details,
            }),
        }, JsonOptions);
    }

    [McpServerTool(
        Name = "list_recent",
        Title = "List most-recent memories",
        Destructive = false, OpenWorld = false, ReadOnly = true, Idempotent = true)]
    [Description("""
        List the most-recently-recorded events in the configured workstream. Useful when
        you want to check whether something has already been remembered before calling
        `remember` again with the same content.
        """)]
    public static async Task<string> ListRecent(
        IMemoryQueryAPI api,
        CapabilityToken token,
        [Description("Maximum events to return. Default 25, max 500.")] int limit = 25,
        CancellationToken ct = default)
    {
        if (token.Workstream is null)
            throw new InvalidOperationException("Server is not configured for a single workstream; pass one explicitly.");
        var items = await api.ListRecentAsync(token.Workstream.Value, limit, token, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(items.Select(i => new
        {
            event_id = i.EventId.Value,
            category = i.Category.ToString(),
            summary = i.Summary,
            valid_at = i.ValidAt,
            recorded_at = i.RecordedAt,
        }), JsonOptions);
    }

    [McpServerTool(
        Name = "distill",
        Title = "Get a synthesized context bundle for the workstream",
        Destructive = false, OpenWorld = false, ReadOnly = true, Idempotent = true)]
    [Description("""
        Returns a compact, decision-useful synthesis of the workstream's memory (an
        OrientationSummary + section index + bullets). Until Phase 5 ships, this returns
        a "degraded" stub bundle that names the missing distillation worker so the
        calling LLM can fall back to `query`/`list_recent` instead.
        """)]
    public static async Task<string> Distill(
        IMemoryQueryAPI api,
        CapabilityToken token,
        [Description("Force refresh even if a cached bundle is available. Useful after curation.")] bool forceRefresh = false,
        [Description("Soft token budget. null = use the agent default.")] int? tokenBudget = null,
        CancellationToken ct = default)
    {
        if (token.Workstream is null)
            throw new InvalidOperationException("Server is not configured for a single workstream; pass one explicitly.");
        var bundle = await api.DistillAsync(token.Workstream.Value,
            new DistillOptions(forceRefresh, tokenBudget), token, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            workstream = bundle.Workstream.Value,
            orientation = bundle.Orientation.Paragraph,
            section_count = bundle.Sections.Count,
            is_stale = bundle.IsStale,
            generated_at = bundle.GeneratedAt,
            events_covered_through = bundle.EventsCoveredThrough.Value,
        }, JsonOptions);
    }

    [McpServerTool(
        Name = "forget",
        Title = "Revoke an event (tombstone)",
        Destructive = true, OpenWorld = false, ReadOnly = false, Idempotent = true)]
    [Description("""
        Revoke a remembered event by id. The event's metadata stays in the log (audit
        trail), but any associated body content is zeroed and the event is filtered out
        of subsequent queries. Use this when the user asks Mneme to "forget" something
        for privacy or accuracy reasons. Idempotent — revoking the same event twice
        returns the original revocation metadata.
        """)]
    public static async Task<string> Forget(
        Mneme.Revocation.IRevocationService revocation,
        CapabilityToken token,
        [Description("Event id to revoke.")] string eventId,
        [Description("Reason for revocation (recorded verbatim in the audit log).")] string reason,
        CancellationToken ct = default)
    {
        if (token.Workstream is null)
            throw new InvalidOperationException("Server is not configured for a single workstream; pass one explicitly.");
        var result = await revocation.RevokeAsync(
            new EventId(eventId), token.Workstream.Value, token.Principal, reason, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            event_id = result.EventId.Value,
            revoked_at = result.RevokedAt,
            already_revoked = result.AlreadyRevoked,
            body_zeroed = result.BodyZeroed,
        }, JsonOptions);
    }

    [McpServerTool(
        Name = "improve",
        Title = "Curate (amend / annotate / pin / demote / revert) a memory",
        Destructive = true, OpenWorld = false, ReadOnly = false, Idempotent = true)]
    [Description("""
        HITL curation surface. Use when the user has flagged a memory as wrong, stale,
        or worth highlighting. ALWAYS elicit explicit user confirmation before
        destructive operations (`amend`). Operations:
          amend     — replace a fact's content (requires `newContent`; pre-state hash
                      computed server-side so the call is race-safe).
          annotate  — attach human commentary to an event (non-destructive).
          pin       — boost retrieval weight (multiplier > 1.0, default 2.0).
          demote    — suppress retrieval weight (0.0 < multiplier < 1.0, default 0.3).
          revert    — undo a previous curation by its curation_event_id.
        """)]
    public static async Task<string> Improve(
        IMemoryCurator curator,
        CurationCapability cap,
        Mneme.Storage.SqliteConnectionFactory factory,
        [Description("One of: amend, annotate, pin, demote, revert.")] string operation,
        [Description("Event id (for amend/annotate/pin/demote) or curation_event_id (for revert).")] string targetId,
        [Description("Required for amend.")] string? newContent = null,
        [Description("Annotation text (annotate) / rationale (amend/revert).")] string? rationale = null,
        [Description("Multiplier for pin (>1.0) or demote (0.0..1.0). Defaults: pin=2.0, demote=0.3.")] float? multiplier = null,
        CancellationToken ct = default)
    {
        var target = new EventId(targetId);
        CurationResult result = operation.ToLowerInvariant() switch
        {
            "amend" => await curator.AmendFactAsync(
                new FactId(targetId),
                PreStateHasher.ComputeHash(factory, target),
                new FactAmendment(newContent ?? throw new ArgumentException("newContent required for amend"),
                                  rationale ?? "amend via MCP"),
                cap, ct).ConfigureAwait(false),
            "annotate" => await curator.AnnotateAsync(target,
                rationale ?? throw new ArgumentException("rationale required for annotate"),
                cap, ct).ConfigureAwait(false),
            "pin" => await curator.PinAsync(target, PinScope.Workstream, multiplier ?? 2.0f, cap, ct).ConfigureAwait(false),
            "demote" => await curator.DemoteAsync(target, multiplier ?? 0.3f, cap, ct).ConfigureAwait(false),
            "revert" => await curator.RevertCurationAsync(target, rationale ?? "revert via MCP", cap, ct).ConfigureAwait(false),
            _ => throw new ArgumentException(
                $"Unknown operation '{operation}'. Use one of: amend, annotate, pin, demote, revert.",
                nameof(operation)),
        };
        return JsonSerializer.Serialize(new
        {
            curation_event_id = result.CurationEventId.Value,
            recorded_at = result.RecordedAt,
            pre_state_hash = result.PreStateHash,
        }, JsonOptions);
    }

    [McpServerTool(
        Name = "distill_session",
        Title = "Distill new session entries into Mneme memory",
        Destructive = true, OpenWorld = false, ReadOnly = false, Idempotent = true)]
    [Description("""
        Hand Mneme the entries that have accumulated in the agent's session context since the
        last distillation watermark for `session_id`. Mneme will pass them through the host's
        registered session distiller, ingest any epistemic events the distiller chose to extract
        (each citing the source entry range), and atomically advance the watermark. Idempotent
        on (session_id, from_entry_id, to_entry_id): re-calling with the same range is a no-op.

        `entries` is a JSON array of {entry_id, timestamp, kind, text, source_ref?} objects.
        `kind` is one of: UserMessage, AssistantMessage, FileContent, ToolCall, ToolResult,
        SubAgentOutput, SystemNote, External. `entry_id` must be monotonic within the session.

        Use `get_watermark` first to discover the last-distilled entry id, then send the
        entries strictly after it.
        """)]
    public static async Task<string> DistillSession(
        IMemoryAgent agent,
        CapabilityToken token,
        [Description("Session id whose context is being distilled.")] string sessionId,
        [Description("JSON array of context entries. See description for shape.")] string entries,
        CancellationToken ct = default)
    {
        if (token.Workstream is null)
        {
            throw new InvalidOperationException("Server is not configured for a single workstream.");
        }
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("session_id is required.", nameof(sessionId));
        }
        var parsed = ParseEntries(entries);
        var result = await agent.DistillSessionAsync(
            new SessionId(sessionId), parsed, token, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            new_events = result.NewEvents.Select(e => e.Value),
            new_watermark = new
            {
                session_id = result.NewWatermark.Session.Value,
                last_entry_id = result.NewWatermark.LastDistilledEntryId,
                distilled_at = result.NewWatermark.DistilledAt,
                distiller_version = result.NewWatermark.DistillerVersion,
            },
            dropped = result.Dropped?.Select(d => new { entry_id = d.EntryId, reason = d.Reason }),
            was_no_op = result.WasNoOp,
        }, JsonOptions);
    }

    [McpServerTool(
        Name = "get_watermark",
        Title = "Read the distillation watermark for a session",
        Destructive = false, OpenWorld = false, ReadOnly = true, Idempotent = true)]
    [Description("""
        Return the last-distilled entry id for `session_id`, or null if the session has never
        been distilled. Call before `distill_session` to know which entries are new.
        """)]
    public static async Task<string> GetWatermark(
        IMemoryAgent agent,
        [Description("Session id to query.")] string sessionId,
        CancellationToken ct = default)
    {
        var w = await agent.GetWatermarkAsync(new SessionId(sessionId), ct).ConfigureAwait(false);
        if (w is null) return "null";
        return JsonSerializer.Serialize(new
        {
            session_id = w.Session.Value,
            last_entry_id = w.LastDistilledEntryId,
            distilled_at = w.DistilledAt,
            distiller_version = w.DistillerVersion,
        }, JsonOptions);
    }

    private static IReadOnlyList<ContextEntry> ParseEntries(string entries)
    {
        var list = new List<ContextEntry>();
        using var doc = JsonDocument.Parse(entries);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("entries must be a JSON array.", nameof(entries));
        }
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var entryId = GetRequiredString(el, "entry_id");
            var ts = DateTimeOffset.Parse(GetRequiredString(el, "timestamp"), System.Globalization.CultureInfo.InvariantCulture);
            var kindStr = GetRequiredString(el, "kind");
            if (!Enum.TryParse<ContextEntryKind>(kindStr, ignoreCase: true, out var kind))
            {
                throw new ArgumentException($"Unknown kind '{kindStr}' (valid: UserMessage, AssistantMessage, FileContent, ToolCall, ToolResult, SubAgentOutput, SystemNote, External).", nameof(entries));
            }
            var text = GetRequiredString(el, "text");
            string? sourceRef = el.TryGetProperty("source_ref", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            list.Add(new ContextEntry(entryId, ts, kind, text, sourceRef));
        }
        return list;
    }

    private static string GetRequiredString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"entry missing required string field '{name}'.");
        }
        return v.GetString() ?? throw new ArgumentException($"entry field '{name}' is null.");
    }
}
