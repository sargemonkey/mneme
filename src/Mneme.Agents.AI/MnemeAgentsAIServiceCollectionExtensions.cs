using Microsoft.Extensions.DependencyInjection;
using Mneme.Contracts;

namespace Mneme.Agents.AI;

/// <summary>
/// DI ergonomic: <c>services.AddMnemeContextProvider(...)</c> registers
/// the provider against the workstream the host already configured via
/// <c>AddMneme</c>. Capability token is resolved from DI.
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
        services.AddSingleton(sp => new MnemeContextProvider(
            query: sp.GetRequiredService<IMemoryQueryAPI>(),
            token: sp.GetRequiredService<CapabilityToken>(),
            workstream: workstream,
            tokenBudget: tokenBudget,
            systemPromptPrefix: systemPromptPrefix));
        return services;
    }
}
