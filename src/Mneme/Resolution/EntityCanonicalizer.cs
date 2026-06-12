using System.Security.Cryptography;
using System.Text;
using Mneme.Contracts;

namespace Mneme.Resolution;

/// <summary>
/// Tier 1 of the conservative three-tier entity resolution: deterministic
/// canonicalization + UUID5 hashing into a fixed namespace. Two entities
/// with the same canonical key in the same workstream get the same
/// <see cref="EntityId"/> across the lifetime of the workstream, so
/// re-asserting them is an idempotent no-op.
/// </summary>
/// <remarks>
/// <para>
/// Canonicalization rules (deliberately restrictive — Mneme is
/// <em>stricter than Graphiti</em> on auto-merge; see
/// <c>research-design-lessons.md §3.4</c>):
/// <list type="bullet">
///   <item><see cref="EntityKind.Email"/>: lowercased; for gmail.com /
///         googlemail.com, dots are stripped from the local part and the
///         host is normalised to gmail.com (since gmail treats these as
///         the same mailbox).</item>
///   <item><see cref="EntityKind.GitHubLogin"/>: lowercased (GitHub
///         logins are case-insensitive at the API level).</item>
///   <item><see cref="EntityKind.LinearId"/> /
///         <see cref="EntityKind.SlackId"/> /
///         <see cref="EntityKind.StripeId"/>: as-is (these are opaque
///         IDs already).</item>
///   <item><see cref="EntityKind.Url"/>: scheme + host lowercased,
///         default ports (80/443) stripped, fragment removed.</item>
///   <item><see cref="EntityKind.Name"/> and <see cref="EntityKind.Other"/>:
///         <strong>no Tier 1 auto-merge</strong> — they always fall through
///         to Tier 2/3, because two people named "John Smith" can mean
///         two different people.</item>
/// </list>
/// </para>
/// <para>
/// The UUID5 namespace is per-workstream — so the same canonical key in
/// two different workstreams yields two different entity ids, preserving
/// workstream isolation by design.
/// </para>
/// </remarks>
public static class EntityCanonicalizer
{
    /// <summary>
    /// Compute the canonical form of an identifier. Returns
    /// <see cref="string.Empty"/> when the kind is not eligible for
    /// Tier 1 auto-merge.
    /// </summary>
    public static string Canonicalize(EntityKind kind, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return kind switch
        {
            EntityKind.Email       => CanonicalizeEmail(raw),
            EntityKind.GitHubLogin => raw.Trim().ToLowerInvariant(),
            EntityKind.LinearId    => raw.Trim(),
            EntityKind.SlackId     => raw.Trim(),
            EntityKind.StripeId    => raw.Trim(),
            EntityKind.Url         => CanonicalizeUrl(raw),
            EntityKind.Name        => string.Empty, // explicit: names never auto-merge
            EntityKind.Other       => string.Empty,
            _                      => string.Empty,
        };
    }

    /// <summary>True when the kind + raw value canonicalize to a non-empty key (Tier 1 eligible).</summary>
    public static bool IsAutoMergeEligible(EntityKind kind, string raw) =>
        !string.IsNullOrEmpty(Canonicalize(kind, raw));

    /// <summary>
    /// Compute the per-workstream UUID5 entity id for a canonical key.
    /// Same inputs ⇒ same id, across processes, across machines, forever.
    /// </summary>
    public static EntityId ComputeEntityId(WorkstreamId workstream, EntityKind kind, string canonicalKey)
    {
        if (string.IsNullOrEmpty(canonicalKey))
        {
            // Caller asked for an id when there is no canonical key —
            // return a fresh opaque id so the entity still gets a stable
            // identifier; future re-assertions will *not* match this id.
            return new EntityId("ent-" + Guid.NewGuid().ToString("N"));
        }
        var ns = $"mneme:entity:{workstream.Value}:{(int)kind}";
        var bytes = Encoding.UTF8.GetBytes(ns + ":" + canonicalKey);
        // SHA1 is fine here: this is a name -> id derivation, not a
        // security primitive. UUID5 itself is defined to use SHA1.
        var hash = SHA1.HashData(bytes);
        // Set version (5) and variant per RFC 4122.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var guid = new Guid(new ReadOnlySpan<byte>(hash, 0, 16));
        return new EntityId("ent-" + guid.ToString("N"));
    }

    private static string CanonicalizeEmail(string raw)
    {
        var trimmed = raw.Trim().ToLowerInvariant();
        var at = trimmed.IndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1) return string.Empty;
        var local = trimmed[..at];
        var host = trimmed[(at + 1)..];
        if (host is "gmail.com" or "googlemail.com")
        {
            local = local.Replace(".", string.Empty);
            host = "gmail.com";
        }
        return local + "@" + host;
    }

    private static string CanonicalizeUrl(string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return string.Empty;
        var port = (uri.Scheme == "http" && uri.Port == 80) || (uri.Scheme == "https" && uri.Port == 443)
            ? string.Empty
            : ":" + uri.Port;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (string.IsNullOrEmpty(path)) path = "/";
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}{path}{uri.Query}";
    }
}
