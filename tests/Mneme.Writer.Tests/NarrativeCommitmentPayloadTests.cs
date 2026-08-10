using Mneme.Contracts;
using Mneme.Hosting.Profiles;
using Mneme.Ingest.Redaction;
using Mneme.Storage;
using Mneme.Writer;

namespace Mneme.Writer.Tests;

/// <summary>
/// Host-free unit proofs of the <see cref="NarrativeCommitmentPayload"/> contract
/// and its <see cref="NarrativeCommitmentDescriptor"/>: shared-serializer
/// round-trip via the registry, and the descriptor's redaction / text / summary
/// seams (covering the free-text ScenePath / Quote fields the ingest-path test
/// doesn't exercise directly).
/// </summary>
public sealed class NarrativeCommitmentPayloadTests
{
    public NarrativeCommitmentPayloadTests()
        => PayloadDescriptorRegistry.Register(new NarrativeCommitmentDescriptor());

    [Fact]
    public void Defaults_are_promise_kind_and_goal_category()
    {
        var p = new NarrativeCommitmentPayload("commitment:x", CommitmentRole.Setup, "a promise");
        Assert.Equal("promise", p.Kind);
        Assert.Equal(EpistemicCategory.Goal, p.Category);
    }

    [Fact]
    public void Roundtrips_through_the_shared_serializer_with_its_discriminator()
    {
        var original = new NarrativeCommitmentPayload(
            "commitment:chekhovs-gun", CommitmentRole.Payoff,
            "the gun is fired", "foreshadow", ScenePath: "ch09.md", Quote: "she pulled the trigger");

        var json = EventSerialization.SerializePayload(original);
        Assert.Contains("\"$type\":\"NarrativeCommitmentPayload\"", json);

        var back = Assert.IsType<NarrativeCommitmentPayload>(EventSerialization.DeserializePayload(json));
        Assert.Equal(original, back);
    }

    [Fact]
    public void Descriptor_summary_reads_role_kind_and_description()
    {
        var descriptor = new NarrativeCommitmentDescriptor();
        var p = new NarrativeCommitmentPayload("commitment:x", CommitmentRole.Setup,
            "a loaded gun over the mantel", "foreshadow");
        Assert.Equal("[Setup] foreshadow: a loaded gun over the mantel", descriptor.Summarize(p));
    }

    [Fact]
    public void Descriptor_redacts_description_scene_and_quote()
    {
        var descriptor = new NarrativeCommitmentDescriptor();
        var p = new NarrativeCommitmentPayload(
            CommitmentId: "commitment:x", Role: CommitmentRole.Setup,
            Description: "token supersecret1 here", Kind: "foreshadow",
            ScenePath: "supersecret2", Quote: "supersecret3");

        var (payload, had, hits) = descriptor.Redact(p, new MaskWordRedactor("supersecret"));
        var r = Assert.IsType<NarrativeCommitmentPayload>(payload);

        Assert.True(had);
        Assert.Equal(3, hits);
        Assert.DoesNotContain("supersecret", r.Description);
        Assert.DoesNotContain("supersecret", r.ScenePath);
        Assert.DoesNotContain("supersecret", r.Quote);
        // CommitmentId (slug) and Kind (controlled vocab) are left intact.
        Assert.Equal("commitment:x", r.CommitmentId);
        Assert.Equal("foreshadow", r.Kind);
    }

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
