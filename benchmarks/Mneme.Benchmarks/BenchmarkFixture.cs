using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mneme.Benchmarks;

public sealed record BenchmarkFixture(
    string Name,
    string Workstream,
    IReadOnlyList<BenchmarkTurn> Turns,
    IReadOnlyList<BenchmarkProbe> Probes,
    string? TemporalNote = null);

public sealed record BenchmarkTurn(
    string Speaker,
    DateTimeOffset At,
    string Content,
    bool ShouldCapture = true,
    string Category = "Evidence");

public sealed record BenchmarkProbe(
    string Question,
    string ExpectedSubstring,
    DateTimeOffset? AsOf = null);

internal static class FixtureLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static BenchmarkFixture Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BenchmarkFixture>(json, Options)
            ?? throw new InvalidOperationException($"Empty or malformed fixture: {path}");
    }

    public static IEnumerable<BenchmarkFixture> LoadAll(string directory)
    {
        foreach (var f in Directory.EnumerateFiles(directory, "*.json"))
        {
            yield return Load(f);
        }
    }
}
