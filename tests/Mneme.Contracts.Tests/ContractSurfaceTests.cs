using System.Reflection;

namespace Mneme.Contracts.Tests;

/// <summary>
/// Surface-level invariants for every public type in Mneme.Contracts. These
/// tests guard against accidentally shipping non-public interfaces, missing
/// types, or accidentally introducing implementation classes into the
/// contracts assembly.
/// </summary>
public sealed class ContractSurfaceTests
{
    private static readonly Assembly ContractsAssembly = typeof(IMemoryAgent).Assembly;

    [Fact]
    public void ContractsAssembly_OnlyContainsRecordsInterfacesEnumsAndExceptions()
    {
        // Mneme.Contracts is a pure contract surface. The only public types
        // allowed are: records, interfaces, enums, and exception classes.
        // This test catches accidentally landing an implementation class
        // (e.g., InMemoryMemoryAgent) in the contracts assembly.
        foreach (var t in ContractsAssembly.GetExportedTypes())
        {
            // Generated CompilerGenerated attribute machinery — skip.
            if (t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            {
                continue;
            }

            var isRecord = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.Name == "<Clone>$") || IsValueRecord(t);
            var isInterface = t.IsInterface;
            var isEnum = t.IsEnum;
            var isException = typeof(Exception).IsAssignableFrom(t);

            Assert.True(
                isRecord || isInterface || isEnum || isException,
                $"Type '{t.FullName}' is none of: record, interface, enum, exception. " +
                "Mneme.Contracts is meant to be implementation-free.");
        }
    }

    [Fact]
    public void AllInterfaces_StartWithI()
    {
        var bad = ContractsAssembly.GetExportedTypes()
            .Where(t => t.IsInterface && !t.Name.StartsWith('I'))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(bad.Count == 0, "Interfaces not starting with 'I': " + string.Join(", ", bad));
    }

    [Theory]
    [InlineData(typeof(IMemoryAgent))]
    [InlineData(typeof(IMemoryQueryAPI))]
    [InlineData(typeof(IMemoryCurator))]
    [InlineData(typeof(ICurationLog))]
    [InlineData(typeof(IReviewQueue))]
    public void CoreInterfaces_AreExported(Type t)
    {
        Assert.True(t.IsPublic);
        Assert.True(t.IsInterface);
    }

    [Theory]
    [InlineData(typeof(CaptureEvent))]
    [InlineData(typeof(IngestResult))]
    [InlineData(typeof(CapabilityToken))]
    [InlineData(typeof(CurationCapability))]
    [InlineData(typeof(QuerySpec))]
    [InlineData(typeof(QueryRequest))]
    [InlineData(typeof(QueryResult))]
    [InlineData(typeof(QueryResultItem))]
    [InlineData(typeof(QueryExplain))]
    [InlineData(typeof(ScoreDetails))]
    [InlineData(typeof(DistillOptions))]
    [InlineData(typeof(ContextBundle))]
    [InlineData(typeof(BundleIndex))]
    [InlineData(typeof(BundleSection))]
    [InlineData(typeof(BundleSectionRef))]
    [InlineData(typeof(OrientationSummary))]
    [InlineData(typeof(LookupHints))]
    [InlineData(typeof(LookupHint))]
    [InlineData(typeof(CurationResult))]
    [InlineData(typeof(CurationEntry))]
    [InlineData(typeof(FactAmendment))]
    [InlineData(typeof(FactSplitPart))]
    [InlineData(typeof(FactMerged))]
    [InlineData(typeof(PendingReviewItem))]
    [InlineData(typeof(CaptureProvenance))]
    [InlineData(typeof(EvidencePayload))]
    [InlineData(typeof(FactPayload))]
    [InlineData(typeof(DecisionPayload))]
    [InlineData(typeof(HypothesisPayload))]
    [InlineData(typeof(GoalPayload))]
    [InlineData(typeof(ActionPayload))]
    [InlineData(typeof(OutcomePayload))]
    public void CoreRecords_AreExportedAndInstantiable(Type t)
    {
        Assert.True(t.IsPublic);
        // sealed = locked surface; consumers can't subclass our records.
        Assert.True(t.IsSealed, $"Record {t.Name} should be sealed.");
    }

    [Theory]
    [InlineData(typeof(EventId))]
    [InlineData(typeof(WorkstreamId))]
    [InlineData(typeof(FactId))]
    [InlineData(typeof(EntityId))]
    [InlineData(typeof(PrincipalId))]
    [InlineData(typeof(CaptureSourceId))]
    public void Identifiers_AreReadOnlyRecordStructs(Type t)
    {
        Assert.True(t.IsValueType, $"{t.Name} must be a struct.");
        Assert.True(t.GetCustomAttribute<System.Runtime.CompilerServices.IsReadOnlyAttribute>() is not null,
            $"{t.Name} must be readonly.");
    }

    [Theory]
    [InlineData(typeof(EpistemicCategory))]
    [InlineData(typeof(EventChannel))]
    [InlineData(typeof(Classification))]
    [InlineData(typeof(CurationType))]
    [InlineData(typeof(WorkstreamMode))]
    [InlineData(typeof(PinScope))]
    [InlineData(typeof(HypothesisState))]
    [InlineData(typeof(GoalState))]
    [InlineData(typeof(OutcomePolarity))]
    public void CoreEnums_AreExported(Type t)
    {
        Assert.True(t.IsPublic);
        Assert.True(t.IsEnum);
    }

    [Theory]
    [InlineData(typeof(StaleProposalError))]
    [InlineData(typeof(CapabilityDeniedError))]
    public void CoreExceptions_AreExported(Type t)
    {
        Assert.True(t.IsPublic);
        Assert.True(typeof(Exception).IsAssignableFrom(t));
        Assert.True(t.IsSealed, $"{t.Name} should be sealed; exceptions are not for derivation.");
    }

    private static bool IsValueRecord(Type t) =>
        t.IsValueType && t.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.NonPublic) is not null;
}
