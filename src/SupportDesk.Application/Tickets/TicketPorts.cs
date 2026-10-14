using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Tickets;

namespace SupportDesk.Application.Tickets;

/// <summary>
/// SLA engine hooks, called synchronously inside ticket transactions so due dates are always consistent with the
/// ticket state that was just saved.
/// </summary>
public interface ISlaEngine
{
    /// <summary>Selects the policy (contract → conditions → default) and starts first-response/resolution clocks.</summary>
    Task ApplyAsync(Ticket ticket, CancellationToken ct);
    /// <summary>Re-targets after priority, department, customer, contract or product changes (keeps elapsed time).</summary>
    Task OnTicketChangedAsync(Ticket ticket, IReadOnlyList<FieldChange> changes, CancellationToken ct);
    Task OnStatusChangedAsync(Ticket ticket, TicketStatus from, TicketStatus to, CancellationToken ct);
    Task OnAgentPublicReplyAsync(Ticket ticket, DateTime at, CancellationToken ct);
    Task OnCustomerReplyAsync(Ticket ticket, DateTime at, CancellationToken ct);
    /// <summary>Stops all clocks (merged/deleted tickets).</summary>
    Task CancelAsync(Ticket ticket, string reason, CancellationToken ct);
}

/// <summary>Automatic assignment (round robin, load balanced, skill-based, territory/customer/product rules).</summary>
public interface IRoutingService
{
    /// <summary>Sets TeamId/AssigneeId on an unassigned ticket according to the department/team routing configuration.</summary>
    Task<RoutingDecision> RouteAsync(Ticket ticket, CancellationToken ct);
    /// <summary>Returns the delegate when the agent is out of office (active delegation or approved leave with a delegate).</summary>
    Task<Guid?> ResolveDelegateAsync(Guid userId, CancellationToken ct);
}

public sealed record RoutingDecision(Guid? TeamId, Guid? AssigneeId, string Strategy, string Reason);

/// <summary>Support eligibility from contracts, warranties and entitlements.</summary>
public interface IEntitlementService
{
    Task EvaluateAsync(Ticket ticket, CancellationToken ct);
}

/// <summary>Adds items to the agent workbench context panel (knowledge suggestions, AI recommendations, engineering links…).</summary>
public interface ITicketContextContributor
{
    Task ContributeAsync(Ticket ticket, TicketContextBuilder context, CancellationToken ct);
}

public sealed class TicketContextBuilder
{
    public List<ArticleSuggestionDto> Articles { get; } = [];
    public List<AiRecommendationDto> AiRecommendations { get; } = [];
    public List<EngineeringLinkDto> EngineeringLinks { get; } = [];
    public List<ExternalReferenceDto> RelatedTransactions { get; } = [];
}
