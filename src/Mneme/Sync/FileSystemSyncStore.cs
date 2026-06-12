using System.Security.Cryptography;
using Mneme.Contracts;

namespace Mneme.Sync;

/// <summary>
/// Phase 10 default <see cref="ISyncStore"/>: rooted at a local
/// directory. Drop-in for testing, single-user laptop sync, USB-stick
/// backups, or as a starting point for hosts implementing against S3,
/// Azure Blob, GCS, R2.
/// </summary>
public sealed class FileSystemSyncStore : ISyncStore
{
    private readonly string _root;
    public string Id { get; }

    public FileSystemSyncStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        Id = "fs://" + _root.Replace('\\', '/');
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var dir = Path.Combine(_root, prefix.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Select(f => prefix + Path.GetFileName(f))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public async Task<ReadOnlyMemory<byte>?> ReadAsync(string key, CancellationToken ct = default)
    {
        var path = Combine(key);
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> content, string contentSha256Hex, CancellationToken ct = default)
    {
        var path = Combine(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var existingHash = Convert.ToHexString(SHA256.HashData(existing));
            if (!existingHash.Equals(contentSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Idempotency violation: '{key}' already exists with different content.");
            }
            return;
        }
        await File.WriteAllBytesAsync(path, content.ToArray(), ct).ConfigureAwait(false);
    }

    private string Combine(string key) =>
        Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
}
