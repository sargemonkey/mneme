using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Sync;

/// <summary>
/// Phase 10 — local-first cloud sync. Push and pull event-log snapshots
/// through any host-supplied <see cref="ISyncStore"/>. Merge correctness
/// comes from ULID event ids + append-only memory_events; conflict-free
/// by design.
/// </summary>
public sealed class SyncEngine
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnectionFactory _connections;
    private readonly ISyncStore _store;

    public SyncEngine(SqliteConnectionFactory connections, ISyncStore store)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(store);
        _connections = connections;
        _store = store;
    }

    public async Task<SyncSnapshotResult> PushAsync(WorkstreamId workstream, CancellationToken ct = default)
    {
        var rows = LoadSnapshot(workstream);
        var payload = Serialize(rows);
        var compressed = Compress(payload);
        var hash = Hash(compressed);
        var id = NowUlid() + "-" + hash[..12];
        var key = KeyFor(workstream, id);
        await _store.WriteAsync(key, compressed, hash, ct).ConfigureAwait(false);
        return new SyncSnapshotResult(
            Workstream: workstream, SnapshotId: id, Key: key,
            BytesWritten: compressed.Length, EventCount: rows.Events.Count,
            RevocationCount: rows.Revocations.Count, CurationCount: rows.Curations.Count);
    }

    public async Task<SyncPullResult> PullAsync(WorkstreamId workstream, CancellationToken ct = default)
    {
        var prefix = KeyPrefix(workstream);
        var keys = await _store.ListAsync(prefix, ct).ConfigureAwait(false);
        var totalEvents = 0; var totalRevocations = 0; var totalCurations = 0; var snapshotsApplied = 0;
        foreach (var key in keys)
        {
            var raw = await _store.ReadAsync(key, ct).ConfigureAwait(false);
            if (raw is null) continue;
            var payload = Decompress(raw.Value);
            var rows = Deserialize(payload);
            var (e, r, cu) = ApplyMerge(rows);
            totalEvents += e; totalRevocations += r; totalCurations += cu;
            snapshotsApplied++;
        }
        return new SyncPullResult(workstream, snapshotsApplied, totalEvents, totalRevocations, totalCurations);
    }

    private static string KeyFor(WorkstreamId ws, string snapshotId) =>
        $"workstreams/{ws.Value}/snapshots/{snapshotId}.jsonl.gz";

    private static string KeyPrefix(WorkstreamId ws) =>
        $"workstreams/{ws.Value}/snapshots/";

    private SnapshotPayload LoadSnapshot(WorkstreamId workstream)
    {
        using var c = _connections.Open();
        var events = new List<EventRowDto>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT event_id, workstream_id, event_channel, category, schema_version,
                       valid_at, invalid_at, created_at, expired_at,
                       payload_json, provenance_json, content_shape, classification, artifact_id
                FROM memory_events WHERE workstream_id = $ws;
                """;
            cmd.Parameters.AddWithValue("$ws", workstream.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                events.Add(new EventRowDto(
                    r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
                    r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                    r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                    r.GetString(9), r.GetString(10), r.GetInt32(11),
                    r.IsDBNull(12) ? 0 : r.GetInt32(12),
                    r.IsDBNull(13) ? null : r.GetString(13)));
            }
        }
        var revocations = new List<RevocationRowDto>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT event_id, workstream_id, revoked_at, revoked_by, reason FROM memory_revocations WHERE workstream_id = $ws;";
            cmd.Parameters.AddWithValue("$ws", workstream.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                revocations.Add(new RevocationRowDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            }
        }
        var curations = new List<CurationRowDto>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT event_id, target_event_id, workstream_id, curation_type, curator,
                       rationale, occurred_at, pre_state_hash, payload_json, reverted_by
                FROM curation_events WHERE workstream_id = $ws;
                """;
            cmd.Parameters.AddWithValue("$ws", workstream.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                curations.Add(new CurationRowDto(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
                    r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                    r.IsDBNull(9) ? null : r.GetString(9)));
            }
        }
        return new SnapshotPayload(SchemaVersion, workstream.Value, events, revocations, curations);
    }

    private (int events, int revocations, int curations) ApplyMerge(SnapshotPayload payload)
    {
        using var c = _connections.Open();
        using var tx = c.BeginTransaction();
        var newEvents = 0; var newRevs = 0; var newCurs = 0;

        foreach (var e in payload.Events)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO memory_events(
                    event_id, workstream_id, event_channel, category, schema_version,
                    valid_at, invalid_at, created_at, expired_at,
                    payload_json, provenance_json, content_shape, classification, artifact_id)
                VALUES ($eid, $ws, $ch, $cat, $sv, $va, $ia, $ca, $ea, $pj, $prj, $cs, $cls, $aid);
                """;
            cmd.Parameters.AddWithValue("$eid", e.EventId);
            cmd.Parameters.AddWithValue("$ws", e.WorkstreamId);
            cmd.Parameters.AddWithValue("$ch", e.EventChannel);
            cmd.Parameters.AddWithValue("$cat", e.Category);
            cmd.Parameters.AddWithValue("$sv", e.SchemaVersion);
            cmd.Parameters.AddWithValue("$va", e.ValidAt);
            cmd.Parameters.AddWithValue("$ia", (object?)e.InvalidAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ca", e.CreatedAt);
            cmd.Parameters.AddWithValue("$ea", (object?)e.ExpiredAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pj", e.PayloadJson);
            cmd.Parameters.AddWithValue("$prj", e.ProvenanceJson);
            cmd.Parameters.AddWithValue("$cs", e.ContentShape);
            cmd.Parameters.AddWithValue("$cls", e.Classification);
            cmd.Parameters.AddWithValue("$aid", (object?)e.ArtifactId ?? DBNull.Value);
            newEvents += cmd.ExecuteNonQuery();
        }
        foreach (var r in payload.Revocations)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO memory_revocations(event_id, workstream_id, revoked_at, revoked_by, reason)
                VALUES ($eid, $ws, $at, $by, $r);
                """;
            cmd.Parameters.AddWithValue("$eid", r.EventId);
            cmd.Parameters.AddWithValue("$ws", r.WorkstreamId);
            cmd.Parameters.AddWithValue("$at", r.RevokedAt);
            cmd.Parameters.AddWithValue("$by", r.RevokedBy);
            cmd.Parameters.AddWithValue("$r", r.Reason);
            newRevs += cmd.ExecuteNonQuery();
        }
        foreach (var cu in payload.Curations)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO curation_events(event_id, target_event_id, workstream_id, curation_type,
                    curator, rationale, occurred_at, pre_state_hash, payload_json, reverted_by)
                VALUES ($eid, $tid, $ws, $ct, $cur, $rat, $at, $hash, $pj, $rev);
                """;
            cmd.Parameters.AddWithValue("$eid", cu.EventId);
            cmd.Parameters.AddWithValue("$tid", cu.TargetEventId);
            cmd.Parameters.AddWithValue("$ws", cu.WorkstreamId);
            cmd.Parameters.AddWithValue("$ct", cu.CurationType);
            cmd.Parameters.AddWithValue("$cur", cu.Curator);
            cmd.Parameters.AddWithValue("$rat", cu.Rationale);
            cmd.Parameters.AddWithValue("$at", cu.OccurredAt);
            cmd.Parameters.AddWithValue("$hash", cu.PreStateHash);
            cmd.Parameters.AddWithValue("$pj", cu.PayloadJson);
            cmd.Parameters.AddWithValue("$rev", (object?)cu.RevertedBy ?? DBNull.Value);
            newCurs += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return (newEvents, newRevs, newCurs);
    }

    private static byte[] Serialize(SnapshotPayload payload)
    {
        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.WriteLine(JsonSerializer.Serialize(new { schemaVersion = payload.SchemaVersion, workstream = payload.WorkstreamId }, Json));
            foreach (var e in payload.Events) writer.WriteLine(JsonSerializer.Serialize(new { kind = "event", row = e }, Json));
            foreach (var r in payload.Revocations) writer.WriteLine(JsonSerializer.Serialize(new { kind = "revocation", row = r }, Json));
            foreach (var c in payload.Curations) writer.WriteLine(JsonSerializer.Serialize(new { kind = "curation", row = c }, Json));
        }
        return ms.ToArray();
    }

    private static SnapshotPayload Deserialize(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var reader = new StreamReader(ms);
        var headerLine = reader.ReadLine() ?? throw new InvalidOperationException("empty snapshot");
        using var headerDoc = JsonDocument.Parse(headerLine);
        var ws = headerDoc.RootElement.GetProperty("workstream").GetString() ?? throw new InvalidOperationException("missing workstream");
        var sv = headerDoc.RootElement.GetProperty("schemaVersion").GetInt32();
        var events = new List<EventRowDto>();
        var revs = new List<RevocationRowDto>();
        var curs = new List<CurationRowDto>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            using var doc = JsonDocument.Parse(line);
            var kind = doc.RootElement.GetProperty("kind").GetString();
            var row = doc.RootElement.GetProperty("row").GetRawText();
            switch (kind)
            {
                case "event":      events.Add(JsonSerializer.Deserialize<EventRowDto>(row, Json)!); break;
                case "revocation": revs.Add(JsonSerializer.Deserialize<RevocationRowDto>(row, Json)!); break;
                case "curation":   curs.Add(JsonSerializer.Deserialize<CurationRowDto>(row, Json)!); break;
            }
        }
        return new SnapshotPayload(sv, ws, events, revs, curs);
    }

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal))
        {
            gz.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    private static byte[] Decompress(ReadOnlyMemory<byte> data)
    {
        using var src = new MemoryStream(data.ToArray());
        using var gz = new GZipStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        gz.CopyTo(dst);
        return dst.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string NowUlid()
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return ms.ToString("X12", CultureInfo.InvariantCulture);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record SyncSnapshotResult(WorkstreamId Workstream, string SnapshotId, string Key, long BytesWritten, int EventCount, int RevocationCount, int CurationCount);
public sealed record SyncPullResult(WorkstreamId Workstream, int SnapshotsApplied, int NewEvents, int NewRevocations, int NewCurations);

internal sealed record SnapshotPayload(int SchemaVersion, string WorkstreamId, IReadOnlyList<EventRowDto> Events, IReadOnlyList<RevocationRowDto> Revocations, IReadOnlyList<CurationRowDto> Curations);
internal sealed record EventRowDto(string EventId, string WorkstreamId, int EventChannel, int Category, int SchemaVersion, string ValidAt, string? InvalidAt, string CreatedAt, string? ExpiredAt, string PayloadJson, string ProvenanceJson, int ContentShape, int Classification, string? ArtifactId);
internal sealed record RevocationRowDto(string EventId, string WorkstreamId, string RevokedAt, string RevokedBy, string Reason);
internal sealed record CurationRowDto(string EventId, string TargetEventId, string WorkstreamId, int CurationType, string Curator, string Rationale, string OccurredAt, string PreStateHash, string PayloadJson, string? RevertedBy);
