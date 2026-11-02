using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Administration;
using SupportDesk.Domain;
using SupportDesk.Domain.Administration;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration;

public sealed record NotificationChannelDefaults(bool InApp, bool Email, bool Push, bool Digest);

/// <summary>
/// Tenant notification defaults (setting "notifications.policy"): per-event channel matrix used when a user has no
/// preference of their own, plus default quiet hours and digest schedule for users without personal settings.
/// </summary>
public sealed record NotificationPolicySettings
{
    public Dictionary<string, NotificationChannelDefaults> Events { get; init; } = new();
    public string? QuietHoursStart { get; init; }
    public string? QuietHoursEnd { get; init; }
    public string TimeZone { get; init; } = "UTC";
    /// <summary>Hourly or Daily: how often digest e-mails go out for events delivered by digest.</summary>
    public string DigestFrequency { get; init; } = nameof(DigestMode.Daily);
    public string DailyDigestAt { get; init; } = "08:00";

    public static readonly IReadOnlyDictionary<string, (string Label, string Description)> EventLabels = new Dictionary<string, (string, string)>
    {
        [NotificationTypes.TicketAssigned] = ("Ticket assigned to me", "A ticket is assigned to the agent."),
        [NotificationTypes.TicketMentioned] = ("Mentioned in a note", "Someone @mentions the agent in a ticket."),
        [NotificationTypes.CustomerReplied] = ("Customer replied", "A customer replies on a ticket the agent owns or watches."),
        [NotificationTypes.TicketEscalated] = ("Ticket escalated", "A ticket the agent is involved in is escalated."),
        [NotificationTypes.SlaWarning] = ("SLA at risk", "An SLA target is about to be missed."),
        [NotificationTypes.SlaBreached] = ("SLA breached", "An SLA target was missed."),
        [NotificationTypes.ApprovalRequested] = ("Approval requested", "The agent is asked to approve something."),
        [NotificationTypes.ApprovalDecided] = ("Approval decided", "A request the agent raised was approved or rejected."),
        [NotificationTypes.FollowUpDue] = ("Follow-up due", "A scheduled follow-up is due."),
        [NotificationTypes.AiProposal] = ("AI proposal", "AI proposes an action that needs a decision."),
        [NotificationTypes.AnomalyDetected] = ("Anomaly detected", "Unusual ticket volume or sentiment is detected."),
        [NotificationTypes.ChatWaiting] = ("Chat waiting", "A visitor is waiting in live chat."),
        [NotificationTypes.ChatTransferred] = ("Chat transferred to me", "Another agent transfers a live chat to the agent."),
        [NotificationTypes.System] = ("System messages", "Administrative and system notices.")
    };

    /// <summary>Effective tenant default for an event type (built-in defaults when the tenant has not configured it).</summary>
    public (bool InApp, bool Email, bool Push, bool Teams, bool Digest) For(string type)
    {
        var builtIn = NotificationTypes.Defaults(type);
        return Events.TryGetValue(type, out var e) ? (e.InApp, e.Email, e.Push, builtIn.Teams, e.Digest) : (true, builtIn.Email, builtIn.Push, builtIn.Teams, false);
    }

    public DigestMode DigestMode => Enum.TryParse<DigestMode>(DigestFrequency, true, out var m) && m != DigestMode.None ? m : DigestMode.Daily;

    /// <summary>Quiet hours / digest time to use for a user who has not saved personal notification settings.</summary>
    public NotificationSettings AsUserDefaults(Guid userId) => new()
    {
        UserId = userId,
        QuietHoursStart = TimeOnly.TryParse(QuietHoursStart, out var qs) ? qs : null,
        QuietHoursEnd = TimeOnly.TryParse(QuietHoursEnd, out var qe) ? qe : null,
        TimeZone = TimeZone,
        DailyDigestAt = TimeOnly.TryParse(DailyDigestAt, out var dd) ? dd : new TimeOnly(8, 0)
    };

    /// <summary>Loads the tenant policy for the current request context (defaults when not configured).</summary>
    public static async Task<NotificationPolicySettings> LoadAsync(DeskDbContext db, CancellationToken ct)
    {
        var json = await db.TenantSettings.AsNoTracking().Where(s => s.Key == AdminSettingKeys.NotificationPolicy).Select(s => s.ValueJson).FirstOrDefaultAsync(ct);
        return Json.DeserializeOr(json, new NotificationPolicySettings());
    }
}

/// <summary>Built-in e-mail templates that tenants can override. Raw placeholders carry system-generated HTML and are inserted after sanitising.</summary>
public static class EmailTemplateCatalog
{
    public sealed record Placeholder(string Name, string Description, string Sample, bool Raw = false);

    public sealed record Definition(string Key, string Name, string Audience, string Description, string Subject, string BodyHtml, IReadOnlyList<Placeholder> Placeholders);

    public const string CustomerAcknowledgement = "customer.ticket_acknowledgement";
    public const string CustomerCsatSurvey = "customer.csat_survey";
    public const string AgentNotification = "agent.notification";
    public const string AgentDigest = "agent.digest";

    private static readonly Placeholder Brand = new("brand.name", "Brand (or mailbox) name", "Wings Support");
    private static readonly Placeholder TicketNumber = new("ticket.number", "Ticket reference with prefix", "#1042");
    private static readonly Placeholder TicketSubject = new("ticket.subject", "Ticket subject", "Invoice export fails for March");

    public static readonly IReadOnlyList<Definition> All =
    [
        new(CustomerAcknowledgement, "New ticket acknowledgement", "Customer",
            "Automatic reply when a customer's e-mail creates a ticket (if the mailbox has acknowledgements switched on).",
            "Re: {{ticket.subject}}",
            "<p>Hello {{contact.name}},</p><p>Thank you for contacting {{brand.name}}. We have received your request and logged it as <strong>{{ticket.number}}</strong>.</p><p>Our team will get back to you shortly. To add more information, simply reply to this e-mail and keep the reference in the subject.</p>",
            [new("contact.name", "Customer's name", "Ramesh Kumar"), Brand, TicketNumber, TicketSubject, new("portal.url", "Help center address", "https://help.example.com/portal/wings")]),
        new(CustomerCsatSurvey, "Satisfaction survey", "Customer",
            "Sent when a ticket is resolved (if satisfaction surveys are switched on).",
            "How did we do? {{ticket.subject}}",
            "<p>Hello,</p><p>Your request <strong>{{ticket.number}} — {{ticket.subject}}</strong> was resolved by {{agent.name}}.</p><p>How satisfied are you with the support you received?</p><p>{{survey.buttons}}</p><p>You can also leave a comment at <a href=\"{{survey.link}}\">{{survey.link}}</a>. This survey link expires on {{survey.expires}}.</p>",
            [TicketNumber, TicketSubject, new("agent.name", "Agent who resolved the ticket", "Arjun Mehta"), new("survey.link", "Survey address", "https://help.example.com/portal/wings/csat/abc123"),
                new("survey.buttons", "Rating buttons 1–5 (generated)", "<a href=\"#\">1 ★</a> <a href=\"#\">2 ★</a> <a href=\"#\">3 ★</a> <a href=\"#\">4 ★</a> <a href=\"#\">5 ★</a>", Raw: true),
                new("survey.expires", "Expiry date of the survey", "28 Oct 2026"), Brand]),
        new(AgentNotification, "Agent notification e-mail", "Agent",
            "Immediate e-mail for a notification (assignment, mention, SLA breach…) when the agent receives e-mail for that event.",
            "{{notification.title}}",
            "<p>{{notification.body}}</p><p><a href=\"{{notification.link}}\">Open in SupportDesk</a></p>",
            [new("user.name", "Agent's name", "Priya Sharma"), new("notification.title", "Notification title", "#1042 assigned to you"),
                new("notification.body", "Notification text", "Invoice export fails for March"), new("notification.link", "Link to the record", "https://desk.example.com/tickets/1042")]),
        new(AgentDigest, "Agent digest e-mail", "Agent",
            "Hourly or daily summary of notifications delivered by digest.",
            "Your SupportDesk digest ({{digest.count}})",
            "<p>Hello {{user.name}},</p><p>Here is your summary of {{digest.count}} notification(s):</p>{{digest.items}}",
            [new("user.name", "Agent's name", "Priya Sharma"), new("digest.count", "Number of items", "3"),
                new("digest.items", "List of notifications (generated)", "<ul><li><strong>#1042 assigned to you</strong> — Invoice export fails</li><li><strong>SLA at risk on #1038</strong></li></ul>", Raw: true)])
    ];

    public static Definition? Find(string key) => All.FirstOrDefault(d => d.Key == key);
}

/// <summary>Renders {{placeholder}} templates: values are HTML-encoded, output is sanitised, raw (system-generated) placeholders are inserted last.</summary>
public static partial class EmailTemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")]
    private static partial Regex Placeholder();

    public static IReadOnlyList<string> Unknown(string? template, IEnumerable<string> known)
    {
        var set = known.ToHashSet(StringComparer.Ordinal);
        return Placeholder().Matches(template ?? "").Select(m => m.Groups[1].Value).Where(n => !set.Contains(n)).Distinct().ToList();
    }

    public static string RenderSubject(string template, IReadOnlyDictionary<string, string?> values) =>
        TextNormalization.Truncate(Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? StripTags(v) : "").Replace('\r', ' ').Replace('\n', ' ').Trim(), 500) ?? "";

    public static string RenderBody(string template, IReadOnlyDictionary<string, string?> values, IReadOnlySet<string> rawKeys, HtmlSanitization html)
    {
        var markers = new Dictionary<string, string>();
        var withValues = Placeholder().Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            values.TryGetValue(name, out var v);
            if (rawKeys.Contains(name))
            {
                var marker = $"sdraw{markers.Count}x{Guid.NewGuid():N}";
                markers[marker] = v ?? "";
                return marker;
            }
            return WebUtility.HtmlEncode(v ?? "");
        });
        var safe = html.Sanitize(withValues);
        foreach (var (marker, raw) in markers) safe = safe.Replace(marker, raw, StringComparison.Ordinal);
        return safe;
    }

    private static string StripTags(string? v) => WebUtility.HtmlDecode(Regex.Replace(v ?? "", "<[^>]+>", ""));
}

/// <summary>Renders tenant e-mail templates (override or built-in default) for the sending modules, and the brand e-mail footer.</summary>
public sealed class EmailTemplateService(DeskDbContext db, HtmlSanitization html)
{
    public async Task<(string Subject, string BodyHtml)> RenderAsync(string key, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        var def = EmailTemplateCatalog.Find(key) ?? throw new InvalidOperationException($"Unknown e-mail template '{key}'.");
        var custom = await db.Set<NotificationTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.Key == key && t.IsActive, ct);
        return Render(def, custom?.Subject ?? def.Subject, custom?.BodyHtml ?? def.BodyHtml, values);
    }

    public (string Subject, string BodyHtml) Render(EmailTemplateCatalog.Definition def, string subject, string body, IReadOnlyDictionary<string, string?> values)
    {
        var raw = def.Placeholders.Where(p => p.Raw).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return (EmailTemplateRenderer.RenderSubject(subject, values), EmailTemplateRenderer.RenderBody(body, values, raw, html));
    }

    /// <summary>The brand's e-mail footer as a block to append, or empty.</summary>
    public async Task<string> BrandFooterAsync(Guid? brandId, CancellationToken ct)
    {
        var footer = brandId is { } b
            ? await db.Brands.AsNoTracking().Where(x => x.Id == b).Select(x => x.EmailFooterHtml).FirstOrDefaultAsync(ct)
            : await db.Brands.AsNoTracking().Where(x => x.IsDefault).Select(x => x.EmailFooterHtml).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(footer) ? "" : $"<div class=\"sd-brand-footer\">{html.Sanitize(footer)}</div>";
    }
}

/// <summary>Notification settings administration: tenant default channel matrix, quiet hours, digest frequency, and e-mail templates with preview.</summary>
public sealed class NotificationAdminService(
    DeskDbContext db,
    RequestContext ctx,
    IAuditWriter audit,
    AdminSettingsStore store,
    EmailTemplateService templates,
    AdminChangeNotifier changes)
{
    private void RequireAccess() => ctx.RequireAny(Permissions.AdminOrganisation, Permissions.AdminChannels);

    public async Task<NotificationPolicyDto> GetPolicyAsync(CancellationToken ct)
    {
        RequireAccess();
        var (p, version) = await store.GetAsync<NotificationPolicySettings>(AdminSettingKeys.NotificationPolicy, ct);
        var events = NotificationTypes.All.Select(t =>
        {
            var d = p.For(t);
            var (label, description) = NotificationPolicySettings.EventLabels.GetValueOrDefault(t, (t, ""));
            return new NotificationEventDefaultDto(t, label, description, d.InApp, d.Email, d.Push, d.Digest);
        }).ToList();
        return new NotificationPolicyDto(events, p.QuietHoursStart, p.QuietHoursEnd, p.TimeZone, p.DigestMode.ToString(), p.DailyDigestAt, version);
    }

    public async Task<NotificationPolicyDto> SavePolicyAsync(SaveNotificationPolicyRequest r, CancellationToken ct)
    {
        RequireAccess();
        var errors = new Dictionary<string, string>();
        var unknown = (r.Events ?? []).Select(e => e.EventType).Where(t => !NotificationTypes.All.Contains(t)).ToList();
        if (unknown.Count > 0) errors["events"] = $"Unknown events: {string.Join(", ", unknown)}.";
        string? T(string? v, string field)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            if (TimeOnly.TryParse(v, out var t)) return t.ToString("HH:mm");
            errors[field] = "Use a time such as 22:00.";
            return null;
        }
        var qs = T(r.QuietHoursStart, "quietHoursStart");
        var qe = T(r.QuietHoursEnd, "quietHoursEnd");
        if ((qs is null) != (qe is null) && !errors.ContainsKey("quietHoursStart") && !errors.ContainsKey("quietHoursEnd"))
            errors["quietHoursEnd"] = "Set both the start and the end of quiet hours, or neither.";
        if (qs is not null && qs == qe) errors["quietHoursEnd"] = "Quiet hours must end at a different time than they start.";
        var digestAt = T(r.DailyDigestAt, "dailyDigestAt") ?? "08:00";
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(r.TimeZone ?? "", out _)) errors["timeZone"] = "Unknown time zone.";
        if (!Enum.TryParse<DigestMode>(r.DigestFrequency, true, out var freq) || freq == DigestMode.None) errors["digestFrequency"] = "Choose Hourly or Daily.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var value = new NotificationPolicySettings
        {
            Events = (r.Events ?? []).ToDictionary(e => e.EventType, e => new NotificationChannelDefaults(e.InApp, e.Email, e.Push, e.Digest)),
            QuietHoursStart = qs, QuietHoursEnd = qe, TimeZone = r.TimeZone!, DigestFrequency = freq.ToString(), DailyDigestAt = digestAt
        };
        if (await store.StageAsync(AdminSettingKeys.NotificationPolicy, value, "Notification defaults changed", ct))
        {
            changes.Changed("notifications");
            await db.SaveChangesAsync(ct);
        }
        return await GetPolicyAsync(ct);
    }

    public async Task<List<NotificationTemplateDto>> ListTemplatesAsync(CancellationToken ct)
    {
        RequireAccess();
        var custom = await db.Set<NotificationTemplate>().AsNoTracking().ToDictionaryAsync(t => t.Key, ct);
        return EmailTemplateCatalog.All.Select(d => Map(d, custom.GetValueOrDefault(d.Key))).ToList();
    }

    public async Task<NotificationTemplateDto> GetTemplateAsync(string key, CancellationToken ct)
    {
        RequireAccess();
        var def = EmailTemplateCatalog.Find(key) ?? throw new NotFoundException("Template", key);
        return Map(def, await db.Set<NotificationTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.Key == key, ct));
    }

    public async Task<NotificationTemplateDto> SaveTemplateAsync(string key, SaveNotificationTemplateRequest r, CancellationToken ct)
    {
        RequireAccess();
        var def = EmailTemplateCatalog.Find(key) ?? throw new NotFoundException("Template", key);
        var errors = new Dictionary<string, string>();
        var subject = (r.Subject ?? "").Trim();
        if (subject.Length is < 1 or > 500) errors["subject"] = "Enter a subject of up to 500 characters.";
        if (string.IsNullOrWhiteSpace(r.BodyHtml) || r.BodyHtml.Length > 50_000) errors["bodyHtml"] = "Enter a body of up to 50,000 characters.";
        var known = def.Placeholders.Select(p => p.Name).ToList();
        var unknown = EmailTemplateRenderer.Unknown(subject, known).Concat(EmailTemplateRenderer.Unknown(r.BodyHtml, known)).Distinct().ToList();
        if (unknown.Count > 0) errors["bodyHtml"] = $"Unknown placeholders: {string.Join(", ", unknown.Select(u => "{{" + u + "}}"))}.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var row = await db.Set<NotificationTemplate>().FirstOrDefaultAsync(t => t.Key == key, ct);
        if (row is null)
        {
            row = new NotificationTemplate { Key = key };
            db.Set<NotificationTemplate>().Add(row);
        }
        else AdminRules.ApplyRowVersion(db, row, r.RowVersion, "template");
        var before = new { row.Subject, row.IsActive };
        row.Subject = subject;
        row.BodyHtml = r.BodyHtml!;
        row.IsActive = r.IsActive;
        audit.Write(AdminAuditActions.TemplateChanged, "NotificationTemplate", row.Id, $"E-mail template '{def.Name}' saved{(r.IsActive ? "" : " (inactive — default in use)")}",
            new { key, from = before, to = new { subject, r.IsActive } });
        changes.Changed("templates", row.Id);
        await db.SaveChangesAsync(ct);
        return Map(def, row);
    }

    /// <summary>Removes the override: the built-in default is used again.</summary>
    public async Task<NotificationTemplateDto> ResetTemplateAsync(string key, CancellationToken ct)
    {
        RequireAccess();
        var def = EmailTemplateCatalog.Find(key) ?? throw new NotFoundException("Template", key);
        var row = await db.Set<NotificationTemplate>().FirstOrDefaultAsync(t => t.Key == key, ct);
        if (row is not null)
        {
            db.Set<NotificationTemplate>().Remove(row);
            audit.Write(AdminAuditActions.TemplateChanged, "NotificationTemplate", row.Id, $"E-mail template '{def.Name}' reset to the default");
            changes.Changed("templates", row.Id);
            await db.SaveChangesAsync(ct);
        }
        return Map(def, null);
    }

    public async Task<TemplatePreviewDto> PreviewAsync(string key, PreviewTemplateRequest r, CancellationToken ct)
    {
        RequireAccess();
        var def = EmailTemplateCatalog.Find(key) ?? throw new NotFoundException("Template", key);
        var saved = await db.Set<NotificationTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.Key == key && t.IsActive, ct);
        var subject = r.Subject ?? saved?.Subject ?? def.Subject;
        var body = r.BodyHtml ?? saved?.BodyHtml ?? def.BodyHtml;
        var values = def.Placeholders.ToDictionary(p => p.Name, p => (string?)p.Sample);
        var brand = r.BrandId is { } b ? await db.Brands.AsNoTracking().FirstOrDefaultAsync(x => x.Id == b, ct) : await db.Brands.AsNoTracking().FirstOrDefaultAsync(x => x.IsDefault, ct);
        if (brand is not null && values.ContainsKey("brand.name")) values["brand.name"] = brand.Name;
        var (s, html) = templates.Render(def, subject, body, values);
        if (def.Audience == "Customer") html += await templates.BrandFooterAsync(brand?.Id, ct);
        var known = def.Placeholders.Select(p => p.Name).ToList();
        return new TemplatePreviewDto(s, html, EmailTemplateRenderer.Unknown(subject, known).Concat(EmailTemplateRenderer.Unknown(body, known)).Distinct().ToList());
    }

    private static NotificationTemplateDto Map(EmailTemplateCatalog.Definition d, NotificationTemplate? t) => new(d.Key, d.Name, d.Audience, d.Description,
        t?.Subject ?? d.Subject, t?.BodyHtml ?? d.BodyHtml, t is not null, t?.IsActive ?? false,
        d.Placeholders.Select(p => new TemplatePlaceholderDto(p.Name, p.Description, p.Sample)).ToList(), d.Subject, d.BodyHtml, t?.UpdatedAt,
        t is null ? null : AdminRules.Version(t.RowVersion));
}
