using Mneme.Ingest.Redaction;

namespace Mneme.Tests;

public sealed class RegexRedactorTests
{
    private readonly RegexRedactor _r = new();

    [Theory]
    [InlineData("openai-key",       "Token: sk-abcdefghijklmnopqrstuvwxyz1234567890",       "<REDACTED:openai-key>")]
    [InlineData("openai-key",       "key=sk-proj-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789",      "<REDACTED:openai-key>")]
    [InlineData("aws-access-key",   "id=AKIAIOSFODNN7EXAMPLE end",                          "<REDACTED:aws-access-key>")]
    [InlineData("github-pat",       "x ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789 y",         "<REDACTED:github-pat>")]
    [InlineData("google-api-key",   "k=AIzaSyA-1234567890abcdefghijklmnopqrstu end",         "<REDACTED:google-api-key>")]
    [InlineData("slack-token",      "tok xoxb-1234567890-abcdefghij end",                    "<REDACTED:slack-token>")]
    [InlineData("bearer-token",     "Authorization: Bearer abcdefghijklmnopqrstuvwxyz12345",  "<REDACTED:bearer-token>")]
    [InlineData("jwt",              "tok=eyJabcdefghij.eyJabcdefghij.signaturepart12 end",   "<REDACTED:jwt>")]
    public void Redacts_known_secret_shapes(string ruleName, string input, string expectedMarker)
    {
        var result = _r.Redact(input);
        Assert.True(result.HadHits, $"expected a hit for rule '{ruleName}' in '{input}'");
        Assert.Contains(expectedMarker, result.RedactedContent);
        Assert.Contains(result.Hits, h => h.RuleName == ruleName);
    }

    [Fact]
    public void Leaves_clean_text_untouched()
    {
        const string input = "the quick brown fox jumps over the lazy dog";
        var result = _r.Redact(input);
        Assert.False(result.HadHits);
        Assert.Equal(input, result.RedactedContent);
    }

    [Fact]
    public void Pem_private_key_block_redacted()
    {
        const string input = "header\n-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAA==\n-----END RSA PRIVATE KEY-----\nfooter";
        var result = _r.Redact(input);
        Assert.True(result.HadHits);
        Assert.Contains("<REDACTED:pem-private-key>", result.RedactedContent);
        Assert.DoesNotContain("MIIBOgIBAA", result.RedactedContent);
    }

    [Fact]
    public void Multiple_hits_in_one_input_all_redacted()
    {
        const string input = "ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789 and AKIAIOSFODNN7EXAMPLE";
        var result = _r.Redact(input);
        Assert.True(result.HadHits);
        Assert.True(result.Hits.Count >= 2);
        Assert.Contains("<REDACTED:github-pat>", result.RedactedContent);
        Assert.Contains("<REDACTED:aws-access-key>", result.RedactedContent);
    }

    [Fact]
    public void Empty_input_yields_empty_result()
    {
        var result = _r.Redact("");
        Assert.False(result.HadHits);
        Assert.Equal("", result.RedactedContent);
    }
}
