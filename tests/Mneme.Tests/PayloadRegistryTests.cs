using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;
using Mneme.Hosting;
using Mneme.Hosting.Profiles;
using Mneme.Ingest.Redaction;
using Mneme.Storage;

namespace Mneme.Tests;

/// <summary>
/// Proves the Profile SDK's payload registry (ADR-0005 / Phase 15.A) actually
/// works end to end: a <em>satellite</em> payload type, registered via an
/// <see cref="IPayloadDescriptor"/>, round-trips through the shared serializer,
/// gets redacted by its descriptor, and — crucially — the closed-set safety
/// property survives (an unknown <c>$type</c> is still rejected).
/// </summary>
public sealed class PayloadRegistryTests
{
    // A stand-in domain payload that base Mneme has no compile-time knowledge of.
    private sealed record TestDomainPayload(string Title, string? Secret) : EventPayload
    {
        public override EpistemicCategory Category => EpistemicCategory.Evidence;
    }

    private sealed class TestDomainDescriptor : IPayloadDescriptor
    {
        public Type PayloadType => typeof(TestDomainPayload);
        public string Discriminator => "TestDomainPayload";

        public (EventPayload Payload, bool HadHits, int HitCount) Redact(EventPayload payload, IRedactor redactor)
        {
            var p = (TestDomainPayload)payload;
            var t = redactor.Redact(p.Title);
            var hits = t.Hits.Count;
            var had = t.HadHits;
            string? secret = p.Secret;
            if (secret is not null)
            {
                var s = redactor.Redact(secret);
                secret = s.RedactedContent;
                had |= s.HadHits;
                hits += s.Hits.Count;
            }
            return (p with { Title = t.RedactedContent, Secret = secret }, had, hits);
        }

        public string ExtractText(EventPayload payload)
        {
            var p = (TestDomainPayload)payload;
            return p.Title + " " + (p.Secret ?? string.Empty);
        }

        public string Summarize(EventPayload payload) => ((TestDomainPayload)payload).Title;
    }

    public PayloadRegistryTests() => PayloadDescriptorRegistry.Register(new TestDomainDescriptor());

    [Fact]
    public void Registered_satellite_payload_roundtrips_through_the_shared_serializer()
    {
        var original = new TestDomainPayload("chapter one", "none");
        var json = EventSerialization.SerializePayload(original);

        // The discriminator the descriptor declared is what lands on the wire.
        Assert.Contains("\"$type\":\"TestDomainPayload\"", json);

        var back = EventSerialization.DeserializePayload(json);
        var typed = Assert.IsType<TestDomainPayload>(back);
        Assert.Equal("chapter one", typed.Title);
        Assert.Equal(EpistemicCategory.Evidence, back.Category);
    }

    [Fact]
    public void Built_in_payloads_still_roundtrip_alongside_a_registered_satellite()
    {
        // Registering a satellite must not disturb the eight built-ins.
        var f = new FactPayload("the sky is blue", System.Array.Empty<EventId>());
        var back = EventSerialization.DeserializePayload(EventSerialization.SerializePayload(f));
        Assert.IsType<FactPayload>(back);
    }

    [Fact]
    public void Unknown_discriminator_is_still_rejected()
    {
        // The closed-set safety property (ADR-0005): an unregistered $type must
        // not deserialize into anything.
        const string hostile = "{\"$type\":\"System.Diagnostics.Process\",\"title\":\"x\"}";
        Assert.ThrowsAny<JsonException>(() => EventSerialization.DeserializePayload(hostile));
    }

    [Fact]
    public async Task Descriptor_redaction_runs_on_the_public_ingest_path_for_a_satellite_payload()
    {
        // The real proof: a satellite payload ingested through the public API
        // must have its descriptor-declared free-text fields redacted before it
        // reaches the WAL (locked decision #11, extended to profile payloads).
        var tmp = Path.Combine(Path.GetTempPath(), "mneme-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var services = new ServiceCollection();
            services.AddMneme(o =>
            {
                o.WorkstreamId = "reg-ws";
                o.SqlitePath = Path.Combine(tmp, "reg.db");
                o.UserId = "host";
            });
            using var sp = services.BuildServiceProvider();
            var agent = sp.GetRequiredService<IMemoryAgent>();

            const string secret = "set password = supersecretvalue123 before deploying";
            await agent.IngestAsync(new CaptureEvent(
                new EventId("reg-sat-1"), new WorkstreamId("reg-ws"), EventChannel.Epistemic,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                new TestDomainPayload("chapter one", secret),
                new CaptureProvenance(new CaptureSourceId("t"), new PrincipalId("u"))));

            var factory = sp.GetRequiredService<SqliteConnectionFactory>();
            using var c = factory.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT payload_json FROM memory_events WHERE event_id = 'reg-sat-1';";
            var raw = (string)cmd.ExecuteScalar()!;
            Assert.Contains("\"$type\":\"TestDomainPayload\"", raw);
            Assert.DoesNotContain("supersecretvalue123", raw);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
