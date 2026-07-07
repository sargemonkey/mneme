using System.Text.Json;

namespace Mneme.Contracts.Tests;

public sealed class EventPayloadTests
{
    [Fact]
    public void EvidencePayload_CategoryIsEvidence()
    {
        var p = new EvidencePayload("hello", "chat://x");
        Assert.Equal(EpistemicCategory.Evidence, p.Category);
        Assert.Equal(Classification.Public, p.Classification);
    }

    [Fact]
    public void FactPayload_CategoryIsFact()
    {
        var p = new FactPayload("the sky is blue", new List<EventId>());
        Assert.Equal(EpistemicCategory.Fact, p.Category);
    }

    [Fact]
    public void FactPayload_TriplesDefaultNull_AndAreSettable()
    {
        var bare = new FactPayload("Melanie lives in Sweden", Array.Empty<EventId>());
        Assert.Null(bare.Triples);

        var triple = new FactTriple("Melanie", "lives_in", "Sweden");
        var withTriples = bare with { Triples = new[] { triple } };
        Assert.NotNull(withTriples.Triples);
        Assert.Single(withTriples.Triples!);
        Assert.Equal("Melanie", withTriples.Triples![0].Subject);
        Assert.Equal("lives_in", withTriples.Triples[0].Predicate);
        Assert.Equal("Sweden", withTriples.Triples[0].Object);
    }

    [Fact]
    public void FactPayload_WithTriples_RoundTripsThroughJson()
    {
        var p = new FactPayload("Melanie's grandma is from Sweden", Array.Empty<EventId>(),
            new[] { new FactTriple("Melanie's grandma", "nationality", "Swedish") });
        var json = JsonSerializer.Serialize<EventPayload>(p, Fixtures.JsonOptions);
        var back = Assert.IsType<FactPayload>(JsonSerializer.Deserialize<EventPayload>(json, Fixtures.JsonOptions));
        Assert.NotNull(back.Triples);
        Assert.Equal("Melanie's grandma", back.Triples![0].Subject);
        Assert.Equal("nationality", back.Triples[0].Predicate);
        Assert.Equal("Swedish", back.Triples[0].Object);
    }

    [Fact]
    public void FactPayload_LegacyJsonWithoutTriples_DeserializesToNull()
    {
        // A payload serialized before Triples existed must still load (append-only
        // log is permanent; old FactPayload rows have no triples field).
        const string legacy = """{"$type":"FactPayload","statement":"old fact","supportingEvents":[]}""";
        var back = Assert.IsType<FactPayload>(JsonSerializer.Deserialize<EventPayload>(legacy, Fixtures.JsonOptions));
        Assert.Equal("old fact", back.Statement);
        Assert.Null(back.Triples);
    }

    [Fact]
    public void DecisionPayload_CategoryIsDecision()
    {
        var p = new DecisionPayload("ship it", "we tested", Array.Empty<EventId>(), new PrincipalId("u"));
        Assert.Equal(EpistemicCategory.Decision, p.Category);
    }

    [Fact]
    public void HypothesisPayload_CategoryIsHypothesis()
    {
        var p = new HypothesisPayload("could be auth", HypothesisState.Open);
        Assert.Equal(EpistemicCategory.Hypothesis, p.Category);
    }

    [Fact]
    public void GoalPayload_CategoryIsGoal()
    {
        var p = new GoalPayload("ship by Q3", GoalState.Active);
        Assert.Equal(EpistemicCategory.Goal, p.Category);
    }

    [Fact]
    public void ActionPayload_CategoryIsAction()
    {
        var p = new ActionPayload("opened PR #42", new EventId("dec-1"), "https://github.com/x/y/pull/42");
        Assert.Equal(EpistemicCategory.Action, p.Category);
    }

    [Fact]
    public void OutcomePayload_CategoryIsOutcome()
    {
        var p = new OutcomePayload("PR merged", new EventId("act-1"), OutcomePolarity.Positive);
        Assert.Equal(EpistemicCategory.Outcome, p.Category);
    }

    [Fact]
    public void EventPayload_PolymorphicSerialization_Roundtrips()
    {
        // The $type discriminator must be present and round-trip the
        // concrete payload type so consumers can deserialize a heterogeneous
        // event stream without losing type information.
        EventPayload payload = new EvidencePayload("hello", "chat://x", Classification.Confidential);
        var json = JsonSerializer.Serialize(payload, Fixtures.JsonOptions);
        Assert.Contains("$type", json);
        Assert.Contains(nameof(EvidencePayload), json);

        var roundtripped = JsonSerializer.Deserialize<EventPayload>(json, Fixtures.JsonOptions);
        Assert.NotNull(roundtripped);
        var evidence = Assert.IsType<EvidencePayload>(roundtripped);
        Assert.Equal("hello", evidence.Content);
        Assert.Equal("chat://x", evidence.Source);
        Assert.Equal(Classification.Confidential, evidence.Classification);
    }

    [Fact]
    public void EventPayload_AllSevenSubtypes_AreRegisteredForPolymorphism()
    {
        // Every category must have a registered derived type. Adding a new
        // EpistemicCategory without a payload subtype + JsonDerivedType
        // attribute would break ingest deserialization at runtime; this
        // test catches it at build/test time.
        EventPayload[] all =
        {
            new EvidencePayload("e", null),
            new FactPayload("f", Array.Empty<EventId>()),
            new DecisionPayload("d", "r", Array.Empty<EventId>(), new PrincipalId("u")),
            new HypothesisPayload("h", HypothesisState.Open),
            new GoalPayload("g", GoalState.Active),
            new ActionPayload("a", null, null),
            new OutcomePayload("o", new EventId("x"), OutcomePolarity.Neutral),
        };

        foreach (var p in all)
        {
            var json = JsonSerializer.Serialize(p, Fixtures.JsonOptions);
            var back = JsonSerializer.Deserialize<EventPayload>(json, Fixtures.JsonOptions);
            Assert.NotNull(back);
            Assert.Equal(p.Category, back!.Category);
            Assert.Equal(p.GetType(), back.GetType());
        }
    }
}

public sealed class CaptureEventTests
{
    [Fact]
    public void CaptureEvent_RoundtripsViaJson()
    {
        var evt = Fixtures.NewEvidenceEvent();
        var json = JsonSerializer.Serialize(evt, Fixtures.JsonOptions);
        var back = JsonSerializer.Deserialize<CaptureEvent>(json, Fixtures.JsonOptions);
        Assert.NotNull(back);
        Assert.Equal(evt.EventId, back!.EventId);
        Assert.Equal(evt.WorkstreamId, back.WorkstreamId);
        Assert.Equal(evt.Channel, back.Channel);
        Assert.Equal(evt.ValidAt, back.ValidAt);
        Assert.Equal(evt.RecordedAt, back.RecordedAt);
        Assert.Equal(evt.Provenance, back.Provenance);
        Assert.Equal(evt.SchemaVersion, back.SchemaVersion);
        Assert.IsType<EvidencePayload>(back.Payload);
    }

    [Fact]
    public void CaptureEvent_DefaultSchemaVersionIsOne()
    {
        var evt = Fixtures.NewEvidenceEvent();
        Assert.Equal(1, evt.SchemaVersion);
    }

    [Fact]
    public void IngestResult_DefaultsToNotDuplicate()
    {
        var r = new IngestResult(new EventId("e1"), DateTimeOffset.UtcNow);
        Assert.False(r.WasDuplicate);
    }

    [Fact]
    public void CaptureSourceId_RoundTripsValue()
    {
        var s = new CaptureSourceId("plugin-x");
        Assert.Equal("plugin-x", s.Value);
        Assert.Equal("plugin-x", s.ToString());
    }
}
