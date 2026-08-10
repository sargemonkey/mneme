using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Storage;
using Mneme.Writer;

namespace Mneme.Writer.Tests;

/// <summary>
/// End-to-end proof of the writer setup→payoff ledger (ADR-0005 / Phase 15.B):
/// a <see cref="NarrativeCommitmentPayload"/> pair (setup + payoff sharing a
/// commitment id) becomes a persisted, queryable ledger, and
/// <see cref="IWriterMemory.GetOpenCommitmentsAsync"/> returns exactly the
/// unpaid promises / dangling setups — the writer domain's headline gap. Also
/// exercises the <see cref="Mneme.Hosting.Profiles.ISchemaModule"/> path of the
/// Profile SDK (the profile owns <c>projection_narrative_commitments</c>), which
/// the continuity slice deliberately skipped.
/// </summary>
public sealed class NarrativeCommitmentTests : IDisposable
{
    private readonly string _tmpDir;
    private const string Ws = "novel";

    public NarrativeCommitmentTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-writer-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ } }

    // ── host + helpers ────────────────────────────────────────────────────

    private (ServiceProvider sp, IMemoryAgent agent, IWriterMemory writer) BuildHost()
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = Ws;
            o.SqlitePath = Path.Combine(_tmpDir, "novel.mneme.db");
            o.UserId = "author";
        }).AddMnemeWriterProfile();
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IMemoryAgent>(), sp.GetRequiredService<IWriterMemory>());
    }

    private static CaptureEvent Commitment(string id, string commitmentId, CommitmentRole role,
        string description, string kind = "foreshadow", DateTimeOffset? storyTime = null,
        string principal = "author")
    {
        var at = storyTime ?? DateTimeOffset.UtcNow;
        return new CaptureEvent(
            new EventId(id), new WorkstreamId(Ws), EventChannel.Epistemic,
            ValidAt: at, RecordedAt: DateTimeOffset.UtcNow,
            new NarrativeCommitmentPayload(commitmentId, role, description, kind),
            new CaptureProvenance(new CaptureSourceId("editor"), new PrincipalId(principal)));
    }

    private static CapabilityToken Token(string principal = "author")
        => new(
            Principal: new PrincipalId(principal),
            Workstream: new WorkstreamId(Ws),
            NotBefore: DateTimeOffset.UtcNow.AddMinutes(-5),
            NotAfter: DateTimeOffset.UtcNow.AddHours(1),
            AllowedCategories: Array.Empty<EpistemicCategory>());

    // ── the headline: unpaid promises are first-class + persisted ─────────

    [Fact]
    public async Task A_setup_with_no_payoff_is_an_open_commitment()
    {
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(Commitment("s1", "commitment:chekhovs-gun", CommitmentRole.Setup,
            "a loaded gun hangs over the mantel"));

        var open = await writer.GetOpenCommitmentsAsync(Token());
        var c = Assert.Single(open);
        Assert.Equal("commitment:chekhovs-gun", c.CommitmentId);
        Assert.Equal("foreshadow", c.Kind);
        Assert.Equal("a loaded gun hangs over the mantel", c.Description);
        Assert.Equal(new EventId("s1"), c.SetupEvent);
    }

    [Fact]
    public async Task A_setup_that_is_paid_off_is_not_open()
    {
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(Commitment("s1", "commitment:chekhovs-gun", CommitmentRole.Setup,
            "a loaded gun hangs over the mantel"));
        await agent.IngestAsync(Commitment("p1", "commitment:chekhovs-gun", CommitmentRole.Payoff,
            "Vera fires the gun in the study"));

        Assert.Empty(await writer.GetOpenCommitmentsAsync(Token()));
    }

    [Fact]
    public async Task Payoff_can_arrive_before_setup_and_still_closes_the_promise()
    {
        // Ingest order must not matter — the ledger pairs by commitment_id, not
        // by arrival. (An author might record the payoff scene first.)
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(Commitment("p1", "commitment:locked-box", CommitmentRole.Payoff,
            "Helios finally opens the box"));
        await agent.IngestAsync(Commitment("s1", "commitment:locked-box", CommitmentRole.Setup,
            "Helios carries a box he refuses to open"));

        Assert.Empty(await writer.GetOpenCommitmentsAsync(Token()));
    }

    [Fact]
    public async Task Only_the_unpaid_promises_are_returned_among_several()
    {
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        var t0 = DateTimeOffset.UtcNow;
        // paid
        await agent.IngestAsync(Commitment("s1", "commitment:gun", CommitmentRole.Setup, "the gun", storyTime: t0));
        await agent.IngestAsync(Commitment("p1", "commitment:gun", CommitmentRole.Payoff, "gun fired"));
        // unpaid (earlier in story)
        await agent.IngestAsync(Commitment("s2", "commitment:prophecy", CommitmentRole.Setup, "the prophecy",
            storyTime: t0.AddMinutes(-10)));
        // unpaid (later in story)
        await agent.IngestAsync(Commitment("s3", "commitment:scar", CommitmentRole.Setup, "the mysterious scar",
            storyTime: t0.AddMinutes(10)));

        var open = await writer.GetOpenCommitmentsAsync(Token());
        Assert.Equal(2, open.Count);
        // Ordered earliest-in-story first.
        Assert.Equal("commitment:prophecy", open[0].CommitmentId);
        Assert.Equal("commitment:scar", open[1].CommitmentId);
    }

    [Fact]
    public async Task Distinct_commitment_ids_do_not_pay_each_other_off()
    {
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(Commitment("s1", "commitment:gun", CommitmentRole.Setup, "the gun"));
        // A payoff for a DIFFERENT promise must not close the gun setup.
        await agent.IngestAsync(Commitment("p1", "commitment:other", CommitmentRole.Payoff, "unrelated payoff"));

        var c = Assert.Single(await writer.GetOpenCommitmentsAsync(Token()));
        Assert.Equal("commitment:gun", c.CommitmentId);
    }

    // ── the invariant: rebuildable from the append-only log ───────────────

    [Fact]
    public async Task The_ledger_is_rebuildable_from_the_log()
    {
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(Commitment("s1", "commitment:gun", CommitmentRole.Setup, "the gun"));
        await agent.IngestAsync(Commitment("s2", "commitment:prophecy", CommitmentRole.Setup, "the prophecy"));
        await agent.IngestAsync(Commitment("p1", "commitment:gun", CommitmentRole.Payoff, "gun fired"));
        Assert.Single(await writer.GetOpenCommitmentsAsync(Token())); // only prophecy is open

        // Nuke the profile's own table, then RebuildAll must reconstruct it.
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using (var conn = factory.Open())
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM projection_narrative_commitments;";
            del.ExecuteNonQuery();
        }
        Assert.Empty(await writer.GetOpenCommitmentsAsync(Token()));

        var counts = sp.GetRequiredService<Mneme.Projections.ProjectorPipeline>().RebuildAll();
        Assert.True(counts.ContainsKey("writer-narrative-commitments"),
            "commitment projector did not participate in RebuildAll");
        Assert.Equal(3, counts["writer-narrative-commitments"]); // 3 commitment events replayed

        var c = Assert.Single(await writer.GetOpenCommitmentsAsync(Token()));
        Assert.Equal("commitment:prophecy", c.CommitmentId);
    }

    // ── SDK: the profile's own schema module actually created its table ───

    [Fact]
    public void Schema_module_created_the_commitments_table_and_recorded_its_version()
    {
        var (sp, _, _) = BuildHost();
        using var _d = sp;
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();

        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM projection_narrative_commitments;";
            Assert.Equal(0L, (long)q.ExecuteScalar()!);
        }
        using (var v = c.CreateCommand())
        {
            v.CommandText = "SELECT value FROM schema_meta WHERE key = 'module:writer-commitments';";
            Assert.Equal("1", v.ExecuteScalar() as string);
        }
    }

    // ── locked decision #11: secrets redacted inline at ingest ────────────

    [Fact]
    public async Task Secret_in_a_commitment_description_is_redacted_before_the_wal()
    {
        var (sp, agent, _) = BuildHost();
        using var _d = sp;

        await agent.IngestAsync(new CaptureEvent(
            new EventId("sec-1"), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new NarrativeCommitmentPayload("commitment:x", CommitmentRole.Setup,
                "deploy uses password = supersecretvalue123 somewhere"),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("author"))));

        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM memory_events WHERE event_id = 'sec-1';";
        var raw = (string)cmd.ExecuteScalar()!;
        Assert.Contains("\"$type\":\"NarrativeCommitmentPayload\"", raw);
        Assert.DoesNotContain("supersecretvalue123", raw);
    }

    // ── locked decision #8: capability-guarded read ───────────────────────

    [Fact]
    public async Task Expired_token_is_denied()
    {
        var (sp, _, writer) = BuildHost();
        using var _d = sp;
        var expired = new CapabilityToken(
            new PrincipalId("author"), new WorkstreamId(Ws),
            DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1),
            Array.Empty<EpistemicCategory>());
        await Assert.ThrowsAsync<CapabilityDeniedError>(
            () => writer.GetOpenCommitmentsAsync(expired));
    }

    [Fact]
    public async Task Token_that_disallows_the_goal_category_is_denied()
    {
        // Commitments ride under Goal; a Fact-only token must not read them.
        var (sp, _, writer) = BuildHost();
        using var _d = sp;
        var factOnly = new CapabilityToken(
            new PrincipalId("author"), new WorkstreamId(Ws),
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1),
            AllowedCategories: new[] { EpistemicCategory.Fact });
        await Assert.ThrowsAsync<CapabilityDeniedError>(
            () => writer.GetOpenCommitmentsAsync(factOnly));
    }

    // ── second-order: a hidden payoff must not silently close a promise ───

    [Fact]
    public async Task A_private_payoff_by_another_author_does_not_close_the_promise_for_a_different_viewer()
    {
        // Second-order safety: whether a promise reads as "paid" depends on what
        // the VIEWER can see. A payoff that is Private to ghostwriter must not
        // close author's promise when author queries (and its existence is not
        // leaked); the owner ghostwriter still sees it closed.
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(Commitment("s1", "commitment:twist", CommitmentRole.Setup,
            "a hint that the mentor is the villain", principal: "author"));
        await agent.IngestAsync(Commitment("p1", "commitment:twist", CommitmentRole.Payoff,
            "the mentor is unmasked", principal: "ghostwriter"));

        // Force the payoff Private (visibility = 0, owner-only) to ghostwriter.
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using (var conn = factory.Open())
        using (var vis = conn.CreateCommand())
        {
            vis.CommandText = """
                INSERT INTO memory_visibility(event_id, workstream_id, visibility, set_at)
                VALUES ('p1', $ws, 0, $at)
                ON CONFLICT(event_id) DO UPDATE SET visibility = 0;
                """;
            vis.Parameters.AddWithValue("$ws", Ws);
            vis.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            vis.ExecuteNonQuery();
        }

        // author can't see the private payoff → the promise still reads OPEN.
        var authorView = await writer.GetOpenCommitmentsAsync(Token("author"));
        Assert.Equal("commitment:twist", Assert.Single(authorView).CommitmentId);

        // ghostwriter owns the payoff → for them the promise is CLOSED.
        Assert.Empty(await writer.GetOpenCommitmentsAsync(Token("ghostwriter")));
    }
}
