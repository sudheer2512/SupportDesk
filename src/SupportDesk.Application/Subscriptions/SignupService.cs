using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tenancy;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Subscriptions;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Subscriptions;

/// <summary>
/// Public self-service trial sign-up: validates the workspace identifier (availability + reserved words), throttles per IP
/// and e-mail, ignores honeypot submissions, e-mails a 6-digit verification code (shown on screen only in development and
/// test environments, like the MFA demo code), and on verification provisions an isolated tenant with the default
/// configuration through <see cref="TenantProvisioningService"/>.
/// </summary>
public sealed class SignupService(
    DeskDbContext db,
    RequestContext ctx,
    PasswordService passwords,
    ISystemMailer mailer,
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    IHostEnvironment env)
{
    public const int CodeMinutes = 30;
    public const int MaxAttempts = 5;
    public const int MaxPerIpPerHour = 5;
    public const int MaxPerEmailPerHour = 3;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "www", "api", "app", "admin", "administrator", "platform", "portal", "signup", "sign-up", "login", "logout", "support", "help", "status",
        "docs", "hubs", "static", "assets", "mail", "smtp", "billing", "supportdesk", "system", "root", "test", "demo", "security", "oauth", "well-known"
    };

    private bool ShowCode => configuration.GetValue<bool?>("Signup:ShowVerificationCode") ?? (env.IsDevelopment() || env.IsEnvironment("Testing"));

    public async Task<SlugAvailabilityDto> CheckSlugAsync(string? requested, CancellationToken ct = default)
    {
        var slug = TextNormalization.Slugify(requested ?? "", 40);
        var reason = await UnavailableReasonAsync(slug, null, ct);
        string? suggestion = null;
        if (reason is not null && slug.Length >= 3)
        {
            for (var i = 2; i < 50 && suggestion is null; i++)
            {
                var candidate = TextNormalization.Slugify($"{slug}-{i}", 40);
                if (await UnavailableReasonAsync(candidate, null, ct) is null) suggestion = candidate;
            }
        }
        return new SlugAvailabilityDto(slug, reason is null, reason, suggestion);
    }

    public async Task<List<PlanDto>> PlansAsync(CancellationToken ct = default) =>
        (await db.Plans.AsNoTracking().Where(p => p.IsPublic).OrderBy(p => p.SortOrder).ToListAsync(ct)).Select(p => PlanCatalog.ToDto(p)).ToList();

    public async Task<SignupStartedDto> StartAsync(SignupRequest r, CancellationToken ct = default)
    {
        var now = db.Clock.UtcNow;
        // Honeypot: bots fill every field. Pretend success without storing or sending anything.
        if (!string.IsNullOrWhiteSpace(r.Website))
            return new SignupStartedDto(Guid.NewGuid(), r.AdminEmail ?? "", now.AddMinutes(CodeMinutes), null);

        var errors = new Dictionary<string, string>();
        var company = r.Company?.Trim() ?? "";
        var name = r.AdminName?.Trim() ?? "";
        var email = TextNormalization.NormalizeEmail(r.AdminEmail);
        var slug = TextNormalization.Slugify(r.Slug ?? "", 40);
        if (company.Length is < 2 or > 120) errors["company"] = "Enter your company name (2–120 characters).";
        if (name.Length is < 2 or > 120) errors["adminName"] = "Enter your name (2–120 characters).";
        if (email is null || email.Length > 254) errors["adminEmail"] = "Enter a valid work e-mail address.";
        var passwordErrors = passwords.Validate(r.Password ?? "", new PasswordPolicy());
        if (passwordErrors.Count > 0) errors["password"] = string.Join(" ", passwordErrors);
        if (await UnavailableReasonAsync(slug, email, ct) is { } slugReason) errors["slug"] = slugReason;
        var planCode = string.IsNullOrWhiteSpace(r.PlanCode) ? "trial" : r.PlanCode.Trim().ToLowerInvariant();
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Code == planCode && p.IsPublic, ct);
        if (plan is null) errors["planCode"] = "Choose one of the available plans.";
        var timeZone = string.IsNullOrWhiteSpace(r.TimeZone) ? "UTC" : r.TimeZone.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _)) timeZone = "UTC";
        if (errors.Count > 0) throw new ValidationException(errors);

        var hourAgo = now.AddHours(-1);
        var ip = ctx.IpAddress;
        if (ip is not null && await db.Set<TrialSignup>().CountAsync(s => s.IpAddress == ip && s.CreatedAt > hourAgo, ct) >= MaxPerIpPerHour)
            throw new DomainException("rate_limited", "Too many sign-ups from this network. Try again in an hour.", 429);
        if (await db.Set<TrialSignup>().CountAsync(s => s.AdminEmailNormalized == email && s.CreatedAt > hourAgo, ct) >= MaxPerEmailPerHour)
            throw new DomainException("rate_limited", "Too many sign-up attempts for this e-mail address. Try again in an hour.", 429);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var signup = new TrialSignup
        {
            CompanyName = company, Slug = slug, AdminName = name, AdminEmail = r.AdminEmail!.Trim(), AdminEmailNormalized = email!,
            PasswordHash = passwords.Hash(r.Password!), PlanCode = plan!.Code, CodeHash = TokenHasher.Hash(code), ExpiresAt = now.AddMinutes(CodeMinutes),
            IpAddress = ip, Status = SignupStatus.Pending
        };
        db.Set<TrialSignup>().Add(signup);
        await db.SaveChangesAsync(ct);
        await mailer.SendAsync(null, signup.AdminEmail, "Verify your SupportDesk workspace",
            $"<p>Hi {WebUtility.HtmlEncode(name)},</p><p>Your verification code for <strong>{WebUtility.HtmlEncode(company)}</strong> ({slug}) is " +
            $"<strong style=\"font-size:20px;letter-spacing:3px\">{code}</strong>.</p><p>It expires in {CodeMinutes} minutes. If you did not sign up, ignore this e-mail.</p>",
            $"Your SupportDesk verification code is {code}. It expires in {CodeMinutes} minutes.", null, ct);
        return new SignupStartedDto(signup.Id, signup.AdminEmail, signup.ExpiresAt, ShowCode ? code : null);
    }

    public async Task<SignupCompletedDto> VerifyAsync(Guid signupId, SignupVerifyRequest r, CancellationToken ct = default)
    {
        var now = db.Clock.UtcNow;
        var signup = await db.Set<TrialSignup>().FirstOrDefaultAsync(s => s.Id == signupId, ct);
        if (signup is null || signup.Status != SignupStatus.Pending)
            throw new DomainException("signup_invalid", "This sign-up is no longer valid. Start again.", 410);
        if (signup.ExpiresAt < now)
        {
            signup.Status = SignupStatus.Expired;
            await db.SaveChangesAsync(ct);
            throw new DomainException("signup_expired", "The verification code has expired. Start again.", 410);
        }
        signup.Attempts++;
        var supplied = new string((r.Code ?? "").Where(char.IsDigit).ToArray());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(TokenHasher.Hash(supplied)), Encoding.ASCII.GetBytes(signup.CodeHash)))
        {
            if (signup.Attempts >= MaxAttempts) signup.Status = SignupStatus.Locked;
            await db.SaveChangesAsync(ct);
            throw new DomainException("invalid_code", signup.Status == SignupStatus.Locked
                ? "Too many incorrect codes. Start the sign-up again."
                : "That code is not correct.", signup.Status == SignupStatus.Locked ? 410 : 422);
        }
        if (await UnavailableReasonAsync(signup.Slug, signup.AdminEmailNormalized, ct, signup.Id) is { } reason)
            throw new ConflictException("slug_taken", reason);

        ProvisionedTenant provisioned;
        DateTime? trialEnds;
        string planName;
        using (var scope = scopes.CreateScope())
        {
            var sp = scope.ServiceProvider;
            provisioned = await sp.GetRequiredService<TenantProvisioningService>().ProvisionAsync(new ProvisionTenantRequest(
                signup.Slug, signup.CompanyName, signup.PlanCode, signup.AdminEmail, signup.AdminName, Guid.NewGuid().ToString("N") + "Aa1!",
                Trial: true, Seats: 3, DemoMode: ShowCode), ct);
            var sdb = sp.GetRequiredService<DeskDbContext>();
            var admin = await sdb.Users.FirstAsync(u => u.Id == provisioned.AdminUserId, ct);
            admin.PasswordHash = signup.PasswordHash;
            var sub = await sdb.Subscriptions.OrderByDescending(s => s.StartedAt).FirstAsync(ct);
            trialEnds = sub.TrialEndsAt;
            sub.BillingContactName = signup.AdminName;
            sub.BillingContactEmail = signup.AdminEmail;
            planName = (await sdb.Plans.FirstAsync(p => p.Id == sub.PlanId, ct)).Name;
            sp.GetRequiredService<IAuditWriter>().Write("tenant.signed_up", "Tenant", provisioned.TenantId,
                $"Workspace created by self-service sign-up ({signup.AdminEmail})", new { signup.Slug, signup.PlanCode, signup.IpAddress });
            await sdb.SaveChangesAsync(ct);
        }
        signup.Status = SignupStatus.Completed;
        signup.CompletedAt = now;
        signup.TenantId = provisioned.TenantId;
        signup.CodeHash = "";
        signup.PasswordHash = "";
        CatalogAudit.Write(db, null, ActorType.System, null, "Self-service sign-up", "tenant.signed_up", "Tenant", provisioned.TenantId,
            $"Trial workspace '{signup.Slug}' created by sign-up ({signup.AdminEmail})", new { signup.Slug, signup.PlanCode }, now, ctx.IpAddress, ctx.CorrelationId);
        await db.SaveChangesAsync(ct);
        await mailer.SendAsync(provisioned.TenantId, signup.AdminEmail, "Your SupportDesk workspace is ready",
            $"<p>Welcome to SupportDesk!</p><p>Sign in with workspace <strong>{signup.Slug}</strong> and your e-mail address. Your trial runs until " +
            $"{trialEnds:dd MMM yyyy}.</p>", null, null, ct);
        return new SignupCompletedDto(provisioned.TenantId, signup.Slug, signup.AdminEmail, planName, trialEnds);
    }

    private async Task<string?> UnavailableReasonAsync(string slug, string? email, CancellationToken ct, Guid? exceptSignup = null)
    {
        if (slug.Length < 3) return "Use at least 3 letters or digits.";
        if (Reserved.Contains(slug)) return "This name is reserved.";
        if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct) || await db.AllTenants<Brand>().AnyAsync(b => b.Slug == slug, ct))
            return "This workspace name is already taken.";
        var now = db.Clock.UtcNow;
        var pendingByOther = await db.Set<TrialSignup>().AnyAsync(s => s.Slug == slug && s.Status == SignupStatus.Pending && s.ExpiresAt > now
                                                                     && s.Id != exceptSignup && (email == null || s.AdminEmailNormalized != email), ct);
        return pendingByOther ? "This workspace name is being registered right now. Choose another." : null;
    }
}
