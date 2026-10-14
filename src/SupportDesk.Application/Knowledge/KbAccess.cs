using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Who is reading the knowledge base. Customers carry the attributes that Restricted-article rules match on
/// (customer, customer group, tier, owned products).
/// </summary>
public sealed record KbReader(
    KnowledgeAudience Audience,
    Guid? BrandId,
    Guid? ContactId,
    Guid? CustomerId,
    Guid? CustomerGroupId,
    string? CustomerTier,
    IReadOnlyList<Guid> ProductIds)
{
    public static KbReader Anonymous(Guid? brandId) => new(KnowledgeAudience.Public, brandId, null, null, null, null, []);
    public static readonly KbReader Agent = new(KnowledgeAudience.Agent, null, null, null, null, null, []);
    public bool IsCustomer => Audience != KnowledgeAudience.Agent;
}

/// <summary>
/// The single source of truth for "may this reader see this article/category". Customer audiences only ever see
/// live (published, not archived/expired) articles whose visibility, brand, category and access rules allow it.
/// </summary>
public sealed class KbAccess(DeskDbContext db, RequestContext ctx)
{
    public static readonly string[] EditorialPermissions = [Permissions.KbAuthor, Permissions.KbReview, Permissions.KbPublish];

    /// <summary>Agents need some knowledge permission to use the desk knowledge base.</summary>
    public void RequireAgentReader()
    {
        ctx.RequireAgent();
        ctx.RequireAny(Permissions.KbViewInternal, Permissions.KbAuthor, Permissions.KbReview, Permissions.KbPublish, Permissions.AdminKnowledge);
    }

    public bool IsEditor => EditorialPermissions.Any(ctx.Has);

    /// <summary>Agent-side visibility: drafts need an editorial permission; internal articles need kb.view_internal (or editorial).</summary>
    public bool AgentCanRead(Article a)
    {
        var live = IsLive(a, db.Clock.UtcNow);
        if (!live && !IsEditor) return false;
        if (a.Visibility == KbVisibility.Internal && !ctx.Has(Permissions.KbViewInternal) && !IsEditor) return false;
        return true;
    }

    /// <summary>Agent-side list filter equivalent of <see cref="AgentCanRead"/>.</summary>
    public IQueryable<Article> AgentReadable(IQueryable<Article> q)
    {
        var now = db.Clock.UtcNow;
        if (!IsEditor) q = q.Where(a => a.PublishedVersionId != null && (a.ExpiresAt == null || a.ExpiresAt > now));
        if (!IsEditor && !ctx.Has(Permissions.KbViewInternal)) q = q.Where(a => a.Visibility != KbVisibility.Internal);
        return q;
    }

    public static bool IsLive(Article a, DateTime now) =>
        a.PublishedVersionId is not null && a.Status is not (ArticleStatus.Archived or ArticleStatus.Expired) && (a.ExpiresAt is null || a.ExpiresAt > now);

    /// <summary>Builds the reader for a portal contact (customer attributes and owned products).</summary>
    public async Task<KbReader> ForContactAsync(Guid contactId, Guid? brandId, CancellationToken ct = default)
    {
        var customerId = await db.Contacts.AsNoTracking().Where(c => c.Id == contactId).Select(c => c.CustomerId).FirstOrDefaultAsync(ct);
        var reader = await ForCustomerAsync(customerId, brandId, ct);
        return reader with { ContactId = contactId };
    }

    public async Task<KbReader> ForCustomerAsync(Guid? customerId, Guid? brandId, CancellationToken ct = default)
    {
        if (customerId is not { } cid) return new KbReader(KnowledgeAudience.AuthenticatedCustomer, brandId, null, null, null, null, []);
        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == cid).Select(c => new { c.CustomerGroupId, c.Tier }).FirstOrDefaultAsync(ct);
        var installed = await db.Installations.AsNoTracking().Where(i => i.CustomerId == cid && i.Status == InstallationStatus.Active).Select(i => i.ProductId).ToListAsync(ct);
        var entitled = await db.Entitlements.AsNoTracking().Where(e => e.CustomerId == cid && e.IsActive && e.ProductId != null).Select(e => e.ProductId!.Value).ToListAsync(ct);
        return new KbReader(KnowledgeAudience.AuthenticatedCustomer, brandId, null, cid, customer?.CustomerGroupId, customer?.Tier,
            installed.Concat(entitled).Distinct().ToList());
    }

    /// <summary>Live articles the reader may open (customer audiences) or all live articles (agent audience).</summary>
    public IQueryable<Article> Readable(KbReader r)
    {
        var now = db.Clock.UtcNow;
        var q = db.Articles.AsNoTracking().Where(a => a.PublishedVersionId != null && a.Status != ArticleStatus.Archived && a.Status != ArticleStatus.Expired
                                                      && (a.ExpiresAt == null || a.ExpiresAt > now));
        switch (r.Audience)
        {
            case KnowledgeAudience.Public:
                q = q.Where(a => a.Visibility == KbVisibility.Public);
                break;
            case KnowledgeAudience.AuthenticatedCustomer:
            {
                var cust = r.CustomerId;
                var group = r.CustomerGroupId;
                var tier = r.CustomerTier;
                var products = r.ProductIds.ToList();
                q = q.Where(a => a.Visibility == KbVisibility.Public || a.Visibility == KbVisibility.Authenticated ||
                                 (a.Visibility == KbVisibility.Restricted && cust != null && db.ArticleAccessRules.Any(x => x.ArticleId == a.Id
                                     && (x.CustomerId == null || x.CustomerId == cust)
                                     && (x.CustomerGroupId == null || x.CustomerGroupId == group)
                                     && (x.CustomerTier == null || x.CustomerTier == tier)
                                     && (x.ProductId == null || products.Contains(x.ProductId.Value))
                                     && (x.CustomerId != null || x.CustomerGroupId != null || x.CustomerTier != null || x.ProductId != null))));
                break;
            }
        }
        if (r.BrandId is { } brand)
        {
            var b = brand.ToString();
            q = q.Where(a => a.BrandIdsJson == "[]" || a.BrandIdsJson.Contains(b));
        }
        if (r.IsCustomer)
        {
            var anonymous = r.Audience == KnowledgeAudience.Public;
            q = q.Where(a => a.CategoryId == null || db.KbCategories.Any(c => c.Id == a.CategoryId && c.Visibility != KbVisibility.Internal
                                                                              && (!anonymous || c.Visibility == KbVisibility.Public)));
        }
        return q;
    }

    /// <summary>Categories shown to the reader (portal navigation).</summary>
    public IQueryable<KbCategory> ReadableCategories(KbReader r)
    {
        var q = db.KbCategories.AsNoTracking();
        if (r.IsCustomer)
        {
            q = q.Where(c => c.Visibility != KbVisibility.Internal);
            if (r.Audience == KnowledgeAudience.Public) q = q.Where(c => c.Visibility == KbVisibility.Public);
        }
        if (r.BrandId is { } brand)
        {
            var b = brand.ToString();
            q = q.Where(c => c.BrandIdsJson == "[]" || c.BrandIdsJson.Contains(b));
        }
        return q;
    }

    /// <summary>Visibility check of one article for a reader (portal article page, attachments, feedback).</summary>
    public Task<bool> CanReadAsync(Guid articleId, KbReader r, CancellationToken ct = default) =>
        Readable(r).AnyAsync(a => a.Id == articleId, ct);
}
