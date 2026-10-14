using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDesk.Application;

/// <summary>Departments, teams, agents, skills, shifts, leave, delegation, business calendars and SLA policy administration.</summary>
public static class OrganisationModule
{
    public static IServiceCollection AddOrganisationModule(this IServiceCollection s, IConfiguration configuration)
    {
        return s;
    }
}
