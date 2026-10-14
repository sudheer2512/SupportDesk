using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Approvals;
using SupportDesk.Application.Organisation;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Routing;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Sla;

namespace SupportDesk.Application;

/// <summary>Departments, teams, agents, skills, shifts, leave, delegation, business calendars and SLA policy administration.</summary>
public static class OrganisationModule
{
    public static IServiceCollection AddOrganisationModule(this IServiceCollection s, IConfiguration configuration)
    {
        // Structure & routing configuration
        s.AddScoped<RoutingService>(); // concrete type for the routing preview (IRoutingService is registered by the kernel)
        s.AddScoped<DepartmentService>();
        s.AddScoped<TeamService>();
        s.AddScoped<SkillService>();
        s.AddScoped<IJobHandler, DepartmentReassignJobHandler>();

        // Agents, invitations, plan seats
        s.AddScoped<AgentSeatGuard>();
        s.AddScoped<AgentAdminService>();
        s.AddScoped<AgentInvitationService>();
        s.AddScoped<IJobHandler, AgentOffboardJobHandler>();

        // Workforce: shifts, leave (Approvals integration), delegation
        s.AddScoped<WorkforceService>();
        s.AddScoped<IApprovalAction, LeaveApprovalAction>();
        s.AddScoped<IOutboxHandler, OrganisationApprovalHandler>();
        s.AddScoped<IJobHandler, DelegationStartJobHandler>();

        // Supervisor live monitor + realtime agent events
        s.AddScoped<SupervisorMonitorService>();
        s.AddScoped<IOutboxHandler, OrganisationRealtimeHandler>();

        // Demo data
        s.AddScoped<IDemoSeedContributor, OrganisationDemoSeedContributor>();

        s.AddSlaAdministration(configuration);
        return s;
    }
}

/// <summary>SLA policies, business calendars/holidays and live breach views (administration side of the SLA engine).</summary>
public static class SlaAdministrationModule
{
    public static IServiceCollection AddSlaAdministration(this IServiceCollection s, IConfiguration configuration)
    {
        s.AddScoped<SlaPolicyAdminService>();
        s.AddScoped<BusinessCalendarService>();
        s.AddScoped<SlaBreachService>();
        s.AddScoped<IJobHandler, SlaRetargetJobHandler>();
        return s;
    }
}
