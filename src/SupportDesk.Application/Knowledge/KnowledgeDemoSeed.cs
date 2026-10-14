using SupportDesk.Application.Seed;

namespace SupportDesk.Application.Knowledge;

/// <summary>Knowledge demo data (articles, translations, feedback, community, troubleshooting flows) for tenant "wings".</summary>
public sealed class KnowledgeDemoSeed : IDemoSeedContributor
{
    public int Order => 30;

    public Task SeedAsync(DemoSeedContext context, CancellationToken ct) => Task.CompletedTask;
}
