using System.Text.Json;
using Mneme.Contracts;

namespace Mneme.Contracts.Tests;

/// <summary>
/// Covers the polymorphic <see cref="Citation"/> set — every shape must
/// round-trip through <see cref="JsonSerializer"/> with its <c>$type</c>
/// discriminator, because citations are serialized inside every event's
/// provenance and must deserialize back to the exact concrete type.
/// </summary>
public class CitationTests
{
    [Fact]
    public void Derived_Roundtrips_WithTypeDiscriminator()
    {
        Citation citation = new Citation.Derived(
            new[] { Fixtures.NewEventId("aa"), Fixtures.NewEventId("bb") },
            "dreamer/consolidator@2026-08");

        var json = JsonSerializer.Serialize(citation, Fixtures.JsonOptions);
        Assert.Contains("$type", json);
        Assert.Contains(nameof(Citation.Derived), json);

        var back = Assert.IsType<Citation.Derived>(
            JsonSerializer.Deserialize<Citation>(json, Fixtures.JsonOptions));
        Assert.Equal(2, back.From.Count);
        Assert.Equal(Fixtures.NewEventId("aa"), back.From[0]);
        Assert.Equal(Fixtures.NewEventId("bb"), back.From[1]);
        Assert.Equal("dreamer/consolidator@2026-08", back.ConsolidatorId);
    }

    [Fact]
    public void AllCitationShapes_Roundtrip_ToConcreteType()
    {
        var citations = new Citation[]
        {
            new Citation.SessionRange(new SessionId("sess-1"), "0001", "0009"),
            new Citation.Manual("alice", "entered by hand"),
            new Citation.Workflow("github-actions", "run-42", "deploy"),
            new Citation.External("jira", "PROJ-123", new Uri("https://example.test/PROJ-123")),
            new Citation.Derived(new[] { Fixtures.NewEventId("cc") }, "dreamer@1"),
        };

        foreach (var c in citations)
        {
            var json = JsonSerializer.Serialize(c, Fixtures.JsonOptions);
            var back = JsonSerializer.Deserialize<Citation>(json, Fixtures.JsonOptions);
            Assert.NotNull(back);
            // The $type discriminator must dispatch back to the exact concrete
            // type. (Records with collection members don't compare structurally
            // across array→List deserialization, so element checks for the
            // collection-bearing Derived shape live in its dedicated test.)
            Assert.Equal(c.GetType(), back!.GetType());
        }
    }

    [Fact]
    public void CaptureProvenance_CarriesDerivedCitation_Roundtrips()
    {
        // A consolidated event carries a Derived citation on its provenance;
        // the whole envelope must round-trip so the audit chain survives.
        var provenance = new CaptureProvenance(
            new CaptureSourceId("dreamer"),
            new PrincipalId("consolidator"),
            Context: "dream-run-7",
            Citation: new Citation.Derived(
                new[] { Fixtures.NewEventId("01"), Fixtures.NewEventId("02") },
                "dreamer/consolidator@2026-08"));

        var json = JsonSerializer.Serialize(provenance, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<CaptureProvenance>(json, Fixtures.JsonOptions);

        var derived = Assert.IsType<Citation.Derived>(back!.Citation);
        Assert.Equal(2, derived.From.Count);
        Assert.Equal("dreamer/consolidator@2026-08", derived.ConsolidatorId);
    }

    [Fact]
    public void LegacyCitation_WithoutDerived_StillDeserializes()
    {
        // Events serialized before Derived existed must still load — the
        // append-only log is permanent.
        const string legacy = """{"$type":"SessionRange","session":{"value":"sess-9"},"fromEntryId":"0001","toEntryId":"0003"}""";
        var back = Assert.IsType<Citation.SessionRange>(
            JsonSerializer.Deserialize<Citation>(legacy, Fixtures.JsonOptions));
        Assert.Equal("0001", back.FromEntryId);
        Assert.Equal("0003", back.ToEntryId);
    }
}
