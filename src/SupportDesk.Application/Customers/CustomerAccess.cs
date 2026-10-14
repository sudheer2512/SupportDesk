using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Identity;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Customers;

/// <summary>
/// Customer data scope. Agents whose roles grant <see cref="CustomerScope.AccountManaged"/> only see the customers they
/// manage as account manager — and everything that hangs off them (contacts, locations, installations, contracts…).
/// System processes and integration clients see all customers. Out-of-scope records surface as 404.
/// </summary>
public static class CustomerAccess
{
    public static bool IsRestricted(RequestContext ctx) => ctx.IsAgent && ctx.CustomerScope == CustomerScope.AccountManaged;

    public static IQueryable<Customer> VisibleTo(this IQueryable<Customer> query, RequestContext ctx)
    {
        if (!IsRestricted(ctx)) return query;
        var uid = ctx.UserId ?? Guid.Empty;
        return query.Where(c => c.AccountManagerUserId == uid);
    }

    /// <summary>Ids of visible customers as a sub-query (for filtering child entities).</summary>
    public static IQueryable<Guid> VisibleCustomerIds(DeskDbContext db, RequestContext ctx) => db.Customers.VisibleTo(ctx).Select(c => c.Id);

    public static IQueryable<Contact> VisibleTo(this IQueryable<Contact> query, RequestContext ctx, DeskDbContext db)
    {
        if (!IsRestricted(ctx)) return query;
        var ids = VisibleCustomerIds(db, ctx);
        return query.Where(k => k.CustomerId != null && ids.Contains(k.CustomerId.Value));
    }

    public static async Task<Customer> LoadVisibleAsync(DeskDbContext db, RequestContext ctx, Guid id, CancellationToken ct, bool track = true)
    {
        var query = track ? db.Customers : db.Customers.AsNoTracking();
        return await query.VisibleTo(ctx).FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Customer", id);
    }

    public static async Task EnsureVisibleAsync(DeskDbContext db, RequestContext ctx, Guid customerId, CancellationToken ct)
    {
        if (!await db.Customers.VisibleTo(ctx).AnyAsync(c => c.Id == customerId, ct)) throw new NotFoundException("Customer", customerId);
    }

    public static async Task<Contact> LoadVisibleContactAsync(DeskDbContext db, RequestContext ctx, Guid id, CancellationToken ct, bool includeMerged = false)
    {
        var contact = await db.Contacts.VisibleTo(ctx, db).FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Contact", id);
        if (!includeMerged && contact.Status == ContactStatus.Erased) throw new NotFoundException("Contact", id);
        return contact;
    }
}

/// <summary>Small parsing helpers shared by the customer, product and contract services.</summary>
internal static class Parse
{
    public static List<string> List(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static List<T> Enums<T>(string? csv) where T : struct, System.Enum =>
        List(csv).Select(v => System.Enum.TryParse<T>(v, true, out var e) ? (T?)e : null).Where(e => e is not null).Select(e => e!.Value).ToList();

    public static T Enum<T>(string? value, T fallback, string field) where T : struct, System.Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (System.Enum.TryParse<T>(value, true, out var result) && System.Enum.IsDefined(result)) return result;
        throw new Tickets.ValidationException(new Dictionary<string, string> { [field] = $"Unknown value '{value}'." });
    }

    public static string? Clean(string? value, int max = 450)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length > max ? v[..max] : v;
    }

    public static bool Asc(string? dir, bool defaultAsc) =>
        dir is null ? defaultAsc : string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);

    public static DateTime Iso(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static string Iso(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static string RowVersion(byte[] rv) => Convert.ToBase64String(rv);

    public static byte[] RowVersion(string? value, string entity)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new Tickets.ValidationException(new Dictionary<string, string> { ["rowVersion"] = $"The {entity} version is required for updates." });
        try { return Convert.FromBase64String(value); }
        catch (FormatException) { throw new Tickets.ValidationException(new Dictionary<string, string> { ["rowVersion"] = "Invalid version." }); }
    }
}
