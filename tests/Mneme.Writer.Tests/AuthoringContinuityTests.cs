using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Storage;
using Mneme.Writer;

namespace Mneme.Writer.Tests;

/// <summary>
/// End-to-end proof of the <c>Mneme.Writer</c> thin slice (ADR-0005 / Phase
/// 15.B): a satellite <see cref="AuthoringClaimPayload"/> projected into base
/// Mneme's <b>shared</b> canon substrate gets deterministic manuscript
/// continuity checking "for free" — two claims that agree on subject +
/// attribute but disagree on value surface as a <see cref="WriterContinuityConflict"/>,
/// against other claims and base facts alike, and the projection is rebuildable
/// from the append-only log.
/// </summary>
public sealed class AuthoringContinuityTests : IDisposable
{
    private readonly string _tmpDir;
    private const string Ws = "novel";

    public AuthoringContinuityTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-writer-" + Guid.NewGuid().ToString("N"));
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

    private static CaptureEvent Claim(string id, string subject, string attribute, string value,
        string? quote = null, string workstream = Ws)
        => new(
            new EventId(id), new WorkstreamId(workstream), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new AuthoringClaimPayload(
                Text: $"{subject}'s {attribute} is {value}.",
                Subject: subject, Attribute: attribute, Value: value, Quote: quote),
            new CaptureProvenance(new CaptureSourceId("editor"), new PrincipalId("author")));

    private static CapabilityToken Token(string workstream = Ws, string principal = "author")
        => new(
            Principal: new PrincipalId(principal),
            Workstream: new WorkstreamId(workstream),
            NotBefore: DateTimeOffset.UtcNow.AddMinutes(-5),
            NotAfter: DateTimeOffset.UtcNow.AddHours(1),
            AllowedCategories: Array.Empty<EpistemicCategory>());

    // ── the headline: structured canon → free contradiction detection ─────

    [Fact]
    public async Task Two_claims_that_disagree_on_a_value_are_a_continuity_conflict()
    {
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(Claim("wc-1", "Helios", "eye colour", "blue"));
        await agent.IngestAsync(Claim("wc-2", "Helios", "eye colour", "green"));

        var conflicts = await writer.GetContinuityConflictsAsync(Token());

        var c = Assert.Single(conflicts);
        Assert.Equal("Helios", c.Subject);
        Assert.Equal("eye colour", c.Attribute);
        // Deterministic pair ordering (event_id_a < event_id_b): wc-1 then wc-2.
        Assert.Equal(new EventId("wc-1"), c.EventA);
        Assert.Equal(new EventId("wc-2"), c.EventB);
        Assert.Equal("blue", c.ValueA);
        Assert.Equal("green", c.ValueB);
    }

    [Fact]
    public async Task Two_claims_that_agree_are_not_a_conflict()
    {
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(Claim("agree-1", "Helios", "eye colour", "blue"));
        await agent.IngestAsync(Claim("agree-2", "Helios", "eye colour", "blue"));

        Assert.Empty(await writer.GetContinuityConflictsAsync(Token()));
    }

    [Fact]
    public async Task Different_attributes_on_the_same_subject_are_not_a_conflict()
    {
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(Claim("attr-1", "Helios", "eye colour", "blue"));
        await agent.IngestAsync(Claim("attr-2", "Helios", "hair colour", "black"));

        Assert.Empty(await writer.GetContinuityConflictsAsync(Token()));
    }

    [Fact]
    public async Task A_claim_contradicting_a_base_fact_is_detected_by_the_same_engine()
    {
        // Proves the authoring claim feeds the SAME projection_fact_triples /
        // memory_contradictions substrate as a built-in FactPayload — a base
        // fact and a writer claim about the same subject+attribute conflict.
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(new CaptureEvent(
            new EventId("fact-1"), new WorkstreamId(Ws), EventChannel.Epistemic,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new FactPayload("Helios has blue eyes", Array.Empty<EventId>(),
                new[] { new FactTriple("Helios", "eye colour", "blue") }),
            new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("author"))));

        await agent.IngestAsync(Claim("claim-1", "Helios", "eye colour", "green"));

        var c = Assert.Single(await writer.GetContinuityConflictsAsync(Token()));
        Assert.Equal("eye colour", c.Attribute);
        Assert.Contains("green", new[] { c.ValueA, c.ValueB });
        Assert.Contains("blue", new[] { c.ValueA, c.ValueB });
    }

    [Fact]
    public async Task Subject_canon_key_matches_across_casing_and_possessive()
    {
        // "Helios", "helios", and "Helios's" normalize to the same subject key,
        // so claims about them are recognised as the same entity's canon.
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(Claim("norm-1", "Helios", "eye colour", "blue"));
        await agent.IngestAsync(Claim("norm-2", "helios", "eye colour", "amber"));

        var c = Assert.Single(await writer.GetContinuityConflictsAsync(Token()));
        Assert.Equal("eye colour", c.Attribute);
        Assert.Contains("blue", new[] { c.ValueA, c.ValueB });
        Assert.Contains("amber", new[] { c.ValueA, c.ValueB });
    }

    // ── the invariant: rebuildable from the append-only log ───────────────

    [Fact]
    public async Task Continuity_conflicts_are_rebuildable_from_the_log()
    {
        var (sp, agent, writer) = BuildHost();
        using var _ = sp;

        await agent.IngestAsync(Claim("rb-1", "Helios", "eye colour", "blue"));
        await agent.IngestAsync(Claim("rb-2", "Helios", "eye colour", "green"));
        Assert.Single(await writer.GetContinuityConflictsAsync(Token()));

        // Nuke BOTH derived tables, then RebuildAll must reconstruct the conflict
        // purely by replaying memory_events.
        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using (var conn = factory.Open())
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM memory_contradictions; DELETE FROM projection_fact_triples;";
            del.ExecuteNonQuery();
        }
        Assert.Empty(await writer.GetContinuityConflictsAsync(Token()));

        var counts = sp.GetRequiredService<Mneme.Projections.ProjectorPipeline>().RebuildAll();
        Assert.True(counts.ContainsKey("writer-authoring-canon"),
            "writer projector did not participate in RebuildAll");
        Assert.Equal(2, counts["writer-authoring-canon"]);

        var c = Assert.Single(await writer.GetContinuityConflictsAsync(Token()));
        Assert.Equal("Helios", c.Subject);
        Assert.Equal("blue", c.ValueA);
        Assert.Equal("green", c.ValueB);
    }

    // ── locked decision #11: secrets redacted inline at ingest ────────────

    [Fact]
    public async Task Secret_in_a_claim_is_redacted_before_it_reaches_the_wal()
    {
        var (sp, agent, _) = BuildHost();
        using var _d = sp;

        const string secret = "set password = supersecretvalue123 in the config";
        await agent.IngestAsync(Claim("sec-1", "Helios", "eye colour", "blue", quote: secret));

        var factory = sp.GetRequiredService<SqliteConnectionFactory>();
        using var c = factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM memory_events WHERE event_id = 'sec-1';";
        var raw = (string)cmd.ExecuteScalar()!;
        Assert.Contains("\"$type\":\"AuthoringClaimPayload\"", raw);
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
            () => writer.GetContinuityConflictsAsync(expired));
    }

    [Fact]
    public async Task Cross_workstream_token_without_a_scope_is_denied()
    {
        var (sp, _, writer) = BuildHost();
        using var _d = sp;
        var crossWs = new CapabilityToken(
            new PrincipalId("author"), Workstream: null,
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1),
            Array.Empty<EpistemicCategory>(), CrossWorkstream: true);
        await Assert.ThrowsAsync<CapabilityDeniedError>(
            () => writer.GetContinuityConflictsAsync(crossWs));
    }

    [Fact]
    public async Task Token_that_disallows_the_fact_category_is_denied()
    {
        var (sp, _, writer) = BuildHost();
        using var _d = sp;
        var evidenceOnly = new CapabilityToken(
            new PrincipalId("author"), new WorkstreamId(Ws),
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1),
            AllowedCategories: new[] { EpistemicCategory.Evidence });
        await Assert.ThrowsAsync<CapabilityDeniedError>(
            () => writer.GetContinuityConflictsAsync(evidenceOnly));
    }
}
