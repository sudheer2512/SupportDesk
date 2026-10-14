using SupportDesk.Application.Identity;
using SupportDesk.Domain.Rules;

namespace SupportDesk.UnitTests.Platform;

public sealed class ApiScopeRulesTests
{
    private static HashSet<string> Scopes(params string[] s) => new(s, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Read_scopes_grant_view_permissions_only()
    {
        var read = Scopes("tickets:read");
        Assert.True(ApiScopeRules.Allows(read, Permissions.TicketsView));
        Assert.False(ApiScopeRules.Allows(read, Permissions.TicketsReply));
        Assert.True(ApiScopeRules.Allows(Scopes("tickets:write"), Permissions.TicketsReply));
    }

    [Fact]
    public void Every_catalog_scope_grants_at_least_one_permission()
    {
        foreach (var scope in ApiScopes.Catalog)
            Assert.True(ApiScopes.PermissionsFor([scope]).Count > 0, $"Scope '{scope}' grants nothing.");
    }

    [Fact]
    public void Scopes_without_a_permission_module_are_mapped_explicitly()
    {
        // Reporting needs team-wide numbers; an API client has no tickets of its own.
        Assert.Superset(new HashSet<string> { Permissions.AnalyticsView, Permissions.AnalyticsTeam }, ApiScopes.PermissionsFor(["analytics:read"]));
        Assert.Contains(Permissions.AdminIntegrations, ApiScopes.PermissionsFor(["integrations:write"]));
        // Administration has no read-only permissions besides the audit log: admin:read must not allow changing settings.
        var adminRead = ApiScopes.PermissionsFor(["admin:read"]);
        Assert.Contains(Permissions.AdminAudit, adminRead);
        Assert.DoesNotContain(Permissions.AdminRoles, adminRead);
        Assert.DoesNotContain(Permissions.AdminSecurity, adminRead);
    }

    [Fact]
    public void Computed_permissions_and_the_per_request_check_agree()
    {
        string[][] samples = [["tickets:read"], ["analytics:read", "kb:read"], ["integrations:write"], ["admin:read"], ["*"], ["customers:write", "ai:write"]];
        foreach (var sample in samples)
        {
            var granted = ApiScopes.PermissionsFor(sample);
            foreach (var p in Permissions.Catalog)
                Assert.Equal(granted.Contains(p.Code), ApiScopeRules.Allows(Scopes(sample), p.Code));
        }
    }
}
