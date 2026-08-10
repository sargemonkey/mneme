using Mneme.Contracts;
using Mneme.Hosting.Profiles;
using Mneme.Ingest.Redaction;
using Mneme.Storage;
using Mneme.Writer;

namespace Mneme.Writer.Tests;

/// <summary>
/// Host-free unit proofs of the <see cref="AuthoringClaimPayload"/> contract and
/// its <see cref="AuthoringClaimDescriptor"/>: the triple mapping, the shared-
/// serializer round-trip via the registry (closed-set safety preserved), and
/// the descriptor's redaction / text / summary seams.
/// </summary>
public sealed class AuthoringClaimPayloadTests
{
    public AuthoringClaimPayloadTests()
        => PayloadDescriptorRegistry.Register(new AuthoringClaimDescriptor());

    [Fact]
    public void ToTriple_maps_subject_attribute_value_to_subject_predicate_object()
    {
        var p = new AuthoringClaimPayload("Helios has blue eyes.", "Helios", "eye colour", "blue");
        var t = p.ToTriple();
        Assert.Equal("Helios", t.Subject);
        Assert.Equal("eye colour", t.Predicate);
        Assert.Equal("blue", t.Object);
    }

    [Fact]
    public void Defaults_are_asserted_status_and_internal_canon_grounding()
    {
        var p = new AuthoringClaimPayload("t", "s", "a", "v");
        Assert.Equal("asserted", p.Status);
        Assert.Equal(AuthoringGrounding.InternalCanon, p.Grounding);
        Assert.Equal(EpistemicCategory.Fact, p.Category);
    }

    [Fact]
    public void Roundtrips_through_the_shared_serializer_with_its_discriminator()
    {
        var original = new AuthoringClaimPayload(
            "Helios has blue eyes.", "Helios", "eye colour", "blue",
            ScenePath: "ch02.md", Quote: "his blue eyes", Status: "verified",
            SourceRef: null, ThreadId: "thread:helios-arc",
            Grounding: AuthoringGrounding.InternalCanon);

        var json = EventSerialization.SerializePayload(original);
        Assert.Contains("\"$type\":\"AuthoringClaimPayload\"", json);

        var back = Assert.IsType<AuthoringClaimPayload>(EventSerialization.DeserializePayload(json));
        Assert.Equal(original, back);
    }

    [Fact]
    public void Descriptor_summary_and_text_expose_the_structured_assertion()
    {
        var descriptor = new AuthoringClaimDescriptor();
        var p = new AuthoringClaimPayload("Helios has blue eyes.", "Helios", "eye colour", "blue",
            Quote: "his blue eyes");

        Assert.Equal("Helios — eye colour: blue", descriptor.Summarize(p));
        var text = descriptor.ExtractText(p);
        Assert.Contains("Helios", text);
        Assert.Contains("blue", text);
        Assert.Contains("his blue eyes", text);
    }

    [Fact]
    public void Descriptor_redacts_every_free_text_field()
    {
        var descriptor = new AuthoringClaimDescriptor();
        var p = new AuthoringClaimPayload(
            Text: "token supersecret1 leaked",
            Subject: "supersecret2",
            Attribute: "supersecret3",
            Value: "supersecret4",
            ScenePath: "supersecret5",
            Quote: "supersecret6",
            SourceRef: "supersecret7");

        var (payload, had, hits) = descriptor.Redact(p, new MaskWordRedactor("supersecret"));
        var r = Assert.IsType<AuthoringClaimPayload>(payload);

        Assert.True(had);
        Assert.Equal(7, hits);
        foreach (var field in new[] { r.Text, r.Subject, r.Attribute, r.Value, r.ScenePath, r.Quote, r.SourceRef })
        {
            Assert.DoesNotContain("supersecret", field);
        }
    }

    // A trivial redactor that masks any whitespace-delimited word starting with
    // the given marker — enough to prove the descriptor visits every field.
    private sealed class MaskWordRedactor : IRedactor
    {
        private readonly string _marker;
        public MaskWordRedactor(string marker) => _marker = marker;

        public RedactionResult Redact(string content)
        {
            if (string.IsNullOrEmpty(content)) return new RedactionResult(content, Array.Empty<RedactionHit>());
            var hits = new List<RedactionHit>();
            var words = content.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                if (words[i].StartsWith(_marker, StringComparison.Ordinal))
                {
                    hits.Add(new RedactionHit("secret", "[REDACTED]", i, words[i].Length));
                    words[i] = "[REDACTED]";
                }
            }
            return new RedactionResult(string.Join(' ', words), hits);
        }
    }
}
