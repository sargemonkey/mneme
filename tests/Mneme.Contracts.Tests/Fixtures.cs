using System.Text.Json;

namespace Mneme.Contracts.Tests;

/// <summary>Common fixtures for round-trip tests.</summary>
internal static class Fixtures
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static EventId NewEventId(string suffix = "01") => new($"01HJ000000000000000000000{suffix}");
    public static WorkstreamId NewWorkstream() => new("cust-acme-q3");
    public static FactId NewFactId() => new("fact-abc-001");
    public static EntityId NewEntityId() => new("entity-john-doe");
    public static PrincipalId NewPrincipal() => new("user@example.com");
    public static CaptureSourceId NewSource() => new("plugin-github");

    public static CaptureProvenance NewProvenance() =>
        new(NewSource(), NewPrincipal(), "session-42");

    public static CaptureEvent NewEvidenceEvent() => new(
        NewEventId(),
        NewWorkstream(),
        EventChannel.Epistemic,
        DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
        DateTimeOffset.Parse("2026-06-01T12:00:01Z"),
        new EvidencePayload("hello world", "chat://session-42/turn-3"),
        NewProvenance());

    public static CapabilityToken NewReadToken(bool crossWorkstream = false) => new(
        NewPrincipal(),
        crossWorkstream ? null : NewWorkstream(),
        DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow.AddHours(1),
        Array.Empty<EpistemicCategory>(),
        crossWorkstream);

    public static CurationCapability NewFullCurationCap() => new(
        NewPrincipal(),
        NewWorkstream(),
        DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow.AddHours(1),
        CanAmend: true,
        CanAnnotate: true,
        CanPin: true,
        CanDemote: true,
        CanSplit: true,
        CanMerge: true,
        CanRevert: true,
        CanReview: true);
}
