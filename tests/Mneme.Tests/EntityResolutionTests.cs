using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Resolution;

namespace Mneme.Tests;

public sealed class EntityCanonicalizerTests
{
    [Theory]
    [InlineData("Alice@Example.COM", "alice@example.com")]
    [InlineData("alice@example.com", "alice@example.com")]
    [InlineData("A.L.I.C.E@gmail.com", "alice@gmail.com")]
    [InlineData("alice@googlemail.com", "alice@gmail.com")]
    [InlineData("first.last+tag@OUTLOOK.com", "first.last+tag@outlook.com")] // dots only stripped for gmail
    public void Email_canonicalizes(string raw, string expected) =>
        Assert.Equal(expected, EntityCanonicalizer.Canonicalize(EntityKind.Email, raw));

    [Theory]
    [InlineData("JacobMS", "jacobms")]
    [InlineData("github-actions", "github-actions")]
    public void GitHubLogin_lowercases(string raw, string expected) =>
        Assert.Equal(expected, EntityCanonicalizer.Canonicalize(EntityKind.GitHubLogin, raw));

    [Theory]
    [InlineData("https://Example.com:443/foo/", "https://example.com/foo")]
    [InlineData("HTTP://example.com:80",        "http://example.com/")]
    [InlineData("https://foo.bar/path?q=1",     "https://foo.bar/path?q=1")]
    public void Url_canonicalizes(string raw, string expected) =>
        Assert.Equal(expected, EntityCanonicalizer.Canonicalize(EntityKind.Url, raw));

    [Theory]
    [InlineData(EntityKind.Name)]
    [InlineData(EntityKind.Other)]
    public void Name_and_Other_never_auto_merge(EntityKind k)
    {
        Assert.Equal(string.Empty, EntityCanonicalizer.Canonicalize(k, "anything"));
        Assert.False(EntityCanonicalizer.IsAutoMergeEligible(k, "anything"));
    }

    [Fact]
    public void Same_canonical_key_same_workstream_yields_same_id()
    {
        var w = new WorkstreamId("ws-a");
        var a = EntityCanonicalizer.ComputeEntityId(w, EntityKind.Email, "alice@gmail.com");
        var b = EntityCanonicalizer.ComputeEntityId(w, EntityKind.Email, "alice@gmail.com");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Same_canonical_key_different_workstream_yields_different_id()
    {
        var key = "alice@gmail.com";
        var a = EntityCanonicalizer.ComputeEntityId(new WorkstreamId("ws-a"), EntityKind.Email, key);
        var b = EntityCanonicalizer.ComputeEntityId(new WorkstreamId("ws-b"), EntityKind.Email, key);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(100)]
    public void PopularityWeight_dampens_with_count(int count)
    {
        var w = EntityResolver.PopularityWeight(count);
        Assert.InRange(w, 0.0, 1.0);
        if (count <= 1) Assert.Equal(1.0, w);
        else Assert.True(w < 1.0, $"weight {w} should be < 1.0 for count={count}");
    }
}

public sealed class EntityResolverTests : IDisposable
{
    private readonly string _tmpDir;
    public EntityResolverTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-er-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private (ServiceProvider sp, EntityResolver resolver, EventId seed) Build(
        IEmbeddingProvider? embeddings = null, IEntityProposer? proposer = null, string ws = "er-ws")
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = ws;
            o.SqlitePath = Path.Combine(_tmpDir, ws + ".db");
            o.UserId = "alice";
        });
        if (embeddings is not null) services.AddSingleton(embeddings);
        if (proposer is not null) services.AddSingleton(proposer);
        var sp = services.BuildServiceProvider();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var seed = new EventId("er-seed-001");
        agent.IngestAsync(new CaptureEvent(
            seed, new WorkstreamId(ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload("seed", "t"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")))).GetAwaiter().GetResult();
        return (sp, sp.GetRequiredService<EntityResolver>(), seed);
    }

    [Fact]
    public async Task Tier1_email_re_assertion_returns_same_entity_id()
    {
        var (sp, resolver, seed) = Build();
        using var _ = sp;
        var ws = new WorkstreamId("er-ws");
        var first = await resolver.ResolveAsync(ws, EntityKind.Email, "Alice@Example.com", "Alice", seed);
        var second = await resolver.ResolveAsync(ws, EntityKind.Email, "alice@example.COM", "ALICE", seed);
        Assert.Equal(EntityResolutionTier.Deterministic, first.Tier);
        Assert.Equal(EntityResolutionTier.Deterministic, second.Tier);
        Assert.True(first.WasNew);
        Assert.False(second.WasNew);
        Assert.Equal(first.Entity.EntityId, second.Entity.EntityId);
        Assert.Equal(2, second.Entity.MentionCount);
    }

    [Fact]
    public async Task Tier1_workstream_isolation()
    {
        var (sp, resolver, seed) = Build(ws: "ws-iso");
        using var _ = sp;
        var a = await resolver.ResolveAsync(new WorkstreamId("ws-iso"), EntityKind.Email, "alice@gmail.com", "Alice", seed);
        // Same email different workstream — must produce a different entity id.
        var b = EntityCanonicalizer.ComputeEntityId(new WorkstreamId("ws-other"), EntityKind.Email, "alice@gmail.com");
        Assert.NotEqual(a.Entity.EntityId, b);
    }

    [Fact]
    public async Task Name_alone_never_auto_merges_Tier1()
    {
        var (sp, resolver, seed) = Build();
        using var _ = sp;
        var ws = new WorkstreamId("er-ws");
        var a = await resolver.ResolveAsync(ws, EntityKind.Name, "Alice", "Alice", seed);
        var b = await resolver.ResolveAsync(ws, EntityKind.Name, "Alice", "Alice", seed);
        // Two different entities — no Tier 1, no proposer → each is New.
        Assert.NotEqual(a.Entity.EntityId, b.Entity.EntityId);
        Assert.Equal(EntityResolutionTier.New, a.Tier);
        Assert.Equal(EntityResolutionTier.New, b.Tier);
    }

    [Fact]
    public async Task Tier2_embedding_above_threshold_auto_merges()
    {
        var fake = new FakeEmbedding(dim: 4);
        // Seed: any text containing "alpha" gets vector (1,0,0,0).
        fake.Map("Alice Smith", new[] { 1f, 0f, 0f, 0f });
        fake.Map("alice smith", new[] { 1f, 0f, 0f, 0f }); // cosine=1.0 vs Alice
        fake.Map("Bob Jones",   new[] { 0f, 1f, 0f, 0f });

        var (sp, resolver, seed) = Build(embeddings: fake);
        using var _ = sp;
        var ws = new WorkstreamId("er-ws");
        var a = await resolver.ResolveAsync(ws, EntityKind.Name, "X", "Alice Smith", seed);
        var b = await resolver.ResolveAsync(ws, EntityKind.Name, "X", "alice smith", seed);
        Assert.Equal(EntityResolutionTier.Embedding, b.Tier);
        Assert.Equal(a.Entity.EntityId, b.Entity.EntityId);
    }

    [Fact]
    public async Task Tier2_below_threshold_does_not_merge()
    {
        var fake = new FakeEmbedding(dim: 4);
        fake.Map("Alice Smith", new[] { 1f, 0f, 0f, 0f });
        fake.Map("Bob Jones",   new[] { 0f, 1f, 0f, 0f }); // cosine=0 vs Alice

        var (sp, resolver, seed) = Build(embeddings: fake);
        using var _ = sp;
        var ws = new WorkstreamId("er-ws");
        var a = await resolver.ResolveAsync(ws, EntityKind.Name, "X", "Alice Smith", seed);
        var b = await resolver.ResolveAsync(ws, EntityKind.Name, "X", "Bob Jones", seed);
        Assert.NotEqual(a.Entity.EntityId, b.Entity.EntityId);
        Assert.NotEqual(EntityResolutionTier.Embedding, b.Tier);
    }

    [Fact]
    public async Task Tier3_proposer_persists_high_confidence_merge_proposal()
    {
        var proposer = new FakeProposer((left, right) =>
            // Always propose merging if both display names start with same first letter.
            left.DisplayName[0] == right.DisplayName[0]
                ? new EntityMergeProposal(
                    ProposalId: "p-" + Guid.NewGuid().ToString("N"),
                    WinnerId: right.EntityId,
                    LoserIds: new[] { left.EntityId },
                    Confidence: 0.9,
                    Rationale: "same initial",
                    ProposedBy: "fake/proposer@1",
                    ProposedAt: DateTimeOffset.UtcNow,
                    WinnerStateHash: "irrelevant-for-this-test")
                : null);

        var (sp, resolver, seed) = Build(proposer: proposer);
        using var _ = sp;
        var ws = new WorkstreamId("er-ws");
        var a = await resolver.ResolveAsync(ws, EntityKind.Name, "X", "Alice Smith", seed);
        var b = await resolver.ResolveAsync(ws, EntityKind.Name, "X", "Alex Smith", seed);
        Assert.Equal(EntityResolutionTier.LlmProposed, b.Tier);
        Assert.NotEqual(a.Entity.EntityId, b.Entity.EntityId); // NOT auto-merged.

        var pending = resolver.ListPendingProposals(ws);
        Assert.Single(pending);
        Assert.Equal(0.9, pending[0].Confidence);
    }

    [Fact]
    public async Task Tier3_low_confidence_proposals_are_discarded()
    {
        var proposer = new FakeProposer((l, r) => new EntityMergeProposal(
            ProposalId: "p-" + Guid.NewGuid().ToString("N"),
            WinnerId: r.EntityId, LoserIds: new[] { l.EntityId },
            Confidence: 0.2, Rationale: "weak",
            ProposedBy: "fake/proposer@1", ProposedAt: DateTimeOffset.UtcNow,
            WinnerStateHash: "x"));

        var (sp, resolver, seed) = Build(proposer: proposer);
        using var _ = sp;
        var ws = new WorkstreamId("er-ws");
        await resolver.ResolveAsync(ws, EntityKind.Name, "X", "Alice Smith", seed);
        var b = await resolver.ResolveAsync(ws, EntityKind.Name, "X", "Alex Smith", seed);
        Assert.Equal(EntityResolutionTier.New, b.Tier); // dropped → still new
        Assert.Empty(resolver.ListPendingProposals(ws));
    }

    [Fact]
    public async Task Reject_proposal_removes_it_from_pending()
    {
        var proposer = new FakeProposer((l, r) => new EntityMergeProposal(
            ProposalId: "p-test-1",
            WinnerId: r.EntityId, LoserIds: new[] { l.EntityId },
            Confidence: 0.9, Rationale: "x",
            ProposedBy: "fake", ProposedAt: DateTimeOffset.UtcNow,
            WinnerStateHash: "x"));

        var (sp, resolver, seed) = Build(proposer: proposer);
        using var _ = sp;
        var ws = new WorkstreamId("er-ws");
        await resolver.ResolveAsync(ws, EntityKind.Name, "X", "Alice", seed);
        await resolver.ResolveAsync(ws, EntityKind.Name, "X", "Alex", seed);
        Assert.Single(resolver.ListPendingProposals(ws));

        await resolver.RejectProposalAsync("p-test-1", new PrincipalId("alice"), "no thanks");
        Assert.Empty(resolver.ListPendingProposals(ws));
    }

    [Fact]
    public async Task Confirm_proposal_with_stale_hash_throws()
    {
        // We compute a winner-state-hash from a non-current state and try
        // to confirm — should StaleProposalError.
        var proposer = new FakeProposer((l, r) => new EntityMergeProposal(
            ProposalId: "p-stale",
            WinnerId: r.EntityId, LoserIds: new[] { l.EntityId },
            Confidence: 0.9, Rationale: "x",
            ProposedBy: "fake", ProposedAt: DateTimeOffset.UtcNow,
            WinnerStateHash: new string('0', 64))); // deliberately wrong

        var (sp, resolver, seed) = Build(proposer: proposer);
        using var _ = sp;
        var ws = new WorkstreamId("er-ws");
        await resolver.ResolveAsync(ws, EntityKind.Name, "X", "Alice", seed);
        await resolver.ResolveAsync(ws, EntityKind.Name, "X", "Alex", seed);

        await Assert.ThrowsAsync<StaleProposalError>(async () =>
            await resolver.ConfirmProposalAsync("p-stale", new PrincipalId("alice"), "looks good"));
    }

    // ----- test doubles ------------------------------------------------------

    private sealed class FakeEmbedding : IEmbeddingProvider
    {
        private readonly Dictionary<string, float[]> _map = new();
        public string Id => "fake/embedding";
        public int Dimensions { get; }
        public FakeEmbedding(int dim) { Dimensions = dim; }
        public void Map(string text, float[] vector) => _map[text] = vector;
        public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            var result = texts.Select(t => _map.TryGetValue(t, out var v)
                ? new ReadOnlyMemory<float>(v)
                : new ReadOnlyMemory<float>(new float[Dimensions])).ToArray();
            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(result);
        }
    }

    private sealed class FakeProposer : IEntityProposer
    {
        private readonly Func<Entity, Entity, EntityMergeProposal?> _fn;
        public string Id => "fake/proposer@1";
        public FakeProposer(Func<Entity, Entity, EntityMergeProposal?> fn) { _fn = fn; }
        public Task<IReadOnlyList<EntityMergeProposal>> ProposeAsync(IReadOnlyList<EntityMergeCandidatePair> candidates, CancellationToken ct = default)
        {
            var result = new List<EntityMergeProposal>();
            foreach (var pair in candidates)
            {
                var p = _fn(pair.Left, pair.Right);
                if (p is not null) result.Add(p);
            }
            return Task.FromResult<IReadOnlyList<EntityMergeProposal>>(result);
        }
    }
}
