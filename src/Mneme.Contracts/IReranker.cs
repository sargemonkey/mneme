namespace Mneme.Contracts;

/// <summary>
/// Host-supplied reranker that re-scores a candidate set against the query
/// jointly (e.g., a cross-encoder, a hosted rerank API like Cohere/Jina, or an
/// LLM listwise reranker). The query API retrieves a wider candidate pool with
/// its hybrid (semantic + lexical + recency) scorer, then — if a reranker is
/// registered — hands that pool here to pick the final, better-ordered top-k.
/// </summary>
/// <remarks>
/// Sixth host-pluggable seam, symmetric to <see cref="ISessionDistiller"/>,
/// <see cref="IDistiller"/>, <see cref="IEmbeddingProvider"/>,
/// <see cref="IEntityProposer"/>, and <see cref="ISyncStore"/>: the SDK ships
/// the interface and the retrieval plumbing, the host brings the model. With
/// no reranker registered, retrieval returns the hybrid ranking unchanged.
/// <para>
/// Bi-encoder retrieval (embeddings) is fast but scores query and document
/// independently; a cross-encoder scores the pair together and is markedly
/// more precise at the top of the list — the standard two-stage
/// retrieve-then-rerank pattern. This is the seam for stage two.
/// </para>
/// </remarks>
public interface IReranker
{
    /// <summary>Stable identifier (e.g., <c>"cross-encoder/ms-marco-MiniLM-L6-v2"</c>). Surfaced in query diagnostics.</summary>
    string Id { get; }

    /// <summary>
    /// Re-score <paramref name="candidates"/> against <paramref name="query"/>
    /// and return the <paramref name="topK"/> most relevant, highest first.
    /// Implementations may return fewer than <paramref name="topK"/> but must
    /// not invent candidates not present in the input.
    /// </summary>
    Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        int topK,
        CancellationToken ct = default);
}

/// <summary>One candidate handed to a reranker.</summary>
/// <param name="EventId">The candidate event.</param>
/// <param name="Text">The candidate's text (the summary the retriever surfaced).</param>
public sealed record RerankCandidate(EventId EventId, string Text);

/// <summary>One reranked result.</summary>
/// <param name="EventId">The candidate event, echoed from the input.</param>
/// <param name="Score">Relevance score, higher = more relevant. Need not be normalized.</param>
public sealed record RerankResult(EventId EventId, double Score);
