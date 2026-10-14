using System.Linq.Expressions;
using System.Text.Json;
using SupportDesk.Domain;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Tickets;

/// <summary>
/// Data-scope enforcement for agents: All, Departments, Team or Own (plus tickets they watch or are assigned to).
/// Always applied in queries; single-ticket loads return 404 when out of scope (no existence leak).
/// </summary>
public static class TicketAccess
{
    public static IQueryable<Ticket> VisibleTo(this IQueryable<Ticket> q, RequestContext ctx, DeskDbContext db)
    {
        if (ctx.IsSystem || ctx.ActorType == ActorType.ApiClient && ctx.UserId is null) return q;
        if (ctx.IsContact) throw new ForbiddenException("Customer access uses the portal endpoints.");
        if (!ctx.Has(Permissions.TicketsView)) return q.Where(_ => false);
        var uid = ctx.UserId ?? Guid.Empty;
        var depts = ctx.DepartmentIds.ToList();
        var teams = ctx.TeamIds.ToList();
        var watched = db.TicketWatchers.Where(w => w.UserId == uid).Select(w => w.TicketId);
        return ctx.TicketScope switch
        {
            TicketScope.All => q,
            TicketScope.Departments => q.Where(t => depts.Contains(t.DepartmentId) || t.AssigneeId == uid || watched.Contains(t.Id)),
            TicketScope.Team => q.Where(t => (t.TeamId != null && teams.Contains(t.TeamId.Value)) || t.AssigneeId == uid || watched.Contains(t.Id)),
            _ => q.Where(t => t.AssigneeId == uid || watched.Contains(t.Id))
        };
    }

    /// <summary>
    /// Entitlement override: always with tickets.override_entitlement; additionally, when the tenant setting
    /// AllowAgentsToOverrideEntitlement is on, any agent who may edit the ticket (tickets.edit). A reason is always
    /// required and the override is audited (TicketService.OverrideEntitlementAsync).
    /// </summary>
    public static bool CanOverrideEntitlement(RequestContext ctx, bool agentsMayOverride) =>
        ctx.IsSystem || ctx.Has(Permissions.TicketsOverrideEntitlement) || (agentsMayOverride && ctx.Has(Permissions.TicketsEdit));

    /// <summary>
    /// Active legal holds covering a ticket: holds on its customer, its contact, or naming the ticket in TicketIdsJson.
    /// Null keys never match (a contact-only hold does not cover tickets without a customer).
    /// </summary>
    public static IQueryable<Domain.Platform.LegalHold> HoldsCovering(DeskDbContext db, Guid ticketId, Guid? customerId, Guid? contactId)
    {
        var id = ticketId.ToString();
        return db.LegalHolds.Where(h => h.ReleasedAt == null &&
            ((customerId != null && h.CustomerId == customerId) || (contactId != null && h.ContactId == contactId) ||
             (h.TicketIdsJson != null && h.TicketIdsJson.Contains(id))));
    }

    public static bool CanView(Ticket t, RequestContext ctx, bool isWatcher)
    {
        if (ctx.IsSystem || ctx.ActorType == ActorType.ApiClient && ctx.UserId is null) return true;
        if (!ctx.Has(Permissions.TicketsView)) return false;
        if (t.AssigneeId == ctx.UserId || isWatcher) return true;
        return ctx.TicketScope switch
        {
            TicketScope.All => true,
            TicketScope.Departments => ctx.DepartmentIds.Contains(t.DepartmentId),
            TicketScope.Team => t.TeamId.HasValue && ctx.TeamIds.Contains(t.TeamId.Value),
            _ => false
        };
    }
}

/// <summary>
/// Translates the shared condition model (saved views, bulk selections, report filters) into a SQL-translatable
/// predicate over tickets. Unsupported fields are rejected rather than silently ignored.
/// </summary>
public static class TicketFilterTranslator
{
    private static readonly ParameterExpression T = Expression.Parameter(typeof(Ticket), "t");

    public static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["priority"] = nameof(Ticket.Priority), ["severity"] = nameof(Ticket.Severity), ["statusCategory"] = nameof(Ticket.StatusCategory),
        ["statusId"] = nameof(Ticket.StatusId), ["departmentId"] = nameof(Ticket.DepartmentId), ["teamId"] = nameof(Ticket.TeamId),
        ["assigneeId"] = nameof(Ticket.AssigneeId), ["channel"] = nameof(Ticket.Channel), ["customerId"] = nameof(Ticket.CustomerId),
        ["contactId"] = nameof(Ticket.ContactId), ["productId"] = nameof(Ticket.ProductId), ["moduleId"] = nameof(Ticket.ModuleId),
        ["versionId"] = nameof(Ticket.VersionId), ["installationId"] = nameof(Ticket.InstallationId), ["categoryId"] = nameof(Ticket.CategoryId),
        ["subcategoryId"] = nameof(Ticket.SubcategoryId), ["issueTypeId"] = nameof(Ticket.IssueTypeId), ["slaState"] = nameof(Ticket.SlaState),
        ["isEscalated"] = nameof(Ticket.IsEscalated), ["entitlementStatus"] = nameof(Ticket.EntitlementStatus), ["sentiment"] = nameof(Ticket.Sentiment),
        ["brandId"] = nameof(Ticket.BrandId), ["subject"] = nameof(Ticket.Subject), ["createdAt"] = nameof(Ticket.CreatedAt),
        ["dueAt"] = nameof(Ticket.DueAt), ["lastActivityAt"] = nameof(Ticket.LastActivityAt), ["resolvedAt"] = nameof(Ticket.ResolvedAt),
        ["reopenCount"] = nameof(Ticket.ReopenCount), ["csatScore"] = nameof(Ticket.CsatScore), ["language"] = nameof(Ticket.Language),
        ["parentTicketId"] = nameof(Ticket.ParentTicketId), ["locationId"] = nameof(Ticket.LocationId)
    };

    public static Expression<Func<Ticket, bool>> Translate(ConditionGroup group, DateTime now)
    {
        var body = Group(group, now);
        return Expression.Lambda<Func<Ticket, bool>>(body, T);
    }

    private static Expression Group(ConditionGroup g, DateTime now)
    {
        var parts = g.Conditions.Select(c => One(c, now)).Concat(g.Groups.Where(x => !x.IsEmpty).Select(x => Group(x, now))).ToList();
        if (parts.Count == 0) return Expression.Constant(true);
        var isOr = string.Equals(g.Logic, "or", StringComparison.OrdinalIgnoreCase);
        return parts.Aggregate((a, b) => isOr ? Expression.OrElse(a, b) : Expression.AndAlso(a, b));
    }

    private static Expression One(Condition c, DateTime now)
    {
        if (c.Field.Equals("tags", StringComparison.OrdinalIgnoreCase)) return Tags(c);
        if (!Fields.TryGetValue(c.Field, out var prop)) throw new DomainException("validation_failed", $"Field '{c.Field}' cannot be used in ticket filters.");
        var member = Expression.Property(T, prop);
        var type = member.Type;
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var values = ConditionEvaluator.ExpectedList(c.Value);

        Expression Const(string raw) => Expression.Constant(Parse(raw, underlying, now), type);

        switch (c.Op)
        {
            case ConditionOps.Equals or ConditionOps.In:
                return values.Select(v => (Expression)EqualsExpr(member, Const(v), underlying)).Aggregate(Expression.OrElse);
            case ConditionOps.NotEquals or ConditionOps.NotIn:
                return Expression.Not(values.Select(v => (Expression)EqualsExpr(member, Const(v), underlying)).Aggregate(Expression.OrElse));
            case ConditionOps.IsEmpty:
                return type == typeof(string)
                    ? Expression.Call(typeof(string), nameof(string.IsNullOrEmpty), null, member)
                    : Nullable.GetUnderlyingType(type) is not null ? Expression.Equal(member, Expression.Constant(null, type)) : Expression.Constant(false);
            case ConditionOps.IsNotEmpty:
                return type == typeof(string)
                    ? Expression.Not(Expression.Call(typeof(string), nameof(string.IsNullOrEmpty), null, member))
                    : Nullable.GetUnderlyingType(type) is not null ? Expression.NotEqual(member, Expression.Constant(null, type)) : Expression.Constant(true);
            case ConditionOps.Contains when type == typeof(string):
                return Expression.Call(member, typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!, Expression.Constant(values[0]));
            case ConditionOps.NotContains when type == typeof(string):
                return Expression.Not(Expression.Call(member, typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!, Expression.Constant(values[0])));
            case ConditionOps.StartsWith when type == typeof(string):
                return Expression.Call(member, typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!, Expression.Constant(values[0]));
            case ConditionOps.GreaterThan: return Compare(member, Const(values[0]), ExpressionType.GreaterThan);
            case ConditionOps.GreaterOrEqual: return Compare(member, Const(values[0]), ExpressionType.GreaterThanOrEqual);
            case ConditionOps.LessThan: return Compare(member, Const(values[0]), ExpressionType.LessThan);
            case ConditionOps.LessOrEqual: return Compare(member, Const(values[0]), ExpressionType.LessThanOrEqual);
            case ConditionOps.Before: return Compare(member, Const(values[0]), ExpressionType.LessThan);
            case ConditionOps.After: return Compare(member, Const(values[0]), ExpressionType.GreaterThan);
            case ConditionOps.Between when values.Count == 2:
                return Expression.AndAlso(Compare(member, Const(values[0]), ExpressionType.GreaterThanOrEqual), Compare(member, Const(values[1]), ExpressionType.LessThanOrEqual));
            case ConditionOps.WithinLastMinutes when underlying == typeof(DateTime):
                return Compare(member, Expression.Constant(now.AddMinutes(-double.Parse(values[0])), type), ExpressionType.GreaterThanOrEqual);
            case ConditionOps.OlderThanMinutes when underlying == typeof(DateTime):
                return Compare(member, Expression.Constant(now.AddMinutes(-double.Parse(values[0])), type), ExpressionType.LessThan);
            default:
                throw new DomainException("validation_failed", $"Operator '{c.Op}' is not supported for '{c.Field}' in ticket filters.");
        }
    }

    private static Expression EqualsExpr(Expression member, Expression value, Type underlying) => Expression.Equal(member, value);

    private static Expression Compare(Expression member, Expression value, ExpressionType op) => Expression.MakeBinary(op, member, value);

    private static Expression Tags(Condition c)
    {
        // t => t.Tags.Any(tt => names.Contains(tt.Tag.Name))
        var names = ConditionEvaluator.ExpectedList(c.Value).ToList();
        Expression<Func<Ticket, bool>> any = t => t.Tags.Any(tt => names.Contains(tt.Tag!.Name));
        var invoked = new ReplaceParameter(any.Parameters[0], T).Visit(any.Body)!;
        return c.Op is ConditionOps.NotContains or ConditionOps.NotIn or ConditionOps.NotEquals ? Expression.Not(invoked) : invoked;
    }

    private static object? Parse(string raw, Type type, DateTime now)
    {
        if (raw.Length == 0 || raw.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (type == typeof(Guid)) return Guid.Parse(raw);
        if (type.IsEnum) return Enum.Parse(type, raw, true);
        if (type == typeof(DateTime)) return raw.Equals("now", StringComparison.OrdinalIgnoreCase) ? now
            : DateTime.Parse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        if (type == typeof(bool)) return bool.Parse(raw);
        if (type == typeof(int)) return int.Parse(raw);
        return raw;
    }

    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
