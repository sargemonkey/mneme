namespace Mneme.Ingest.Validation;

/// <summary>
/// Validates workstream identifiers at every public API boundary. The
/// rule is deliberately tight: lowercase ASCII letters, digits, hyphens,
/// underscores, and dots only; 1–128 characters; must start with an
/// alphanumeric character; cannot contain consecutive separators; cannot
/// end with a separator.
/// </summary>
/// <remarks>
/// <para>
/// This is a path-traversal guard as well as a hygiene check —
/// workstream ids flow into directory and file names in later phases
/// (snapshot sync, sidecar deployment), so the rule rejects
/// <c>../</c>, NUL bytes, whitespace, glob characters, and anything
/// otherwise capable of escaping a workstream-scoped tree. Pattern
/// borrowed from Basic Memory's <c>validate_project_path()</c>; see
/// <c>research-design-lessons.md §3.5</c>.
/// </para>
/// <para>
/// The same regex is enforced again at the MCP boundary in Phase 8 so
/// hostile MCP clients cannot bypass it by talking directly to a
/// transport that skips the .NET API.
/// </para>
/// </remarks>
public static class WorkstreamIdValidator
{
    private static readonly System.Text.RegularExpressions.Regex Pattern =
        new(@"^[a-z0-9](?:[a-z0-9]|[-_.](?=[a-z0-9])){0,127}$",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True if <paramref name="value"/> matches the workstream-id rule.</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && Pattern.IsMatch(value);

    /// <summary>
    /// Throw <see cref="ArgumentException"/> if <paramref name="value"/>
    /// fails validation, naming <paramref name="paramName"/> in the message.
    /// </summary>
    public static void EnsureValid(string? value, string paramName = "workstreamId")
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"Workstream id '{value}' is not valid. Must be 1–128 chars, " +
                "lowercase ASCII letters/digits, separated only by single " +
                "'-', '_', or '.', starting with alphanumeric.",
                paramName);
        }
    }
}
