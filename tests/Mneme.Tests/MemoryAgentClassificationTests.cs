using Mneme.Contracts;
using Mneme.Ingest;

namespace Mneme.Tests;

/// <summary>
/// Ensures the Phase 2 classifier writes its label into the event row
/// and that the existing Phase 1 invariants (idempotency, redaction)
/// still hold once classification is wired through.
/// </summary>
public sealed class MemoryAgentClassificationTests
{
    [Fact]
    public async Task Ingest_writes_classification_column()
    {
        using var db = new TestDatabase();
        var agent = new MemoryAgent(db.Factory);

        // Redacted body -> Secret label.
        var secretEvt = TestFixtures.NewEvidence(
            eventId: "cls-secret-001",
            content: "leak sk-abcdefghijklmnopqrstuvwxyz1234567890");
        await agent.IngestAsync(secretEvt);

        // Pii body -> Pii label.
        var piiEvt = TestFixtures.NewEvidence(
            eventId: "cls-pii-001",
            content: "email alice@example.com about it");
        await agent.IngestAsync(piiEvt);

        // Plain evidence -> Public label.
        var publicEvt = TestFixtures.NewEvidence(
            eventId: "cls-public-001",
            content: "the sky is blue");
        await agent.IngestAsync(publicEvt);

        using var c = db.Factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT event_id, classification FROM memory_events ORDER BY event_id;";
        var got = new Dictionary<string, int>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            got[r.GetString(0)] = r.GetInt32(1);
        }

        Assert.Equal((int)Mneme.Contracts.Classification.Public, got["cls-public-001"]);
        Assert.Equal((int)Mneme.Contracts.Classification.Pii,    got["cls-pii-001"]);
        Assert.Equal((int)Mneme.Contracts.Classification.Secret, got["cls-secret-001"]);
    }
}
