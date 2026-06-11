using Mneme.Contracts;

namespace Mneme.Query;

/// <summary>
/// Centralised capability-token enforcement for the Phase 4 query API.
/// Returns a <see cref="ResolvedCapability"/> describing the effective
/// allow-set, or throws <see cref="CapabilityDeniedError"/>.
/// </summary>
/// <remarks>
/// Rules (in order):
/// <list type="number">
///   <item>Token must not have a null <c>Principal.Value</c> and must
///         carry a non-empty <see cref="CapabilityToken.AllowedCategories"/>
///         or rely on the empty-means-all convention.</item>
///   <item>Token must be valid at the supplied <c>now</c>.</item>
///   <item>Workstream scope: if the requested workstream is non-null,
///         it must match <see cref="CapabilityToken.Workstream"/>
///         <em>unless</em> the token has
///         <see cref="CapabilityToken.CrossWorkstream"/> = <c>true</c>
///         AND <see cref="CapabilityToken.Workstream"/> = <c>null</c>.</item>
///   <item>Channel: <see cref="EventChannel.Technical"/> requires
///         <see cref="CapabilityToken.IncludeTechnical"/> = <c>true</c>.</item>
///   <item>Categories: the effective allow-set is the intersection of
///         the requested categories (or all when empty) and the token's
///         <see cref="CapabilityToken.AllowedCategories"/> (or all when
///         empty). Empty intersection is a denial.</item>
/// </list>
/// </remarks>
internal static class CapabilityEnforcement
{
    public static ResolvedCapability Enforce(
        CapabilityToken token,
        WorkstreamId? requested,
        IReadOnlyCollection<EpistemicCategory>? requestedCategories,
        EventChannel channel,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (!token.IsValidAt(now))
        {
            throw new CapabilityDeniedError(
                $"token validity window [{token.NotBefore:O}..{token.NotAfter:O}] does not include {now:O}");
        }

        // Workstream check.
        var crossOk = token.CrossWorkstream && token.Workstream is null;
        if (requested is not null)
        {
            if (!crossOk && (token.Workstream is null || token.Workstream.Value != requested.Value))
            {
                throw new CapabilityDeniedError(
                    $"token is scoped to workstream '{token.Workstream?.Value ?? "<none>"}'; query asked for '{requested.Value.Value}'");
            }
        }
        else if (!crossOk)
        {
            throw new CapabilityDeniedError(
                "query did not name a workstream and token does not grant cross-workstream access");
        }

        // Channel check.
        if (channel == EventChannel.Technical && !token.IncludeTechnical)
        {
            throw new CapabilityDeniedError(
                "token does not grant Technical channel access (set IncludeTechnical=true)");
        }

        // Category intersection.
        var tokenAll = token.AllowedCategories;
        var reqAll = requestedCategories ?? Array.Empty<EpistemicCategory>();
        IReadOnlyCollection<EpistemicCategory> effective;
        if (tokenAll.Count == 0 && reqAll.Count == 0)
        {
            effective = Enum.GetValues<EpistemicCategory>();
        }
        else if (tokenAll.Count == 0)
        {
            effective = reqAll.ToArray();
        }
        else if (reqAll.Count == 0)
        {
            effective = tokenAll.ToArray();
        }
        else
        {
            var set = new HashSet<EpistemicCategory>(tokenAll);
            set.IntersectWith(reqAll);
            if (set.Count == 0)
            {
                throw new CapabilityDeniedError(
                    "no overlap between the categories the token allows and the categories the query requested");
            }
            effective = set.ToArray();
        }

        return new ResolvedCapability(
            EffectiveCategories: effective,
            CrossWorkstream: crossOk && requested is null,
            ScopeWorkstream: requested);
    }
}

/// <summary>Outcome of a successful capability check — the allow-set the rest of the pipeline runs against.</summary>
/// <param name="EffectiveCategories">Categories the query may return (intersection of token + request).</param>
/// <param name="CrossWorkstream">True if the query is operating cross-workstream.</param>
/// <param name="ScopeWorkstream">When non-null, the single workstream the query is scoped to.</param>
internal sealed record ResolvedCapability(
    IReadOnlyCollection<EpistemicCategory> EffectiveCategories,
    bool CrossWorkstream,
    WorkstreamId? ScopeWorkstream);
