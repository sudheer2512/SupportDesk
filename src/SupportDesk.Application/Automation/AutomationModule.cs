using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Application;

/// <summary>Automation rules (trigger → conditions → actions), blueprint workflows, webhooks.</summary>
public static class AutomationModule
{
    public static IServiceCollection AddAutomationModule(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
