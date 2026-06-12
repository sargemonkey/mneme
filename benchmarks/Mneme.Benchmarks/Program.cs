namespace Mneme.Benchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var fixtureRoot = args.FirstOrDefault()
            ?? Path.Combine(AppContext.BaseDirectory, "fixtures");
        var dataRoot = Path.Combine(AppContext.BaseDirectory, "bench-data");

        if (!Directory.Exists(fixtureRoot))
        {
            Console.Error.WriteLine($"Fixture directory not found: {fixtureRoot}");
            return 2;
        }

        var runner = new BenchmarkRunner(dataRoot);
        var fixtures = FixtureLoader.LoadAll(fixtureRoot).ToArray();
        if (fixtures.Length == 0)
        {
            Console.Error.WriteLine($"No fixtures found in {fixtureRoot}");
            return 3;
        }

        var any = 0; var totalHits = 0; var totalProbes = 0;
        foreach (var fixture in fixtures)
        {
            var report = await runner.RunAsync(fixture);
            Console.WriteLine($"# {report.FixtureName}");
            Console.WriteLine($"  events ingested : {report.EventsIngested}");
            Console.WriteLine($"  ingest p50      : {report.IngestP50Ms} ms");
            Console.WriteLine($"  ingest p99      : {report.IngestP99Ms} ms");
            Console.WriteLine($"  probes          : {report.ProbeResults.Count}");
            Console.WriteLine($"  hits            : {report.Hits} / {report.ProbeResults.Count}  (recall = {report.Recall:P0})");
            foreach (var p in report.ProbeResults.Where(x => !x.Hit))
            {
                Console.WriteLine($"   x MISS: \"{Trim(p.Question, 60)}\" expected \"{Trim(p.ExpectedSubstring, 50)}\"");
            }
            any++;
            totalHits += report.Hits;
            totalProbes += report.ProbeResults.Count;
        }
        Console.WriteLine();
        Console.WriteLine($"== overall: {totalHits}/{totalProbes} probes hit across {any} fixture(s)  (recall = {(totalProbes == 0 ? 0 : (double)totalHits / totalProbes):P0})");
        return totalHits == totalProbes ? 0 : 1;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
