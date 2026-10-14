using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Common;
using SupportDesk.Application.Identity;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Analytics;

public sealed record ReportExportJobPayload(Guid ExportId);

/// <summary>
/// Report exports: a request creates a <see cref="ReportExport"/> row and a durable job; the job renders the report
/// under the requester's data scope (CSV, Excel or PDF), stores the file and marks the export ready for download.
/// </summary>
public sealed class ReportExportService(DeskDbContext db, RequestContext ctx, IJobQueue jobs, IAuditWriter audit, FileService files)
{
    public const string JobType = "analytics.report-export";

    public async Task<ReportExportDto> CreateAsync(CreateReportExportRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsExport);
        var report = ReportCatalog.Get(r.ReportKey);
        if (!Enum.TryParse<ReportFormat>(r.Format, true, out var format) || !Enum.IsDefined(format))
            throw new ValidationException(new Dictionary<string, string> { ["format"] = "Format must be Csv, Xlsx or Pdf." });
        var parameters = AnalyticsEngine.NormaliseParameters(r.Parameters);
        var since = db.Clock.UtcNow.AddMinutes(-1);
        var me = ctx.UserId ?? Guid.Empty;
        if (await db.Set<ReportExport>().CountAsync(e => e.RequestedById == me && e.CreatedAt >= since, ct) >= 10)
            throw new DomainException("rate_limited", "Too many exports in the last minute. Try again shortly.", 429);
        var export = new ReportExport
        {
            RequestedById = me, ReportKey = report.Key, Title = report.Name, ParametersJson = Json.Serialize(parameters), Format = format,
            Status = ReportRunStatus.Queued, CreatedAt = db.Clock.UtcNow
        };
        db.Add(export);
        jobs.Enqueue(JobType, new ReportExportJobPayload(export.Id), idempotencyKey: $"report-export:{export.Id:N}", maxAttempts: 3);
        audit.Write(AuditActions.DataExported, "ReportExport", export.Id, $"Exported report \"{report.Name}\" as {format}", new { report = report.Key, format = format.ToString(), parameters });
        await db.SaveChangesAsync(ct);
        return Map(export);
    }

    public async Task<List<ReportExportDto>> ListAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsExport);
        var me = ctx.UserId ?? Guid.Empty;
        var list = await db.Set<ReportExport>().AsNoTracking().Where(e => e.RequestedById == me).OrderByDescending(e => e.CreatedAt).Take(50).ToListAsync(ct);
        return list.Select(Map).ToList();
    }

    public async Task<ReportExportDto> GetAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsExport);
        return Map(await LoadMineAsync(id, ct));
    }

    public async Task<(Stream Content, string FileName, string ContentType)> DownloadAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AnalyticsExport);
        var e = await LoadMineAsync(id, ct);
        if (e.Status != ReportRunStatus.Succeeded || e.FileId is not { } fileId) throw new ConflictException("export_not_ready", "The export is not ready yet.");
        var (file, stream) = await files.OpenAsync(fileId, ct);
        return (stream, e.FileName ?? file.FileName, file.ContentType);
    }

    private async Task<ReportExport> LoadMineAsync(Guid id, CancellationToken ct)
    {
        var me = ctx.UserId ?? Guid.Empty;
        return await db.Set<ReportExport>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && e.RequestedById == me, ct)
               ?? throw new NotFoundException("Export", id);
    }

    public static ReportExportDto Map(ReportExport e) => new(e.Id, e.ReportKey, e.Title, e.Format.ToString(), e.Status.ToString(), e.FileName, e.SizeBytes,
        e.RowCount, e.Error, e.CreatedAt, e.CompletedAt, e.Status == ReportRunStatus.Succeeded ? $"/api/v1/analytics/exports/{e.Id}/download" : null);
}

/// <summary>Runs a report as a given agent (fresh DI scope with that agent's principal) and renders it.</summary>
public sealed class ReportRunner(IServiceScopeFactory scopes)
{
    public sealed record Rendered(ReportResultDto Report, byte[] Content, string ContentType, string FileName, int RowCount);

    /// <summary>Returns null when the user is no longer an active agent.</summary>
    public async Task<Rendered?> RunAsAsync(Guid tenantId, Guid userId, string reportKey, IReadOnlyDictionary<string, string> parameters, ReportFormat format,
        Func<IServiceProvider, Rendered, Task>? withFile, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<RequestContext>().CorrelationId = $"report:{reportKey}";
        if (!await sp.GetRequiredService<PrincipalLoader>().ApplyAgentAsync(tenantId, userId, ct)) return null;
        var report = await sp.GetRequiredService<ReportService>().RunAsync(reportKey, AnalyticsEngine.QueryFrom(parameters), ct);
        var (content, contentType, ext) = ReportRenderer.Render(report, format);
        var rendered = new Rendered(report, content, contentType, ReportRenderer.FileName(report, ext), ReportRenderer.RowCount(report));
        if (withFile is not null) await withFile(sp, rendered);
        return rendered;
    }
}

public sealed class ReportExportJobHandler(DeskDbContext db, ReportRunner runner) : JobHandler<ReportExportJobPayload>
{
    public override string Type => ReportExportService.JobType;

    protected override async Task ExecuteAsync(ReportExportJobPayload payload, BackgroundJob job, CancellationToken ct)
    {
        var export = await db.Set<ReportExport>().FirstOrDefaultAsync(e => e.Id == payload.ExportId, ct);
        if (export is null || export.Status == ReportRunStatus.Succeeded) return; // idempotent
        export.Status = ReportRunStatus.Running;
        await db.SaveChangesAsync(ct);
        try
        {
            Guid? fileId = null;
            var parameters = Json.DeserializeOr(export.ParametersJson, new Dictionary<string, string>());
            var rendered = await runner.RunAsAsync(export.TenantId, export.RequestedById, export.ReportKey, parameters, export.Format, async (sp, r) =>
            {
                var stored = await sp.GetRequiredService<FileService>().UploadBytesAsync(r.Content, r.FileName, r.ContentType, "analytics-export", ct);
                fileId = stored.Id;
            }, ct);
            if (rendered is null) throw new DomainException("export_requester_inactive", "The user who requested this export is no longer active.");
            export.Status = ReportRunStatus.Succeeded;
            export.FileId = fileId;
            export.FileName = rendered.FileName;
            export.SizeBytes = rendered.Content.LongLength;
            export.RowCount = rendered.RowCount;
            export.Error = null;
            export.CompletedAt = db.Clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DomainException || job.Attempts >= job.MaxAttempts)
        {
            // Permanent failure (validation, permission, limits) or the last attempt: record it for the requester.
            export.Status = ReportRunStatus.Failed;
            export.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            export.CompletedAt = db.Clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }
}
