using Mneme.Contracts;

namespace Mneme.Hosting;

/// <summary>
/// Options for the <see cref="MnemeServiceCollectionExtensions.AddMneme"/>
/// ergonomic DI helper. Covers the 90% case — single-workstream
/// embedded host — and constructs a sensible <see cref="CapabilityToken"/>
/// internally. Cross-workstream and confidential-read scenarios are
/// still expressible by registering the full
/// <see cref="CapabilityToken"/> manually.
/// </summary>
/// <remarks>
/// Source guidance: <c>plans/research-design-lessons.md §3.5 + §4.9</c>.
/// </remarks>
public sealed class MnemeOptions
{
    /// <summary>The single workstream this host operates against.</summary>
    public string? WorkstreamId { get; set; }

    /// <summary>Absolute path to the SQLite database file.</summary>
    public string? SqlitePath { get; set; }

    /// <summary>
    /// Optional SQLCipher passphrase for transparent at-rest encryption. When set, the
    /// entire database file is encrypted on disk (plaintext only in memory); when null the
    /// database is plain SQLite (default). Key management is the host's responsibility —
    /// supply a strong random key from an OS-backed store (e.g. DPAPI / Keychain). Changing
    /// or losing the key makes an existing encrypted database unreadable.
    /// </summary>
    public string? EncryptionKey { get; set; }

    /// <summary>Principal id used as the default for the auto-built capability token.</summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Categories the auto-built capability token may query. Defaults to
    /// every <see cref="EpistemicCategory"/>.
    /// </summary>
    public IReadOnlyCollection<EpistemicCategory>? PermittedCategories { get; set; }

    /// <summary>
    /// Whether default queries include events on the
    /// <see cref="EventChannel.Technical"/> channel. Default <c>false</c>
    /// (epistemic only).
    /// </summary>
    public bool IncludeTechnical { get; set; }

    /// <summary>
    /// Whether hybrid retrieval boosts events whose fact-triple subject matches
    /// an entity named in the query (subject-scoped attribution). Default
    /// <c>false</c>: a controlled LoCoMo A/B (see benchmarks ANALYSIS.md
    /// "Experiment 6") showed the retrieval-side boost <em>regresses</em>
    /// (it reorders/displaces facts like the losing "replacement" prototype).
    /// The validated win came from <em>appending</em> triples as supplementary
    /// answer context, which is a host/distillation concern, not retrieval
    /// reweighting. The lever and the projection_fact_triples index are kept for
    /// that future approach; enable only if you have measured a gain on your data.
    /// </summary>
    public bool SubjectAttributionBoost { get; set; }

    /// <summary>
    /// Validity window for the auto-built capability token. Default:
    /// now → now + 30 days.
    /// </summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(30);
}
