using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest.Validation;
using Mneme.Resolution;
using Mneme.Storage;

namespace Mneme.Search;

/// <summary>
/// Semantic-retrieval index over per-event embedding vectors. Vectors are
/// produced by the host-supplied <see cref="IEmbeddingProvider"/>, stored as
/// raw float32 BLOBs in <c>event_embeddings</c>, and searched by brute-force
/// cosine KNN.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why brute force, not sqlite-vec:</strong> at v1 scale (a workstream
/// holds thousands, not millions, of events) a linear cosine scan over the
/// stored vectors is sub-millisecond and needs no native extension. sqlite-vec
/// becomes worthwhile only at the million-vector scale (Phase 11). This keeps
/// the dependency surface BCL + Microsoft.Data.Sqlite and unblocks semantic
/// retrieval — and a proper LoCoMo run — today.
/// </para>
/// <para>
/// <strong>Embedding happens off the ingest hot path.</strong> The locked
/// sync-ingest / async-distillation split means we never call the (possibly
/// remote) embedding model during <see cref="IMemoryAgent.IngestAsync"/>.
/// Hosts call <see cref="BackfillAsync"/> on their own schedule (or the
/// benchmark/eval harness calls it once after loading a corpus) to embed any
/// events that don't yet have a vector for the current provider.
/// </para>
/// <para>
/// When no <see cref="IEmbeddingProvider"/> is registered, <see cref="IsEnabled"/>
/// is <c>false</c> and every method is a no-op — the query API falls back to
/// lexical (FTS5) retrieval.
/// </para>
/// </remarks>
public sealed class VectorIndex
{
    private readonly SqliteConnectionFactory _connections;
    private readonly IEmbeddingProvider? _provider;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _recencyHalfLife;

    public VectorIndex(SqliteConnectionFactory connections, IEmbeddingProvider? provider)
        : this(connections, provider, TimeProvider.System, TimeSpan.FromDays(30)) { }

    public VectorIndex(SqliteConnectionFactory connections, IEmbeddingProvider? provider, TimeProvider clock, TimeSpan recencyHalfLife)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _provider = provider;
        _clock = clock;
        _recencyHalfLife = recencyHalfLife;
    }

    /// <summary>True when a host embedding provider is registered; semantic search is available.</summary>
    public bool IsEnabled => _provider is not null;

    /// <summary>Provider id (or <c>null</c> when disabled). Stamped on stored vectors.</summary>
    public string? ProviderId => _provider?.Id;

    /// <summary>
    /// Embed and store vectors for every epistemic, non-revoked event in the
    /// workstream that doesn't already have a vector for the current provider.
    /// Idempotent and resumable. Returns the number of events embedded.
    /// </summary>
    public async Task<int> BackfillAsync(WorkstreamId workstream, CancellationToken ct = default)
    {
        if (_provider is null) return 0;
        WorkstreamIdValidator.EnsureValid(workstream.Value, nameof(workstream));

        var pending = LoadPending(workstream, _provider.Id);
        if (pending.Count == 0) return 0;

        const int batch = 64;
        var embedded = 0;
        for (var i = 0; i < pending.Count; i += batch)
        {
            ct.ThrowIfCancellationRequested();
            var slice = pending.Skip(i).Take(batch).ToArray();
            var vectors = await _provider.EmbedAsync(slice.Select(p => p.Text).ToArray(), ct).ConfigureAwait(false);
            if (vectors.Count != slice.Length)
            {
                throw new InvalidOperationException(
                    $"Embedding provider '{_provider.Id}' returned {vectors.Count} vectors for {slice.Length} inputs.");
            }
            Store(workstream, slice, vectors, _provider.Id);
            embedded += slice.Length;
        }
        return embedded;
    }

    /// <summary>
    /// Semantic top-k: embed <paramref name="queryText"/>, cosine-score every
    /// stored vector in the workstream for the current provider, and return the
    /// <paramref name="k"/> highest as <c>(eventId, semantic[0,1])</c>. Cosine
    /// in <c>[-1,1]</c> is mapped to <c>[0,1]</c> via <c>(cos+1)/2</c>.
    /// </summary>
    public async Task<IReadOnlyList<VectorHit>> SearchAsync(WorkstreamId workstream, string queryText, int k, CancellationToken ct = default)
    {
        if (_provider is null || string.IsNullOrWhiteSpace(queryText) || k <= 0)
        {
            return Array.Empty<VectorHit>();
        }
        WorkstreamIdValidator.EnsureValid(workstream.Value, nameof(workstream));

        var queryVec = (await _provider.EmbedAsync(new[] { queryText }, ct).ConfigureAwait(false))[0];
        var rows = LoadVectors(workstream, _provider.Id);
        if (rows.Count == 0) return Array.Empty<VectorHit>();

        var now = _clock.GetUtcNow();
        var scored = new List<VectorHit>(rows.Count);
        var qSpan = queryVec.Span;
        foreach (var (eventId, createdAt, vector) in rows)
        {
            var cos = EmbeddingCosine.Similarity(qSpan, vector);
            // Clamp to [0,1]: natural-language embeddings rarely go negative,
            // and treating orthogonal (unrelated) vectors as 0 keeps both the
            // fusion weights and the semantic gate meaningful.
            var semantic = Math.Clamp(cos, 0.0, 1.0);
            var recency = _recencyHalfLife <= TimeSpan.Zero
                ? 1.0
                : Math.Exp(-(now - createdAt).TotalDays / Math.Max(0.001, _recencyHalfLife.TotalDays));
            scored.Add(new VectorHit(new EventId(eventId), semantic, recency));
        }
        scored.Sort(static (a, b) => b.Semantic.CompareTo(a.Semantic));
        return scored.Count <= k ? scored : scored.GetRange(0, k);
    }

    private List<(string EventId, string Text)> LoadPending(WorkstreamId ws, string providerId)
    {
        var pending = new List<(string, string)>();
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT e.event_id, e.payload_json
            FROM memory_events e
            LEFT JOIN memory_revocations r ON r.event_id = e.event_id
            LEFT JOIN event_embeddings em ON em.event_id = e.event_id AND em.provider_id = $pid
            WHERE e.workstream_id = $ws
              AND e.event_channel = 0
              AND r.event_id IS NULL
              AND em.event_id IS NULL
            ORDER BY e.created_at ASC;
            """;
        cmd.Parameters.AddWithValue("$ws", ws.Value);
        cmd.Parameters.AddWithValue("$pid", providerId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var text = ExtractText(rd.GetString(1));
            if (string.IsNullOrWhiteSpace(text)) continue;
            pending.Add((rd.GetString(0), text));
        }
        return pending;
    }

    private List<(string EventId, DateTimeOffset CreatedAt, float[] Vector)> LoadVectors(WorkstreamId ws, string providerId)
    {
        var rows = new List<(string, DateTimeOffset, float[])>();
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT em.event_id, em.dim, em.vector, e.created_at
            FROM event_embeddings em
            JOIN memory_events e ON e.event_id = em.event_id
            LEFT JOIN memory_revocations r ON r.event_id = em.event_id
            WHERE em.workstream_id = $ws AND em.provider_id = $pid
              AND r.event_id IS NULL;
            """;
        cmd.Parameters.AddWithValue("$ws", ws.Value);
        cmd.Parameters.AddWithValue("$pid", providerId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var dim = rd.GetInt32(1);
            var blob = (byte[])rd[2];
            var createdAt = DateTimeOffset.Parse(rd.GetString(3), CultureInfo.InvariantCulture);
            rows.Add((rd.GetString(0), createdAt, BytesToFloats(blob, dim)));
        }
        return rows;
    }

    private void Store(WorkstreamId ws, (string EventId, string Text)[] slice,
        IReadOnlyList<ReadOnlyMemory<float>> vectors, string providerId)
    {
        var now = _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        using var c = _connections.Open();
        using var tx = c.BeginTransaction();
        for (var i = 0; i < slice.Length; i++)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO event_embeddings(event_id, workstream_id, provider_id, dim, vector, created_at)
                VALUES ($eid, $ws, $pid, $dim, $vec, $ca)
                ON CONFLICT(event_id) DO UPDATE SET
                    provider_id = excluded.provider_id,
                    dim         = excluded.dim,
                    vector      = excluded.vector,
                    created_at  = excluded.created_at;
                """;
            cmd.Parameters.AddWithValue("$eid", slice[i].EventId);
            cmd.Parameters.AddWithValue("$ws", ws.Value);
            cmd.Parameters.AddWithValue("$pid", providerId);
            cmd.Parameters.AddWithValue("$dim", vectors[i].Length);
            cmd.Parameters.AddWithValue("$vec", FloatsToBytes(vectors[i].Span));
            cmd.Parameters.AddWithValue("$ca", now);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    internal static byte[] FloatsToBytes(ReadOnlySpan<float> v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        for (var i = 0; i < v.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), v[i]);
        }
        return bytes;
    }

    internal static float[] BytesToFloats(byte[] bytes, int dim)
    {
        var v = new float[dim];
        for (var i = 0; i < dim; i++)
        {
            v[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
        }
        return v;
    }

    private static string ExtractText(string payloadJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            foreach (var field in new[] { "statement", "content" })
            {
                if (doc.RootElement.TryGetProperty(field, out var v) &&
                    v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return v.GetString() ?? string.Empty;
                }
            }
        }
        catch { /* malformed payloads yield empty text and are skipped */ }
        return string.Empty;
    }
}

/// <summary>A semantic-search hit: the event plus its cosine-derived score.</summary>
/// <param name="EventId">The matched event.</param>
/// <param name="Semantic">Cosine similarity mapped to [0,1] (higher = closer).</param>
/// <param name="RecencyWeight">Exponential age decay in [0,1].</param>
public sealed record VectorHit(EventId EventId, double Semantic, double RecencyWeight);
