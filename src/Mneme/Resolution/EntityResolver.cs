using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Ingest.Validation;
using Mneme.Storage;

namespace Mneme.Resolution;

/// <summary>
/// Phase 6 conservative three-tier entity resolver. Owns the
/// <c>entity_index</c> / <c>entity_mentions</c> / <c>entity_merges</c> /
/// <c>entity_merge_proposals</c> tables.
/// </summary>
/// <remarks>
/// <para>
/// Tier 1 (deterministic, auto-merge): <see cref="EntityCanonicalizer"/>
/// produces a UUID5 from a canonical key. Same canonical key in the same
/// workstream ⇒ same entity id, full stop. <em>Names alone never
/// auto-merge.</em>
/// </para>
/// <para>
/// Tier 2 (embedding ≥0.95, auto-merge): only runs when an
/// <see cref="IEmbeddingProvider"/> is registered. Loads candidate
/// embeddings of the same kind in the same workstream, compares cosine,
/// auto-merges the strongest match above 0.95. Threshold matches
/// Mem0 main.py:919 — see <c>research-design-lessons.md §3.4</c>.
/// </para>
/// <para>
/// Tier 3 (LLM propose, never auto-merge): only runs when an
/// <see cref="IEntityProposer"/> is registered. Hands candidate pairs to
/// the host's proposer; high-confidence proposals (≥0.5) are persisted
/// to <c>entity_merge_proposals</c> for human confirm/reject. Confirmation
/// re-cites the winner's pre-merge canonical state hash so a stale
/// proposal can't overwrite a freshly-changed entity.
/// </para>
/// </remarks>
public sealed class EntityResolver
{
    /// <summary>Tier 2 cosine threshold (Mem0 parity).</summary>
    public const double EmbeddingMatchThreshold = 0.95;
    /// <summary>Tier 3 minimum confidence for persisting a proposal.</summary>
    public const double MinProposalConfidence = 0.5;

    private readonly SqliteConnectionFactory _connections;
    private readonly IEmbeddingProvider? _embeddings;
    private readonly IEntityProposer? _proposer;
    private readonly TimeProvider _clock;

    public EntityResolver(SqliteConnectionFactory connections)
        : this(connections, null, null, TimeProvider.System) { }

    public EntityResolver(
        SqliteConnectionFactory connections,
        IEmbeddingProvider? embeddings,
        IEntityProposer? proposer,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        _connections = connections;
        _embeddings = embeddings;
        _proposer = proposer;
        _clock = clock;
    }

    /// <summary>
    /// Resolve an entity mention. Tier 1 short-circuit on canonical key
    /// hit; Tier 2 cosine compare; Tier 3 proposes (no auto-merge). New
    /// entity is created (and counted) when no tier matches.
    /// </summary>
    public async Task<EntityResolution> ResolveAsync(
        WorkstreamId workstream,
        EntityKind kind,
        string rawIdentifier,
        string displayName,
        EventId mentionedIn,
        CancellationToken ct = default)
    {
        WorkstreamIdValidator.EnsureValid(workstream.Value, nameof(workstream));
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("displayName is required.", nameof(displayName));
        }

        var canonical = EntityCanonicalizer.Canonicalize(kind, rawIdentifier);
        var now = _clock.GetUtcNow();

        // Tier 1 — deterministic.
        if (!string.IsNullOrEmpty(canonical))
        {
            var id = EntityCanonicalizer.ComputeEntityId(workstream, kind, canonical);
            using var c = _connections.Open();
            using var tx = c.BeginTransaction();
            var existing = TryReadEntity(c, tx, id);
            if (existing is not null)
            {
                UpdateMentionStats(c, tx, id, now);
                RecordMention(c, tx, id, mentionedIn, displayName, now);
                tx.Commit();
                return new EntityResolution(existing with { LastSeenAt = now, MentionCount = existing.MentionCount + 1 },
                    EntityResolutionTier.Deterministic, WasNew: false);
            }
            var fresh = new Entity(id, kind, canonical, displayName, workstream, now, now, 1);
            InsertEntity(c, tx, fresh);
            RecordMention(c, tx, id, mentionedIn, displayName, now);
            tx.Commit();
            return new EntityResolution(fresh, EntityResolutionTier.Deterministic, WasNew: true);
        }

        // Tier 2 — embedding similarity (requires provider + an existing
        // candidate pool in the same kind/workstream).
        if (_embeddings is not null)
        {
            var candidates = LoadCandidates(workstream, kind, limit: 200);
            if (candidates.Count > 0)
            {
                var vectors = await _embeddings
                    .EmbedAsync(new[] { displayName }.Concat(candidates.Select(e => e.DisplayName)).ToArray(), ct)
                    .ConfigureAwait(false);
                var query = vectors[0].Span;
                Entity? best = null;
                float bestSim = 0f;
                for (var i = 0; i < candidates.Count; i++)
                {
                    var sim = Mneme.Resolution.EmbeddingCosine.Similarity(query, vectors[i + 1].Span);
                    if (sim > bestSim) { bestSim = sim; best = candidates[i]; }
                }
                if (best is not null && bestSim >= EmbeddingMatchThreshold)
                {
                    using var c = _connections.Open();
                    using var tx = c.BeginTransaction();
                    UpdateMentionStats(c, tx, best.EntityId, now);
                    RecordMention(c, tx, best.EntityId, mentionedIn, displayName, now);
                    tx.Commit();
                    return new EntityResolution(best with { LastSeenAt = now, MentionCount = best.MentionCount + 1 },
                        EntityResolutionTier.Embedding, WasNew: false);
                }
            }
        }

        // Tier 3 — LLM proposes. We always materialise a fresh entity so
        // the mention has a real id to bind to; the proposal links it to
        // any candidate the LLM thinks it should fold into.
        var newId = EntityCanonicalizer.ComputeEntityId(workstream, kind, string.Empty);
        var freshEntity = new Entity(newId, kind, canonical, displayName, workstream, now, now, 1);
        using (var c = _connections.Open())
        using (var tx = c.BeginTransaction())
        {
            InsertEntity(c, tx, freshEntity);
            RecordMention(c, tx, newId, mentionedIn, displayName, now);
            tx.Commit();
        }

        if (_proposer is not null)
        {
            var siblings = LoadCandidates(workstream, kind, limit: 50)
                .Where(e => e.EntityId.Value != newId.Value)
                .ToArray();
            if (siblings.Length > 0)
            {
                var pairs = siblings.Select(s => new EntityMergeCandidatePair(freshEntity, s)).ToArray();
                var proposals = await _proposer.ProposeAsync(pairs, ct).ConfigureAwait(false);
                foreach (var proposal in proposals)
                {
                    if (proposal.Confidence < MinProposalConfidence) continue;
                    PersistProposal(workstream, proposal, now);
                }
                if (proposals.Any(p => p.Confidence >= MinProposalConfidence))
                {
                    return new EntityResolution(freshEntity, EntityResolutionTier.LlmProposed, WasNew: true);
                }
            }
        }

        return new EntityResolution(freshEntity, EntityResolutionTier.New, WasNew: true);
    }

    /// <summary>
    /// Confirm a Tier 3 proposal. Verifies the cited <c>WinnerStateHash</c>
    /// matches the winner's current canonical state and only then records
    /// the merge.
    /// </summary>
    public async Task<EntityMerge> ConfirmProposalAsync(
        string proposalId, PrincipalId confirmedBy, string rationale, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(proposalId);
        if (string.IsNullOrEmpty(confirmedBy.Value)) throw new ArgumentException("confirmedBy required");
        if (string.IsNullOrWhiteSpace(rationale)) throw new ArgumentException("rationale required");

        using var c = _connections.Open();
        using var tx = c.BeginTransaction();

        // Read the proposal.
        EntityMergeProposal proposal;
        int status;
        using (var sel = c.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT winner_id, loser_ids_json, confidence, rationale, proposed_by,
                       proposed_at, winner_state_hash, status
                FROM entity_merge_proposals WHERE proposal_id = $id;
                """;
            sel.Parameters.AddWithValue("$id", proposalId);
            using var r = sel.ExecuteReader();
            if (!r.Read())
            {
                throw new InvalidOperationException($"No proposal '{proposalId}'.");
            }
            proposal = new EntityMergeProposal(
                ProposalId: proposalId,
                WinnerId: new EntityId(r.GetString(0)),
                LoserIds: (JsonSerializer.Deserialize<string[]>(r.GetString(1)) ?? Array.Empty<string>())
                    .Select(s => new EntityId(s)).ToArray(),
                Confidence: r.GetDouble(2),
                Rationale: r.GetString(3),
                ProposedBy: r.GetString(4),
                ProposedAt: DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
                WinnerStateHash: r.GetString(6));
            status = r.GetInt32(7);
        }
        if (status != 0) throw new InvalidOperationException($"Proposal '{proposalId}' is already resolved.");

        // Recompute winner state hash and compare. Mismatch ⇒ stale.
        var actualHash = ComputeStateHash(c, tx, proposal.WinnerId);
        if (!string.Equals(actualHash, proposal.WinnerStateHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new StaleProposalError(new EventId(proposal.ProposalId), proposal.WinnerStateHash, actualHash);
        }

        var now = _clock.GetUtcNow();
        foreach (var loser in proposal.LoserIds)
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO entity_merges(winner_id, loser_id, confirmed_by, confirmed_at, rationale)
                VALUES ($w, $l, $by, $at, $r)
                ON CONFLICT(winner_id, loser_id) DO NOTHING;
                """;
            ins.Parameters.AddWithValue("$w", proposal.WinnerId.Value);
            ins.Parameters.AddWithValue("$l", loser.Value);
            ins.Parameters.AddWithValue("$by", confirmedBy.Value);
            ins.Parameters.AddWithValue("$at", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            ins.Parameters.AddWithValue("$r", rationale);
            ins.ExecuteNonQuery();
        }
        using (var upd = c.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE entity_merge_proposals
                   SET status = 1, resolved_by = $by, resolved_at = $at
                 WHERE proposal_id = $id;
                """;
            upd.Parameters.AddWithValue("$by", confirmedBy.Value);
            upd.Parameters.AddWithValue("$at", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            upd.Parameters.AddWithValue("$id", proposalId);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
        await Task.CompletedTask;
        return new EntityMerge(proposal.WinnerId, proposal.LoserIds, confirmedBy, now, rationale);
    }

    /// <summary>Reject a Tier 3 proposal.</summary>
    public Task RejectProposalAsync(string proposalId, PrincipalId rejectedBy, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(proposalId);
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE entity_merge_proposals
               SET status = 2, resolved_by = $by, resolved_at = $at
             WHERE proposal_id = $id AND status = 0;
            """;
        cmd.Parameters.AddWithValue("$by", rejectedBy.Value);
        cmd.Parameters.AddWithValue("$at", _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$id", proposalId);
        var n = cmd.ExecuteNonQuery();
        if (n == 0) throw new InvalidOperationException($"No pending proposal '{proposalId}'.");
        return Task.CompletedTask;
    }

    /// <summary>List pending Tier 3 proposals for a workstream (newest first).</summary>
    public IReadOnlyList<EntityMergeProposal> ListPendingProposals(WorkstreamId workstream, int limit = 100)
    {
        var result = new List<EntityMergeProposal>();
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT proposal_id, winner_id, loser_ids_json, confidence, rationale,
                   proposed_by, proposed_at, winner_state_hash
            FROM entity_merge_proposals
            WHERE workstream_id = $ws AND status = 0
            ORDER BY proposed_at DESC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$ws", workstream.Value);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new EntityMergeProposal(
                ProposalId: r.GetString(0),
                WinnerId: new EntityId(r.GetString(1)),
                LoserIds: (JsonSerializer.Deserialize<string[]>(r.GetString(2)) ?? Array.Empty<string>())
                    .Select(s => new EntityId(s)).ToArray(),
                Confidence: r.GetDouble(3),
                Rationale: r.GetString(4),
                ProposedBy: r.GetString(5),
                ProposedAt: DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
                WinnerStateHash: r.GetString(7)));
        }
        return result;
    }

    /// <summary>
    /// Popularity-dampening weight for retrieval scoring. Pattern from
    /// Mem0 main.py:1515-1517: <c>w = 1 / (1 + 0.001 * (n-1)^2)</c>.
    /// Prevents widely-shared entities ("john.smith") from dominating
    /// fuzzy matches forever.
    /// </summary>
    public static double PopularityWeight(int mentionCount)
    {
        if (mentionCount <= 1) return 1.0;
        var n = mentionCount - 1;
        return 1.0 / (1.0 + 0.001 * n * n);
    }

    // ----- private helpers ---------------------------------------------------

    private static Entity? TryReadEntity(SqliteConnection c, SqliteTransaction? tx, EntityId id)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT entity_id, workstream_id, kind, canonical_key, display_name,
                   first_seen_at, last_seen_at, mention_count
            FROM entity_index WHERE entity_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.Value);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    private List<Entity> LoadCandidates(WorkstreamId ws, EntityKind kind, int limit)
    {
        var result = new List<Entity>(limit);
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT entity_id, workstream_id, kind, canonical_key, display_name,
                   first_seen_at, last_seen_at, mention_count
            FROM entity_index
            WHERE workstream_id = $ws AND kind = $k
            ORDER BY mention_count DESC, last_seen_at DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$ws", ws.Value);
        cmd.Parameters.AddWithValue("$k", (int)kind);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(Map(r));
        return result;
    }

    private static Entity Map(SqliteDataReader r) => new(
        EntityId: new EntityId(r.GetString(0)),
        Kind: (EntityKind)r.GetInt32(2),
        CanonicalKey: r.GetString(3),
        DisplayName: r.GetString(4),
        Workstream: new WorkstreamId(r.GetString(1)),
        FirstSeenAt: DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
        LastSeenAt: DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
        MentionCount: r.GetInt32(7));

    private static void InsertEntity(SqliteConnection c, SqliteTransaction tx, Entity e)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO entity_index(entity_id, workstream_id, kind, canonical_key, display_name,
                first_seen_at, last_seen_at, mention_count)
            VALUES ($id, $ws, $k, $ck, $dn, $f, $l, $mc);
            """;
        cmd.Parameters.AddWithValue("$id", e.EntityId.Value);
        cmd.Parameters.AddWithValue("$ws", e.Workstream.Value);
        cmd.Parameters.AddWithValue("$k", (int)e.Kind);
        cmd.Parameters.AddWithValue("$ck", e.CanonicalKey);
        cmd.Parameters.AddWithValue("$dn", e.DisplayName);
        cmd.Parameters.AddWithValue("$f", e.FirstSeenAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$l", e.LastSeenAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$mc", e.MentionCount);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateMentionStats(SqliteConnection c, SqliteTransaction tx, EntityId id, DateTimeOffset now)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE entity_index
               SET mention_count = mention_count + 1, last_seen_at = $at
             WHERE entity_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.Value);
        cmd.Parameters.AddWithValue("$at", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static void RecordMention(SqliteConnection c, SqliteTransaction tx,
        EntityId id, EventId mentionedIn, string displayAsserted, DateTimeOffset at)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO entity_mentions(entity_id, event_id, asserted_display, at)
            VALUES ($id, $eid, $dn, $at)
            ON CONFLICT(entity_id, event_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$id", id.Value);
        cmd.Parameters.AddWithValue("$eid", mentionedIn.Value);
        cmd.Parameters.AddWithValue("$dn", displayAsserted);
        cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private void PersistProposal(WorkstreamId ws, EntityMergeProposal proposal, DateTimeOffset at)
    {
        using var c = _connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entity_merge_proposals(proposal_id, workstream_id, winner_id,
                loser_ids_json, confidence, rationale, proposed_by, proposed_at, winner_state_hash)
            VALUES ($pid, $ws, $w, $loj, $cf, $r, $by, $at, $hash);
            """;
        cmd.Parameters.AddWithValue("$pid", proposal.ProposalId);
        cmd.Parameters.AddWithValue("$ws", ws.Value);
        cmd.Parameters.AddWithValue("$w", proposal.WinnerId.Value);
        cmd.Parameters.AddWithValue("$loj", JsonSerializer.Serialize(proposal.LoserIds.Select(x => x.Value).ToArray()));
        cmd.Parameters.AddWithValue("$cf", proposal.Confidence);
        cmd.Parameters.AddWithValue("$r", proposal.Rationale);
        cmd.Parameters.AddWithValue("$by", proposal.ProposedBy);
        cmd.Parameters.AddWithValue("$at", proposal.ProposedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$hash", proposal.WinnerStateHash);
        cmd.ExecuteNonQuery();
    }

    private static string ComputeStateHash(SqliteConnection c, SqliteTransaction? tx, EntityId id)
    {
        var entity = TryReadEntity(c, tx, id)
            ?? throw new InvalidOperationException($"No entity '{id.Value}' for state-hash recomputation.");
        var canonical = $"v1|kind={(int)entity.Kind}|key={entity.CanonicalKey}|name={entity.DisplayName}|mentions={entity.MentionCount}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
