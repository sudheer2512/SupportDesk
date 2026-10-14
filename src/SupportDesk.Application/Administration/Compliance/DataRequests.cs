using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Compliance;
using SupportDesk.Domain;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Administration.Compliance;

public sealed record DataRequestJob(Guid RequestId);

/// <summary>
/// Data-subject requests (admin.compliance). Export: a durable job builds a zip of the contact's profile, tickets,
/// public conversation, channel messages and attachments; it can be downloaded once through an expiring one-time link.
/// Erasure: needs an approval (by a second compliance administrator when one exists), is refused while a legal hold
/// covers the contact, then a durable job pseudonymises the contact and redacts their content while ticket statistics
/// (counts, timings, SLA, CSAT scores) stay intact. Every step is audited.
/// </summary>
public sealed class DataRequestService(
    DeskDbContext db,
    RequestContext ctx,
    IAuditWriter audit,
    IJobQueue jobs,
    LegalHoldMatcher holds,
    IFileStorage storage,
    IConfiguration configuration)
{
    public const string ExportJob = "compliance.export";
    public const string ErasureJob = "compliance.erasure";
    public int LinkHours => configuration.GetValue("Compliance:DownloadLinkHours", 24);

    public async Task<List<DataRequestDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var q = db.DataRequests.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<DataRequestStatus>(status, true, out var st)) q = q.Where(r => r.Status == st);
        var rows = await q.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync(ct);
        var result = new List<DataRequestDto>();
        foreach (var r in rows) result.Add(await ToDtoAsync(r, ct));
        return result;
    }

    public async Task<DataRequestDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        return await ToDtoAsync(await LoadAsync(id, ct), ct);
    }

    public async Task<DataRequestDto> CreateAsync(CreateDataRequestRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        if (!Enum.TryParse<DataRequestType>(r.Type, true, out var type))
            throw new ValidationException(new Dictionary<string, string> { ["type"] = "Choose Export or Erasure." });
        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.ContactId, ct)
                      ?? throw new ValidationException(new Dictionary<string, string> { ["contactId"] = "Contact not found." });
        if (contact.Status == ContactStatus.Erased) throw new ConflictException("contact_erased", "This contact has already been erased.");
        if (await db.DataRequests.AnyAsync(x => x.ContactId == contact.Id && x.Type == type && (x.Status == DataRequestStatus.Pending || x.Status == DataRequestStatus.Processing), ct))
            throw new ConflictException("request_open", $"An open {type.ToString().ToLowerInvariant()} request already exists for this contact.");
        if (type == DataRequestType.Erasure && await holds.HoldBlockingContactAsync(contact.Id, ct) is { } hold)
            throw new ConflictException("legal_hold", $"Erasure is refused: legal hold '{hold.Name}' covers this contact.");

        var request = new DataRequest
        {
            Type = type, ContactId = contact.Id, Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim()[..Math.Min(2000, r.Notes.Trim().Length)],
            Status = type == DataRequestType.Export ? DataRequestStatus.Processing : DataRequestStatus.Pending
        };
        db.DataRequests.Add(request);
        if (type == DataRequestType.Export) jobs.Enqueue(ExportJob, new DataRequestJob(request.Id), idempotencyKey: $"{ExportJob}:{request.Id}");
        audit.Write(type == DataRequestType.Export ? AuditActions.DataExported : "compliance.erasure_requested", "Contact", contact.Id,
            type == DataRequestType.Export ? $"Data export requested for contact {contact.FullName}" : $"Erasure requested for contact {contact.FullName} (awaiting approval)",
            new { requestId = request.Id, type = type.ToString(), request.Notes });
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(request, ct);
    }

    public async Task<DataRequestDto> ApproveAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var request = await LoadAsync(id, ct);
        if (request.Type != DataRequestType.Erasure || request.Status != DataRequestStatus.Pending)
            throw new ConflictException("request_not_pending", "Only pending erasure requests can be approved.");
        if (request.CreatedById == ctx.UserId && await OtherComplianceAdminExistsAsync(ct))
            throw new DomainException("four_eyes_required", "Another compliance administrator must approve an erasure you requested.", 403);
        if (await holds.HoldBlockingContactAsync(request.ContactId, ct) is { } hold)
            throw new ConflictException("legal_hold", $"Erasure is refused: legal hold '{hold.Name}' covers this contact.");
        request.Status = DataRequestStatus.Processing;
        request.ApprovedById = ctx.UserId;
        request.ApprovedAt = db.Clock.UtcNow;
        jobs.Enqueue(ErasureJob, new DataRequestJob(request.Id), idempotencyKey: $"{ErasureJob}:{request.Id}");
        audit.Write("compliance.erasure_approved", "Contact", request.ContactId, "Erasure approved",
            new { requestId = request.Id, selfApproved = request.CreatedById == ctx.UserId });
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(request, ct);
    }

    public async Task<DataRequestDto> RejectAsync(Guid id, RejectDataRequestRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var request = await LoadAsync(id, ct);
        if (request.Status != DataRequestStatus.Pending) throw new ConflictException("request_not_pending", "Only pending requests can be rejected.");
        var reason = r.Reason?.Trim() ?? "";
        if (reason.Length is < 3 or > 1000) throw new ValidationException(new Dictionary<string, string> { ["reason"] = "Give a reason (3–1000 characters)." });
        request.Status = DataRequestStatus.Rejected;
        request.RejectionReason = reason;
        request.CompletedAt = db.Clock.UtcNow;
        audit.Write("compliance.request_rejected", "Contact", request.ContactId, $"{request.Type} request rejected: {reason}", new { requestId = request.Id, reason });
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(request, ct);
    }

    /// <summary>Issues a one-time download link (replaces any previous link) for a completed, not yet downloaded export.</summary>
    public async Task<DownloadLinkDto> CreateDownloadLinkAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminCompliance);
        var request = await LoadAsync(id, ct);
        if (request.Type != DataRequestType.Export || request.Status != DataRequestStatus.Completed || request.ResultStorageKey is null)
            throw new ConflictException("export_not_ready", "The export is not ready.");
        if (request.DownloadedAt is not null) throw new DomainException("export_downloaded", "This export was already downloaded. Create a new export request.", 410);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        request.DownloadTokenHash = TokenHasher.Hash(token);
        request.DownloadExpiresAt = db.Clock.UtcNow.AddHours(LinkHours);
        audit.Write(AuditActions.DataExported, "Contact", request.ContactId, "One-time download link issued for a data export",
            new { requestId = request.Id, request.DownloadExpiresAt });
        await db.SaveChangesAsync(ct);
        return new DownloadLinkDto($"/api/v1/compliance/downloads/{ctx.TenantId:N}/{token}", request.DownloadExpiresAt.Value);
    }

    /// <summary>
    /// Anonymous download by one-time token (the token is the credential; the tenant comes from the link and is
    /// verified against the token's request). Marks the export downloaded so the link cannot be used again.
    /// </summary>
    public async Task<(Stream Content, string FileName)> DownloadAsync(string token, CancellationToken ct = default)
    {
        var hash = TokenHasher.Hash(token ?? "");
        var request = await db.DataRequests.FirstOrDefaultAsync(r => r.DownloadTokenHash == hash, ct);
        var now = db.Clock.UtcNow;
        if (request is null || request.ResultStorageKey is null || request.DownloadedAt is not null || request.DownloadExpiresAt < now)
            throw new DomainException("link_invalid", "This download link is invalid, expired or was already used.", 410);
        var stream = await storage.OpenReadAsync(request.ResultStorageKey, ct);
        request.DownloadedAt = now;
        request.DownloadTokenHash = null;
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = ctx.TenantId, OccurredAt = now, ActorType = ActorType.System, ActorName = "One-time download link", Action = AuditActions.DataExported,
            EntityType = "Contact", EntityId = request.ContactId, Summary = "Data export downloaded (link used and invalidated)",
            ChangesJson = Json.Serialize(new { requestId = request.Id }), IpAddress = ctx.IpAddress, UserAgent = ctx.UserAgent, CorrelationId = ctx.CorrelationId
        });
        await db.SaveChangesAsync(ct);
        return (stream, request.ResultFileName ?? "export.zip");
    }

    /// <summary>Deletes export files that were downloaded or are older than 7 days (called by the retention task).</summary>
    public async Task CleanupExportsAsync(CancellationToken ct)
    {
        var old = db.Clock.UtcNow.AddDays(-7);
        var done = await db.DataRequests.Where(r => r.Type == DataRequestType.Export && r.ResultStorageKey != null
                                                    && (r.DownloadedAt != null || r.CompletedAt < old)).ToListAsync(ct);
        foreach (var r in done)
        {
            try { await storage.DeleteAsync(r.ResultStorageKey!, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            r.ResultStorageKey = null;
            r.DownloadTokenHash = null;
        }
        if (done.Count > 0) await db.SaveChangesAsync(ct);
    }

    private async Task<bool> OtherComplianceAdminExistsAsync(CancellationToken ct)
    {
        var me = ctx.UserId;
        return await (from ur in db.UserRoles
                      join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                      join u in db.Users on ur.UserId equals u.Id
                      where rp.PermissionCode == Permissions.AdminCompliance && u.IsActive && u.Id != me
                      select u.Id).AnyAsync(ct);
    }

    private async Task<DataRequest> LoadAsync(Guid id, CancellationToken ct) =>
        await db.DataRequests.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Data request", id);

    private async Task<DataRequestDto> ToDtoAsync(DataRequest r, CancellationToken ct)
    {
        var contact = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(c => c.Id == r.ContactId).Select(c => c.FullName).FirstOrDefaultAsync(ct) ?? "(deleted contact)";
        var userIds = new[] { r.CreatedById, r.ApprovedById }.Where(x => x is not null).Select(x => x!.Value).ToList();
        var users = await db.Users.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var blocked = r.Type == DataRequestType.Erasure && r.Status == DataRequestStatus.Pending && await holds.HoldBlockingContactAsync(r.ContactId, ct) is not null;
        var now = db.Clock.UtcNow;
        return new DataRequestDto(r.Id, r.Type.ToString(), r.Status.ToString(), r.ContactId, contact, r.Notes, r.CreatedAt,
            r.CreatedById is { } c ? users.GetValueOrDefault(c) : null, r.ApprovedAt, r.ApprovedById is { } a ? users.GetValueOrDefault(a) : null,
            r.CompletedAt, r.RejectionReason, r.Error,
            r.Type == DataRequestType.Export && r.Status == DataRequestStatus.Completed && r.DownloadedAt is null && r.ResultStorageKey is not null,
            r.DownloadExpiresAt > now ? r.DownloadExpiresAt : null, r.DownloadedAt, r.ResultFileName, r.ResultSizeBytes, blocked);
    }
}

/// <summary>Builds the data-subject export zip (idempotent: a retry rebuilds the same file).</summary>
public sealed class DataExportJobHandler(DeskDbContext db, IFileStorage storage, IAuditWriter audit, ILogger<DataExportJobHandler> logger) : JobHandler<DataRequestJob>
{
    public override string Type => DataRequestService.ExportJob;

    protected override async Task ExecuteAsync(DataRequestJob payload, BackgroundJob job, CancellationToken ct)
    {
        var request = await db.DataRequests.FirstOrDefaultAsync(r => r.Id == payload.RequestId, ct);
        if (request is null || request.Status != DataRequestStatus.Processing) return;
        try
        {
            var contact = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().FirstAsync(c => c.Id == request.ContactId, ct);
            var customer = contact.CustomerId is { } cid ? await db.Customers.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
            var identities = await db.ContactIdentities.AsNoTracking().Where(i => i.ContactId == contact.Id)
                .Select(i => new { channel = i.Channel.ToString(), i.Identifier, i.IsVerified, i.CreatedAt }).ToListAsync(ct);
            var consents = await db.ContactConsents.AsNoTracking().Where(c => c.ContactId == contact.Id)
                .Select(c => new { channel = c.Channel.ToString(), purpose = c.Purpose.ToString(), status = c.Status.ToString(), c.Source, c.RecordedAt }).ToListAsync(ct);
            var tickets = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(t => t.ContactId == contact.Id)
                .OrderBy(t => t.CreatedAt).ToListAsync(ct);
            var ticketIds = tickets.Select(t => t.Id).ToList();
            var threads = await db.TicketThreads.AsNoTracking().Where(t => ticketIds.Contains(t.TicketId) && t.Visibility == Visibility.Public)
                .OrderBy(t => t.TicketId).ThenBy(t => t.Seq).ToListAsync(ct);
            var messages = await db.Messages.AsNoTracking().Where(m => m.ContactId == contact.Id).OrderBy(m => m.OccurredAt).ToListAsync(ct);
            var attachments = await db.TicketAttachments.AsNoTracking().Where(a => ticketIds.Contains(a.TicketId) && a.Visibility == Visibility.Public).ToListAsync(ct);
            var fileIds = attachments.Select(a => a.FileId).Distinct().ToList();
            var files = await db.StoredFiles.AsNoTracking().Where(f => fileIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);

            await using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string name, object value)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var s = entry.Open();
                    s.Write(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(value, Pretty)));
                }
                Add("profile.json", new
                {
                    contact.Id, contact.FullName, contact.FirstName, contact.LastName, contact.Email, contact.Phone, contact.Mobile, contact.WhatsAppNumber,
                    contact.JobTitle, contact.PreferredLanguage, contact.TimeZone, organisation = customer, status = contact.Status.ToString(), contact.CreatedAt,
                    contact.PortalLastLoginAt, customFields = Json.Element(contact.CustomFieldsJson), identities, consents
                });
                Add("tickets.json", tickets.Select(t => new
                {
                    t.Number, t.Subject, description = t.DescriptionText, channel = t.Channel.ToString(), priority = t.Priority.ToString(),
                    status = t.StatusCategory.ToString(), t.CreatedAt, t.FirstResponseAt, t.ResolvedAt, t.ClosedAt, t.CsatScore, t.CsatComment
                }));
                Add("conversations.json", threads.Select(th => new
                {
                    ticket = tickets.First(t => t.Id == th.TicketId).Number, th.Seq, kind = th.Kind.ToString(), author = th.AuthorName, th.Subject,
                    body = th.BodyText ?? TextNormalization.HtmlToText(th.BodyHtml), th.CreatedAt
                }));
                Add("messages.json", messages.Select(m => new
                {
                    channel = m.Channel.ToString(), direction = m.Direction.ToString(), m.FromAddress, m.FromName, m.Subject,
                    body = m.BodyText ?? TextNormalization.HtmlToText(m.BodyHtml), m.OccurredAt
                }));
                foreach (var a in attachments)
                {
                    if (!files.TryGetValue(a.FileId, out var file) || file.ScanStatus is ScanStatus.Infected or ScanStatus.Blocked) continue;
                    var number = tickets.First(t => t.Id == a.TicketId).Number;
                    var entry = zip.CreateEntry($"attachments/{number}/{a.Id:N}-{FileService.SanitizeFileName(a.FileName)}", CompressionLevel.Fastest);
                    try
                    {
                        await using var src = await storage.OpenReadAsync(file.StorageKey, ct);
                        await using var dst = entry.Open();
                        await src.CopyToAsync(dst, ct);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
                    {
                        logger.LogWarning(ex, "Attachment {File} missing from storage during export {Request}", a.FileId, request.Id);
                    }
                }
                var readme = zip.CreateEntry("README.txt");
                await using (var w = new StreamWriter(readme.Open()))
                    await w.WriteAsync($"Personal data export for {contact.FullName}, generated {db.Clock.UtcNow:yyyy-MM-dd HH:mm} UTC.\n" +
                                       "profile.json: contact profile, identities and consents\ntickets.json: tickets raised by the contact\n" +
                                       "conversations.json: public ticket conversation\nmessages.json: channel messages\nattachments/: public ticket attachments\n");
            }

            var key = $"{request.TenantId:N}/compliance/exports/{request.Id:N}.zip";
            if (await storage.ExistsAsync(key, ct)) await storage.DeleteAsync(key, ct);
            buffer.Position = 0;
            await storage.SaveAsync(key, buffer, "application/zip", ct);
            request.ResultStorageKey = key;
            request.ResultFileName = $"data-export-{TextNormalization.Slugify(contact.FullName, 40)}-{db.Clock.UtcNow:yyyyMMdd}.zip";
            request.ResultSizeBytes = buffer.Length;
            request.Status = DataRequestStatus.Completed;
            request.CompletedAt = db.Clock.UtcNow;
            request.Error = null;
            audit.Write(AuditActions.DataExported, "Contact", contact.Id,
                $"Data export ready for {contact.FullName}: {tickets.Count} tickets, {threads.Count} conversation entries, {messages.Count} messages, {attachments.Count} attachments",
                new { requestId = request.Id, tickets = tickets.Count, threads = threads.Count, messages = messages.Count, attachments = attachments.Count, bytes = buffer.Length });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && job.Attempts >= job.MaxAttempts)
        {
            request.Status = DataRequestStatus.Failed;
            request.Error = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions Pretty = new(Json.Options) { WriteIndented = true };
}

/// <summary>Pseudonymises an approved erasure's contact and redacts their content; statistics stay intact. Idempotent.</summary>
public sealed class DataErasureJobHandler(DeskDbContext db, IFileStorage storage, IAuditWriter audit, IEventPublisher events, LegalHoldMatcher holds,
    ILogger<DataErasureJobHandler> logger) : JobHandler<DataRequestJob>
{
    public const string RedactedText = "[erased at the customer's request]";
    public override string Type => DataRequestService.ErasureJob;

    protected override async Task ExecuteAsync(DataRequestJob payload, BackgroundJob job, CancellationToken ct)
    {
        var request = await db.DataRequests.FirstOrDefaultAsync(r => r.Id == payload.RequestId, ct);
        if (request is null || request.Status != DataRequestStatus.Processing) return;
        if (await holds.HoldBlockingContactAsync(request.ContactId, ct) is { } hold)
        {
            request.Status = DataRequestStatus.Rejected;
            request.RejectionReason = $"Refused: legal hold '{hold.Name}' was placed before the erasure ran.";
            request.CompletedAt = db.Clock.UtcNow;
            audit.Write("compliance.erasure_refused", "Contact", request.ContactId, request.RejectionReason, new { requestId = request.Id });
            await db.SaveChangesAsync(ct);
            return;
        }
        var storageKeys = new List<string>();
        await db.InTransactionAsync(async () =>
        {
            var contact = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).FirstAsync(c => c.Id == request.ContactId, ct);
            var pseudonym = $"Erased contact {contact.Id.ToString("N")[..8].ToUpperInvariant()}";
            var cid = contact.Id;
            contact.FirstName = null;
            contact.LastName = null;
            contact.FullName = pseudonym;
            contact.Email = null;
            contact.EmailNormalized = null;
            contact.Phone = null;
            contact.PhoneNormalized = null;
            contact.Mobile = null;
            contact.WhatsAppNumber = null;
            contact.JobTitle = null;
            contact.TimeZone = null;
            contact.PortalPasswordHash = null;
            contact.PortalAccess = PortalAccessScope.None;
            contact.CustomFieldsJson = "{}";
            contact.ErpContactId = null;
            contact.Status = ContactStatus.Erased;
            contact.ErasedAt = db.Clock.UtcNow;

            await db.ContactIdentities.Where(i => i.ContactId == cid).ExecuteDeleteAsync(ct);
            await db.ContactConsents.Where(c => c.ContactId == cid).ExecuteDeleteAsync(ct);
            var ticketIds = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(t => t.ContactId == cid).Select(t => t.Id).ToListAsync(ct);
            // Ticket statistics stay (status, dates, SLA, counts, CSAT score); the requester's own words are removed.
            await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(t => t.ContactId == cid).ExecuteUpdateAsync(u => u
                .SetProperty(t => t.DescriptionHtml, (string?)null).SetProperty(t => t.DescriptionText, RedactedText).SetProperty(t => t.CsatComment, (string?)null), ct);
            await db.TicketThreads.Where(t => t.AuthorContactId == cid).ExecuteUpdateAsync(u => u
                .SetProperty(t => t.BodyHtml, (string?)null).SetProperty(t => t.BodyText, RedactedText).SetProperty(t => t.AuthorName, pseudonym)
                .SetProperty(t => t.FromAddress, (string?)null).SetProperty(t => t.ToJson, (string?)null).SetProperty(t => t.CcJson, (string?)null)
                .SetProperty(t => t.IsRedacted, true), ct);
            await db.Messages.Where(m => m.ContactId == cid).ExecuteUpdateAsync(u => u
                .SetProperty(m => m.BodyHtml, (string?)null).SetProperty(m => m.BodyText, RedactedText).SetProperty(m => m.FromAddress, (string?)null)
                .SetProperty(m => m.FromName, pseudonym).SetProperty(m => m.Subject, (string?)null).SetProperty(m => m.ToJson, (string?)null)
                .SetProperty(m => m.CcJson, (string?)null).SetProperty(m => m.MetadataJson, (string?)null), ct);
            await db.SatisfactionResponses.Where(s => s.ContactId == cid).ExecuteUpdateAsync(u => u.SetProperty(s => s.Comment, (string?)null), ct);

            // Files the contact uploaded are removed (rows now, blobs after commit).
            var uploads = await db.TicketAttachments.Where(a => a.UploadedByContactId == cid).Select(a => new { a.Id, a.FileId }).ToListAsync(ct);
            if (uploads.Count > 0)
            {
                var attachmentIds = uploads.Select(u => u.Id).ToList();
                await db.TicketAttachments.Where(a => attachmentIds.Contains(a.Id)).ExecuteDeleteAsync(ct);
                var fileIds = uploads.Select(u => u.FileId).Distinct().ToList();
                var keys = await db.StoredFiles.Where(f => fileIds.Contains(f.Id) && !db.TicketAttachments.Any(a => a.FileId == f.Id))
                    .Select(f => new { f.Id, f.StorageKey }).ToListAsync(ct);
                foreach (var k in keys)
                {
                    try
                    {
                        if (await db.StoredFiles.Where(f => f.Id == k.Id).ExecuteDeleteAsync(ct) > 0) storageKeys.Add(k.StorageKey);
                    }
                    catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 547) { /* still referenced elsewhere */ }
                }
            }
            await db.SearchDocuments.Where(d => (d.EntityType == "Contact" && d.EntityId == cid)).ExecuteDeleteAsync(ct);

            request.Status = DataRequestStatus.Completed;
            request.CompletedAt = db.Clock.UtcNow;
            request.Error = null;
            events.Publish(EventTypes.ContactUpdated, "Contact", cid, new EntityEventPayload(cid, pseudonym, [new FieldChange("status", "Active", "Erased")]));
            audit.Write(AuditActions.ContactErased, "Contact", cid,
                $"Contact erased (pseudonymised as '{pseudonym}'); {ticketIds.Count} tickets kept for statistics, {uploads.Count} uploaded attachments removed",
                new { requestId = request.Id, tickets = ticketIds.Count, attachmentsRemoved = uploads.Count });
            await db.SaveChangesAsync(ct);
        }, ct);
        foreach (var key in storageKeys)
        {
            try { await storage.DeleteAsync(key, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not delete file {Key}", key); }
        }
    }
}
