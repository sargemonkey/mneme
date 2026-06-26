using BenchmarkDotNet.Running;

namespace Mneme.Benchmarks.Perf;

/// <summary>
/// Entry point for the Mneme performance microbenchmarks (BenchmarkDotNet).
/// </summary>
/// <remarks>
/// Run all:        <c>dotnet run -c Release --project benchmarks/Mneme.Benchmarks.Perf</c><br/>
/// Run one class:  append <c>--filter *IngestBenchmarks*</c><br/>
/// Quick smoke:    append <c>--job short</c> (fewer iterations, rougher numbers).
/// <para>
/// This is distinct from <c>Mneme.Benchmarks</c>, which measures retrieval
/// <em>quality</em> (LoCoMo/LongMemEval recall). This project measures
/// <em>performance</em>: ingest throughput and query latency.
/// </para>
/// </remarks>
internal static class Program
{
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
