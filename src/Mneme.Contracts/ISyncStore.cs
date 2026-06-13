namespace Mneme.Contracts;

/// <summary>
/// Host-supplied object store the snapshot sync layer pushes/pulls against.
/// Symmetric to <see cref="IDistiller"/> / <see cref="ISessionDistiller"/> /
/// <see cref="IEmbeddingProvider"/> — the SDK never imports an S3 SDK
/// (or any other cloud SDK). Hosts wire their own backend:
/// </summary>
/// <remarks>
/// <para>
/// Three operations, all keyed on opaque object keys. Implementations
/// must be eventually-consistent at minimum; the snapshot sync model
/// tolerates re-uploads (same content hash) and out-of-order reads.
/// </para>
/// <para>
/// Default implementation: <c>Mneme.Sync.FileSystemSyncStore</c> in
/// the impl assembly. Hosts implementing against S3 / Azure Blob /
/// GCS / R2 / R2D2 / a USB stick / scp / git plug in here.
/// </para>
/// </remarks>
public interface ISyncStore
{
    /// <summary>Stable identifier for this store (e.g., <c>"fs://D:/mneme-backups"</c> or <c>"s3://bucket/prefix"</c>).</summary>
    string Id { get; }

    /// <summary>List object keys at <paramref name="prefix"/> in lexicographic order. Empty when nothing exists.</summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default);

    /// <summary>Read an object by key. Returns null on not-found.</summary>
    Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Write an object idempotently. Implementations MUST be safe to
    /// retry — re-writing the same (key, contentHash) is a no-op; a
    /// write that changes the bytes under an existing key throws.
    /// </summary>
    Task WriteAsync(string key, ReadOnlyMemory<byte> content, string contentSha256Hex, CancellationToken ct = default);
}

