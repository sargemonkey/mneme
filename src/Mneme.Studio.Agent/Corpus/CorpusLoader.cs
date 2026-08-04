using System.Globalization;
using System.Text.Json;

namespace Mneme.Studio.Agent.Corpus;

/// <summary>One replayable turn from a corpus conversation.</summary>
/// <param name="Speaker">Who spoke.</param>
/// <param name="Text">What they said.</param>
/// <param name="At">Event-time of the turn (drives Mneme's bi-temporal ordering).</param>
/// <param name="Session">1-based session number the turn belongs to.</param>
internal sealed record CorpusTurn(string Speaker, string Text, DateTimeOffset At, int Session);

/// <summary>A multi-session conversation the UI can replay into Mneme.</summary>
/// <param name="Id">Stable sample id.</param>
/// <param name="Title">Human-readable label for the picker.</param>
/// <param name="Turns">All turns in chronological order.</param>
internal sealed record CorpusConversation(string Id, string Title, IReadOnlyList<CorpusTurn> Turns);

/// <summary>
/// Compact, self-contained loader for the LoCoMo JSON shape
/// (<see href="https://github.com/snap-research/locomo"/>). Deliberately does
/// <em>not</em> reference the benchmark project (which pulls the ONNX runtime);
/// the app only needs to read conversations, not score answers.
/// </summary>
/// <remarks>
/// Reads the bundled <c>corpus/locomo-sample.json</c> by default. Set the
/// <c>MNEME_LOCOMO_PATH</c> environment variable to a full LoCoMo dataset file
/// to replay the real thing.
/// </remarks>
internal static class CorpusLoader
{
    public static string ResolvePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("MNEME_LOCOMO_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }
        return Path.Combine(AppContext.BaseDirectory, "corpus", "locomo-sample.json");
    }

    public static IReadOnlyList<CorpusConversation> Load(string? path = null)
    {
        path ??= ResolvePath();
        if (!File.Exists(path))
        {
            return Array.Empty<CorpusConversation>();
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CorpusConversation>();
        }

        var result = new List<CorpusConversation>();
        var index = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var parsed = ParseSample(el, index++);
            if (parsed.Turns.Count > 0) result.Add(parsed);
        }
        return result;
    }

    private static CorpusConversation ParseSample(JsonElement el, int index)
    {
        var id = el.TryGetProperty("sample_id", out var sid) && sid.ValueKind == JsonValueKind.String
            ? sid.GetString()!
            : $"corpus-{index:D3}";

        var turns = new List<CorpusTurn>();
        string? a = null, b = null;
        if (el.TryGetProperty("conversation", out var conv) && conv.ValueKind == JsonValueKind.Object)
        {
            if (conv.TryGetProperty("speaker_a", out var sa) && sa.ValueKind == JsonValueKind.String) a = sa.GetString();
            if (conv.TryGetProperty("speaker_b", out var sb) && sb.ValueKind == JsonValueKind.String) b = sb.GetString();

            for (var n = 1; n <= 50; n++)
            {
                if (!conv.TryGetProperty($"session_{n}", out var session) || session.ValueKind != JsonValueKind.Array)
                {
                    // Tolerate a single gap in session numbering, then stop.
                    if (!conv.TryGetProperty($"session_{n + 1}", out _)) break;
                    continue;
                }
                var at = ParseSessionTime(conv, $"session_{n}", n);
                var offset = 0;
                foreach (var turn in session.EnumerateArray())
                {
                    var speaker = turn.TryGetProperty("speaker", out var sp) ? sp.GetString() ?? "" : "";
                    var text = turn.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    // Spread turns within a session by a minute each so ordering is stable.
                    turns.Add(new CorpusTurn(speaker, text, at.AddMinutes(offset++), n));
                }
            }
        }

        var speakers = a is not null && b is not null ? $"{a} & {b}" : "conversation";
        var title = $"{id} — {speakers} ({turns.Count} turns)";
        return new CorpusConversation(id, title, turns);
    }

    // LoCoMo session times look like "1:56 pm on 8 May, 2023". Parse tolerantly;
    // fall back to a synthetic per-session date so bi-temporal ordering holds.
    private static DateTimeOffset ParseSessionTime(JsonElement conv, string sessionKey, int n)
    {
        if (conv.TryGetProperty(sessionKey + "_date_time", out var dt) && dt.ValueKind == JsonValueKind.String)
        {
            var cleaned = dt.GetString()!.Replace(" on ", " ", StringComparison.OrdinalIgnoreCase);
            string[] formats =
            {
                "h:mm tt d MMM, yyyy", "h:mm tt d MMMM, yyyy",
                "h:mm tt dd MMM, yyyy", "h:mm tt dd MMMM, yyyy", "htt d MMM, yyyy",
            };
            if (DateTimeOffset.TryParseExact(cleaned, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
            if (DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var loose))
            {
                return loose;
            }
        }
        return new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(n);
    }
}
