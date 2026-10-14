using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Administration;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration;

/// <summary>
/// Brands &amp; portals (admin.organisation): name, globally unique slug, logo/favicon (validated, scanned uploads served
/// from /api/v1/brand-assets), colours, custom domain with DNS instructions and a resolution check, locales, portal
/// options and the e-mail footer. The portal keeps reading the same Brand record (PortalConfigDto is unchanged).
/// </summary>
public sealed partial class BrandAdminService(
    DeskDbContext db,
    RequestContext ctx,
    IAuditWriter audit,
    HtmlSanitization html,
    FileService files,
    LimitGuard limits,
    IConfiguration config,
    AdminChangeNotifier changes)
{
    public const string AssetPurpose = "brand-asset";
    public const string AssetRoute = "/api/v1/brand-assets/";
    public const long MaxAssetBytes = 2 * 1024 * 1024;
    private static readonly string[] ReservedSlugs = ["admin", "api", "portal", "assets", "widget", "hubs", "login", "static", "docs", "openapi"];

    [GeneratedRegex("^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex Locale();

    public async Task<List<AdminBrandListItemDto>> ListAsync(string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminOrganisation);
        var brands = await db.Brands.AsNoTracking().OrderByDescending(b => b.IsDefault).ThenBy(b => b.Name).ToListAsync(ct);
        return brands.Select(b => new AdminBrandListItemDto(b.Id, b.Name, b.Slug, b.CustomDomain, b.LogoUrl, b.PrimaryColor, b.AccentColor, b.IsDefault,
            b.PortalEnabled, PortalUrl(baseUrl, b))).ToList();
    }

    public async Task<AdminBrandDto> GetAsync(Guid id, string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminOrganisation);
        var b = await db.Brands.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Brand", id);
        return Map(b, baseUrl);
    }

    public async Task<AdminBrandDto> CreateAsync(SaveBrandRequest r, string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminOrganisation);
        var count = await db.Brands.CountAsync(ct);
        var plan = await limits.GetLimitsAsync(ct);
        if (count >= 1) await limits.RequireFeatureAsync(PlanFeatures.MultiBrand, ct);
        if (plan.MaxBrands is { } max && count >= max)
            throw new LimitExceededException("brands", $"Your plan ({plan.PlanName}) allows {max} brand(s). Upgrade the plan or remove a brand.");
        var b = new Brand { IsDefault = count == 0 };
        await ApplyAsync(b, r, ct);
        db.Brands.Add(b);
        audit.Write(AdminAuditActions.BrandChanged, "Brand", b.Id, $"Brand '{b.Name}' created ({b.Slug})", new { b.Slug, b.CustomDomain });
        changes.Changed("brands", b.Id);
        await db.SaveChangesAsync(ct);
        return Map(b, baseUrl);
    }

    public async Task<AdminBrandDto> UpdateAsync(Guid id, SaveBrandRequest r, string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminOrganisation);
        var b = await db.Brands.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Brand", id);
        AdminRules.ApplyRowVersion(db, b, r.RowVersion, "brand");
        var before = Snapshot(b);
        await ApplyAsync(b, r, ct);
        if (b.IsDefault && !b.PortalEnabled) throw new ValidationException(new Dictionary<string, string> { ["portalEnabled"] = "The default brand's portal cannot be switched off." });
        var after = Snapshot(b);
        var changed = before.Where(kv => !Equals(after.GetValueOrDefault(kv.Key), kv.Value)).Select(kv => kv.Key).ToList();
        audit.Write(AdminAuditActions.BrandChanged, "Brand", b.Id, $"Brand '{b.Name}' updated",
            new { changed, from = changed.ToDictionary(k => k, k => before[k]), to = changed.ToDictionary(k => k, k => after.GetValueOrDefault(k)) });
        changes.Changed("brands", b.Id);
        await db.SaveChangesAsync(ct);
        return Map(b, baseUrl);
    }

    public async Task<AdminBrandDto> MakeDefaultAsync(Guid id, string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminOrganisation);
        var brands = await db.Brands.ToListAsync(ct);
        var b = brands.FirstOrDefault(x => x.Id == id) ?? throw new NotFoundException("Brand", id);
        if (!b.PortalEnabled) throw new ConflictException("brand_portal_disabled", "Enable the brand's portal before making it the default.");
        foreach (var other in brands) other.IsDefault = other.Id == id;
        audit.Write(AdminAuditActions.BrandChanged, "Brand", b.Id, $"Brand '{b.Name}' is now the default brand");
        changes.Changed("brands", b.Id);
        await db.SaveChangesAsync(ct);
        return Map(b, baseUrl);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminOrganisation);
        var b = await db.Brands.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Brand", id);
        if (b.IsDefault) throw new ConflictException("brand_default", "The default brand cannot be deleted. Make another brand the default first.");
        if (await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(t => t.BrandId == id, ct))
            throw new ConflictException("brand_in_use", $"Tickets belong to '{b.Name}'. Switch its portal off instead of deleting it.");
        if (await db.ChannelAccounts.AnyAsync(a => a.BrandId == id, ct))
            throw new ConflictException("brand_in_use", $"Channels are connected to '{b.Name}'. Move them to another brand first.");
        db.Brands.Remove(b);
        audit.Write(AdminAuditActions.BrandChanged, "Brand", b.Id, $"Brand '{b.Name}' deleted ({b.Slug})");
        changes.Changed("brands", b.Id);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            throw new ConflictException("brand_in_use", $"'{b.Name}' is still referenced (knowledge base, community or layouts). Switch its portal off instead.");
        }
    }

    /// <summary>Uploads a logo or favicon (PNG, JPEG, GIF or WebP; scanned) and points the brand at it.</summary>
    public async Task<AdminBrandDto> UploadAssetAsync(Guid id, string kind, Stream content, string fileName, string? contentType, string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminOrganisation);
        if (kind is not ("logo" or "favicon")) throw new NotFoundException("Brand asset", kind);
        var b = await db.Brands.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Brand", id);
        if (Path.GetExtension(fileName ?? "").ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"))
            throw new DomainException("file_type_blocked", "Upload a PNG, JPEG, GIF or WebP image.", 415);
        var stored = await files.UploadAsync(content, fileName!, contentType, AssetPurpose, ct);
        if (!stored.ContentType.StartsWith("image/", StringComparison.Ordinal) || !FileService.IsInlineSafe(stored.ContentType))
            throw new DomainException("file_type_blocked", "Upload a PNG, JPEG, GIF or WebP image.", 415);
        if (stored.SizeBytes > MaxAssetBytes) throw new DomainException("file_too_large", "Brand images may be at most 2 MB.", 413);
        var url = AssetRoute + stored.Id;
        if (kind == "logo") b.LogoUrl = url; else b.FaviconUrl = url;
        audit.Write(AdminAuditActions.BrandChanged, "Brand", b.Id, $"Brand '{b.Name}' {kind} uploaded", new { file = stored.FileName, stored.SizeBytes, stored.Sha256 });
        changes.Changed("brands", b.Id);
        await db.SaveChangesAsync(ct);
        return Map(b, baseUrl);
    }

    public async Task<AdminBrandDto> RemoveAssetAsync(Guid id, string kind, string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminOrganisation);
        if (kind is not ("logo" or "favicon")) throw new NotFoundException("Brand asset", kind);
        var b = await db.Brands.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Brand", id);
        if (kind == "logo") b.LogoUrl = null; else b.FaviconUrl = null;
        audit.Write(AdminAuditActions.BrandChanged, "Brand", b.Id, $"Brand '{b.Name}' {kind} removed");
        changes.Changed("brands", b.Id);
        await db.SaveChangesAsync(ct);
        return Map(b, baseUrl);
    }

    /// <summary>Resolves the custom domain and the portal host and reports whether they share an address (CNAME/A set up).</summary>
    public async Task<DomainCheckResultDto> CheckDomainAsync(Guid id, string baseUrl, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminOrganisation);
        var b = await db.Brands.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Brand", id);
        if (string.IsNullOrEmpty(b.CustomDomain)) throw new ConflictException("no_custom_domain", "Set a custom domain first.");
        var target = DnsTarget(baseUrl);
        var domainAddresses = await ResolveAsync(b.CustomDomain, ct);
        var targetAddresses = await ResolveAsync(target, ct);
        var resolves = domainAddresses.Count > 0;
        var points = resolves && domainAddresses.Intersect(targetAddresses).Any();
        var message = !resolves ? $"{b.CustomDomain} does not resolve yet. DNS changes can take up to 48 hours."
            : points ? $"{b.CustomDomain} points to the portal. Customers can use it once HTTPS is configured for the host."
            : $"{b.CustomDomain} resolves, but not to {target}. Check the CNAME record.";
        return new DomainCheckResultDto(b.CustomDomain, resolves, points, domainAddresses, targetAddresses, message);
    }

    private static async Task<List<string>> ResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            return addresses.Select(a => a.ToString()).Distinct().OrderBy(a => a).ToList();
        }
        catch (SocketException) { return []; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return []; }
        catch (ArgumentException) { return []; }
    }

    private async Task ApplyAsync(Brand b, SaveBrandRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 100) errors["name"] = "Enter a name of 2–100 characters.";
        var slug = (r.Slug ?? "").Trim().ToLowerInvariant();
        if (!AdminRules.Slug().IsMatch(slug) || slug.Length < 2) errors["slug"] = "Use 2–64 lower-case letters, digits and hyphens.";
        else if (ReservedSlugs.Contains(slug)) errors["slug"] = "This address is reserved.";
        else if (await db.AllTenants<Brand>().AnyAsync(x => x.Slug == slug && x.Id != b.Id, ct)) errors["slug"] = "This portal address is already taken.";
        var domain = AdminRules.Clean(r.CustomDomain, 253)?.ToLowerInvariant().TrimEnd('.');
        if (domain is not null)
        {
            if (!AdminRules.HostName().IsMatch(domain)) errors["customDomain"] = "Enter a host name such as support.example.com (no https:// or path).";
            else if (await db.AllTenants<Brand>().AnyAsync(x => x.CustomDomain == domain && x.Id != b.Id, ct)) errors["customDomain"] = "This domain is already used by another portal.";
        }
        if (!AdminRules.HexColor().IsMatch(r.PrimaryColor ?? "")) errors["primaryColor"] = "Use a hex colour such as #2563eb.";
        if (!AdminRules.HexColor().IsMatch(r.AccentColor ?? "")) errors["accentColor"] = "Use a hex colour such as #0ea5e9.";
        var email = AdminRules.Clean(r.SupportEmail, 320);
        if (email is not null && TextNormalization.NormalizeEmail(email) is null) errors["supportEmail"] = "Enter a valid e-mail address.";
        var title = (r.PortalTitle ?? "").Trim();
        if (title.Length is < 1 or > 100) errors["portalTitle"] = "Enter a portal title of up to 100 characters.";
        var welcome = AdminRules.Clean(r.PortalWelcome, 1000);
        if (r.DefaultDepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d, ct)) errors["defaultDepartmentId"] = "Unknown department.";
        var defaultLocale = (r.DefaultLocale ?? "").Trim();
        if (!Locale().IsMatch(defaultLocale)) errors["defaultLocale"] = "Use a language code such as en or en-GB.";
        var locales = (r.Locales ?? []).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (locales.Any(l => !Locale().IsMatch(l)) || locales.Count > 20) errors["locales"] = "Use up to 20 language codes such as en, fr or pt-BR.";
        if (!locales.Contains(defaultLocale, StringComparer.OrdinalIgnoreCase)) locales.Insert(0, defaultLocale);
        var authMode = AdminRules.ParseEnum(r.PortalAuthMode, "portalAuthMode", errors, PortalAuthMode.Local);
        if (r.AllowAnonymousTickets && !r.PortalEnabled) errors["allowAnonymousTickets"] = "Anonymous tickets need the portal.";
        if (errors.Count > 0) throw new ValidationException(errors);

        b.Name = name;
        b.Slug = slug;
        b.CustomDomain = domain;
        b.PrimaryColor = r.PrimaryColor!.ToLowerInvariant();
        b.AccentColor = r.AccentColor!.ToLowerInvariant();
        b.SupportEmail = email;
        b.SupportPhone = AdminRules.Clean(r.SupportPhone, 40);
        b.PortalTitle = title;
        b.PortalWelcome = welcome;
        b.FooterHtml = string.IsNullOrWhiteSpace(r.FooterHtml) ? null : html.Sanitize(r.FooterHtml);
        b.EmailFooterHtml = string.IsNullOrWhiteSpace(r.EmailFooterHtml) ? null : html.Sanitize(r.EmailFooterHtml);
        b.DefaultDepartmentId = r.DefaultDepartmentId;
        b.DefaultLocale = defaultLocale;
        b.SupportedLocalesJson = Json.Serialize(locales);
        b.PortalEnabled = r.PortalEnabled;
        b.KnowledgeBaseEnabled = r.KnowledgeBaseEnabled;
        b.CommunityEnabled = r.CommunityEnabled;
        b.ChatEnabled = r.ChatEnabled;
        b.AiAssistantEnabled = r.AiAssistantEnabled;
        b.AllowAnonymousTickets = r.AllowAnonymousTickets;
        b.AllowSelfRegistration = r.AllowSelfRegistration;
        b.PortalAuthMode = authMode;
    }

    private static Dictionary<string, object?> Snapshot(Brand b) => new()
    {
        ["name"] = b.Name, ["slug"] = b.Slug, ["customDomain"] = b.CustomDomain, ["primaryColor"] = b.PrimaryColor, ["accentColor"] = b.AccentColor,
        ["supportEmail"] = b.SupportEmail, ["supportPhone"] = b.SupportPhone, ["portalTitle"] = b.PortalTitle, ["portalWelcome"] = b.PortalWelcome,
        ["footerHtml"] = b.FooterHtml, ["emailFooterHtml"] = b.EmailFooterHtml, ["defaultDepartmentId"] = b.DefaultDepartmentId, ["defaultLocale"] = b.DefaultLocale,
        ["locales"] = b.SupportedLocalesJson, ["portalEnabled"] = b.PortalEnabled, ["knowledgeBaseEnabled"] = b.KnowledgeBaseEnabled,
        ["communityEnabled"] = b.CommunityEnabled, ["chatEnabled"] = b.ChatEnabled, ["aiAssistantEnabled"] = b.AiAssistantEnabled,
        ["allowAnonymousTickets"] = b.AllowAnonymousTickets, ["allowSelfRegistration"] = b.AllowSelfRegistration, ["portalAuthMode"] = b.PortalAuthMode.ToString()
    };

    /// <summary>Host customers point their CNAME at: Portal:CustomDomainTarget, else the public host of the application.</summary>
    private string DnsTarget(string baseUrl)
    {
        if (config["Portal:CustomDomainTarget"] is { Length: > 0 } t) return t.Trim().TrimEnd('.');
        var publicUrl = config["App:PublicUrl"] is { Length: > 0 } a ? a : config["Communication:PublicBaseUrl"] is { Length: > 0 } c ? c : baseUrl;
        return Uri.TryCreate(publicUrl, UriKind.Absolute, out var u) ? u.Host : "localhost";
    }

    private string PublicBase(string baseUrl) =>
        (config["App:PublicUrl"] is { Length: > 0 } a ? a : config["Communication:PublicBaseUrl"] is { Length: > 0 } c ? c : baseUrl).TrimEnd('/');

    private string PortalUrl(string baseUrl, Brand b) => $"{PublicBase(baseUrl)}/portal/{b.Slug}";

    private AdminBrandDto Map(Brand b, string baseUrl)
    {
        BrandDnsDto? dns = null;
        if (b.CustomDomain is { } domain)
        {
            var target = DnsTarget(baseUrl);
            dns = new BrandDnsDto("CNAME", domain, target,
                $"At your DNS provider, create a CNAME record for {domain} pointing to {target}. If {domain} is a root (apex) domain, use an ALIAS/ANAME record or the A records of {target} instead. Then configure an HTTPS certificate for {domain} on the portal host.");
        }
        return new AdminBrandDto(b.Id, b.Name, b.Slug, b.CustomDomain, b.LogoUrl, b.FaviconUrl, b.PrimaryColor, b.AccentColor, b.SupportEmail, b.SupportPhone,
            b.PortalTitle, b.PortalWelcome, b.FooterHtml, b.EmailFooterHtml, b.DefaultDepartmentId, b.DefaultLocale, Json.StringList(b.SupportedLocalesJson), b.IsDefault,
            b.PortalEnabled, b.KnowledgeBaseEnabled, b.CommunityEnabled, b.ChatEnabled, b.AiAssistantEnabled, b.AllowAnonymousTickets, b.AllowSelfRegistration,
            b.PortalAuthMode.ToString(), PortalUrl(baseUrl, b), dns, b.UpdatedAt, AdminRules.Version(b.RowVersion));
    }
}

/// <summary>
/// Serves brand logos/favicons publicly (unauthenticated; inline-safe images only). Only files a brand currently uses as
/// its logo or favicon are served — an upload merely labelled "brand-asset" by someone else never becomes public.
/// </summary>
public sealed class BrandAssetService(DeskDbContext db, IFileStorage storage)
{
    public async Task<(StoredFile File, Stream Content)> OpenAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.AllTenants<StoredFile>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId && f.Purpose == BrandAdminService.AssetPurpose, ct);
        if (file is null || file.ScanStatus is ScanStatus.Infected or ScanStatus.Blocked || !file.ContentType.StartsWith("image/", StringComparison.Ordinal)
            || !FileService.IsInlineSafe(file.ContentType))
            throw new NotFoundException("Image", fileId);
        var url = BrandAdminService.AssetRoute + fileId;
        if (!await db.AllTenants<Brand>().AnyAsync(b => b.TenantId == file.TenantId && (b.LogoUrl == url || b.FaviconUrl == url), ct))
            throw new NotFoundException("Image", fileId);
        return (file, await storage.OpenReadAsync(file.StorageKey, ct));
    }
}
