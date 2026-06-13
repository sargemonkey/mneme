using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Sessions;

/// <summary>
/// Orchestrates a single <see cref="IMemoryAgent.DistillSessionAsync"/> call:
/// reads the persisted watermark, filters the supplied entries to the new
/// tail, runs the host <see cref="ISessionDistiller"/>, ingests the produced
/// events with <see cref="Citation.SessionRange"/> stamps, and advances the
/// watermark atomically. Idempotent on (session, from-entry-id, to-entry-id).
/// </summary>
/// <remarks>
/// <para>
/// Composition is deliberately layered: ingest still flows through
/// <see cref="IMemoryAgent.IngestAsync"/> so the same validate → redact →
/// classify → WAL pipeline applies. The watermark is updated in a *separate*
/// transaction after the last successful ingest — Mneme tolerates partial
/// progress (some events ingested, watermark not yet advanced) and treats
/// the next call as a retry that will see idempotent inserts for the
/// already-stored events and a fresh attempt to commit the watermark.
/// </para>
/// <para>
/// The "no distiller registered" case throws a clear
/// <see cref="InvalidOperationException"/> rather than falling back to a
/// heuristic. Session distillation is host-defined by design; the heuristic
/// path exists only on the read-side bundle synthesizer (see
/// <c>DistillationPromptBuilder</c>).
/// </para>
/// </remarks>
public sealed class SessionDistillationCoordinator
{
    private readonly SqliteConnectionFactory _connections;
    private readonly IMemoryAgent _agent;
    private readonly ISessionDistiller? _distiller;
    private readonly TimeProvider _clock;

    public SessionDistillationCoordinator(
        SqliteConnectionFactory connections,
        IMemoryAgent agent,
        ISessionDistiller? distiller,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _agent = agent;
        _distiller = distiller;
        _clock = clock;
    }

    public async Task<DistillSessionResult> DistillAsync(
        SessionId session,
        IReadOnlyList<ContextEntry> entries,
        CapabilityToken capability,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(capability);
        if (string.IsNullOrEmpty(session.Value))
        {
            throw new ArgumentException("SessionId is required.", nameof(session));
        }
        if (capability.Workstream is not { } workstream)
        {
            throw new InvalidOperationException(
                "DistillSessionAsync requires a workstream-scoped CapabilityToken.");
        }
        if (_distiller is null)
        {
            throw new InvalidOperationException(
                "DistillSessionAsync requires an ISessionDistiller. Register one with " +
                "services.AddSingleton<ISessionDistiller>(sp => new YourDistiller(...));");
        }

        var watermark = ReadWatermark(session);
        var tail = TailAfter(entries, watermark);
        if (tail.Count == 0)
        {
            var unchanged = watermark ?? EmptyWatermark(session, _distiller.Id, _clock);
            return new DistillSessionResult(Array.Empty<EventId>(), unchanged, null, WasNoOp: true);
        }

        var fromEntryId = tail[0].EntryId;
        var toEntryId = tail[^1].EntryId;
        if (TryGetExistingRun(session, fromEntryId, toEntryId) is { } prior)
        {
            // Idempotent replay — same range was distilled before. Re-use the
            // existing watermark unchanged so caller can detect the no-op.
            return new DistillSessionResult(Array.Empty<EventId>(), prior, null, WasNoOp: true);
        }

        var priorFacts = ReadPriorFacts(workstream, limit: 50);
        var request = new SessionDistillationRequest(
            Session: session,
            Workstream: workstream,
            Entries: tail,
            PriorWatermark: watermark,
            PriorFacts: priorFacts,
            TokenBudget: 4096);

        var distilled = await _distiller.DistillAsync(request, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var ingested = new List<EventId>(distilled.Events.Count);
        foreach (var ev in distilled.Events)
        {
            var citation = BuildCitation(session, ev.SupportingEntryIds, fromEntryId, toEntryId);
            var validAt = ev.ValidAt ?? LastTimestampFor(tail, ev.SupportingEntryIds) ?? tail[^1].Timestamp;
            var envelope = new CaptureEvent(
                EventId: ev.EventId ?? new EventId("sess-" + Guid.NewGuid().ToString("N")),
                WorkstreamId: workstream,
                Channel: EventChannel.Epistemic,
                ValidAt: validAt,
                RecordedAt: _clock.GetUtcNow(),
                Payload: ev.Payload,
                Provenance: new CaptureProvenance(
                    Source: new CaptureSourceId("session/" + _distiller.Id),
                    Principal: capability.Principal,
                    Context: session.Value,
                    Citation: citation));
            var result = await _agent.IngestAsync(envelope, ct).ConfigureAwait(false);
            ingested.Add(result.EventId);
        }

        var advanced = new ContextWatermark(
            Session: session,
            LastDistilledEntryId: toEntryId,
            DistilledAt: _clock.GetUtcNow(),
            DistillerVersion: _distiller.Id);
        WriteWatermarkAndRun(advanced, fromEntryId, toEntryId, ingested.Count);

        return new DistillSessionResult(ingested, advanced, distilled.Dropped, WasNoOp: false);
    }

    public ContextWatermark? GetWatermark(SessionId session) => ReadWatermark(session);

    private static ContextWatermark EmptyWatermark(SessionId session, string distillerId, TimeProvider clock) =>
        new(session, LastDistilledEntryId: string.Empty, clock.GetUtcNow(), distillerId);

    private static IReadOnlyList<ContextEntry> TailAfter(
        IReadOnlyList<ContextEntry> entries, ContextWatermark? watermark)
    {
        if (watermark is null || string.IsNullOrEmpty(watermark.LastDistilledEntryId))
        {
            return entries;
        }
        var cutoff = watermark.LastDistilledEntryId;
        // Monotonic id semantics: entries strictly after cutoff. Compare
        // lexicographically (ULID, padded ordinal, both work).
        var tail = new List<ContextEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (string.CompareOrdinal(e.EntryId, cutoff) > 0)
            {
                tail.Add(e);
            }
        }
        return tail;
    }

    private static Citation.SessionRange BuildCitation(
        SessionId session,
        IReadOnlyList<string> supporting,
        string fallbackFrom,
        string fallbackTo)
    {
        if (supporting is null || supporting.Count == 0)
        {
            return new Citation.SessionRange(session, fallbackFrom, fallbackTo);
        }
        var min = supporting[0];
        var max = supporting[0];
        for (var i = 1; i < supporting.Count; i++)
        {
            var s = supporting[i];
            if (string.CompareOrdinal(s, min) < 0) min = s;
            if (string.CompareOrdinal(s, max) > 0) max = s;
        }
        return new Citation.SessionRange(session, min, max);
    }

    private static DateTimeOffset? LastTimestampFor(
        IReadOnlyList<ContextEntry> tail, IReadOnlyList<string> supportingIds)
    {
        if (supportingIds is null || supportingIds.Count == 0) return null;
        DateTimeOffset? best = null;
        var ids = new HashSet<string>(supportingIds, StringComparer.Ordinal);
        foreach (var e in tail)
        {
            if (ids.Contains(e.EntryId))
            {
                if (best is null || e.Timestamp > best.Value)
                {
                    best = e.Timestamp;
                }
            }
        }
        return best;
    }

    private ContextWatermark? ReadWatermark(SessionId session)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT last_entry_id, distilled_at, distiller_version
            FROM distillation_watermarks
            WHERE session_id = $sid;
            """;
        cmd.Parameters.AddWithValue("$sid", session.Value);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ContextWatermark(
            Session: session,
            LastDistilledEntryId: r.GetString(0),
            DistilledAt: DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
            DistillerVersion: r.GetString(2));
    }

    private ContextWatermark? TryGetExistingRun(SessionId session, string fromEntryId, string toEntryId)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT distilled_at
            FROM distillation_runs
            WHERE session_id = $sid AND from_entry_id = $from AND to_entry_id = $to;
            """;
        cmd.Parameters.AddWithValue("$sid", session.Value);
        cmd.Parameters.AddWithValue("$from", fromEntryId);
        cmd.Parameters.AddWithValue("$to", toEntryId);
        var raw = cmd.ExecuteScalar() as string;
        if (raw is null) return null;
        // Reuse the persisted watermark (always at-or-after this run's to-id).
        return ReadWatermark(session)
            ?? new ContextWatermark(session, toEntryId,
                DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture), _distiller!.Id);
    }

    private IReadOnlyList<PriorFact> ReadPriorFacts(WorkstreamId ws, int limit)
    {
        var list = new List<PriorFact>();
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT e.event_id, e.category, e.valid_at, e.payload_json
            FROM memory_events e
            LEFT JOIN memory_revocations r ON r.event_id = e.event_id
            WHERE e.workstream_id = $ws AND r.event_id IS NULL
              AND e.event_channel = 0
            ORDER BY e.created_at DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$ws", ws.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = new EventId(r.GetString(0));
            var category = (EpistemicCategory)r.GetInt32(1);
            var validAt = DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture);
            var statement = ExtractStatement(r.GetString(3));
            list.Add(new PriorFact(id, category, statement, validAt));
        }
        return list;
    }

    private static string ExtractStatement(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            foreach (var field in new[] { "statement", "content" })
            {
                if (doc.RootElement.TryGetProperty(field, out var v) &&
                    v.ValueKind == JsonValueKind.String)
                {
                    return v.GetString() ?? string.Empty;
                }
            }
        }
        catch { /* malformed payloads are surfaced as empty statements */ }
        return string.Empty;
    }

    private void WriteWatermarkAndRun(ContextWatermark watermark, string fromEntryId, string toEntryId, int eventsCount)
    {
        using var c = _connections.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO distillation_watermarks(session_id, last_entry_id, distilled_at, distiller_version)
                VALUES ($sid, $lid, $at, $dv)
                ON CONFLICT(session_id) DO UPDATE SET
                    last_entry_id     = excluded.last_entry_id,
                    distilled_at      = excluded.distilled_at,
                    distiller_version = excluded.distiller_version;
                """;
            cmd.Parameters.AddWithValue("$sid", watermark.Session.Value);
            cmd.Parameters.AddWithValue("$lid", watermark.LastDistilledEntryId);
            cmd.Parameters.AddWithValue("$at", watermark.DistilledAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$dv", watermark.DistillerVersion);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO distillation_runs(session_id, from_entry_id, to_entry_id, distilled_at, events_count)
                VALUES ($sid, $from, $to, $at, $n)
                ON CONFLICT(session_id, from_entry_id, to_entry_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$sid", watermark.Session.Value);
            cmd.Parameters.AddWithValue("$from", fromEntryId);
            cmd.Parameters.AddWithValue("$to", toEntryId);
            cmd.Parameters.AddWithValue("$at", watermark.DistilledAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$n", eventsCount);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
