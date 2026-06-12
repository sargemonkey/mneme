using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mneme.Agents.AI;
using Mneme.Contracts;
using Mneme.Hosting;

namespace Mneme.Agents.AI.Tests;

public sealed class MnemeContextProviderTests : IDisposable
{
    private readonly string _tmpDir;
    public MnemeContextProviderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-maf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    [Fact]
    public void RenderMarkdown_includes_orientation_sections_and_footer()
    {
        var now = DateTimeOffset.UtcNow;
        var bundle = new ContextBundle(
            Workstream: new WorkstreamId("w"),
            Orientation: new OrientationSummary("Where things stand.", "test/d@1", now, new EventId("e1")),
            Index: new BundleIndex("test/d@1", 1024, 200, now, new EventId("e1"),
                new[] { new BundleSectionRef("facts", "Facts", EpistemicCategory.Fact, 100) }),
            Sections: new[]
            {
                new BundleSection("facts", "Facts", EpistemicCategory.Fact, "- a fact [e1]",
                    "test/d@1", now, new EventId("e1"), 1024, 100, new[] { new EventId("e1") }),
            },
            Hints: new LookupHints(new[] { new LookupHint("kw", new EventId("e2"), "context blurb") }),
            GeneratedAt: now,
            EventsCoveredThrough: new EventId("e1"),
            IsStale: false);

        var md = MnemeContextProvider.RenderMarkdown(bundle);
        Assert.Contains("**Where we are:** Where things stand.", md);
        Assert.Contains("### Facts", md);
        Assert.Contains("- a fact [e1]", md);
        Assert.Contains("### Lookup hints", md);
        Assert.Contains("`kw`", md);
        Assert.Contains("test/d@1", md);
        Assert.Contains("covers up to event e1", md);
    }

    [Fact]
    public async Task InvokingAsync_returns_AIContext_with_one_system_message()
    {
        using var sp = BuildHost();
        var provider = sp.GetRequiredService<MnemeContextProvider>();
        var ctx = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(Array.Empty<ChatMessage>()));
        Assert.NotNull(ctx.Messages);
        Assert.Single(ctx.Messages);
        Assert.Equal(ChatRole.System, ctx.Messages[0].Role);
        Assert.Contains("Prior context from Mneme memory:", ctx.Messages[0].Text);
    }

    [Fact]
    public void DI_extension_registers_provider_and_resolves_dependencies()
    {
        using var sp = BuildHost();
        var p = sp.GetRequiredService<MnemeContextProvider>();
        Assert.NotNull(p);
        var p2 = sp.GetRequiredService<MnemeContextProvider>();
        Assert.Same(p, p2);
    }

    private ServiceProvider BuildHost()
    {
        var services = new ServiceCollection();
        services.AddMneme(o =>
        {
            o.WorkstreamId = "maf-test";
            o.SqlitePath = Path.Combine(_tmpDir, "maf.db");
            o.UserId = "alice";
        });
        services.AddMnemeContextProvider(new WorkstreamId("maf-test"));
        return services.BuildServiceProvider();
    }
}
