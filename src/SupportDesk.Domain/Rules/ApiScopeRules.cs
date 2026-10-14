namespace SupportDesk.Domain.Rules;

/// <summary>
/// What an API key / OAuth client scope allows. "{module}:write" covers every permission of the module, "{module}:read" its
/// view permissions; a few scopes don't follow the permission module names and are mapped explicitly. Used both to compute a
/// client's permissions and to check each request, so the two can never disagree.
/// </summary>
public static class ApiScopeRules
{
    public const string All = "*";

    /// <summary>Scopes whose meaning is not "{permission module}:{read|write}".</summary>
    private static readonly Dictionary<string, string[]> Explicit = new(StringComparer.OrdinalIgnoreCase)
    {
        // Reporting tools read team-wide numbers: an API client has no tickets of its own.
        ["analytics:read"] = [Permissions.AnalyticsView, Permissions.AnalyticsTeam],
        // Integration administration (API credentials, webhooks, ERP, engineering trackers) and linking work items.
        ["integrations:write"] = [Permissions.AdminIntegrations, Permissions.EngineeringView, Permissions.EngineeringLink],
        // Administration settings have no separate read permissions; read-only access is the audit log.
        ["admin:read"] = [Permissions.AdminAudit],
    };

    public static bool Allows(IReadOnlySet<string> scopes, string permission)
    {
        if (scopes.Contains(All)) return true;
        var module = permission.Split('.')[0];
        var isRead = permission.EndsWith(".view", StringComparison.Ordinal) || permission.EndsWith(".view_internal", StringComparison.Ordinal);
        if (scopes.Contains($"{module}:write") || (isRead && scopes.Contains($"{module}:read"))) return true;
        foreach (var scope in scopes)
            if (Explicit.TryGetValue(scope, out var granted) && granted.Contains(permission, StringComparer.Ordinal)) return true;
        return false;
    }
}
