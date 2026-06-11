using Microsoft.Data.Sqlite;
using Mneme.Contracts;
using Mneme.Storage;

namespace Mneme.Tests;

/// <summary>
/// Spins up a unique in-memory SQLite database per test (via a random
/// shared-memory name), runs <see cref="SqliteSchema.Initialize"/> against
/// it, and holds a single keep-alive connection so the in-memory db is
/// not torn down between transient connections. Dispose to release.
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    public SqliteConnectionFactory Factory { get; }

    public TestDatabase()
    {
        var name = "mneme-test-" + Guid.NewGuid().ToString("N");
        Factory = SqliteConnectionFactory.ForSharedMemory(name);
        _keepAlive = Factory.Open();
        SqliteSchema.Initialize(_keepAlive);
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

internal static class TestFixtures
{
    public static CaptureEvent NewEvidence(
        string eventId = "01H0EVID0000000000000000000",
        string workstream = "test-ws",
        string content = "the cat sat on the mat",
        DateTimeOffset? validAt = null,
        DateTimeOffset? recordedAt = null,
        EventChannel channel = EventChannel.Epistemic)
    {
        return new CaptureEvent(
            new EventId(eventId),
            new WorkstreamId(workstream),
            channel,
            validAt ?? DateTimeOffset.UtcNow,
            recordedAt ?? DateTimeOffset.UtcNow,
            new EvidencePayload(content, Source: "unit-test"),
            new CaptureProvenance(
                new CaptureSourceId("unit-test"),
                new PrincipalId("test-principal")));
    }
}
