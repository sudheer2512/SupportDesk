using System.Globalization;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Analytics;

/// <summary>Next-run arithmetic for scheduled reports on the schedule's local calendar (DST-safe).</summary>
public static class ScheduleCalculator
{
    /// <summary>The first run strictly after <paramref name="afterUtc"/>.</summary>
    public static DateTime Next(ReportFrequency frequency, TimeOnly runAt, DayOfWeek? dayOfWeek, int? dayOfMonth, string timeZone, DateTime afterUtc)
    {
        var tz = WorkingCalendar.ResolveTimeZone(timeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), tz);
        var date = DateOnly.FromDateTime(local);
        for (var i = 0; i < 800; i++, date = date.AddDays(1))
        {
            var matches = frequency switch
            {
                ReportFrequency.Weekly => date.DayOfWeek == (dayOfWeek ?? DayOfWeek.Monday),
                ReportFrequency.Monthly => date.Day == Math.Min(Math.Clamp(dayOfMonth ?? 1, 1, 28), DateTime.DaysInMonth(date.Year, date.Month)),
                _ => true
            };
            if (!matches) continue;
            var candidate = AnalyticsPeriods.ToUtc(date.ToDateTime(runAt), tz);
            if (candidate > afterUtc) return candidate;
        }
        throw new InvalidOperationException("No next run found.");
    }
}

public sealed record ScheduledReportJobPayload(Guid RunId);

/// <summary>
/// Scheduled reports: CRUD (analytics.schedule; owners edit their own, analytics.team edits all), run-now and delivery
/// history. Each run renders the report under the owner's data scope and e-mails it as an attachment.
/// </summary>
public sealed class ScheduledReportService(DeskDbContext db, RequestContext ctx, IJobQueue jobs, IAuditWriter audit, TicketLookups lookups)
{
    public const string JobType = "analytics.scheduled-report";
    public const int MaxRecipients = 20;

    private Guid Me => ctx.UserId ?? Guid.Empty;
    private bool CanEdit(ScheduledReport s) => s.OwnerUserId == Me || ctx.Has(Permissions.AnalyticsTeam);

    public async Task<List<ScheduledReportDto>> ListAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsSchedule);
        var me = Me;
        var q = db.Set<ScheduledReport>().AsNoTracking();
        if (!ctx.Has(Permissions.AnalyticsTeam)) q = q.Where(s => s.OwnerUserId == me);
        var list = await q.OrderBy(s => s.Name).ToListAsync(ct);
        await lookups.LoadAsync(ct);
        return list.Select(Map).ToList();
    }

    public async Task<ScheduledReportDto> GetAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsSchedule);
        await lookups.LoadAsync(ct);
        return Map(await LoadAsync(id, track: false, ct));
    }

    public async Task<ScheduledReportDto> CreateAsync(SaveScheduledReportRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsSchedule);
        var s = new ScheduledReport { OwnerUserId = Me };
        await ApplyAsync(s, r, ct);
        db.Add(s);
        audit.Write("analytics.schedule_created", "ScheduledReport", s.Id, $"Scheduled report \"{s.Name}\" created", new { s.ReportKey, recipients = Json.StringList(s.RecipientsJson) });
        await db.SaveChangesAsync(ct);
        await lookups.LoadAsync(ct);
        return Map(s);
    }

    public async Task<ScheduledReportDto> UpdateAsync(Guid id, SaveScheduledReportRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsSchedule);
        var s = await LoadAsync(id, track: true, ct);
        if (!CanEdit(s)) throw new ForbiddenException("Only the owner can change this schedule.");
        if (string.IsNullOrWhiteSpace(r.RowVersion)) throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "Reload the schedule and try again." });
        db.Entry(s).Property(x => x.RowVersion).OriginalValue = Convert.FromBase64String(r.RowVersion);
        await ApplyAsync(s, r, ct);
        audit.Write("analytics.schedule_changed", "ScheduledReport", s.Id, $"Scheduled report \"{s.Name}\" updated", new { s.ReportKey, s.IsActive, recipients = Json.StringList(s.RecipientsJson) });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(ErrorCodes.ConcurrencyConflict, "Someone else changed this schedule. Reload to see their version.");
        }
        await lookups.LoadAsync(ct);
        return Map(s);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsSchedule);
        var s = await LoadAsync(id, track: true, ct);
        if (!CanEdit(s)) throw new ForbiddenException("Only the owner can delete this schedule.");
        db.Remove(s);
        audit.Write("analytics.schedule_deleted", "ScheduledReport", s.Id, $"Scheduled report \"{s.Name}\" deleted");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Queues an immediate delivery (recorded in the history as a manual run).</summary>
    public async Task<ScheduledReportRunDto> RunNowAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsSchedule);
        var s = await LoadAsync(id, track: false, ct);
        if (!CanEdit(s)) throw new ForbiddenException("Only the owner can run this schedule.");
        var run = new ScheduledReportRun
        {
            ScheduledReportId = s.Id, ScheduledFor = db.Clock.UtcNow, Manual = true, Status = ReportRunStatus.Queued, CreatedAt = db.Clock.UtcNow,
            RecipientsJson = s.RecipientsJson
        };
        db.Add(run);
        jobs.Enqueue(JobType, new ScheduledReportJobPayload(run.Id), idempotencyKey: $"scheduled-report-run:{run.Id:N}", maxAttempts: 3);
        await db.SaveChangesAsync(ct);
        return MapRun(run);
    }

    public async Task<List<ScheduledReportRunDto>> RunsAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsSchedule);
        await LoadAsync(id, track: false, ct);
        var runs = await db.Set<ScheduledReportRun>().AsNoTracking().Where(r => r.ScheduledReportId == id).OrderByDescending(r => r.CreatedAt).Take(50).ToListAsync(ct);
        return runs.Select(MapRun).ToList();
    }

    private async Task<ScheduledReport> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var me = Me;
        var team = ctx.Has(Permissions.AnalyticsTeam);
        var q = db.Set<ScheduledReport>().Where(s => s.Id == id && (team || s.OwnerUserId == me));
        return await (track ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Scheduled report", id);
    }

    private async Task ApplyAsync(ScheduledReport s, SaveScheduledReportRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 200) errors["name"] = "Enter a name of up to 200 characters.";
        ReportDef? report = null;
        try { report = ReportCatalog.Get(r.ReportKey); } catch (NotFoundException) { errors["reportKey"] = "Choose a report."; }
        IReadOnlyDictionary<string, string> parameters = new Dictionary<string, string>();
        try { parameters = AnalyticsEngine.NormaliseParameters(r.Parameters); }
        catch (ValidationException ex) { foreach (var (k, v) in ex.Errors) errors[$"parameters.{k}"] = v; }
        if (parameters.TryGetValue("period", out var p) && p == "custom") errors["parameters.period"] = "Scheduled reports use a rolling period (e.g. last 7 days).";
        if (!Enum.TryParse<ReportFormat>(r.Format, true, out var format) || !Enum.IsDefined(format)) errors["format"] = "Format must be Csv, Xlsx or Pdf.";
        if (!Enum.TryParse<ReportFrequency>(r.Frequency, true, out var frequency) || !Enum.IsDefined(frequency)) errors["frequency"] = "Frequency must be Daily, Weekly or Monthly.";
        if (!TimeOnly.TryParseExact(r.RunAtLocal ?? "", ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var runAt)) errors["runAtLocal"] = "Enter a time as HH:mm.";
        DayOfWeek? dow = null;
        if (frequency == ReportFrequency.Weekly)
        {
            if (Enum.TryParse<DayOfWeek>(r.DayOfWeek, true, out var d) && Enum.IsDefined(d)) dow = d;
            else errors["dayOfWeek"] = "Choose a day of the week.";
        }
        int? dom = null;
        if (frequency == ReportFrequency.Monthly)
        {
            if (r.DayOfMonth is >= 1 and <= 28) dom = r.DayOfMonth;
            else errors["dayOfMonth"] = "Choose a day between 1 and 28.";
        }
        var tz = string.IsNullOrWhiteSpace(r.TimeZone)
            ? await db.Tenants.AsNoTracking().Where(t => t.Id == ctx.TenantId).Select(t => t.DefaultTimeZone).FirstOrDefaultAsync(ct) ?? "UTC"
            : r.TimeZone.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)) errors["timeZone"] = "Unknown time zone.";
        var recipients = (r.Recipients ?? []).Select(x => (x ?? "").Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        if (recipients.Count == 0) errors["recipients"] = "Add at least one recipient.";
        else if (recipients.Count > MaxRecipients) errors["recipients"] = $"At most {MaxRecipients} recipients.";
        else if (recipients.FirstOrDefault(x => !IsEmail(x)) is { } bad) errors["recipients"] = $"'{bad}' is not a valid e-mail address.";
        if (errors.Count > 0) throw new ValidationException(errors);

        s.Name = name;
        s.ReportKey = report!.Key;
        s.ParametersJson = Json.Serialize(parameters);
        s.Format = format;
        s.Frequency = frequency;
        s.RunAtLocal = runAt;
        s.DayOfWeek = dow;
        s.DayOfMonth = dom;
        s.TimeZone = tz;
        s.RecipientsJson = Json.Serialize(recipients);
        s.IsActive = r.IsActive;
        s.NextRunAt = ScheduleCalculator.Next(frequency, runAt, dow, dom, tz, db.Clock.UtcNow);
    }

    private static bool IsEmail(string s) => s.Length <= 254 && MailAddress.TryCreate(s, out var a) && a.Address == s && s.Contains('.');

    private ScheduledReportDto Map(ScheduledReport s)
    {
        var name = ReportCatalog.All.FirstOrDefault(r => r.Key == s.ReportKey)?.Name ?? s.ReportKey;
        return new ScheduledReportDto(s.Id, s.Name, s.ReportKey, name, Json.DeserializeOr(s.ParametersJson, new Dictionary<string, string>()), s.Format.ToString(),
            s.Frequency.ToString(), s.RunAtLocal.ToString("HH:mm", CultureInfo.InvariantCulture), s.DayOfWeek?.ToString(), s.DayOfMonth, s.TimeZone,
            Json.StringList(s.RecipientsJson), s.OwnerUserId, lookups.User(s.OwnerUserId), s.IsActive, s.NextRunAt, s.LastRunAt, s.LastStatus, CanEdit(s),
            Convert.ToBase64String(s.RowVersion ?? []));
    }

    public static ScheduledReportRunDto MapRun(ScheduledReportRun r) => new(r.Id, r.ScheduledFor, r.Manual, r.Status.ToString(), r.CreatedAt, r.StartedAt, r.CompletedAt,
        Json.StringList(r.RecipientsJson), r.Delivered, r.FileName, r.SizeBytes, r.RowCount, r.Error);
}

/// <summary>Every minute: queue a delivery for each due schedule (one run per slot) and advance its next run.</summary>
public sealed class ScheduledReportTask(TenantScopeRunner runner, ILogger<ScheduledReportTask> logger) : IRecurringTask
{
    public const string TaskName = "analytics.scheduled-reports";
    public string Name => TaskName;
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public Task RunAsync(CancellationToken ct) => runner.ForEachActiveTenantAsync("Scheduled reports", async (sp, tenant, token) =>
    {
        try
        {
            await QueueDueAsync(sp, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Scheduled reports failed for tenant {Tenant}", tenant.Slug);
        }
    }, ct);

    public static async Task<int> QueueDueAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<DeskDbContext>();
        var jobs = sp.GetRequiredService<IJobQueue>();
        var now = db.Clock.UtcNow;
        var due = await db.Set<ScheduledReport>().Where(s => s.IsActive && s.NextRunAt <= now).OrderBy(s => s.NextRunAt).Take(100).ToListAsync(ct);
        var queued = 0;
        foreach (var s in due)
        {
            var run = new ScheduledReportRun
            {
                ScheduledReportId = s.Id, ScheduledFor = s.NextRunAt, Manual = false, Status = ReportRunStatus.Queued, CreatedAt = now, RecipientsJson = s.RecipientsJson
            };
            db.Add(run);
            jobs.Enqueue(ScheduledReportService.JobType, new ScheduledReportJobPayload(run.Id), idempotencyKey: $"scheduled-report:{s.Id:N}:{s.NextRunAt.Ticks}", maxAttempts: 3);
            // Missed slots (downtime) are not replayed: the next run is the first slot after now.
            s.NextRunAt = ScheduleCalculator.Next(s.Frequency, s.RunAtLocal, s.DayOfWeek, s.DayOfMonth, s.TimeZone, now);
            try
            {
                await db.SaveChangesAsync(ct);
                queued++;
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear(); // the slot was queued by another instance
            }
        }
        return queued;
    }
}

/// <summary>Renders and e-mails one scheduled report run (owner's scope), recording the delivery outcome.</summary>
public sealed class ScheduledReportJobHandler(DeskDbContext db, ReportRunner runner, ISystemMailer mailer, ILogger<ScheduledReportJobHandler> logger)
    : JobHandler<ScheduledReportJobPayload>
{
    public override string Type => ScheduledReportService.JobType;

    protected override async Task ExecuteAsync(ScheduledReportJobPayload payload, BackgroundJob job, CancellationToken ct)
    {
        var run = await db.Set<ScheduledReportRun>().FirstOrDefaultAsync(r => r.Id == payload.RunId, ct);
        if (run is null || run.Status is ReportRunStatus.Succeeded or ReportRunStatus.Failed) return; // idempotent: never e-mail twice
        var schedule = await db.Set<ScheduledReport>().FirstOrDefaultAsync(s => s.Id == run.ScheduledReportId, ct);
        if (schedule is null) return;
        run.Status = ReportRunStatus.Running;
        run.StartedAt = db.Clock.UtcNow;
        await db.SaveChangesAsync(ct);

        var recipients = Json.StringList(run.RecipientsJson);
        var failures = new List<string>();
        try
        {
            var parameters = Json.DeserializeOr(schedule.ParametersJson, new Dictionary<string, string>());
            var rendered = await runner.RunAsAsync(schedule.TenantId, schedule.OwnerUserId, schedule.ReportKey, parameters, schedule.Format, null, ct)
                           ?? throw new DomainException("schedule_owner_inactive", "The schedule's owner is no longer an active agent.");
            var subject = $"{schedule.Name} — {rendered.Report.Period.Label} ({rendered.Report.Period.FromDay} – {rendered.Report.Period.ToDay})";
            var html = ReportRenderer.EmailHtml(rendered.Report, schedule.Name);
            var attachment = new MailAttachment(rendered.FileName, rendered.ContentType, rendered.Content);
            foreach (var to in recipients)
            {
                try
                {
                    await mailer.SendAsync(schedule.TenantId, to, subject, html, null, [attachment], ct);
                    run.Delivered++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Scheduled report {Schedule} could not be delivered to {Recipient}", schedule.Id, to);
                    failures.Add($"{to}: {ex.Message}");
                }
            }
            run.FileName = rendered.FileName;
            run.SizeBytes = rendered.Content.LongLength;
            run.RowCount = rendered.RowCount;
            run.Status = failures.Count == 0 ? ReportRunStatus.Succeeded : ReportRunStatus.Failed;
            run.Error = failures.Count == 0 ? null : Trim("Delivery failed for " + string.Join("; ", failures));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Scheduled report {Schedule} failed", schedule.Id);
            run.Status = ReportRunStatus.Failed;
            run.Error = Trim(ex.Message);
        }
        run.CompletedAt = db.Clock.UtcNow;
        schedule.LastRunAt = run.CompletedAt;
        schedule.LastStatus = run.Status == ReportRunStatus.Succeeded ? $"Delivered to {run.Delivered} recipient(s)" : Trim(run.Error ?? "Failed");
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The schedule was edited meanwhile: keep the run history, skip the schedule summary.
            db.Entry(schedule).State = EntityState.Unchanged;
            db.ChangeTracker.Clear();
            db.Attach(run);
            db.Entry(run).State = EntityState.Modified;
            await db.SaveChangesAsync(ct);
        }
    }

    private static string Trim(string s) => s.Length <= 1000 ? s : s[..1000];
}
