using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Curation;
using Mneme.Hosting;
using Mneme.Ingest;

namespace Mneme.Tests;

public sealed class SqliteMemoryCuratorTests : IDisposable
{
    private readonly string _tmpDir;

    public SqliteMemoryCuratorTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-cur-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private (ServiceProvider sp, IMemoryAgent agent, IMemoryCurator curator, ICurationLog log,
             Mneme.Storage.SqliteConnectionFactory factory, EventId seedEvent)
        BuildHost(string workstream = "cur-ws")
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = workstream;
            o.SqlitePath = Path.Combine(_tmpDir, workstream + ".db");
            o.UserId = "alice";
        });
        var sp = services.BuildServiceProvider();
        var agent = sp.GetRequiredService<IMemoryAgent>();
        var seed = new EventId("cur-seed-001");
        agent.IngestAsync(new CaptureEvent(
            seed, new WorkstreamId(workstream), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new EvidencePayload("the cat sat on the mat", "test"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("p")))).GetAwaiter().GetResult();
        return (sp, agent, sp.GetRequiredService<IMemoryCurator>(), sp.GetRequiredService<ICurationLog>(),
                sp.GetRequiredService<Mneme.Storage.SqliteConnectionFactory>(), seed);
    }

    private static CurationCapability FullCap(string ws = "cur-ws") =>
        new(Principal: new PrincipalId("alice"),
            Workstream: new WorkstreamId(ws),
            NotBefore: DateTimeOffset.UtcNow.AddDays(-1),
            NotAfter: DateTimeOffset.UtcNow.AddDays(1),
            CanAmend: true, CanAnnotate: true, CanPin: true, CanDemote: true,
            CanSplit: true, CanMerge: true, CanRevert: true, CanReview: true);

    [Fact]
    public async Task Annotate_appends_curation_event_and_shows_in_log()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        var token = ctx.sp.GetRequiredService<CapabilityToken>();
        var result = await ctx.curator.AnnotateAsync(ctx.seedEvent, "needs follow-up", FullCap());
        Assert.True(result.CurationEventId.HasValue);

        var history = await ctx.log.GetCurationHistoryAsync(
            new WorkstreamId("cur-ws"), DateTimeOffset.UtcNow.AddDays(-1), token);
        Assert.Single(history);
        Assert.Equal(CurationType.Annotated, history[0].Type);
        Assert.Equal("needs follow-up", history[0].Rationale);
    }

    [Fact]
    public async Task Revert_denies_a_capability_scoped_to_a_different_workstream()
    {
        // Regression: RevertCurationAsync must apply the same workstream-scope
        // guard AppendCuration does. Before the fix, a revert-capable token scoped
        // to workstream A could revert curations living in workstream B.
        var ctx = BuildHost(); using var _ = ctx.sp;
        var annotated = await ctx.curator.AnnotateAsync(ctx.seedEvent, "note", FullCap());
        Assert.True(annotated.CurationEventId.HasValue);

        await Assert.ThrowsAsync<CapabilityDeniedError>(
            () => ctx.curator.RevertCurationAsync(annotated.CurationEventId, "undo", FullCap("other-ws")));

        // The correctly-scoped token still works (control).
        var reverted = await ctx.curator.RevertCurationAsync(annotated.CurationEventId, "undo", FullCap());
        Assert.True(reverted.CurationEventId.HasValue);
    }

    [Fact]
    public async Task Amend_with_correct_pre_state_hash_succeeds()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        var hash = PreStateHasher.ComputeHash(ctx.factory, ctx.seedEvent);
        var result = await ctx.curator.AmendFactAsync(
            new FactId(ctx.seedEvent.Value), hash,
            new FactAmendment("the cat sat on the rug", "correction"),
            FullCap());
        Assert.True(result.CurationEventId.HasValue);
        Assert.Equal(hash, result.PreStateHash);
    }

    [Fact]
    public async Task Amend_with_stale_pre_state_hash_throws()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        var staleHash = new string('0', 64);
        await Assert.ThrowsAsync<StaleProposalError>(async () =>
            await ctx.curator.AmendFactAsync(
                new FactId(ctx.seedEvent.Value), staleHash,
                new FactAmendment("anything", "test"),
                FullCap()));
    }

    [Fact]
    public async Task Amend_changes_pre_state_hash_so_repeat_amend_with_old_hash_fails()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        var hash1 = PreStateHasher.ComputeHash(ctx.factory, ctx.seedEvent);
        await ctx.curator.AmendFactAsync(new FactId(ctx.seedEvent.Value), hash1,
            new FactAmendment("v2", "first amend"), FullCap());

        await Assert.ThrowsAsync<StaleProposalError>(async () =>
            await ctx.curator.AmendFactAsync(new FactId(ctx.seedEvent.Value), hash1,
                new FactAmendment("v3", "second amend with stale hash"), FullCap()));

        var hash2 = PreStateHasher.ComputeHash(ctx.factory, ctx.seedEvent);
        Assert.NotEqual(hash1, hash2);
        var ok = await ctx.curator.AmendFactAsync(new FactId(ctx.seedEvent.Value), hash2,
            new FactAmendment("v3", "second amend with fresh hash"), FullCap());
        Assert.True(ok.CurationEventId.HasValue);
    }

    [Fact]
    public async Task Pin_requires_multiplier_above_1()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.curator.PinAsync(ctx.seedEvent, PinScope.Workstream, 1.0f, FullCap()));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.curator.PinAsync(ctx.seedEvent, PinScope.Workstream, 0.5f, FullCap()));
        var ok = await ctx.curator.PinAsync(ctx.seedEvent, PinScope.Workstream, 2.0f, FullCap());
        Assert.True(ok.CurationEventId.HasValue);
    }

    [Fact]
    public async Task Demote_requires_multiplier_between_0_and_1()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.curator.DemoteAsync(ctx.seedEvent, 0.0f, FullCap()));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.curator.DemoteAsync(ctx.seedEvent, 1.0f, FullCap()));
        var ok = await ctx.curator.DemoteAsync(ctx.seedEvent, 0.3f, FullCap());
        Assert.True(ok.CurationEventId.HasValue);
    }

    [Theory]
    [InlineData(nameof(CurationCapability.CanAmend))]
    [InlineData(nameof(CurationCapability.CanAnnotate))]
    [InlineData(nameof(CurationCapability.CanPin))]
    [InlineData(nameof(CurationCapability.CanDemote))]
    [InlineData(nameof(CurationCapability.CanRevert))]
    public async Task Each_operation_denied_when_its_flag_is_unset(string deniedFlag)
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        var cap = new CurationCapability(
            Principal: new PrincipalId("bob"),
            Workstream: new WorkstreamId("cur-ws"),
            NotBefore: DateTimeOffset.UtcNow.AddDays(-1),
            NotAfter: DateTimeOffset.UtcNow.AddDays(1),
            CanAmend: deniedFlag != nameof(CurationCapability.CanAmend),
            CanAnnotate: deniedFlag != nameof(CurationCapability.CanAnnotate),
            CanPin: deniedFlag != nameof(CurationCapability.CanPin),
            CanDemote: deniedFlag != nameof(CurationCapability.CanDemote),
            CanRevert: deniedFlag != nameof(CurationCapability.CanRevert));

        Func<Task> attempt = deniedFlag switch
        {
            nameof(CurationCapability.CanAmend) => async () =>
            {
                var h = PreStateHasher.ComputeHash(ctx.factory, ctx.seedEvent);
                await ctx.curator.AmendFactAsync(new FactId(ctx.seedEvent.Value), h,
                    new FactAmendment("x", "x"), cap);
            },
            nameof(CurationCapability.CanAnnotate) => async () =>
                await ctx.curator.AnnotateAsync(ctx.seedEvent, "x", cap),
            nameof(CurationCapability.CanPin) => async () =>
                await ctx.curator.PinAsync(ctx.seedEvent, PinScope.Workstream, 2.0f, cap),
            nameof(CurationCapability.CanDemote) => async () =>
                await ctx.curator.DemoteAsync(ctx.seedEvent, 0.3f, cap),
            nameof(CurationCapability.CanRevert) => async () =>
                await ctx.curator.RevertCurationAsync(new EventId("x"), "x", cap),
            _ => throw new InvalidOperationException(),
        };
        await Assert.ThrowsAsync<CapabilityDeniedError>(attempt);
    }

    [Fact]
    public async Task Revert_marks_original_curation_as_reverted()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        var pin = await ctx.curator.PinAsync(ctx.seedEvent, PinScope.Workstream, 2.0f, FullCap());
        var rev = await ctx.curator.RevertCurationAsync(pin.CurationEventId, "mistake", FullCap());

        using var c = ctx.factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT reverted_by FROM curation_events WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$id", pin.CurationEventId.Value);
        var revBy = (string?)cmd.ExecuteScalar();
        Assert.Equal(rev.CurationEventId.Value, revBy);
    }

    [Fact]
    public async Task Revert_of_already_reverted_throws()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        var ann = await ctx.curator.AnnotateAsync(ctx.seedEvent, "first", FullCap());
        await ctx.curator.RevertCurationAsync(ann.CurationEventId, "mistake", FullCap());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ctx.curator.RevertCurationAsync(ann.CurationEventId, "again", FullCap()));
    }

    [Fact]
    public async Task Reverting_a_revert_event_is_refused()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        var ann = await ctx.curator.AnnotateAsync(ctx.seedEvent, "x", FullCap());
        var rev = await ctx.curator.RevertCurationAsync(ann.CurationEventId, "x", FullCap());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ctx.curator.RevertCurationAsync(rev.CurationEventId, "x", FullCap()));
    }

    [Fact]
    public async Task PreStateHasher_changes_after_pin()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        var h0 = PreStateHasher.ComputeHash(ctx.factory, ctx.seedEvent);
        await ctx.curator.PinAsync(ctx.seedEvent, PinScope.Workstream, 2.0f, FullCap());
        var h1 = PreStateHasher.ComputeHash(ctx.factory, ctx.seedEvent);
        Assert.NotEqual(h0, h1);
    }

    [Fact]
    public async Task Split_and_Merge_throw_NotImplemented_for_now()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        var h = PreStateHasher.ComputeHash(ctx.factory, ctx.seedEvent);
        await Assert.ThrowsAsync<NotImplementedException>(async () =>
            await ctx.curator.SplitFactAsync(new FactId(ctx.seedEvent.Value),
                new[]
                {
                    new FactSplitPart("a", EpistemicCategory.Fact),
                    new FactSplitPart("b", EpistemicCategory.Fact)
                }, h, FullCap()));
        await Assert.ThrowsAsync<NotImplementedException>(async () =>
            await ctx.curator.MergeFactsAsync(
                new[] { new FactId(ctx.seedEvent.Value), new FactId(ctx.seedEvent.Value) },
                new FactMerged("m", EpistemicCategory.Fact, DateTimeOffset.UtcNow), h, FullCap()));
    }

    [Fact]
    public async Task CurationLog_filters_by_principal()
    {
        var ctx = BuildHost(); using var _ = ctx.sp;
        var token = ctx.sp.GetRequiredService<CapabilityToken>();
        var aliceCap = FullCap();
        var bobCap = aliceCap with { Principal = new PrincipalId("bob") };

        await ctx.curator.AnnotateAsync(ctx.seedEvent, "by alice", aliceCap);
        await ctx.curator.AnnotateAsync(ctx.seedEvent, "by bob",   bobCap);

        var byAlice = await ctx.log.GetCurationsByPrincipalAsync(
            new PrincipalId("alice"), DateTimeOffset.UtcNow.AddDays(-1), token);
        Assert.Single(byAlice);
        Assert.Equal("by alice", byAlice[0].Rationale);
    }
}
