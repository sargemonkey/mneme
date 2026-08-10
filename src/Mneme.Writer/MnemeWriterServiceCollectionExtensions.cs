using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mneme.Hosting.Profiles;
using Mneme.Storage;

namespace Mneme.Writer;

/// <summary>
/// DI ergonomics for the writing-domain profile. Call <em>after</em>
/// <c>AddMneme</c>:
/// <code>
/// services.AddMneme(o => { … })
///         .AddMnemeWriterProfile();
/// </code>
/// It registers the <see cref="WriterProfile"/> (payload descriptor + canon
/// projector) via the base <c>AddMnemeProfile</c> and wires
/// <see cref="IWriterMemory"/> for the continuity read surface.
/// </summary>
public static class MnemeWriterServiceCollectionExtensions
{
    /// <summary>Register the Mneme writing-domain profile and its read surface.</summary>
    public static IServiceCollection AddMnemeWriterProfile(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMnemeProfile<WriterProfile>();
        services.TryAddSingleton<IWriterMemory>(sp => new WriterMemory(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
