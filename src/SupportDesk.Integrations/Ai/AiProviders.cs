using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Ai;
using SupportDesk.Integrations.Ai;
using SupportDesk.Integrations.Ai.Local;

namespace SupportDesk.Integrations;

/// <summary>LLM and embedding providers: built-in local provider, OpenAI, Azure OpenAI, Anthropic, OpenAI-compatible endpoints.</summary>
public static class AiProviders
{
    public static IServiceCollection AddAiProviders(this IServiceCollection s, IConfiguration configuration)
    {
        var attempt = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Ai:TimeoutSeconds", 60), 5, 300));
        var total = attempt * 2 + TimeSpan.FromSeconds(10);
        s.AddSingleton<LocalAiProvider>();
        s.AddHttpClient<OpenAiCompatibleClient>(c => c.Timeout = total + TimeSpan.FromSeconds(5))
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = attempt;
                o.TotalRequestTimeout.Timeout = total;
                o.CircuitBreaker.SamplingDuration = attempt * 2 + TimeSpan.FromSeconds(1);
                o.Retry.MaxRetryAttempts = 2;
            });
        s.AddHttpClient<AnthropicClient>(c => c.Timeout = total + TimeSpan.FromSeconds(5))
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = attempt;
                o.TotalRequestTimeout.Timeout = total;
                o.CircuitBreaker.SamplingDuration = attempt * 2 + TimeSpan.FromSeconds(1);
                o.Retry.MaxRetryAttempts = 2;
            });
        s.AddScoped<IAiProviderFactory, AiProviderFactory>();
        return s;
    }
}

/// <summary>Creates provider clients for a tenant's settings (validated and normalised by AiConfigurationService).</summary>
public sealed class AiProviderFactory(LocalAiProvider local, OpenAiCompatibleClient openAi, AnthropicClient anthropic) : IAiProviderFactory
{
    public IChatModel CreateChat(AiProviderSettings settings) => settings.Provider switch
    {
        AiProviderNames.OpenAI or AiProviderNames.AzureOpenAI or AiProviderNames.OpenAICompatible => new OpenAiCompatibleModel(openAi, settings),
        AiProviderNames.Anthropic => new AnthropicModel(anthropic, settings),
        _ => local
    };

    public IEmbeddingModel CreateEmbedding(AiProviderSettings settings) => settings.Provider switch
    {
        AiProviderNames.OpenAI or AiProviderNames.AzureOpenAI or AiProviderNames.OpenAICompatible => new OpenAiCompatibleModel(openAi, settings),
        _ => local
    };
}
