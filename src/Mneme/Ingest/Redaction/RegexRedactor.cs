using System.Text.RegularExpressions;

namespace Mneme.Ingest.Redaction;

/// <summary>
/// The default regex-based redactor. Ships with a set of rules covering
/// the common high-impact secrets — OpenAI/Anthropic API keys, AWS access
/// keys + secret keys, Azure connection strings, GitHub PATs, Google API
/// keys, generic bearer / api-key / password assignments, JWT-shaped
/// tokens, and PEM private key blocks. The rule set is ported from
/// Cognee's <c>tracing.py:redact_secrets()</c>; see
/// <c>research-design-lessons.md §3.2</c>. Additional rules can be
/// supplied at construction.
/// </summary>
/// <remarks>
/// <para>
/// Per-rule timeouts (200 ms) protect against catastrophic backtracking
/// on adversarial input. The redactor is thread-safe — rules are
/// compiled once and shared across calls.
/// </para>
/// <para>
/// Matches are processed left-to-right; when ranges overlap, the
/// earlier-starting (and on ties, longer) match wins.
/// </para>
/// </remarks>
public sealed class RegexRedactor : IRedactor
{
    private static readonly TimeSpan PerMatchTimeout = TimeSpan.FromMilliseconds(200);

    private readonly IReadOnlyList<RedactionRule> _rules;

    /// <summary>Construct a redactor with the default rule set.</summary>
    public RegexRedactor() : this(DefaultRules) { }

    /// <summary>Construct a redactor with a custom rule set (replaces, does not extend, the defaults).</summary>
    public RegexRedactor(IEnumerable<RedactionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToArray();
    }

    /// <inheritdoc/>
    public RedactionResult Redact(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new RedactionResult(content ?? string.Empty, Array.Empty<RedactionHit>());
        }

        // Collect all candidate matches across all rules, then resolve
        // overlaps deterministically: earliest start wins, longer match
        // wins on ties. This is O(n_rules * input_length) which is fine
        // at our typical content size (sub-megabyte).
        var candidates = new List<RedactionHit>();
        foreach (var rule in _rules)
        {
            foreach (Match m in rule.Pattern.Matches(content))
            {
                if (!m.Success || m.Length == 0)
                {
                    continue;
                }
                candidates.Add(new RedactionHit(rule.Name, rule.Marker, m.Index, m.Length));
            }
        }

        if (candidates.Count == 0)
        {
            return new RedactionResult(content, Array.Empty<RedactionHit>());
        }

        candidates.Sort(static (a, b) =>
        {
            var byStart = a.StartIndex.CompareTo(b.StartIndex);
            return byStart != 0 ? byStart : b.Length.CompareTo(a.Length);
        });

        var chosen = new List<RedactionHit>(candidates.Count);
        var lastEnd = -1;
        foreach (var hit in candidates)
        {
            if (hit.StartIndex < lastEnd)
            {
                continue; // overlaps a chosen hit; skip
            }
            chosen.Add(hit);
            lastEnd = hit.StartIndex + hit.Length;
        }

        // Build the redacted output by stitching original content + markers.
        var sb = new System.Text.StringBuilder(content.Length);
        var cursor = 0;
        foreach (var hit in chosen)
        {
            if (hit.StartIndex > cursor)
            {
                sb.Append(content, cursor, hit.StartIndex - cursor);
            }
            sb.Append(hit.Marker);
            cursor = hit.StartIndex + hit.Length;
        }
        if (cursor < content.Length)
        {
            sb.Append(content, cursor, content.Length - cursor);
        }
        return new RedactionResult(sb.ToString(), chosen);
    }

    /// <summary>
    /// The default rule set, ported from Cognee's
    /// <c>tracing.py:redact_secrets()</c>. Rules are deliberately
    /// conservative — they err on the side of *over*-redacting suspicious
    /// strings rather than letting a real key through.
    /// </summary>
    public static IReadOnlyList<RedactionRule> DefaultRules { get; } = BuildDefaultRules();

    private static IReadOnlyList<RedactionRule> BuildDefaultRules()
    {
        const RegexOptions Opts =
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

        return new RedactionRule[]
        {
            // OpenAI keys (sk-..., sk-proj-..., sk-ant-... for Anthropic too)
            new("openai-key",
                new Regex(@"\bsk-(?:proj-|ant-)?[A-Za-z0-9_\-]{20,}\b", Opts, PerMatchTimeout)),

            // AWS access key IDs
            new("aws-access-key",
                new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b",
                    Opts & ~RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    PerMatchTimeout)),

            // AWS secret access key assignment (looser, requires the kv pair)
            new("aws-secret-key",
                new Regex(@"(?i)aws[_\-]?secret[_\-]?(?:access[_\-]?)?key[""'\s:=]+[""']?[A-Za-z0-9/+=]{40}[""']?",
                    Opts, PerMatchTimeout)),

            // GitHub personal access tokens
            new("github-pat",
                new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", Opts, PerMatchTimeout)),

            // Google API key
            new("google-api-key",
                new Regex(@"\bAIza[0-9A-Za-z\-_]{35}\b",
                    Opts & ~RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    PerMatchTimeout)),

            // Slack bot/user/app tokens
            new("slack-token",
                new Regex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}\b", Opts, PerMatchTimeout)),

            // Azure / SQL-style connection strings carrying a password
            new("connection-string-password",
                new Regex(@"(?i)(?:password|pwd)\s*=\s*[^;""\r\n]+", Opts, PerMatchTimeout)),

            // Bearer token in an Authorization header
            new("bearer-token",
                new Regex(@"(?i)bearer\s+[A-Za-z0-9\-._~+/]{10,}=*", Opts, PerMatchTimeout)),

            // Generic api_key / api-key / apikey assignment
            new("api-key-assignment",
                new Regex(@"(?i)\bapi[_\-]?key[""'\s:=]+[""']?[A-Za-z0-9_\-]{16,}[""']?",
                    Opts, PerMatchTimeout)),

            // Generic password assignment (in JSON / kv / env-style)
            new("password-assignment",
                new Regex(@"(?i)\b(?:password|passwd|secret)[""'\s:=]+[""']?[^\s""',;]{6,}[""']?",
                    Opts, PerMatchTimeout)),

            // JWT-shaped tokens (3 base64-url segments)
            new("jwt",
                new Regex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b",
                    Opts & ~RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    PerMatchTimeout)),

            // PEM private key blocks (multiline, dotall)
            new("pem-private-key",
                new Regex(@"-----BEGIN (?:RSA |DSA |EC |OPENSSH |PGP )?PRIVATE KEY-----[\s\S]+?-----END (?:RSA |DSA |EC |OPENSSH |PGP )?PRIVATE KEY-----",
                    RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    PerMatchTimeout)),
        };
    }
}

/// <summary>A single redaction rule — a named regex and the marker that replaces matches.</summary>
/// <param name="Name">Stable rule name (kebab-case). Used in <see cref="RedactionHit.RuleName"/>.</param>
/// <param name="Pattern">Compiled regex with a per-match timeout configured.</param>
public sealed record RedactionRule(string Name, Regex Pattern)
{
    /// <summary>The replacement marker. Derived from <see cref="Name"/>.</summary>
    public string Marker { get; } = $"<REDACTED:{Name}>";
}
