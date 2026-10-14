using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Application;

/// <summary>Knowledge base, community, guided troubleshooting and tenant search index.</summary>
public static class KnowledgeModule
{
    public static IServiceCollection AddKnowledgeModule(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
