using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Tests;

public sealed class EventSerializationTests
{
    [Fact]
    public void Roundtrip_each_payload_kind()
    {
        EventPayload[] payloads =
        {
            new EvidencePayload("body", "src", Mneme.Contracts.Classification.Internal),
            new FactPayload("the sky is blue", new List<EventId>{ new("01H0EVID00000000000000000A") }),
            new DecisionPayload("use postgres", "scaling", Array.Empty<EventId>(), new PrincipalId("alice")),
            new HypothesisPayload("cache helps", HypothesisState.Open),
            new GoalPayload("ship v1", GoalState.Active),
            new ActionPayload("opened PR", new EventId("01H0EVID00000000000000000B"), "https://example/pr/1"),
            new OutcomePayload("merged", new EventId("01H0EVID00000000000000000C"), OutcomePolarity.Positive),
        };

        foreach (var p in payloads)
        {
            var json = EventSerialization.SerializePayload(p);
            var back = EventSerialization.DeserializePayload(json);
            Assert.Equal(p.GetType(), back.GetType());
            Assert.Equal(p.Category, back.Category);
            // Re-serialize to compare structurally (record equality on
            // payloads holding IReadOnlyList<T> uses reference equality
            // on the list, so direct Assert.Equal(p, back) is unreliable).
            Assert.Equal(json, EventSerialization.SerializePayload(back));
        }
    }

    [Fact]
    public void Roundtrip_provenance()
    {
        var prov = new CaptureProvenance(
            new CaptureSourceId("plugin-x"),
            new PrincipalId("agent-1"),
            Context: "session=abc");
        var json = EventSerialization.SerializeProvenance(prov);
        var back = EventSerialization.DeserializeProvenance(json);
        Assert.Equal(prov, back);
    }
}
