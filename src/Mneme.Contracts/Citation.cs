using System.Text.Json.Serialization;

namespace Mneme.Contracts;

/// <summary>
/// Polymorphic provenance pointer attached to every Mneme event that traces
/// back to a source the host can re-resolve later. Distilled events get a
/// <see cref="SessionRange"/> (Mneme stores no raw text — the host's chat
/// log is the source of truth and can be re-read at any time). Directly-
/// ingested events get one of the simpler shapes depending on whether the
/// signal came from a human (<see cref="Manual"/>), a deterministic platform
/// workflow (<see cref="Workflow"/>), or an external system over a webhook /
/// API (<see cref="External"/>).
/// </summary>
/// <remarks>
/// <para>
/// Citations are immutable and survive on the event forever — including
/// across re-distillation runs that produce new events for the same source
/// range. That's how "why does memory say X?" stays answerable: every
/// distilled event names the session-id + entry-range that produced it,
/// and the host can fetch the original turns from its chat log on demand.
/// </para>
/// <para>
/// The host never has to invent a citation for a directly-ingested event
/// — leave <see cref="CaptureProvenance.Citation"/> <c>null</c> and the
/// SDK treats the call as un-cited (typical for MCP <c>remember</c> calls
/// when the human hasn't provided a structured pointer).
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SessionRange), nameof(SessionRange))]
[JsonDerivedType(typeof(Manual), nameof(Manual))]
[JsonDerivedType(typeof(Workflow), nameof(Workflow))]
[JsonDerivedType(typeof(External), nameof(External))]
public abstract record Citation
{
    /// <summary>
    /// Distilled event citation: a closed range of entries in an agent
    /// session's chat history. The host owns the chat log and can re-
    /// resolve <paramref name="FromEntryId"/> .. <paramref name="ToEntryId"/>
    /// to the original turns on demand. Mneme stores no copy of the text.
    /// </summary>
    /// <param name="Session">Which session the entries belong to.</param>
    /// <param name="FromEntryId">First entry in the cited range (inclusive). Host-assigned monotonic id.</param>
    /// <param name="ToEntryId">Last entry in the cited range (inclusive). Host-assigned monotonic id.</param>
    public sealed record SessionRange(
        SessionId Session,
        string FromEntryId,
        string ToEntryId) : Citation;

    /// <summary>
    /// Human-asserted event (e.g., a fact entered through the MCP
    /// <c>remember</c> tool, or a curator's manual annotation).
    /// </summary>
    /// <param name="AssertedBy">Principal who asserted the event.</param>
    /// <param name="Reason">Optional free-text rationale.</param>
    public sealed record Manual(string AssertedBy, string? Reason) : Citation;

    /// <summary>
    /// Event emitted by a deterministic platform workflow (CI/CD, deploy
    /// pipeline, scheduled job) that's already in epistemic shape — no
    /// LLM interpretation needed.
    /// </summary>
    /// <param name="SystemName">Source system identifier (e.g., <c>github-actions</c>).</param>
    /// <param name="RunId">Run / job identifier within the source system.</param>
    /// <param name="Step">Optional step / stage within the run.</param>
    public sealed record Workflow(string SystemName, string RunId, string? Step) : Citation;

    /// <summary>
    /// Event ingested from an external system through a webhook, API call,
    /// or sensor (e.g., JIRA transition, Datadog alert, calendar event).
    /// </summary>
    /// <param name="SystemName">External system identifier.</param>
    /// <param name="ExternalId">Stable id of the event in the external system.</param>
    /// <param name="Href">Optional URL to the source record for audit / drill-down.</param>
    public sealed record External(string SystemName, string ExternalId, Uri? Href) : Citation;
}
