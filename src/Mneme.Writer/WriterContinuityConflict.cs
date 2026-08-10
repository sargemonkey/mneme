using Mneme.Contracts;

namespace Mneme.Writer;

/// <summary>
/// A detected manuscript <em>continuity conflict</em>: two currently-valid
/// authoring claims (or a claim and a base fact) that agree on the subject and
/// attribute but assert a different value — e.g. one scene establishes Helios's
/// eyes as blue and another as green. Surfaced for human review, never
/// auto-resolved (conservative by design).
/// </summary>
/// <param name="Subject">The entity both claims are about (original surface form, e.g. "Helios").</param>
/// <param name="Attribute">The shared attribute they disagree on (e.g. "eye colour").</param>
/// <param name="EventA">The earlier-sorted claim's event id.</param>
/// <param name="ValueA">The value <see cref="EventA"/> asserts (e.g. "blue").</param>
/// <param name="EventB">The later-sorted claim's event id.</param>
/// <param name="ValueB">The value <see cref="EventB"/> asserts (e.g. "green").</param>
public sealed record WriterContinuityConflict(
    string Subject,
    string Attribute,
    EventId EventA,
    string ValueA,
    EventId EventB,
    string ValueB);
