using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Application;

/// <summary>AI gateway, features (classification, summaries, replies, RAG…), autonomous agents, usage and budgets.</summary>
public static class AiModule
{
    public static IServiceCollection AddAiModule(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
