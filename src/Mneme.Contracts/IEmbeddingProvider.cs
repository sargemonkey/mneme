namespace Mneme.Contracts;

/// <summary>
/// Host-supplied embedding source for Phase 6 entity-resolution Tier 2
/// (and any future semantic-similarity feature). Symmetric to
/// <see cref="ISessionDistiller"/> and <see cref="IDistiller"/> — the SDK
/// never ships an embedding model, so a host using Copilot's models, an
/// OpenAI key, an on-device sentence-transformer, or no embeddings at
/// all can wire whatever it has.
/// </summary>
/// <remarks>
/// Implementations should:
/// <list type="bullet">
///   <item>Return vectors of consistent length per provider instance
///         (mixing dimensionalities corrupts cosine similarity).</item>
///   <item>Normalize their output if the underlying model doesn't
///         already produce unit-length vectors — Mneme's resolver
///         uses a plain dot-product cosine on the returned floats.</item>
/// </list>
/// Phase 6's entity resolver works without an <see cref="IEmbeddingProvider"/>
/// — Tier 1 (deterministic UUID5) and Tier 3 (LLM propose-then-confirm)
/// still function. Tier 2 (embedding ≥0.95) is a no-op when no provider
/// is registered.
/// </remarks>
public interface IEmbeddingProvider
{
    /// <summary>Stable identifier (e.g., <c>"openai/text-embedding-3-small@1536"</c>). Stored alongside cached vectors so a model change can be detected.</summary>
    string Id { get; }

    /// <summary>Vector dimensionality the provider returns. Validated against cached vectors.</summary>
    int Dimensions { get; }

    /// <summary>Embed one or more strings. Order of the result matches the input order.</summary>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default);
}

