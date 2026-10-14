using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Integrations;

/// <summary>Engineering trackers: Azure DevOps Server/Services, GitHub, and a local tracker for development.</summary>
public static class EngineeringAdapters
{
    public static IServiceCollection AddEngineeringAdapters(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
