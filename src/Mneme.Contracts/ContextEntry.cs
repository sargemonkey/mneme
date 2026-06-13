namespace Mneme.Contracts;

/// <summary>
/// One slot in an agent session's monotonically growing context buffer.
/// The host harvests these from its chat client / tool runtime / sub-agent
/// runner and hands them to <see cref="IMemoryAgent.DistillSessionAsync"/>
/// in batches — exactly the slice between the last-known watermark and
/// "right now."
/// </summary>
/// <remarks>
/// <para>
/// Mneme never stores the entry text. The host's chat log is the source of
/// truth; Mneme stores only the distilled epistemic interpretation plus a
/// <see cref="Citation.SessionRange"/> pointer so the original entries can
/// be re-fetched on demand. This keeps the SDK's storage footprint small
/// and avoids duplicating data that already lives in the host.
/// </para>
/// <para>
/// <see cref="EntryId"/> must be monotonic within a session — typically a
/// 0-padded ordinal or a ULID. The watermark advances by storing the
/// <see cref="EntryId"/> of the last-distilled entry; the next call passes
/// only entries strictly after that id.
/// </para>
/// </remarks>
/// <param name="EntryId">Host-assigned id; monotonic within the session.</param>
/// <param name="Timestamp">When the entry landed in the host's context (event-time).</param>
/// <param name="Kind">What kind of content this entry holds (drives the distiller's interpretation).</param>
/// <param name="Text">The full text content of the entry (post-host-redaction).</param>
/// <param name="SourceRef">Optional pointer back to the entry's source (file path, tool call id, sub-agent id…). Recorded for audit; not interpreted by Mneme.</param>
/// <param name="Metadata">Optional free-form key/value metadata the distiller may consult (e.g., tool name, file size, error code).</param>
public sealed record ContextEntry(
    string EntryId,
    DateTimeOffset Timestamp,
    ContextEntryKind Kind,
    string Text,
    string? SourceRef = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Kinds of content a single <see cref="ContextEntry"/> can hold. Drives
/// the distiller's interpretation but does not constrain the epistemic
/// category of any event the distiller produces.
/// </summary>
public enum ContextEntryKind
{
    /// <summary>Message from the user / human operator.</summary>
    UserMessage = 0,

    /// <summary>Message from the agent / model.</summary>
    AssistantMessage = 1,

    /// <summary>Body of a file the agent read into context.</summary>
    FileContent = 2,

    /// <summary>Structured arguments of a tool the agent invoked.</summary>
    ToolCall = 3,

    /// <summary>Structured result returned by an invoked tool.</summary>
    ToolResult = 4,

    /// <summary>Output of a sub-agent / spawned task the parent agent waited on.</summary>
    SubAgentOutput = 5,

    /// <summary>Free-form system note injected by the host runtime (status messages, control events).</summary>
    SystemNote = 6,

    /// <summary>Anything else (webhooks, sensor events, etc.). The distiller should still try to interpret it.</summary>
    External = 7,
}

/// <summary>
/// Tracks how far Mneme has distilled a given session's context. One row
/// per session; updated atomically with the events produced by each
/// distillation call. Hosts read this to know which entries are "new" and
/// must be included in the next call.
/// </summary>
/// <param name="Session">Session this watermark belongs to.</param>
/// <param name="LastDistilledEntryId">The <see cref="ContextEntry.EntryId"/> of the last entry that was included in a distillation run. The next call should pass only entries strictly after this id.</param>
/// <param name="DistilledAt">When the watermark was last advanced.</param>
/// <param name="DistillerVersion">Stable identifier of the distiller that produced the events behind this watermark.</param>
public sealed record ContextWatermark(
    SessionId Session,
    string LastDistilledEntryId,
    DateTimeOffset DistilledAt,
    string DistillerVersion);

/// <summary>
/// Result of a single <see cref="IMemoryAgent.DistillSessionAsync"/> call.
/// </summary>
/// <param name="NewEvents">Event ids produced by this distillation (subset of the entries the LLM judged worth keeping). Empty if the LLM dropped everything.</param>
/// <param name="NewWatermark">The watermark after the call. The host should treat this as the new "from" point for the next distillation.</param>
/// <param name="Dropped">Optional audit trail of entries the distiller explicitly judged not worth keeping. <c>null</c> when the distiller doesn't surface per-entry decisions.</param>
/// <param name="WasNoOp"><c>true</c> when the call was a no-op because this exact session+range had already been distilled (idempotency guard). The <see cref="NewWatermark"/> still reflects the state.</param>
public sealed record DistillSessionResult(
    IReadOnlyList<EventId> NewEvents,
    ContextWatermark NewWatermark,
    IReadOnlyList<DroppedEntry>? Dropped = null,
    bool WasNoOp = false);

/// <summary>
/// One entry the distiller chose not to turn into a Mneme event, recorded
/// for audit. Useful when reviewing distillation quality.
/// </summary>
/// <param name="EntryId">The dropped entry's id.</param>
/// <param name="Reason">Distiller-supplied short reason (e.g., <c>"small talk"</c>, <c>"already captured upstream"</c>).</param>
public sealed record DroppedEntry(string EntryId, string Reason);
