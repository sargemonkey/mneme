using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// One graded question, persisted for resume + error analysis. Richer than the
/// aggregate needs so the JSONL / CSV are useful on their own.
/// </summary>
public sealed record QaRecord(
    string SampleId,
    int QuestionIndex,
    int CategoryId,
    string CategoryLabel,
    string Question,
    string Gold,
    string Predicted,
    bool Correct,
    int ContextTokens);

/// <summary>
/// Append-only JSONL store of graded questions. Lets a long LoCoMo run survive
/// rate-limit interruptions: re-running with the same output directory skips
/// every question already in the file (no repeated LLM calls) and replays its
/// stored grade into the aggregate. Also exports a CSV for spreadsheets.
/// </summary>
public sealed class RunStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _jsonlPath;

    public RunStore(string jsonlPath)
    {
        _jsonlPath = jsonlPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonlPath))!);
    }

    public string JsonlPath => _jsonlPath;

    /// <summary>Load already-graded records keyed by (sampleId, questionIndex).</summary>
    public Dictionary<(string, int), QaRecord> LoadExisting()
    {
        var map = new Dictionary<(string, int), QaRecord>();
        if (!File.Exists(_jsonlPath)) return map;
        foreach (var line in File.ReadLines(_jsonlPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            QaRecord? rec;
            try { rec = JsonSerializer.Deserialize<QaRecord>(line, Json); }
            catch { continue; } // tolerate a torn final line from an interrupted write
            if (rec is not null) map[(rec.SampleId, rec.QuestionIndex)] = rec;
        }
        return map;
    }

    /// <summary>Append one graded record and flush so a crash keeps it.</summary>
    public void Append(QaRecord record)
    {
        using var w = new StreamWriter(_jsonlPath, append: true);
        w.WriteLine(JsonSerializer.Serialize(record, Json));
        w.Flush();
    }

    public void Delete()
    {
        if (File.Exists(_jsonlPath)) File.Delete(_jsonlPath);
    }

    /// <summary>Write all records to a CSV next to the JSONL file. Returns the path.</summary>
    public string ExportCsv(IEnumerable<QaRecord> records)
    {
        var csvPath = Path.ChangeExtension(_jsonlPath, ".csv");
        using var w = new StreamWriter(csvPath, append: false);
        w.WriteLine("sample_id,question_index,category_id,category,correct,context_tokens,question,gold,predicted");
        foreach (var r in records)
        {
            w.WriteLine(string.Join(",",
                Csv(r.SampleId), r.QuestionIndex.ToString(CultureInfo.InvariantCulture),
                r.CategoryId.ToString(CultureInfo.InvariantCulture), Csv(r.CategoryLabel),
                r.Correct ? "1" : "0", r.ContextTokens.ToString(CultureInfo.InvariantCulture),
                Csv(r.Question), Csv(r.Gold), Csv(r.Predicted)));
        }
        return csvPath;
    }

    private static string Csv(string s)
    {
        if (s is null) return "";
        var needsQuote = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        var escaped = s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
        return needsQuote ? $"\"{escaped}\"" : escaped;
    }
}
