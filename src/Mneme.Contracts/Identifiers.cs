namespace Mneme.Contracts;

/// <summary>
/// Identifies a single event in the Mneme event log. Globally unique, monotonic.
/// In v1 backed by a ULID string; the wrapper hides the representation so the
/// event log can change ID schemes (e.g., to add cluster ID) without breaking
/// callers. Two <see cref="EventId"/> values are equal iff their underlying
/// strings are equal (case-sensitive).
/// </summary>
/// <param name="Value">The underlying identifier string. Must be non-empty.</param>
public readonly record struct EventId(string Value)
{
    /// <summary>The empty / sentinel id. Use only to mean "no event yet".</summary>
    public static EventId None { get; } = new(string.Empty);

    /// <summary>True if this id is not the sentinel <see cref="None"/>.</summary>
    public bool HasValue => !string.IsNullOrEmpty(Value);

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// Identifies a workstream — the primary isolation boundary in Mneme.
/// Every <see cref="CaptureEvent"/> belongs to exactly one workstream, and
/// every query is workstream-scoped unless the caller's
/// <see cref="CapabilityToken"/> grants cross-workstream access.
/// </summary>
/// <param name="Value">The workstream identifier. Recommended format: lowercase
/// kebab-case (e.g., <c>cust-acme-q3</c>). Validated by the agent at ingest time.</param>
public readonly record struct WorkstreamId(string Value)
{
    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// Identifies a fact in the <c>facts</c> projection (a synthesized epistemic
/// claim, distinct from the raw <see cref="EventId"/> of the event that
/// produced it). Curation operations on individual claims address the
/// <see cref="FactId"/>, not the raw event.
/// </summary>
/// <param name="Value">The underlying identifier string. Must be non-empty.</param>
public readonly record struct FactId(string Value)
{
    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// Identifies an entity (person, system, organization) in the
/// <c>entity_index</c> projection. Resolved through the three-tier entity
/// resolution pipeline (deterministic UUID5 → embedding → LLM-propose).
/// </summary>
/// <param name="Value">The underlying identifier string. Must be non-empty.</param>
public readonly record struct EntityId(string Value)
{
    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// Identifies a principal (user, service, agent) that can be bound to a
/// <see cref="CapabilityToken"/> or <see cref="CurationCapability"/>. The
/// shape of the value is deployment-specific (e.g., email, GitHub login,
/// SID); Mneme treats it as opaque.
/// </summary>
/// <param name="Value">The underlying identifier string. Must be non-empty.</param>
public readonly record struct PrincipalId(string Value)
{
    /// <inheritdoc/>
    public override string ToString() => Value;
}
