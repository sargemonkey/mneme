using System.Globalization;
using System.Text.Json;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// Loader + model for the LoCoMo dataset
/// (<see href="https://github.com/snap-research/locomo"/>). The on-disk shape
/// is a JSON array of samples; each sample has a multi-session
/// <c>conversation</c> and a list of <c>qa</c> probes. This loader is tolerant
/// of the fields we don't use (event/session summaries, image urls).
/// </summary>
public sealed record LoCoMoSample(
    string SampleId,
    IReadOnlyList<LoCoMoTurn> Turns,
    IReadOnlyList<LoCoMoQa> Questions);

/// <summary>One conversation turn from a session.</summary>
public sealed record LoCoMoTurn(
    string Speaker,
    string Text,
    DateTimeOffset At,
    int SessionNumber);

/// <summary>One question/answer probe with its LoCoMo category.</summary>
public sealed record LoCoMoQa(
    string Question,
    string Answer,
    int CategoryId,
    string CategoryLabel);

public static class LoCoMoDataset
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>LoCoMo's integer category → human label. Mapping per the LoCoMo paper.</summary>
    public static string CategoryLabel(int id) => id switch
    {
        1 => "multi-hop",
        2 => "temporal",
        3 => "open-domain",
        4 => "single-hop",
        5 => "adversarial",
        _ => "other",
    };

    /// <summary>Load all samples from a LoCoMo JSON file.</summary>
    public static IReadOnlyList<LoCoMoSample> Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Expected a top-level JSON array of LoCoMo samples.");
        }
        var samples = new List<LoCoMoSample>();
        var index = 0;
        foreach (var el in root.EnumerateArray())
        {
            samples.Add(ParseSample(el, index++));
        }
        return samples;
    }

    private static LoCoMoSample ParseSample(JsonElement el, int index)
    {
        var sampleId = el.TryGetProperty("sample_id", out var sid) && sid.ValueKind == JsonValueKind.String
            ? sid.GetString()!
            : $"locomo-{index:D3}";

        var turns = new List<LoCoMoTurn>();
        if (el.TryGetProperty("conversation", out var conv) && conv.ValueKind == JsonValueKind.Object)
        {
            // Sessions appear as session_1, session_2, … with sibling
            // session_N_date_time fields. Walk numerically until a gap.
            for (var n = 1; ; n++)
            {
                var key = $"session_{n}";
                if (!conv.TryGetProperty(key, out var session) || session.ValueKind != JsonValueKind.Array)
                {
                    if (n > 50) break; // safety
                    // allow a single gap (some datasets skip numbers) then stop
                    if (!conv.EnumerateObject().Any(p => p.Name.StartsWith($"session_{n + 1}", StringComparison.Ordinal)))
                        break;
                    continue;
                }
                var at = ParseSessionTime(conv, key, n);
                foreach (var turn in session.EnumerateArray())
                {
                    var speaker = turn.TryGetProperty("speaker", out var sp) ? sp.GetString() ?? "" : "";
                    var text = turn.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    turns.Add(new LoCoMoTurn(speaker, text, at, n));
                }
            }
        }

        var qa = new List<LoCoMoQa>();
        if (el.TryGetProperty("qa", out var qaArr) && qaArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qaArr.EnumerateArray())
            {
                var question = q.TryGetProperty("question", out var qq) ? qq.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(question)) continue;
                var category = q.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.Number
                    ? cat.GetInt32() : 0;
                // Adversarial questions store the gold under adversarial_answer.
                var answer =
                    q.TryGetProperty("answer", out var a) ? StringifyAnswer(a) :
                    q.TryGetProperty("adversarial_answer", out var aa) ? StringifyAnswer(aa) : "";
                qa.Add(new LoCoMoQa(question, answer, category, CategoryLabel(category)));
            }
        }

        return new LoCoMoSample(sampleId, turns, qa);
    }

    private static string StringifyAnswer(JsonElement a) => a.ValueKind switch
    {
        JsonValueKind.String => a.GetString() ?? "",
        JsonValueKind.Number => a.GetRawText(),
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        _ => a.GetRawText(),
    };

    // LoCoMo session times look like "1:56 pm on 8 May, 2023". Parse tolerantly;
    // fall back to a synthetic monotonically-increasing time per session so
    // bi-temporal ordering is still well-defined for temporal questions.
    private static DateTimeOffset ParseSessionTime(JsonElement conv, string sessionKey, int n)
    {
        if (conv.TryGetProperty(sessionKey + "_date_time", out var dt) && dt.ValueKind == JsonValueKind.String)
        {
            var raw = dt.GetString()!;
            var cleaned = raw.Replace(" on ", " ", StringComparison.OrdinalIgnoreCase);
            string[] formats =
            {
                "h:mm tt d MMM, yyyy", "h:mm tt d MMMM, yyyy",
                "h:mm tt dd MMM, yyyy", "h:mm tt dd MMMM, yyyy",
                "htt d MMM, yyyy",
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
