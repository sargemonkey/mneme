using System.Text.Json;
using Mneme.Contracts;

namespace Mneme.Storage;

/// <summary>
/// Centralised JSON settings + helpers used to push <see cref="EventPayload"/>
/// and <see cref="CaptureProvenance"/> records into the
/// <c>payload_json</c> / <c>provenance_json</c> columns and read them back.
/// All of Mneme's storage code goes through this type so any future change
/// to the wire shape only has to land in one place.
/// </summary>
/// <remarks>
/// The serializer honours the <c>$type</c> discriminator declared on
/// <see cref="EventPayload"/> via
/// <see cref="System.Text.Json.Serialization.JsonPolymorphicAttribute"/>,
/// skips <c>null</c> writes for compactness, and uses camel-case names so
/// dumps are pleasant to read.
/// </remarks>
public static class EventSerialization
{
    /// <summary>
    /// The single <see cref="JsonSerializerOptions"/> instance used for both
    /// payload and provenance round-trips.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = BuildOptions();

    /// <summary>Serialize an event payload to its <c>payload_json</c> form.</summary>
    public static string SerializePayload(EventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Deserialize <c>payload_json</c> back into a typed payload.</summary>
    public static EventPayload DeserializePayload(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        return JsonSerializer.Deserialize<EventPayload>(json, Options)
            ?? throw new InvalidOperationException("payload_json deserialized to null");
    }

    /// <summary>Serialize the provenance record.</summary>
    public static string SerializeProvenance(CaptureProvenance provenance)
    {
        return JsonSerializer.Serialize(provenance, Options);
    }

    /// <summary>Deserialize the provenance record.</summary>
    public static CaptureProvenance DeserializeProvenance(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        return JsonSerializer.Deserialize<CaptureProvenance>(json, Options)
            ?? throw new InvalidOperationException("provenance_json deserialized to null");
    }

    private static JsonSerializerOptions BuildOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            // Don't HTML-escape `<`, `>`, `&` — Mneme writes to a binary
            // SQLite column, not an HTML page. Keeping markers like
            // `<REDACTED:openai-key>` readable matters for ops and tests.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        };
    }
}
