using SupportDesk.Domain;
using SupportDesk.Domain.Identity;

namespace SupportDesk.Persistence.Context;

/// <summary>
/// Who is acting and for which tenant, for the current DI scope (HTTP request, SignalR invocation, background job).
/// Populated only from trusted sources: the validated access token, a validated API key / OAuth client, the portal
/// host mapping resolved server-side, or the job record. The tenant query filter and write guard in
/// <see cref="DeskDbContext"/> read <see cref="TenantId"/>; an empty tenant fails closed (no rows, no writes).
/// </summary>
public sealed class RequestContext
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    public Guid TenantId { get; private set; }
    public string? TenantSlug { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid? ContactId { get; private set; }
    public Guid? ApiClientId { get; private set; }
    public ActorType ActorType { get; private set; } = ActorType.System;
    public string? ActorName { get; private set; }
    public IReadOnlySet<string> Permissions { get; private set; } = Empty;
    /// <summary>OAuth/API-key scopes; empty for interactive users.</summary>
    public IReadOnlySet<string> Scopes { get; private set; } = Empty;
    public TicketScope TicketScope { get; private set; } = TicketScope.Own;
    public CustomerScope CustomerScope { get; private set; } = CustomerScope.AccountManaged;
    public IReadOnlyList<Guid> DepartmentIds { get; private set; } = [];
    public IReadOnlyList<Guid> TeamIds { get; private set; } = [];
    public bool IsLightAgent { get; private set; }
    public bool IsPlatformOperator { get; private set; }
    public string? CorrelationId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Locale { get; set; }
    public string? TimeZone { get; set; }

    /// <summary>Automation hop count of events produced in this scope (trigger-loop protection).</summary>
    public int ChainDepth { get; set; }
    /// <summary>Outbox event being handled in this scope (causation for events produced by automation).</summary>
    public Guid? CausationEventId { get; set; }
    /// <summary>Rule that is currently executing actions (attribution and loop protection).</summary>
    public Guid? ExecutingRuleId { get; set; }

    /// <summary>Set by trusted platform code paths only (tenant provisioning, platform console). Allows cross-tenant writes.</summary>
    public bool AllowCrossTenant { get; private set; }

    /// <summary>
    /// Pins this scope's database connections to one database (background workers that claim outbox events or jobs, or scan
    /// across tenants): <see cref="Guid.Empty"/> = the shared database, a tenant id = that tenant's dedicated database.
    /// Null (default) routes by <see cref="TenantId"/>.
    /// </summary>
    public Guid? DatabasePin { get; set; }

    public bool HasTenant => TenantId != Guid.Empty;
    public bool IsAgent => UserId.HasValue && ActorType == ActorType.User;
    public bool IsContact => ContactId.HasValue && ActorType == ActorType.Contact;
    public bool IsSystem => ActorType is ActorType.System or ActorType.Automation or ActorType.Ai or ActorType.Integration;

    public void SetTenant(Guid tenantId, string? slug = null)
    {
        if (HasTenant && TenantId != tenantId && !AllowCrossTenant)
            throw new InvalidOperationException("The tenant of a request context cannot be changed once set.");
        TenantId = tenantId;
        TenantSlug = slug ?? TenantSlug;
    }

    public void SetUser(Guid tenantId, Guid userId, string name, IReadOnlySet<string> permissions, TicketScope ticketScope,
        CustomerScope customerScope, IReadOnlyList<Guid> departmentIds, IReadOnlyList<Guid> teamIds, bool isLightAgent)
    {
        SetTenant(tenantId);
        UserId = userId;
        ActorType = ActorType.User;
        ActorName = name;
        Permissions = permissions;
        TicketScope = ticketScope;
        CustomerScope = customerScope;
        DepartmentIds = departmentIds;
        TeamIds = teamIds;
        IsLightAgent = isLightAgent;
    }

    public void SetContact(Guid tenantId, Guid contactId, string name)
    {
        SetTenant(tenantId);
        ContactId = contactId;
        ActorType = ActorType.Contact;
        ActorName = name;
        Permissions = Empty;
    }

    public void SetApiClient(Guid tenantId, Guid clientId, string name, IReadOnlySet<string> scopes, IReadOnlySet<string> permissions,
        Guid? actAsUserId = null)
    {
        SetTenant(tenantId);
        ApiClientId = clientId;
        UserId = actAsUserId;
        ActorType = ActorType.ApiClient;
        ActorName = name;
        Scopes = scopes;
        Permissions = permissions;
        TicketScope = TicketScope.All;
        CustomerScope = CustomerScope.All;
    }

    /// <summary>Background processing on behalf of a tenant (workers, outbox handlers, automation).</summary>
    public void SetSystem(Guid tenantId, string name = "System", ActorType actor = ActorType.System)
    {
        SetTenant(tenantId);
        ActorType = actor;
        ActorName = name;
        Permissions = Domain.Rules.Permissions.AllCodes;
        TicketScope = TicketScope.All;
        CustomerScope = CustomerScope.All;
    }

    public void SetPlatformOperator(Guid operatorId, string name)
    {
        UserId = operatorId;
        ActorType = ActorType.Platform;
        ActorName = name;
        IsPlatformOperator = true;
    }

    /// <summary>Enables cross-tenant writes for trusted platform code (provisioning). Never call from request-driven code paths.</summary>
    public void EnterPlatformScope()
    {
        AllowCrossTenant = true;
        ActorType = ActorType.Platform;
        ActorName ??= "Platform";
    }

    /// <summary>Switch the tenant inside a platform scope (provisioning loops over tenants).</summary>
    public void SwitchTenant(Guid tenantId)
    {
        if (!AllowCrossTenant) throw new InvalidOperationException("Switching tenants requires a platform scope.");
        TenantId = tenantId;
    }

    public bool Has(string permission) => Permissions.Contains(permission) && (Scopes.Count == 0 || ScopeAllows(permission));

    public void Require(string permission)
    {
        if (!Has(permission)) throw new ForbiddenException($"Missing permission '{permission}'.");
    }

    public void RequireAny(params string[] permissions)
    {
        if (!permissions.Any(Has)) throw new ForbiddenException($"Missing permission (one of {string.Join(", ", permissions)}).");
    }

    public void RequireAgent()
    {
        if (!(IsAgent || ActorType == ActorType.ApiClient || IsSystem)) throw new ForbiddenException("Agent access required.");
    }

    /// <summary>Scopes are coarse ("tickets:read", "tickets:write", "customers:read", "*"); a permission is allowed when its module scope is granted.</summary>
    private bool ScopeAllows(string permission) => Domain.Rules.ApiScopeRules.Allows(Scopes, permission);
}
