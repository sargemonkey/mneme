namespace Mneme.Search;

/// <summary>
/// Maps raw FTS5 BM25 scores (negative — more-negative is better) to the
/// [0,1] range using a query-length-adaptive sigmoid. Ported from Mem0's
/// <c>get_bm25_params</c>; see <c>research-design-lessons.md §3.3</c>.
/// </summary>
/// <remarks>
/// <para>
/// Five parameter regimes by token count:
/// <list type="bullet">
///   <item>1–3 tokens:  k=15, x0=−7  (very short queries lean on rare terms)</item>
///   <item>4–6 tokens:  k=10, x0=−10 (short factoid queries)</item>
///   <item>7–9 tokens:  k=8,  x0=−12 (medium)</item>
///   <item>10–15 tokens: k=6, x0=−14 (long natural-language)</item>
///   <item>15+ tokens:  k=4, x0=−16 (very long; spread thinner)</item>
/// </list>
/// </para>
/// <para>
/// Returns a score where 1.0 means "highly relevant" and 0.0 means
/// "irrelevant". FTS5's <c>bm25()</c> returns increasingly negative
/// numbers as relevance increases, so we negate before applying the
/// sigmoid: <c>1 / (1 + exp((-rawScore - x0) / k))</c>.
/// </para>
/// </remarks>
public static class AdaptiveBm25
{
    /// <summary>Map a single FTS5 bm25 score for a given query token count to [0,1].</summary>
    public static double Normalize(double rawBm25Score, int queryTokenCount)
    {
        var (k, x0) = Parameters(queryTokenCount);
        // bm25() is negative; negate so larger == more relevant.
        var x = -rawBm25Score;
        return 1.0 / (1.0 + Math.Exp(-(x - x0) / k));
    }

    /// <summary>The (k, x0) pair for a given token count.</summary>
    public static (double K, double X0) Parameters(int queryTokenCount)
    {
        if (queryTokenCount <= 0) return (15, -7);
        return queryTokenCount switch
        {
            <= 3 => (15, -7),
            <= 6 => (10, -10),
            <= 9 => (8, -12),
            <= 15 => (6, -14),
            _ => (4, -16),
        };
    }

    /// <summary>Naïve token count — splits on whitespace. Good enough for routing the sigmoid.</summary>
    public static int CountTokens(string query) =>
        string.IsNullOrWhiteSpace(query)
            ? 0
            : query.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
}
