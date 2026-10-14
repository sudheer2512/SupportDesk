using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;
using SupportDesk.Persistence.Infrastructure;

namespace SupportDesk.Application.Tickets;

/// <summary>How a ticket is being created (agent form, portal, channel ingestion, API, ERP…).</summary>
public sealed record TicketCreationSource(
    bool CreateInitialThread = false,
    Guid? ChannelAccountId = null,
    Guid? ConversationId = null,
    Guid? MessageId = null,
    string? FromAddress = null,
    string? AuthorName = null,
    IReadOnlyList<string>? To = null,
    IReadOnlyList<string>? Cc = null,
    string? ChannelMetadataJson = null,
    bool PortalSubmission = false,
    bool EnforceRequiredFields = true,
    DateTime? OccurredAt = null)
{
    public static readonly TicketCreationSource Agent = new();
}

public sealed record TicketLockInfo(Guid UserId, string UserName, DateTime Until);

public sealed class ConcurrencyConflictException(Guid ticketId)
    : DomainException(ErrorCodes.ConcurrencyConflict, "The ticket was changed by someone else. Review the latest version and try again.", 409)
{
    public Guid TicketId { get; } = ticketId;
}

public sealed class ApprovalRequiredException(Guid approvalId, string message) : DomainException(ErrorCodes.ApprovalRequired, message, 202)
{
    public Guid ApprovalId { get; } = approvalId;
}

/// <summary>
/// Ticket lifecycle: create, edit, assign, transfer, status changes (configurable transitions), escalate, resolve,
/// reopen, close, merge, split, clone, link, tags, soft locks, entitlement override, delete/spam and bulk actions.
/// Every operation checks permissions and data scope, records history, and publishes outbox events.
/// </summary>
public sealed class TicketService(
    DeskDbContext db,
    RequestContext ctx,
    TicketHistory history,
    IEventPublisher events,
    IAuditWriter audit,
    TenantCounterService counters,
    TenantSettingsService settings,
    HtmlSanitization html,
    CustomFieldValidator customFields,
    ISlaEngine sla,
    IRoutingService routing,
    IEntitlementService entitlements,
    LimitGuard limits,
    UsageMeter usage,
    ContactResolver contacts,
    ITenantCache cache,
    TicketLookups lookups)
{
    /// <summary>Audit action for restoring a deleted/spam ticket from the recycle bin.</summary>
    public const string TicketRestoredAudit = "ticket.restored";

    private List<TicketStatus>? _statuses;

    // ───────────────────────────── Loading ─────────────────────────────

    public async Task<Ticket> LoadForUpdateAsync(Guid id, CancellationToken ct)
    {
        var ticket = await db.Tickets.Include(t => t.Tags).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Ticket", id);
        await EnsureVisibleAsync(ticket, ct);
        return ticket;
    }

    public async Task EnsureVisibleAsync(Ticket ticket, CancellationToken ct)
    {
        if (ctx.IsSystem || ctx.ActorType == ActorType.ApiClient && ctx.UserId is null) return;
        var watcher = ctx.UserId is { } uid && await db.TicketWatchers.AnyAsync(w => w.TicketId == ticket.Id && w.UserId == uid, ct);
        if (!TicketAccess.CanView(ticket, ctx, watcher)) throw new NotFoundException("Ticket", ticket.Id);
    }

    public async Task<List<TicketStatus>> StatusesAsync(CancellationToken ct) =>
        _statuses ??= await db.TicketStatuses.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.SortOrder).ToListAsync(ct);

    public async Task<TicketStatus> StatusForCategoryAsync(StatusCategory category, CancellationToken ct)
    {
        var statuses = await StatusesAsync(ct);
        return statuses.FirstOrDefault(s => s.Category == category && s.IsDefault)
               ?? statuses.FirstOrDefault(s => s.Category == category)
               ?? throw new DomainException("configuration_error", $"No active status is configured for category {category}.");
    }

    private async Task<TicketStatus> StatusAsync(Guid id, CancellationToken ct) =>
        (await StatusesAsync(ct)).FirstOrDefault(s => s.Id == id) ?? throw new DomainException(ErrorCodes.Validation, "Unknown or inactive status.");

    // ───────────────────────────── Create ─────────────────────────────

    public async Task<Ticket> CreateAsync(CreateTicketRequest r, TicketCreationSource source, CancellationToken ct = default)
    {
        // Agents and API clients need tickets.create; portal contacts and system processes (channels, ERP) create through their own paths.
        if (!ctx.IsSystem && !ctx.IsContact) ctx.Require(Permissions.TicketsCreate);
        await limits.EnsureActiveAsync(ct);
        await lookups.LoadAsync(ct);

        if (r.ClientRequestId is { Length: > 0 } key)
        {
            var existing = await db.TicketExternalReferences.Where(x => x.System == "client-request" && x.RecordNumber == key)
                .Select(x => x.TicketId).FirstOrDefaultAsync(ct);
            if (existing != Guid.Empty) return await db.Tickets.Include(t => t.Tags).FirstAsync(t => t.Id == existing, ct);
        }

        var subject = (r.Subject ?? "").Trim();
        if (subject.Length == 0) throw new ValidationException(new Dictionary<string, string> { ["subject"] = "A subject is required." });
        if (subject.Length > 500) subject = subject[..500];
        var now = source.OccurredAt ?? db.Clock.UtcNow;
        var channel = Enum.TryParse<Channel>(r.Channel, true, out var ch) ? ch : Channel.Internal;
        var descriptionHtml = html.Sanitize(r.DescriptionHtml);

        // Contact and customer
        Guid? contactId = r.ContactId;
        Domain.Customers.Contact? contact = null;
        if (ctx.IsContact) contactId = ctx.ContactId;
        else if (contactId is null && r.NewContact is { } nc && (nc.Email is not null || nc.Phone is not null))
        {
            contact = await contacts.FindOrCreateAsync(nc.Email, nc.Phone, nc.FullName, nc.CustomerId ?? r.CustomerId, channel, ct);
            contactId = contact.Id;
        }
        if (contact is null && contactId is not null)
            contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId, ct)
                      ?? throw new ValidationException(new Dictionary<string, string> { ["contactId"] = "Unknown contact." });
        var customerId = r.CustomerId ?? contact?.CustomerId;
        var locationId = r.LocationId ?? contact?.LocationId;

        // Product context from the installation when given
        Guid? productId = r.ProductId, versionId = r.VersionId;
        if (r.InstallationId is { } instId)
        {
            var inst = await db.Installations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instId, ct)
                       ?? throw new ValidationException(new Dictionary<string, string> { ["installationId"] = "Unknown installation." });
            productId ??= inst.ProductId;
            versionId ??= inst.VersionId;
            customerId ??= inst.CustomerId;
            locationId ??= inst.LocationId;
        }

        var departmentId = await ResolveDepartmentAsync(r, productId, ct);
        var status = r.StatusId is { } sid ? await StatusAsync(sid, ct) : await StatusForCategoryAsync(StatusCategory.Open, ct);
        var priority = ParseEnum(r.Priority, TicketPriority.Medium, "priority");
        TicketSeverity? severity = r.Severity is null ? null : ParseEnum(r.Severity, TicketSeverity.Minor, "severity");
        await ValidateReferencesAsync(customerId, productId, r.ModuleId, versionId, r.CategoryId, r.SubcategoryId, r.IssueTypeId, ct);

        var customFieldsJson = await customFields.MergeAsync("Ticket", "{}", r.CustomFields, departmentId,
            enforceRequired: source.EnforceRequiredFields && !ctx.IsSystem, portalOnly: source.PortalSubmission, ct);
        if (r.StatusId is not null && source.EnforceRequiredFields && !ctx.IsSystem)
        {
            // Creating straight into a status must respect "required for this status" custom fields too.
            var missingForStatus = Administration.CustomFieldRules.MissingForStatus(await customFields.DefinitionsAsync("Ticket", ct), customFieldsJson, departmentId, status.Id);
            if (missingForStatus.Count > 0)
                throw new DomainException(ErrorCodes.MissingFields, $"Fill in {string.Join(", ", missingForStatus)} to create the ticket as '{status.Name}'.");
        }

        var number = await counters.NextAsync("ticket", (await settings.TicketsAsync(ct)).NumberStart, ct);
        var ticket = new Ticket
        {
            Number = number,
            Subject = subject,
            DescriptionHtml = descriptionHtml,
            DescriptionText = TextNormalization.HtmlToText(descriptionHtml),
            Channel = channel,
            BrandId = r.BrandId ?? await db.Brands.Where(b => b.IsDefault).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct),
            ChannelAccountId = source.ChannelAccountId,
            CustomerId = customerId,
            ContactId = contactId,
            LocationId = locationId,
            DepartmentId = departmentId,
            TeamId = r.TeamId,
            ProductId = productId,
            ModuleId = r.ModuleId,
            VersionId = versionId,
            InstallationId = r.InstallationId,
            AssetId = r.AssetId,
            CategoryId = r.CategoryId,
            SubcategoryId = r.SubcategoryId,
            IssueTypeId = r.IssueTypeId,
            Priority = priority,
            Severity = severity,
            StatusId = status.Id,
            StatusCategory = status.Category,
            Language = contact?.PreferredLanguage ?? "en",
            ParentTicketId = r.ParentTicketId,
            CustomFieldsJson = customFieldsJson,
            CreatedAt = now,
            LastActivityAt = now
        };
        db.Tickets.Add(ticket);

        if (r.Tags is { Count: > 0 }) await ApplyTagsAsync(ticket, r.Tags, ct);
        if (r.ClientRequestId is { Length: > 0 } idem)
            db.TicketExternalReferences.Add(new TicketExternalReference { TicketId = ticket.Id, System = "client-request", RecordType = "request", RecordNumber = idem, CreatedAt = now });
        if (r.Origin is { } origin)
            db.TicketExternalReferences.Add(new TicketExternalReference
            {
                TicketId = ticket.Id, System = origin.System, RecordType = origin.RecordType, RecordId = origin.RecordId, RecordNumber = origin.RecordNumber,
                Url = origin.Url, ContextJson = origin.Context?.GetRawText(), IsOrigin = true, CreatedAt = now
            });

        if (source.CreateInitialThread)
        {
            var thread = new TicketThread
            {
                TicketId = ticket.Id, Seq = 1, Kind = ThreadKind.CustomerMessage, Visibility = Visibility.Public, Channel = channel,
                Direction = MessageDirection.Inbound, AuthorType = AuthorType.Customer, AuthorContactId = contactId,
                AuthorName = source.AuthorName ?? contact?.FullName, FromAddress = source.FromAddress ?? contact?.Email,
                ToJson = source.To is null ? null : Json.Serialize(source.To), CcJson = source.Cc is null ? null : Json.Serialize(source.Cc),
                Subject = subject, BodyHtml = descriptionHtml, BodyText = ticket.DescriptionText, MessageId = source.MessageId,
                ConversationId = source.ConversationId, ChannelMetadataJson = source.ChannelMetadataJson, CreatedAt = now
            };
            db.TicketThreads.Add(thread);
            ticket.LastThreadSeq = 1;
            ticket.CustomerReplyCount = 1;
            ticket.LastCustomerResponseAt = now;
            await LinkAttachmentsAsync(ticket, thread.Id, r.AttachmentFileIds, Visibility.Public, ct);
        }
        else
        {
            await LinkAttachmentsAsync(ticket, null, r.AttachmentFileIds, Visibility.Public, ct);
        }

        await entitlements.EvaluateAsync(ticket, ct);

        if (r.AssigneeId is { } assignee)
        {
            await EnsureAssignableAsync(assignee, ticket.DepartmentId, ct);
            ticket.AssigneeId = await routing.ResolveDelegateAsync(assignee, ct) ?? assignee;
            ticket.AssignedAt = now;
            ticket.AssignmentCount = 1;
            if (ticket.StatusCategory == StatusCategory.Open && r.StatusId is null) await SetStatusFieldsAsync(ticket, await StatusForCategoryAsync(StatusCategory.Assigned, ct), null);
        }
        else if (!r.SkipRouting)
        {
            var decision = await routing.RouteAsync(ticket, ct);
            if (decision.AssigneeId is not null)
            {
                ticket.AssignedAt = now;
                ticket.AssignmentCount = 1;
                if (ticket.StatusCategory == StatusCategory.Open) await SetStatusFieldsAsync(ticket, await StatusForCategoryAsync(StatusCategory.Assigned, ct), null);
                history.Record(ticket, TicketEventTypes.Assigned, $"Routed to {lookups.User(decision.AssigneeId) ?? "agent"} ({decision.Strategy})",
                    [new FieldChange("assignee", null, decision.AssigneeId.ToString())], new { decision.Strategy, decision.Reason });
            }
        }

        await sla.ApplyAsync(ticket, ct);
        history.Record(ticket, TicketEventTypes.Created, $"Ticket created via {channel}", null, new { channel = channel.ToString(), source.PortalSubmission });
        events.Publish(EventTypes.TicketCreated, "Ticket", ticket.Id, new TicketEventPayload(ticket.Id, ticket.Number));
        if (ticket.AssigneeId is not null)
            events.Publish(EventTypes.TicketAssigned, "Ticket", ticket.Id, new TicketEventPayload(ticket.Id, ticket.Number, [new FieldChange("assigneeId", null, ticket.AssigneeId.ToString())]));
        await db.SaveChangesAsync(ct);
        await usage.IncrementAsync(UsageMetrics.TicketsCreated, 1, ct);
        return ticket;
    }

    private async Task<Guid> ResolveDepartmentAsync(CreateTicketRequest r, Guid? productId, CancellationToken ct)
    {
        if (r.DepartmentId is { } d)
        {
            if (!await db.Departments.AnyAsync(x => x.Id == d && x.IsActive, ct))
                throw new ValidationException(new Dictionary<string, string> { ["departmentId"] = "Unknown or inactive department." });
            return d;
        }
        if (r.BrandId is { } b && await db.Brands.Where(x => x.Id == b).Select(x => x.DefaultDepartmentId).FirstOrDefaultAsync(ct) is { } bd) return bd;
        if (productId is { } p && await db.Products.Where(x => x.Id == p).Select(x => x.DefaultDepartmentId).FirstOrDefaultAsync(ct) is { } pd) return pd;
        if (await db.Brands.Where(x => x.IsDefault).Select(x => x.DefaultDepartmentId).FirstOrDefaultAsync(ct) is { } dd) return dd;
        return await db.Departments.Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => x.Id).FirstOrDefaultAsync(ct) is var first && first != Guid.Empty
            ? first
            : throw new DomainException("configuration_error", "No active department is configured.");
    }

    private async Task ValidateReferencesAsync(Guid? customerId, Guid? productId, Guid? moduleId, Guid? versionId, Guid? categoryId, Guid? subcategoryId,
        Guid? issueTypeId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        if (customerId is { } c && !await db.Customers.AnyAsync(x => x.Id == c, ct)) errors["customerId"] = "Unknown customer.";
        if (productId is { } p && !await db.Products.AnyAsync(x => x.Id == p, ct)) errors["productId"] = "Unknown product.";
        if (moduleId is { } m && !await db.ProductModules.AnyAsync(x => x.Id == m && (productId == null || x.ProductId == productId), ct)) errors["moduleId"] = "The module does not belong to the product.";
        if (versionId is { } v && !await db.ProductVersions.AnyAsync(x => x.Id == v && (productId == null || x.ProductId == productId), ct)) errors["versionId"] = "The version does not belong to the product.";
        if (categoryId is { } cat && !await db.TicketCategories.AnyAsync(x => x.Id == cat, ct)) errors["categoryId"] = "Unknown category.";
        if (subcategoryId is { } sub && !await db.TicketCategories.AnyAsync(x => x.Id == sub && (categoryId == null || x.ParentId == categoryId), ct)) errors["subcategoryId"] = "The subcategory does not belong to the category.";
        if (issueTypeId is { } it && !await db.IssueTypes.AnyAsync(x => x.Id == it, ct)) errors["issueTypeId"] = "Unknown issue type.";
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private async Task LinkAttachmentsAsync(Ticket ticket, Guid? threadId, IReadOnlyList<Guid>? fileIds, Visibility visibility, CancellationToken ct)
    {
        if (fileIds is not { Count: > 0 }) return;
        var files = await db.StoredFiles.Where(f => fileIds.Contains(f.Id)).ToListAsync(ct);
        foreach (var f in files)
        {
            if (!ctx.IsSystem && f.UploadedByUserId != ctx.UserId && f.UploadedByContactId != ctx.ContactId)
                throw new ForbiddenException("You can only attach files you uploaded.");
            db.TicketAttachments.Add(new TicketAttachment
            {
                TicketId = ticket.Id, ThreadId = threadId, FileId = f.Id, FileName = f.FileName, ContentType = f.ContentType, SizeBytes = f.SizeBytes,
                Visibility = visibility, UploadedByUserId = ctx.UserId, UploadedByContactId = ctx.ContactId
            });
        }
    }

    // ───────────────────────────── Update ─────────────────────────────

    public async Task<Ticket> UpdateAsync(Guid id, UpdateTicketRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsEdit);
        var t = await LoadForUpdateAsync(id, ct);
        await EnsureNotLockedByOtherAsync(t, ct);
        SetOriginalRowVersion(t, r.RowVersion);
        await lookups.LoadAsync(ct);
        var cs = new ChangeSet();
        var clear = new HashSet<string>(r.Clear ?? [], StringComparer.OrdinalIgnoreCase);

        if (r.Subject is { } subject && subject.Trim().Length > 0) cs.Set("subject", t.Subject, subject.Trim()[..Math.Min(500, subject.Trim().Length)], v => t.Subject = v);
        if (r.DescriptionHtml is not null)
        {
            var clean = html.Sanitize(r.DescriptionHtml);
            if (cs.Set("description", t.DescriptionHtml, clean, v => t.DescriptionHtml = v, _ => "(edited)"))
                t.DescriptionText = TextNormalization.HtmlToText(clean);
        }
        Guid? Next(Guid? current, Guid? requested, string field) => clear.Contains(field) ? null : requested ?? current;
        cs.Set("customerId", t.CustomerId, Next(t.CustomerId, r.CustomerId, "customerId"), v => t.CustomerId = v);
        cs.Set("contactId", t.ContactId, Next(t.ContactId, r.ContactId, "contactId"), v => t.ContactId = v);
        cs.Set("locationId", t.LocationId, Next(t.LocationId, r.LocationId, "locationId"), v => t.LocationId = v);
        cs.Set("productId", t.ProductId, Next(t.ProductId, r.ProductId, "productId"), v => t.ProductId = v, v => lookups.Product(v));
        cs.Set("moduleId", t.ModuleId, Next(t.ModuleId, r.ModuleId, "moduleId"), v => t.ModuleId = v, v => lookups.Module(v));
        cs.Set("versionId", t.VersionId, Next(t.VersionId, r.VersionId, "versionId"), v => t.VersionId = v, v => lookups.Version(v));
        cs.Set("installationId", t.InstallationId, Next(t.InstallationId, r.InstallationId, "installationId"), v => t.InstallationId = v);
        cs.Set("assetId", t.AssetId, Next(t.AssetId, r.AssetId, "assetId"), v => t.AssetId = v);
        cs.Set("categoryId", t.CategoryId, Next(t.CategoryId, r.CategoryId, "categoryId"), v => t.CategoryId = v, v => lookups.Category(v));
        cs.Set("subcategoryId", t.SubcategoryId, Next(t.SubcategoryId, r.SubcategoryId, "subcategoryId"), v => t.SubcategoryId = v, v => lookups.Category(v));
        cs.Set("issueTypeId", t.IssueTypeId, Next(t.IssueTypeId, r.IssueTypeId, "issueTypeId"), v => t.IssueTypeId = v, v => lookups.IssueType(v));
        if (r.Priority is not null) cs.Set("priority", t.Priority, ParseEnum(r.Priority, t.Priority, "priority"), v => t.Priority = v);
        if (r.Severity is not null || clear.Contains("severity"))
            cs.Set("severity", t.Severity, clear.Contains("severity") ? null : ParseEnum(r.Severity!, t.Severity ?? TicketSeverity.Minor, "severity"), v => t.Severity = v);
        if (r.Language is { Length: > 0 } lang) cs.Set("language", t.Language, lang, v => t.Language = v);
        if (r.CustomFields is not null)
        {
            var merged = await customFields.MergeAsync("Ticket", t.CustomFieldsJson, r.CustomFields, t.DepartmentId, false, false, ct);
            cs.Set("customFields", t.CustomFieldsJson, merged, v => t.CustomFieldsJson = v, _ => "(custom fields)");
        }
        if (!cs.Any) return t;

        await ValidateReferencesAsync(t.CustomerId, t.ProductId, t.ModuleId, t.VersionId, t.CategoryId, t.SubcategoryId, t.IssueTypeId, ct);
        t.LastActivityAt = db.Clock.UtcNow;
        if (cs.Has("customerId") || cs.Has("productId") || cs.Has("installationId") || cs.Has("assetId")) await entitlements.EvaluateAsync(t, ct);
        if (cs.Changes.Any(c => c.Field is "priority" or "customerId" or "productId" or "categoryId" or "installationId"))
            await sla.OnTicketChangedAsync(t, cs.Changes, ct);
        history.Record(t, TicketEventTypes.FieldsChanged, Summarise(cs.Changes), cs.Changes);
        events.Publish(EventTypes.TicketUpdated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, cs.Changes));
        await SaveAsync(t, ct);
        return t;
    }

    // ───────────────────────────── Ownership ─────────────────────────────

    public async Task<Ticket> AssignAsync(Guid id, AssignTicketRequest r, CancellationToken ct = default)
    {
        var t = await LoadForUpdateAsync(id, ct);
        var selfAssign = r.AssigneeId == ctx.UserId && r.TeamId is null;
        if (!(selfAssign && ctx.Has(Permissions.TicketsEdit))) ctx.Require(Permissions.TicketsAssign);
        await EnsureNotLockedByOtherAsync(t, ct);
        await AssignCoreAsync(t, r.AssigneeId, r.TeamId, r.Note, ct);
        await SaveAsync(t, ct);
        return t;
    }

    /// <summary>Assignment without permission checks (routing, automation, approvals). Caller saves.</summary>
    public async Task AssignCoreAsync(Ticket t, Guid? assigneeId, Guid? teamId, string? note, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        if (teamId is { } team && !await db.Teams.AnyAsync(x => x.Id == team && x.DepartmentId == t.DepartmentId, ct))
            throw new ValidationException(new Dictionary<string, string> { ["teamId"] = "The team does not belong to the ticket's department." });
        Guid? finalAssignee = assigneeId;
        string? delegated = null;
        if (assigneeId is { } a)
        {
            await EnsureAssignableAsync(a, t.DepartmentId, ct);
            var del = await routing.ResolveDelegateAsync(a, ct);
            if (del is not null && del != a)
            {
                delegated = $"{lookups.User(a)} is out of office; delegated to {lookups.User(del)}";
                finalAssignee = del;
            }
        }
        var cs = new ChangeSet();
        if (teamId is not null) cs.Set("teamId", t.TeamId, teamId, v => t.TeamId = v, v => lookups.Team(v));
        var changed = cs.Set("assigneeId", t.AssigneeId, finalAssignee, v => t.AssigneeId = v, v => lookups.User(v));
        if (!cs.Any) return;
        var now = db.Clock.UtcNow;
        if (changed && finalAssignee is not null)
        {
            t.AssignedAt = now;
            t.AssignmentCount++;
            if (t.StatusCategory == StatusCategory.Open)
            {
                var from = t.StatusId;
                await SetStatusFieldsAsync(t, await StatusForCategoryAsync(StatusCategory.Assigned, ct), null);
                cs.Add("status", lookups.Status(from), lookups.Status(t.StatusId));
            }
        }
        else if (changed && finalAssignee is null && t.StatusCategory == StatusCategory.Assigned)
        {
            await SetStatusFieldsAsync(t, await StatusForCategoryAsync(StatusCategory.Open, ct), null);
        }
        t.LastActivityAt = now;
        var summary = finalAssignee is null ? "Unassigned" : $"Assigned to {lookups.User(finalAssignee)}" + (delegated is null ? "" : $" ({delegated})");
        history.Record(t, TicketEventTypes.Assigned, summary, cs.Changes, note is null ? null : new { note });
        events.Publish(EventTypes.TicketAssigned, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, cs.Changes, Note: note));
    }

    private async Task EnsureAssignableAsync(Guid userId, Guid departmentId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive) throw new ValidationException(new Dictionary<string, string> { ["assigneeId"] = "Unknown or inactive agent." });
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, ct);
        if (agent is null || agent.IsLightAgent || user.Kind == Domain.Identity.UserKind.LightAgent)
            throw new ValidationException(new Dictionary<string, string> { ["assigneeId"] = "Light agents and collaborators cannot own tickets." });
        if (!await db.AgentDepartments.AnyAsync(d => d.UserId == userId && d.DepartmentId == departmentId, ct))
            throw new ValidationException(new Dictionary<string, string> { ["assigneeId"] = "The agent is not a member of the ticket's department." });
    }

    public async Task<Ticket> TransferAsync(Guid id, TransferTicketRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsTransfer);
        var t = await LoadForUpdateAsync(id, ct);
        await EnsureNotLockedByOtherAsync(t, ct);
        if (t.DepartmentId == r.DepartmentId) throw new DomainException(ErrorCodes.Validation, "The ticket is already in that department.");
        var source = await db.Departments.AsNoTracking().FirstAsync(d => d.Id == t.DepartmentId, ct);
        if (!await db.Departments.AnyAsync(d => d.Id == r.DepartmentId && d.IsActive, ct)) throw new NotFoundException("Department", r.DepartmentId);
        if (source.TransferRequiresApproval && !ctx.Has(Permissions.ApprovalsDecide) && !ctx.IsSystem)
        {
            var approval = new ApprovalRequest
            {
                Type = "transfer", EntityType = "Ticket", EntityId = t.Id, TicketId = t.Id, RequestedById = ctx.UserId ?? Guid.Empty, Reason = r.Reason,
                PayloadJson = Json.Serialize(new { action = "transfer", r.DepartmentId, r.TeamId, r.AssigneeId, r.Reason }),
                ExpiresAt = db.Clock.UtcNow.AddDays(3)
            };
            db.ApprovalRequests.Add(approval);
            history.Record(t, TicketEventTypes.ApprovalRequested, $"Transfer to another department requested (needs supervisor approval)", null, new { approvalId = approval.Id });
            events.Publish(EventTypes.ApprovalRequested, "ApprovalRequest", approval.Id, new EntityEventPayload(approval.Id, "transfer"));
            await db.SaveChangesAsync(ct);
            throw new ApprovalRequiredException(approval.Id, "Transfers out of this department require supervisor approval. A request has been sent.");
        }
        await TransferCoreAsync(t, r.DepartmentId, r.TeamId, r.AssigneeId, r.Reason, ct);
        await SaveAsync(t, ct);
        return t;
    }

    public async Task TransferCoreAsync(Ticket t, Guid departmentId, Guid? teamId, Guid? assigneeId, string? reason, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        var cs = new ChangeSet();
        cs.Set("departmentId", t.DepartmentId, departmentId, v => t.DepartmentId = v, v => lookups.Department(v));
        cs.Set("teamId", t.TeamId, teamId, v => t.TeamId = v, v => lookups.Team(v));
        var keepAssignee = assigneeId ?? (t.AssigneeId is { } current && await db.AgentDepartments.AnyAsync(d => d.UserId == current && d.DepartmentId == departmentId, ct) ? current : null);
        if (assigneeId is { } a) await EnsureAssignableAsync(a, departmentId, ct);
        cs.Set("assigneeId", t.AssigneeId, keepAssignee, v => t.AssigneeId = v, v => lookups.User(v));
        t.LastActivityAt = db.Clock.UtcNow;
        if (t.AssigneeId is null)
        {
            if (t.StatusCategory == StatusCategory.Assigned) await SetStatusFieldsAsync(t, await StatusForCategoryAsync(StatusCategory.Open, ct), null);
            var decision = await routing.RouteAsync(t, ct);
            if (decision.AssigneeId is not null)
            {
                cs.Add("assigneeId", null, lookups.User(decision.AssigneeId));
                t.AssignedAt = db.Clock.UtcNow;
                t.AssignmentCount++;
            }
        }
        await sla.OnTicketChangedAsync(t, cs.Changes, ct);
        history.Record(t, TicketEventTypes.Transferred, $"Transferred to {lookups.Department(departmentId)}", cs.Changes, reason is null ? null : new { reason });
        events.Publish(EventTypes.TicketUpdated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, cs.Changes, Note: reason));
        if (cs.Has("assigneeId")) events.Publish(EventTypes.TicketAssigned, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, cs.Changes));
    }

    // ───────────────────────────── Status lifecycle ─────────────────────────────

    public async Task<Ticket> ChangeStatusAsync(Guid id, ChangeStatusRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsEdit);
        var t = await LoadForUpdateAsync(id, ct);
        await EnsureNotLockedByOtherAsync(t, ct);
        if (r.RowVersion is { Length: > 0 }) SetOriginalRowVersion(t, r.RowVersion);
        var to = await StatusAsync(r.StatusId, ct);
        if (to.Category.IsResolvedOrClosed()) ctx.Require(Permissions.TicketsResolve);
        if (t.StatusCategory.IsResolvedOrClosed() && to.Category.IsOpen()) ctx.Require(Permissions.TicketsReopen);
        await ApplyStatusAsync(t, to, r.Substatus, r.Comment, bypassRules: false, ct);
        await SaveAsync(t, ct);
        return t;
    }

    /// <summary>
    /// Central status transition with all side effects (dates, reopen counters, SLA pause/resume/completion, events).
    /// Enforces configured transitions and blueprints unless <paramref name="bypassRules"/> (system/blueprint engine).
    /// </summary>
    /// <param name="notifyCustomer">Resolve action only: carried on the ticket.status_changed event so the resolution notice
    /// (and CSAT survey) go out only when the agent asked to notify the customer. Null for every other status change.</param>
    public async Task ApplyStatusAsync(Ticket t, TicketStatus to, string? substatus, string? comment, bool bypassRules, CancellationToken ct,
        bool? notifyCustomer = null)
    {
        await lookups.LoadAsync(ct);
        var from = await db.TicketStatuses.AsNoTracking().FirstAsync(s => s.Id == t.StatusId, ct);
        if (from.Id == to.Id && substatus == t.Substatus) return;
        if (!bypassRules && !ctx.IsSystem)
        {
            if (t.WorkflowId is not null && !ctx.Has(Permissions.AdminAutomation))
                throw new DomainException(ErrorCodes.InvalidTransition, "This ticket follows a blueprint. Use one of the blueprint transitions to change its status.");
            var allowed = Json.GuidList(from.AllowedNextJson);
            if (from.Id != to.Id && allowed.Count > 0 && !allowed.Contains(to.Id))
                throw new DomainException(ErrorCodes.InvalidTransition, $"'{from.Name}' cannot move directly to '{to.Name}'.");
            var settingsT = await settings.TicketsAsync(ct);
            if (to.Category == StatusCategory.Resolved && settingsT.RequireResolutionCode && string.IsNullOrEmpty(t.ResolutionCode))
                throw new DomainException(ErrorCodes.MissingFields, "A resolution code is required before resolving. Use Resolve to enter it.");
            // Custom fields configured as "required for this status" (administration → ticket fields).
            var missing = Administration.CustomFieldRules.MissingForStatus(await customFields.DefinitionsAsync("Ticket", ct), t.CustomFieldsJson, t.DepartmentId, to.Id);
            if (missing.Count > 0)
                throw new DomainException(ErrorCodes.MissingFields, $"Fill in {string.Join(", ", missing)} before moving the ticket to '{to.Name}'.");
        }
        var subs = Json.StringList(to.SubstatusesJson);
        if (substatus is not null && subs.Count > 0 && !subs.Contains(substatus, StringComparer.OrdinalIgnoreCase))
            throw new DomainException(ErrorCodes.Validation, $"'{substatus}' is not a valid substatus of '{to.Name}'.");

        var cs = new ChangeSet();
        cs.Add("status", from.Name, to.Name);
        if (substatus != t.Substatus) cs.Add("substatus", t.Substatus, substatus);
        var wasOpen = t.StatusCategory.IsOpen();
        await SetStatusFieldsAsync(t, to, substatus);
        var now = db.Clock.UtcNow;
        string type = TicketEventTypes.StatusChanged;
        if (wasOpen && to.Category == StatusCategory.Resolved) { t.ResolvedAt = now; type = TicketEventTypes.Resolved; }
        if (to.Category == StatusCategory.Closed) { t.ClosedAt = now; t.ResolvedAt ??= now; t.AwaitingCustomerConfirmation = false; type = TicketEventTypes.Closed; }
        if (!wasOpen && to.Category.IsOpen())
        {
            t.ReopenCount++;
            t.ReopenedAt = now;
            t.ResolvedAt = null;
            t.ClosedAt = null;
            t.AwaitingCustomerConfirmation = false;
            type = TicketEventTypes.Reopened;
        }
        t.LastActivityAt = now;
        await sla.OnStatusChangedAsync(t, from, to, ct);
        history.Record(t, type, comment is null ? $"Status: {from.Name} → {to.Name}" : $"Status: {from.Name} → {to.Name} — {comment}", cs.Changes);
        events.Publish(EventTypes.TicketStatusChanged, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, cs.Changes, Note: comment, NotifyCustomer: notifyCustomer));
    }

    private async Task SetStatusFieldsAsync(Ticket t, TicketStatus to, string? substatus)
    {
        t.StatusId = to.Id;
        t.StatusCategory = to.Category;
        t.Substatus = substatus;
        await Task.CompletedTask;
    }

    public async Task<Ticket> EscalateAsync(Guid id, EscalateTicketRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsEscalate);
        var t = await LoadForUpdateAsync(id, ct);
        if (!t.StatusCategory.IsOpen()) throw new DomainException(ErrorCodes.Validation, "Only open tickets can be escalated.");
        await EscalateCoreAsync(t, r.Reason, r.EscalateToUserId, r.RaisePriority, ct);
        await SaveAsync(t, ct);
        return t;
    }

    public async Task EscalateCoreAsync(Ticket t, string reason, Guid? toUserId, bool raisePriority, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        var cs = new ChangeSet();
        cs.Set("isEscalated", t.IsEscalated, true, v => t.IsEscalated = v);
        cs.Set("escalationLevel", t.EscalationLevel, t.EscalationLevel + 1, v => t.EscalationLevel = v);
        if (raisePriority && t.Priority < TicketPriority.Critical)
        {
            cs.Set("priority", t.Priority, t.Priority + 1, v => t.Priority = v);
            await sla.OnTicketChangedAsync(t, cs.Changes, ct);
        }
        if (toUserId is { } u)
        {
            if (!await db.Users.AnyAsync(x => x.Id == u && x.IsActive, ct)) throw new NotFoundException("User", u);
            db.TicketWatchers.Add(new TicketWatcher { TicketId = t.Id, UserId = u, Kind = WatcherKind.Specialist, AddedById = ctx.UserId, AddedAt = db.Clock.UtcNow });
        }
        t.LastActivityAt = db.Clock.UtcNow;
        history.Record(t, TicketEventTypes.Escalated, $"Escalated to level {t.EscalationLevel}: {reason}", cs.Changes, new { reason, toUserId });
        events.Publish(EventTypes.TicketEscalated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, cs.Changes, Note: reason));
    }

    public async Task<Ticket> ResolveAsync(Guid id, ResolveTicketRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsResolve);
        var t = await LoadForUpdateAsync(id, ct);
        await EnsureNotLockedByOtherAsync(t, ct);
        var s = await settings.TicketsAsync(ct);
        if (s.RequireResolutionCode && string.IsNullOrWhiteSpace(r.ResolutionCode))
            throw new ValidationException(new Dictionary<string, string> { ["resolutionCode"] = "A resolution code is required." });
        t.ResolutionCode = r.ResolutionCode ?? t.ResolutionCode;
        t.RootCause = r.RootCause ?? t.RootCause;
        if (r.ResolutionHtml is not null) t.ResolutionHtml = html.Sanitize(r.ResolutionHtml);
        var confirm = r.RequestConfirmation || s.RequireCustomerConfirmation;
        // NotifyCustomer travels on the status event: the resolution notice (ResolutionNoticeHandler) and the CSAT survey
        // are sent only when it is true; false means nothing goes to the customer for this resolution.
        await ApplyStatusAsync(t, await StatusForCategoryAsync(StatusCategory.Resolved, ct), null, r.ResolutionCode,
            bypassRules: t.WorkflowId is not null && ctx.Has(Permissions.AdminAutomation), ct, notifyCustomer: r.NotifyCustomer);
        if (confirm && t.ContactId is not null)
        {
            t.AwaitingCustomerConfirmation = true;
            t.ConfirmationRequestedAt = db.Clock.UtcNow;
        }
        await SaveAsync(t, ct);
        return t;
    }

    public async Task<Ticket> ReopenAsync(Guid id, ReopenTicketRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsReopen);
        var t = await LoadForUpdateAsync(id, ct);
        if (t.StatusCategory.IsOpen()) throw new DomainException(ErrorCodes.Validation, "The ticket is already open.");
        if (t.MergedIntoTicketId is not null) throw new DomainException(ErrorCodes.Validation, "Merged tickets cannot be reopened.");
        await ReopenCoreAsync(t, r.Reason, ct);
        await SaveAsync(t, ct);
        return t;
    }

    public async Task ReopenCoreAsync(Ticket t, string reason, CancellationToken ct)
    {
        var target = t.AssigneeId is null ? await StatusForCategoryAsync(StatusCategory.Open, ct) : await StatusForCategoryAsync(StatusCategory.InProgress, ct);
        await ApplyStatusAsync(t, target, null, $"Reopened: {reason}", bypassRules: true, ct);
    }

    public async Task<Ticket> CloseAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsResolve);
        var t = await LoadForUpdateAsync(id, ct);
        await ApplyStatusAsync(t, await StatusForCategoryAsync(StatusCategory.Closed, ct), null, null, bypassRules: t.WorkflowId is not null && ctx.Has(Permissions.AdminAutomation), ct);
        await SaveAsync(t, ct);
        return t;
    }

    // ───────────────────────────── Merge / split / clone / links ─────────────────────────────

    public async Task<Ticket> MergeAsync(Guid targetId, MergeTicketsRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsMerge);
        if (r.SourceTicketIds.Count == 0 || r.SourceTicketIds.Contains(targetId)) throw new DomainException(ErrorCodes.Validation, "Choose other tickets to merge into this one.");
        var target = await LoadForUpdateAsync(targetId, ct);
        if (target.MergedIntoTicketId is not null) throw new DomainException(ErrorCodes.Validation, "The target ticket was itself merged.");
        var mergeRules = await settings.TicketsAsync(ct);
        if (r.SourceTicketIds.Distinct().Count() > mergeRules.MaxMergeSources)
            throw new DomainException(ErrorCodes.Validation, $"At most {mergeRules.MaxMergeSources} tickets can be merged at once.");
        var sources = new List<Ticket>();
        foreach (var sid in r.SourceTicketIds.Distinct())
        {
            var s = await LoadForUpdateAsync(sid, ct);
            if (s.MergedIntoTicketId is not null) throw new DomainException(ErrorCodes.Validation, $"Ticket {s.Number} was already merged.");
            if (!mergeRules.AllowMergeAcrossCustomers && s.CustomerId != target.CustomerId)
                throw new DomainException(ErrorCodes.Validation, $"Ticket {s.Number} belongs to another customer. Your organisation does not allow merging across customers.");
            if (!mergeRules.AllowMergeAcrossDepartments && s.DepartmentId != target.DepartmentId)
                throw new DomainException(ErrorCodes.Validation, $"Ticket {s.Number} is in another department. Your organisation does not allow merging across departments.");
            sources.Add(s);
        }

        await db.InTransactionAsync(async () =>
        {
            var closed = await StatusForCategoryAsync(StatusCategory.Closed, ct);
            foreach (var s in sources)
            {
                // Move conversation entries (re-sequenced after the target's entries; original timestamps kept).
                var threads = await db.TicketThreads.Where(x => x.TicketId == s.Id).OrderBy(x => x.Seq).ToListAsync(ct);
                foreach (var th in threads)
                {
                    th.TicketId = target.Id;
                    th.Seq = ++target.LastThreadSeq;
                }
                await db.TicketAttachments.Where(a => a.TicketId == s.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.TicketId, target.Id), ct);
                await db.Messages.Where(m => m.TicketId == s.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.TicketId, target.Id), ct);
                await db.Conversations.Where(c => c.TicketId == s.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.TicketId, target.Id), ct);
                await db.TicketExternalReferences.Where(x => x.TicketId == s.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.TicketId, target.Id), ct);
                await db.EngineeringLinks.Where(x => x.TicketId == s.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.TicketId, target.Id), ct);
                await db.TimeEntries.Where(x => x.TicketId == s.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.TicketId, target.Id), ct);
                var targetWatchers = await db.TicketWatchers.Where(w => w.TicketId == target.Id).Select(w => w.UserId).ToListAsync(ct);
                await db.TicketWatchers.Where(w => w.TicketId == s.Id && !targetWatchers.Contains(w.UserId))
                    .ExecuteUpdateAsync(u => u.SetProperty(w => w.TicketId, target.Id), ct);
                var srcTags = await db.TicketTags.Where(x => x.TicketId == s.Id).Select(x => x.TagId).ToListAsync(ct);
                foreach (var tagId in srcTags.Where(tg => target.Tags.All(x => x.TagId != tg)))
                    target.Tags.Add(new TicketTag { TicketId = target.Id, TagId = tagId });

                target.CustomerReplyCount += s.CustomerReplyCount;
                target.AgentReplyCount += s.AgentReplyCount;
                target.TimeSpentMinutes += s.TimeSpentMinutes;
                target.BillableMinutes += s.BillableMinutes;
                target.SupportCost += s.SupportCost;
                if (s.LastCustomerResponseAt > target.LastCustomerResponseAt || target.LastCustomerResponseAt is null) target.LastCustomerResponseAt = s.LastCustomerResponseAt;

                s.MergedIntoTicketId = target.Id;
                s.StatusId = closed.Id;
                s.StatusCategory = closed.Category;
                s.ClosedAt = db.Clock.UtcNow;
                s.LastActivityAt = db.Clock.UtcNow;
                s.TimeSpentMinutes = 0;
                s.BillableMinutes = 0;
                s.SupportCost = 0;
                await sla.CancelAsync(s, "merged", ct);
                db.TicketLinks.Add(new TicketLink { SourceTicketId = s.Id, TargetTicketId = target.Id, LinkType = TicketLinkType.Duplicate, CreatedById = ctx.UserId, CreatedAt = db.Clock.UtcNow });
                history.Record(s, TicketEventTypes.MergedInto, $"Merged into {lookups.Display(target.Number)}", null, new { targetId = target.Id, r.Note });
                events.Publish(EventTypes.TicketMerged, "Ticket", s.Id, new TicketEventPayload(s.Id, s.Number, [new FieldChange("mergedInto", null, target.Id.ToString())]));
            }
            target.LastActivityAt = db.Clock.UtcNow;
            history.Record(target, TicketEventTypes.Merged, $"Merged {string.Join(", ", sources.Select(s => lookups.Display(s.Number)))} into this ticket", null,
                new { sources = sources.Select(s => new { s.Id, s.Number }), r.Note });
            audit.Write(AuditActions.TicketMerged, "Ticket", target.Id, $"Merged {sources.Count} ticket(s) into {target.Number}", new { sources = sources.Select(s => s.Number) });
            events.Publish(EventTypes.TicketUpdated, "Ticket", target.Id, new TicketEventPayload(target.Id, target.Number, [new FieldChange("merged", null, string.Join(",", sources.Select(s => s.Number)))]));
            await db.SaveChangesAsync(ct);
        }, ct);
        return target;
    }

    public async Task<Ticket> SplitAsync(Guid id, SplitTicketRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsSplit);
        if (!(await settings.TicketsAsync(ct)).AllowSplit)
            throw new DomainException("split_disabled", "Splitting tickets is turned off for your organisation.");
        var source = await LoadForUpdateAsync(id, ct);
        if (r.ThreadIds.Count == 0) throw new DomainException(ErrorCodes.Validation, "Select at least one message to split off.");
        var threads = await db.TicketThreads.Where(x => x.TicketId == id && r.ThreadIds.Contains(x.Id)).OrderBy(x => x.Seq).ToListAsync(ct);
        if (threads.Count != r.ThreadIds.Count) throw new DomainException(ErrorCodes.Validation, "Some messages do not belong to this ticket.");
        var first = threads[0];
        var created = await CreateAsync(new CreateTicketRequest
        {
            Subject = r.Subject, DescriptionHtml = first.BodyHtml, Channel = source.Channel.ToString(), BrandId = source.BrandId, CustomerId = source.CustomerId,
            ContactId = source.ContactId, LocationId = source.LocationId, DepartmentId = source.DepartmentId, TeamId = source.TeamId,
            ProductId = source.ProductId, ModuleId = source.ModuleId, VersionId = source.VersionId, InstallationId = source.InstallationId,
            CategoryId = source.CategoryId, Priority = source.Priority.ToString(), ParentTicketId = r.LinkAsChild ? source.Id : null, SkipRouting = true,
            AssigneeId = source.AssigneeId
        }, TicketCreationSource.Agent with { EnforceRequiredFields = false }, ct);
        await db.InTransactionAsync(async () =>
        {
            foreach (var th in threads)
            {
                th.TicketId = created.Id;
                th.Seq = ++created.LastThreadSeq;
                await db.TicketAttachments.Where(a => a.ThreadId == th.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.TicketId, created.Id), ct);
            }
            db.TicketLinks.Add(new TicketLink { SourceTicketId = created.Id, TargetTicketId = source.Id, LinkType = TicketLinkType.SplitFrom, CreatedById = ctx.UserId, CreatedAt = db.Clock.UtcNow });
            history.Record(source, TicketEventTypes.Split, $"{threads.Count} message(s) split into {lookups.Display(created.Number)}", null, new { newTicketId = created.Id });
            history.Record(created, TicketEventTypes.Split, $"Split from {lookups.Display(source.Number)}", null, new { sourceTicketId = source.Id });
            await db.SaveChangesAsync(ct);
        }, ct);
        return created;
    }

    public async Task<Ticket> CloneAsync(Guid id, CancellationToken ct = default)
    {
        // Cloning creates a new ticket: it needs tickets.create (the same check the desk uses for the Clone action).
        ctx.Require(Permissions.TicketsCreate);
        var s = await LoadForUpdateAsync(id, ct);
        var clone = await CreateAsync(new CreateTicketRequest
        {
            Subject = $"{s.Subject} (copy)", DescriptionHtml = s.DescriptionHtml, Channel = Channel.Internal.ToString(), BrandId = s.BrandId, CustomerId = s.CustomerId,
            ContactId = s.ContactId, LocationId = s.LocationId, DepartmentId = s.DepartmentId, ProductId = s.ProductId, ModuleId = s.ModuleId,
            VersionId = s.VersionId, InstallationId = s.InstallationId, CategoryId = s.CategoryId, SubcategoryId = s.SubcategoryId, IssueTypeId = s.IssueTypeId,
            Priority = s.Priority.ToString(), Severity = s.Severity?.ToString(), Tags = s.Tags.Select(x => x.Tag?.Name ?? "").Where(n => n.Length > 0).ToList(),
            CustomFields = Json.Object(s.CustomFieldsJson)
        }, TicketCreationSource.Agent with { EnforceRequiredFields = false }, ct);
        db.TicketLinks.Add(new TicketLink { SourceTicketId = clone.Id, TargetTicketId = s.Id, LinkType = TicketLinkType.ClonedFrom, CreatedById = ctx.UserId, CreatedAt = db.Clock.UtcNow });
        history.Record(clone, TicketEventTypes.Cloned, $"Cloned from {lookups.Display(s.Number)}");
        await db.SaveChangesAsync(ct);
        return clone;
    }

    public async Task<TicketLink> LinkAsync(Guid id, LinkTicketRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsEdit);
        var t = await LoadForUpdateAsync(id, ct);
        var target = await LoadForUpdateAsync(r.TargetTicketId, ct);
        if (t.Id == target.Id) throw new DomainException(ErrorCodes.Validation, "A ticket cannot be linked to itself.");
        var type = ParseEnum(r.LinkType, TicketLinkType.Related, "linkType");
        if (type == TicketLinkType.Related && await db.TicketLinks.AnyAsync(l => l.LinkType == type &&
                ((l.SourceTicketId == t.Id && l.TargetTicketId == target.Id) || (l.SourceTicketId == target.Id && l.TargetTicketId == t.Id)), ct))
            throw new ConflictException("already_linked", "These tickets are already linked.");
        var link = new TicketLink { SourceTicketId = t.Id, TargetTicketId = target.Id, LinkType = type, CreatedById = ctx.UserId, CreatedAt = db.Clock.UtcNow };
        db.TicketLinks.Add(link);
        await lookups.LoadAsync(ct);
        history.Record(t, TicketEventTypes.Linked, $"Linked as {type} to {lookups.Display(target.Number)}");
        history.Record(target, TicketEventTypes.Linked, $"Linked as {type} from {lookups.Display(t.Number)}");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("already_linked", "These tickets are already linked."); }
        return link;
    }

    public async Task UnlinkAsync(Guid id, Guid linkId, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsEdit);
        var t = await LoadForUpdateAsync(id, ct);
        var link = await db.TicketLinks.FirstOrDefaultAsync(l => l.Id == linkId && (l.SourceTicketId == id || l.TargetTicketId == id), ct) ?? throw new NotFoundException("Link", linkId);
        db.TicketLinks.Remove(link);
        history.Record(t, TicketEventTypes.Unlinked, $"Removed {link.LinkType} link");
        await db.SaveChangesAsync(ct);
    }

    public async Task<Ticket> SetTagsAsync(Guid id, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsEdit);
        var t = await LoadForUpdateAsync(id, ct);
        var before = await db.TicketTags.Where(x => x.TicketId == id).Select(x => x.Tag!.Name).ToListAsync(ct);
        db.TicketTags.RemoveRange(t.Tags);
        t.Tags.Clear();
        await ApplyTagsAsync(t, tags, ct);
        var after = tags.Select(NormalizeTag).Where(x => x.Length > 0).Distinct().OrderBy(x => x).ToList();
        if (!before.OrderBy(x => x).SequenceEqual(after))
        {
            var change = new FieldChange("tags", string.Join(", ", before.OrderBy(x => x)), string.Join(", ", after));
            history.Record(t, TicketEventTypes.TagsChanged, "Tags changed", [change]);
            events.Publish(EventTypes.TicketUpdated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, [change]));
            t.LastActivityAt = db.Clock.UtcNow;
        }
        await SaveAsync(t, ct);
        return t;
    }

    public async Task ApplyTagsAsync(Ticket t, IEnumerable<string> tags, CancellationToken ct)
    {
        foreach (var name in tags.Select(NormalizeTag).Where(x => x.Length > 0).Distinct())
        {
            var tag = await db.Tags.FirstOrDefaultAsync(x => x.Name == name, ct) ?? db.Tags.Local.FirstOrDefault(x => x.Name == name);
            if (tag is null)
            {
                tag = new Tag { Name = name };
                db.Tags.Add(tag);
            }
            if (t.Tags.All(x => x.TagId != tag.Id)) t.Tags.Add(new TicketTag { TicketId = t.Id, TagId = tag.Id, Tag = tag });
        }
    }

    public static string NormalizeTag(string tag)
    {
        var s = tag.Trim().ToLowerInvariant();
        return s.Length > 50 ? s[..50] : s;
    }

    // ───────────────────────────── Locks ─────────────────────────────

    public async Task<TicketLockInfo> LockAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsEdit);
        var t = await LoadForUpdateAsync(id, ct);
        var existing = await cache.GetAsync<TicketLockInfo>(LockKey(id), ct);
        if (existing is not null && existing.UserId != ctx.UserId && existing.Until > db.Clock.UtcNow)
            throw new DomainException(ErrorCodes.TicketLocked, $"{existing.UserName} is working on this ticket until {existing.Until:HH:mm} UTC.", 409);
        var minutes = (await settings.TicketsAsync(ct)).SoftLockMinutes;
        var info = new TicketLockInfo(ctx.UserId!.Value, ctx.ActorName ?? "Agent", db.Clock.UtcNow.AddMinutes(minutes));
        await cache.SetAsync(LockKey(id), info, TimeSpan.FromMinutes(minutes), ct);
        events.Publish(EventTypes.TicketUpdated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, [new FieldChange("lock", null, info.UserName)]));
        await db.SaveChangesAsync(ct);
        return info;
    }

    public async Task UnlockAsync(Guid id, CancellationToken ct = default)
    {
        var t = await LoadForUpdateAsync(id, ct);
        var existing = await cache.GetAsync<TicketLockInfo>(LockKey(id), ct);
        if (existing is null) return;
        if (existing.UserId != ctx.UserId && !ctx.Has(Permissions.SupervisorMonitor)) throw new ForbiddenException("Only the lock owner or a supervisor can unlock.");
        await cache.RemoveAsync(LockKey(id), ct);
        events.Publish(EventTypes.TicketUpdated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, [new FieldChange("lock", existing.UserName, null)]));
        await db.SaveChangesAsync(ct);
    }

    public Task<TicketLockInfo?> GetLockAsync(Guid id, CancellationToken ct = default) => cache.GetAsync<TicketLockInfo>(LockKey(id), ct);

    private async Task EnsureNotLockedByOtherAsync(Ticket t, CancellationToken ct)
    {
        if (ctx.IsSystem || ctx.UserId is null) return;
        var l = await cache.GetAsync<TicketLockInfo>(LockKey(t.Id), ct);
        if (l is not null && l.UserId != ctx.UserId && l.Until > db.Clock.UtcNow && !ctx.Has(Permissions.SupervisorMonitor))
            throw new DomainException(ErrorCodes.TicketLocked, $"{l.UserName} has locked this ticket until {l.Until:HH:mm} UTC.", 409);
    }

    private static string LockKey(Guid id) => $"ticket-lock:{id:N}";

    // ───────────────────────────── Entitlement override, delete, spam ─────────────────────────────

    public async Task<Ticket> OverrideEntitlementAsync(Guid id, OverrideEntitlementRequest r, CancellationToken ct = default)
    {
        var agentsMayOverride = (await settings.TicketsAsync(ct)).AllowAgentsToOverrideEntitlement;
        if (!TicketAccess.CanOverrideEntitlement(ctx, agentsMayOverride))
            throw new ForbiddenException(agentsMayOverride
                ? "Overriding support eligibility needs the tickets.edit or tickets.override_entitlement permission."
                : "Overriding support eligibility needs the tickets.override_entitlement permission.");
        if (string.IsNullOrWhiteSpace(r.Reason)) throw new ValidationException(new Dictionary<string, string> { ["reason"] = "A reason is required." });
        var t = await LoadForUpdateAsync(id, ct);
        if (t.MergedIntoTicketId is not null) throw new DomainException(ErrorCodes.Validation, "This ticket was merged; work on the surviving ticket.");
        var before = t.EntitlementStatus;
        t.EntitlementStatus = EntitlementStatus.Overridden;
        t.EntitlementNote = r.Reason;
        if (r.ContractId is { } cid)
        {
            if (!await db.Contracts.AnyAsync(c => c.Id == cid && c.CustomerId == t.CustomerId, ct)) throw new ValidationException(new Dictionary<string, string> { ["contractId"] = "The contract does not belong to this customer." });
            t.ContractId = cid;
        }
        var change = new FieldChange("entitlementStatus", before.ToString(), "Overridden");
        history.Record(t, TicketEventTypes.EntitlementOverridden, $"Support eligibility overridden: {r.Reason}", [change]);
        audit.Write(AuditActions.EntitlementOverridden, "Ticket", t.Id, $"Entitlement overridden on #{t.Number}: {r.Reason}",
            new { r.Reason, r.ContractId, from = before.ToString(), allowedBy = ctx.Has(Permissions.TicketsOverrideEntitlement) ? "permission" : "tenant setting" });
        await sla.OnTicketChangedAsync(t, [change], ct);
        await SaveAsync(t, ct);
        return t;
    }

    public async Task DeleteAsync(Guid id, bool spam, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsDelete);
        var t = await LoadForUpdateAsync(id, ct);
        if (await TicketAccess.HoldsCovering(db, t.Id, t.CustomerId, t.ContactId).AnyAsync(ct))
            throw new DomainException("legal_hold", "This ticket is under legal hold and cannot be deleted.", 409);
        if (spam) t.IsSpam = true;
        t.IsDeleted = true;
        t.DeletedAt = db.Clock.UtcNow;
        await sla.CancelAsync(t, spam ? "spam" : "deleted", ct);
        history.Record(t, spam ? TicketEventTypes.MarkedSpam : TicketEventTypes.Deleted, spam ? "Marked as spam" : "Deleted");
        audit.Write(AuditActions.TicketDeleted, "Ticket", t.Id, $"Ticket #{t.Number} {(spam ? "marked as spam" : "deleted")}");
        events.Publish(EventTypes.TicketDeleted, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number));
        await SaveAsync(t, ct);
    }

    public async Task RestoreAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsDelete);
        var t = await db.Tickets.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted, ct)
                ?? throw new NotFoundException("Deleted ticket", id);
        await EnsureVisibleAsync(t, ct);
        var wasSpam = t.IsSpam;
        t.IsDeleted = false;
        t.IsSpam = false;
        t.DeletedAt = null;
        t.LastActivityAt = db.Clock.UtcNow;
        history.Record(t, TicketEventTypes.Restored, wasSpam ? "Restored from spam" : "Restored from the recycle bin");
        audit.Write(TicketRestoredAudit, "Ticket", t.Id, $"Ticket #{t.Number} restored{(wasSpam ? " (was spam)" : "")}", new { restored = true, wasSpam });
        events.Publish(EventTypes.TicketUpdated, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, [new FieldChange("deleted", "true", "false")]));
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Bulk ─────────────────────────────

    public async Task<BulkResultDto> BulkAsync(BulkTicketRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.TicketsBulk);
        if (r.TicketIds.Count > 500) throw new DomainException(ErrorCodes.Validation, "At most 500 tickets per bulk action.");
        var p = r.Parameters ?? Json.Element("{}");
        string? S(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        Guid? G(string name) => Guid.TryParse(S(name), out var g) ? g : null;
        List<string> L(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

        var failures = new List<BulkFailureDto>();
        var ok = 0;
        foreach (var id in r.TicketIds.Distinct())
        {
            try
            {
                switch (r.Action)
                {
                    case BulkActions.Assign: await AssignAsync(id, new AssignTicketRequest(G("assigneeId"), G("teamId"), "Bulk assignment"), ct); break;
                    case BulkActions.Status: await ChangeStatusAsync(id, new ChangeStatusRequest(G("statusId") ?? throw new DomainException(ErrorCodes.Validation, "statusId is required."), null, "Bulk update", null), ct); break;
                    case BulkActions.Priority:
                    {
                        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Ticket", id);
                        await UpdateAsync(id, new UpdateTicketRequest { RowVersion = Convert.ToBase64String(t.RowVersion), Priority = S("priority") }, ct);
                        break;
                    }
                    case BulkActions.TagsAdd:
                    {
                        var current = await db.TicketTags.Where(x => x.TicketId == id).Select(x => x.Tag!.Name).ToListAsync(ct);
                        await SetTagsAsync(id, current.Concat(L("tags")).ToList(), ct);
                        break;
                    }
                    case BulkActions.TagsRemove:
                    {
                        var remove = L("tags").Select(NormalizeTag).ToHashSet();
                        var current = await db.TicketTags.Where(x => x.TicketId == id).Select(x => x.Tag!.Name).ToListAsync(ct);
                        await SetTagsAsync(id, current.Where(x => !remove.Contains(x)).ToList(), ct);
                        break;
                    }
                    case BulkActions.Transfer: await TransferAsync(id, new TransferTicketRequest(G("departmentId") ?? throw new DomainException(ErrorCodes.Validation, "departmentId is required."), G("teamId"), null, "Bulk transfer"), ct); break;
                    case BulkActions.Close: await CloseAsync(id, ct); break;
                    case BulkActions.Delete: await DeleteAsync(id, false, ct); break;
                    case BulkActions.Spam: await DeleteAsync(id, true, ct); break;
                    case BulkActions.Watch:
                    {
                        var t = await LoadForUpdateAsync(id, ct);
                        if (!await db.TicketWatchers.AnyAsync(w => w.TicketId == id && w.UserId == ctx.UserId, ct))
                            db.TicketWatchers.Add(new TicketWatcher { TicketId = t.Id, UserId = ctx.UserId!.Value, Kind = WatcherKind.Watcher, AddedById = ctx.UserId, AddedAt = db.Clock.UtcNow });
                        await db.SaveChangesAsync(ct);
                        break;
                    }
                    default: throw new DomainException(ErrorCodes.Validation, $"Unknown bulk action '{r.Action}'.");
                }
                ok++;
            }
            catch (DomainException ex)
            {
                failures.Add(new BulkFailureDto(id, ex.Code, ex.Message));
                db.ChangeTracker.Clear();
            }
        }
        return new BulkResultDto(r.TicketIds.Count, ok, failures);
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    public async Task SaveAsync(Ticket t, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException(t.Id);
        }
    }

    private void SetOriginalRowVersion(Ticket t, string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion)) throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "rowVersion is required for updates." });
        byte[] bytes;
        try { bytes = Convert.FromBase64String(rowVersion); }
        catch (FormatException) { throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "Invalid rowVersion." }); }
        if (!bytes.SequenceEqual(t.RowVersion)) throw new ConcurrencyConflictException(t.Id);
        db.Entry(t).Property(x => x.RowVersion).OriginalValue = bytes;
    }

    public static T ParseEnum<T>(string? value, T fallback, string field) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return Enum.TryParse<T>(value, true, out var v) && Enum.IsDefined(v) ? v
            : throw new ValidationException(new Dictionary<string, string> { [field] = $"'{value}' is not a valid {field}." });
    }

    private static string Summarise(IReadOnlyList<FieldChange> changes) =>
        changes.Count == 1 ? $"{changes[0].Field}: {changes[0].From ?? "—"} → {changes[0].To ?? "—"}" : $"{changes.Count} fields changed";
}
