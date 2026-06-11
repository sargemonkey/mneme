namespace Mneme.Contracts;

/// <summary>
/// Read / ingest authorization. Required on every <see cref="IMemoryQueryAPI"/>
/// call. Scopes the caller to a workstream (or grants explicit cross-workstream
/// access) and limits which epistemic categories may be returned.
/// </summary>
/// <remarks>
/// <para>
/// Capability tokens are deliberately <em>not</em> JWTs at the contract level —
/// a deployment that needs JWT-bearer flows can wrap this record in its own
/// validator. The contract here is the minimal shape Mneme's read path
/// consults.
/// </para>
/// <para>
/// A token with <see cref="Workstream"/> = <c>null</c> AND
/// <see cref="CrossWorkstream"/> = <c>true</c> permits cross-workstream
/// queries. Any other combination requires the query's
/// <see cref="QuerySpec.Workstream"/> to match <see cref="Workstream"/>.
/// </para>
/// </remarks>
/// <param name="Principal">Who this token authorizes.</param>
/// <param name="Workstream">Workstream scope, or <c>null</c> when combined with <see cref="CrossWorkstream"/>.</param>
/// <param name="NotBefore">Earliest moment the token is valid.</param>
/// <param name="NotAfter">Latest moment the token is valid.</param>
/// <param name="AllowedCategories">Epistemic categories the token may read. Empty = all categories.</param>
/// <param name="CrossWorkstream">If true and <see cref="Workstream"/> is null, allows cross-workstream queries.</param>
/// <param name="IncludeTechnical">If true, also returns <see cref="EventChannel.Technical"/> events. Default false.</param>
/// <param name="Signature">Optional opaque signature for transport / forwarded scenarios.</param>
public sealed record CapabilityToken(
    PrincipalId Principal,
    WorkstreamId? Workstream,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    IReadOnlyCollection<EpistemicCategory> AllowedCategories,
    bool CrossWorkstream = false,
    bool IncludeTechnical = false,
    string? Signature = null)
{
    /// <summary>True if the token's validity window contains <paramref name="instant"/>.</summary>
    public bool IsValidAt(DateTimeOffset instant) => instant >= NotBefore && instant <= NotAfter;

    /// <summary>True if the token may read events in <paramref name="category"/> (empty <see cref="AllowedCategories"/> means all).</summary>
    public bool Allows(EpistemicCategory category)
        => AllowedCategories.Count == 0 || AllowedCategories.Contains(category);
}
