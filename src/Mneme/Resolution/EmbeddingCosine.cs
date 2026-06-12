namespace Mneme.Resolution;

/// <summary>
/// Cosine-similarity helper used by <see cref="EntityResolver"/>'s Tier 2.
/// Lives in the implementation assembly so <c>Mneme.Contracts</c> stays a
/// pure record / interface / enum surface.
/// </summary>
public static class EmbeddingCosine
{
    /// <summary>
    /// Cosine similarity in <c>[-1, 1]</c>. Assumes both vectors are
    /// non-zero and of equal length; throws otherwise.
    /// </summary>
    public static float Similarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vector length mismatch: {a.Length} vs {b.Length}");
        }
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0f;
        return (float)(dot / Math.Sqrt(na * nb));
    }
}
