using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Integrations;

/// <summary>LLM and embedding providers: built-in local provider, OpenAI, Azure OpenAI, Anthropic, OpenAI-compatible endpoints.</summary>
public static class AiProviders
{
    public static IServiceCollection AddAiProviders(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
