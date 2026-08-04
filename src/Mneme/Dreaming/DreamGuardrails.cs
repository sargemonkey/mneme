using Mneme.Contracts;
using Cls = Mneme.Contracts.Classification;

namespace Mneme.Dreaming;

/// <summary>
/// The safety guardrails for the offline consolidation ("dreaming") pass
/// (ADR-0004 §Privacy), factored out so the per-workstream coordinator and the
/// cross-workstream ("fleet") promotion share one authority. All rules key off
/// the sensitivity <see cref="Cls"/> of the events a derived output was
/// consolidated from.
/// </summary>
public static class DreamGuardrails
{
    /// <summary>A source class that makes a derived output ineligible for sharing beyond its author.</summary>
    public static bool IsSensitive(Cls classification) =>
        classification is Cls.Confidential or Cls.Secret or Cls.Pii;

    /// <summary>
    /// The visibility an output may receive given its source events. Any sensitive
    /// source (Confidential/Secret/Pii) floors the output to
    /// <see cref="Visibility.Private"/>; otherwise the <paramref name="proposed"/>
    /// visibility is honoured up to <see cref="Visibility.Global"/>. Missing source
    /// classifications are treated as sensitive (conservative).
    /// </summary>
    public static Visibility CapVisibility(
        Visibility proposed,
        IReadOnlyList<EventId> derivedFrom,
        IReadOnlyDictionary<string, Cls> sourceClassifications)
    {
        if (!IsGlobalPromotionEligible(derivedFrom, sourceClassifications))
        {
            // At least one source is sensitive (or unknown) -> author-only.
            return Visibility.Private;
        }
        return (Visibility)Math.Min((int)proposed, (int)Visibility.Global);
    }

    /// <summary>
    /// The classification floor for promotion: an output may be promoted to
    /// shared/global visibility only when <em>every</em> source event is
    /// <see cref="Cls.Public"/> or <see cref="Cls.Internal"/>. A source whose
    /// classification is unknown is treated as ineligible.
    /// </summary>
    public static bool IsGlobalPromotionEligible(
        IReadOnlyList<EventId> derivedFrom,
        IReadOnlyDictionary<string, Cls> sourceClassifications)
    {
        if (derivedFrom is null || derivedFrom.Count == 0) return false;
        foreach (var src in derivedFrom)
        {
            if (!sourceClassifications.TryGetValue(src.Value, out var cls)) return false;
            if (IsSensitive(cls)) return false;
        }
        return true;
    }
}
