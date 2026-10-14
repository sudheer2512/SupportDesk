using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Application;

/// <summary>Users, roles, ticket configuration, layouts, security policies, configuration transfer, compliance, audit.</summary>
public static class AdministrationModule
{
    public static IServiceCollection AddAdministrationModule(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
