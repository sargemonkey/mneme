using Mneme.Classification;
using Mneme.Contracts;

namespace Mneme.Tests;

public sealed class RuleBasedClassifierTests
{
    private readonly RuleBasedClassifier _c = new();

    [Fact]
    public async Task Redaction_hits_force_secret_regardless_of_content()
    {
        var r = await _c.ClassifyAsync("clean text", hadRedactionHits: true, EpistemicCategory.Evidence);
        Assert.Equal(Mneme.Contracts.Classification.Secret, r);
    }

    [Theory]
    [InlineData("contact me at alice@example.com tomorrow")]
    [InlineData("ssn on file: 123-45-6789")]
    [InlineData("call +1 (415) 555-0123 to confirm")]
    public async Task Pii_shapes_classified_as_pii(string content)
    {
        var r = await _c.ClassifyAsync(content, false, EpistemicCategory.Evidence);
        Assert.Equal(Mneme.Contracts.Classification.Pii, r);
    }

    [Theory]
    [InlineData("Confidential customer data inside")]
    [InlineData("This is marked NDA do not forward")]
    [InlineData("internal use only please")]
    public async Task Confidential_hints_classified_as_confidential(string content)
    {
        var r = await _c.ClassifyAsync(content, false, EpistemicCategory.Fact);
        Assert.Equal(Mneme.Contracts.Classification.Confidential, r);
    }

    [Fact]
    public async Task Non_evidence_categories_default_to_internal()
    {
        var r = await _c.ClassifyAsync("we picked postgres because of scale", false, EpistemicCategory.Decision);
        Assert.Equal(Mneme.Contracts.Classification.Internal, r);
    }

    [Fact]
    public async Task Evidence_defaults_to_public()
    {
        var r = await _c.ClassifyAsync("the cat sat on the mat", false, EpistemicCategory.Evidence);
        Assert.Equal(Mneme.Contracts.Classification.Public, r);
    }

    [Fact]
    public async Task Empty_content_is_public()
    {
        var r = await _c.ClassifyAsync("", false, EpistemicCategory.Evidence);
        Assert.Equal(Mneme.Contracts.Classification.Public, r);
    }
}
