using System.Collections.Concurrent;
using Mneme.Contracts;

namespace Mneme.Hosting.Profiles;

/// <summary>
/// Process-wide registry of satellite (domain-profile) payload descriptors.
/// Domain profiles register their payload types here — via
/// <c>AddMnemeProfile</c> at composition time, before any (de)serialization —
/// so base Mneme can round-trip, redact, index, and summarize them.
/// </summary>
/// <remarks>
/// The eight built-in payloads are <b>not</b> in this registry; they remain
/// attribute-declared on <see cref="EventPayload"/>. Registration is code in a
/// trusted, app-chosen assembly, so the effective payload set stays a curated,
/// closed set (ADR-0005) — this does not open the door to arbitrary-type
/// deserialization.
/// </remarks>
public static class PayloadDescriptorRegistry
{
    private static readonly ConcurrentDictionary<Type, IPayloadDescriptor> ByType = new();
    private static readonly ConcurrentDictionary<string, IPayloadDescriptor> ByDiscriminator = new();

    /// <summary>Register (or replace) the descriptor for a satellite payload type.</summary>
    /// <exception cref="ArgumentException">
    /// If the type does not derive from <see cref="EventPayload"/>, or the
    /// discriminator is already claimed by a different type.
    /// </exception>
    public static void Register(IPayloadDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!typeof(EventPayload).IsAssignableFrom(descriptor.PayloadType))
        {
            throw new ArgumentException(
                $"{descriptor.PayloadType} does not derive from EventPayload.", nameof(descriptor));
        }
        if (string.IsNullOrWhiteSpace(descriptor.Discriminator))
        {
            throw new ArgumentException("Descriptor discriminator must be non-empty.", nameof(descriptor));
        }
        if (ByDiscriminator.TryGetValue(descriptor.Discriminator, out var existing)
            && existing.PayloadType != descriptor.PayloadType)
        {
            throw new ArgumentException(
                $"Discriminator '{descriptor.Discriminator}' is already registered for {existing.PayloadType}.",
                nameof(descriptor));
        }

        ByType[descriptor.PayloadType] = descriptor;
        ByDiscriminator[descriptor.Discriminator] = descriptor;
        // Rebuild the shared serializer so the new $type resolves immediately.
        Storage.EventSerialization.OnRegistryChanged();
    }

    /// <summary>The descriptor for <paramref name="payloadType"/>, or null if it is a built-in / unregistered type.</summary>
    public static IPayloadDescriptor? TryGet(Type payloadType) =>
        ByType.TryGetValue(payloadType, out var d) ? d : null;

    /// <summary>All currently-registered satellite descriptors.</summary>
    public static IReadOnlyCollection<IPayloadDescriptor> All => ByType.Values.ToArray();
}
