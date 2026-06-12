using Microsoft.Extensions.DependencyInjection;
using Mneme.Capture;
using Mneme.Contracts;

namespace Mneme.Agents.AI;

/// <summary>
/// DI ergonomic: <c>services.AddMnemeContextProvider(...)</c> registers
/// the provider against the workstream the host already configured via
/// <c>AddMneme</c>. Capability token + capture session are resolved
/// from DI; both optional.
/// </summary>
public static class MnemeAgentsAIServiceCollectionExtensions
{
    /// <summary>
    /// Register a singleton <see cref="MnemeContextProvider"/> that the
    /// MAF agent runtime can resolve. Call after <c>AddMneme(...)</c>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="workstream">Workstream the provider sources context from.</param>
    /// <param name="tokenBudget">Soft token budget for each distilled bundle. Default 4096.</param>
    /// <param name="systemPromptPrefix">Optional prefix for the system message that wraps the bundle.</param>
    public static IServiceCollection AddMnemeContextProvider(
        this IServiceCollection services,
        WorkstreamId workstream,
        int tokenBudget = 4096,
        string systemPromptPrefix = "Prior context from Mneme memory:")
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(sp =>
        {
            // CaptureSession's factory throws when no ICapturePolicy is
            // registered (by design). Probe for the policy first so the
            // MAF context provider is happy with auto-distill-only setups.
            CaptureSession? capture = null;
            if (sp.GetService<ICapturePolicy>() is not null)
            {
                capture = sp.GetService<CaptureSession>();
            }
            return new MnemeContextProvider(
                query: sp.GetRequiredService<IMemoryQueryAPI>(),
                token: sp.GetRequiredService<CapabilityToken>(),
                workstream: workstream,
                capture: capture,
                tokenBudget: tokenBudget,
                systemPromptPrefix: systemPromptPrefix);
        });
        return services;
    }
}
