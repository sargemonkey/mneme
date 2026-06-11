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
        might benefit from. Idempotent on the supplied event_id — re-calling with the same
        id is a no-op.
        """)]
    public static async Task<string> Remember(
        IMemoryAgent agent,
        CapabilityToken token,
        [Description("Stable id for the event (idempotency key). Leave blank to auto-generate a ULID-like id.")] string? eventId,
        [Description("Free-text content to remember.")] string content,
        [Description("Optional source descriptor (URL, file path, plugin name).")] string? source = null,
        CancellationToken ct = default)
    {
        if (token.Workstream is null)
        {
            throw new InvalidOperationException("Server is not configured for a single workstream; pass one explicitly.");
        }
        var id = string.IsNullOrWhiteSpace(eventId) ? "mcp-" + Guid.NewGuid().ToString("N") : eventId!;
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
}
