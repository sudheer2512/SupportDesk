using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Portal;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Configuration;
using SupportDesk.Domain.Customers;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Portal;

/// <summary>
/// Customer tickets in the portal. Access scope (Own / Location / Organisation, from the contact record) is enforced in
/// every query; out-of-scope tickets are 404. Only public conversation entries, public attachments, customer-facing
/// status labels and portal-visible custom fields are ever returned. Creation, replies, reopen and closing reuse the
/// ticket core (TicketService / ConversationService) so SLA, routing, history and events behave exactly as for agents.
/// </summary>
public sealed class PortalTicketService(
    DeskDbContext db,
    RequestContext ctx,
    PortalContext portal,
    TicketService tickets,
    ConversationService conversations,
    TicketLookups lookups,
    TicketHistory history,
    TenantSettingsService settings,
    IEventPublisher events,
    FileService files,
    LimitGuard limits)
{
    public const string ScopeOwn = "own";
    public const string ScopeLocation = "location";
    public const string ScopeOrganisation = "organisation";
    private static readonly string[] PortalPriorities = ["Low", "Medium", "High"];
    private static readonly ThreadKind[] PublicKinds = [ThreadKind.CustomerMessage, ThreadKind.AgentReply, ThreadKind.ChatTranscript, ThreadKind.CallLog, ThreadKind.SystemMessage];

    // ───────────────────────────── Scope ─────────────────────────────

    public static string MaxScope(Contact c) => c.PortalAccess switch
    {
        PortalAccessScope.Organisation when c.CustomerId is not null => ScopeOrganisation,
        PortalAccessScope.Location when c.CustomerId is not null && c.LocationId is not null => ScopeLocation,
        _ => ScopeOwn
    };

    public PortalTicketScopeDto Scope()
    {
        var me = portal.Me;
        var max = MaxScope(me);
        var available = new List<string> { ScopeOwn };
        if (max != ScopeOwn && me.LocationId is not null) available.Add(ScopeLocation);
        if (max == ScopeOrganisation) available.Add(ScopeOrganisation);
        return new PortalTicketScopeDto(max, available);
    }

    /// <summary>Tickets the signed-in contact may see in <paramref name="requested"/> scope (capped at their access).</summary>
    public IQueryable<Ticket> Visible(string? requested = null)
    {
        var me = portal.Me;
        var max = MaxScope(me);
        var scope = (requested ?? max).Trim().ToLowerInvariant();
        if (scope == ScopeOrganisation && max != ScopeOrganisation) scope = max;
        if (scope == ScopeLocation && (max == ScopeOwn || me.LocationId is null)) scope = ScopeOwn;
        if (scope is not (ScopeOwn or ScopeLocation or ScopeOrganisation)) scope = max;
        var id = me.Id;
        var customer = me.CustomerId;
        var location = me.LocationId;
        var q = db.Tickets.AsNoTracking().Where(t => !t.IsSpam);
        return scope switch
        {
            ScopeOrganisation => q.Where(t => t.ContactId == id || t.CustomerId == customer),
            ScopeLocation => q.Where(t => t.ContactId == id || (t.CustomerId == customer && t.LocationId == location)),
            _ => q.Where(t => t.ContactId == id)
        };
    }

    // ───────────────────────────── Lists ─────────────────────────────

    public async Task<PagedResult<PortalTicketListItemDto>> ListAsync(PortalTicketQuery q, CancellationToken ct = default)
    {
        var query = Visible(q.Scope);
        switch ((q.Status ?? "all").Trim().ToLowerInvariant())
        {
            case "open": query = query.Where(t => t.StatusCategory < StatusCategory.Resolved); break;
            case "closed": query = query.Where(t => t.StatusCategory >= StatusCategory.Resolved); break;
            case "awaiting":
                query = query.Where(t => t.AwaitingCustomerConfirmation || (t.StatusCategory < StatusCategory.Resolved && t.LastAgentResponseAt != null
                                                                             && (t.LastCustomerResponseAt == null || t.LastAgentResponseAt > t.LastCustomerResponseAt)));
                break;
        }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim().TrimStart('#');
            query = long.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? query.Where(t => t.Number == number || t.Subject.Contains(term))
                : query.Where(t => t.Subject.Contains(term));
        }
        var cursor = DbExtensions.DecodeCursor(q.Cursor);
        if (cursor is not null)
        {
            var v = DateTime.Parse(cursor.V, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            query = query.Where(t => t.LastActivityAt < v || (t.LastActivityAt == v && t.Id.CompareTo(cursor.Id) < 0));
        }
        var limit = Math.Clamp(q.Limit ?? 20, 1, 100);
        var page = await query.OrderByDescending(t => t.LastActivityAt).ThenByDescending(t => t.Id).Take(limit + 1).ToListAsync(ct);
        var more = page.Count > limit;
        if (more) page.RemoveAt(page.Count - 1);
        var items = await MapListAsync(page, ct);
        return new PagedResult<PortalTicketListItemDto>(items, more ? DbExtensions.EncodeCursor(page[^1].LastActivityAt.ToString("O", CultureInfo.InvariantCulture), page[^1].Id) : null);
    }

    public async Task<PortalTicketStatsDto> StatsAsync(CancellationToken ct = default)
    {
        var mine = Visible();
        var open = await mine.CountAsync(t => t.StatusCategory < StatusCategory.Resolved, ct);
        var awaitingYou = await mine.CountAsync(t => t.StatusCategory < StatusCategory.Resolved && t.LastAgentResponseAt != null
                                                     && (t.LastCustomerResponseAt == null || t.LastAgentResponseAt > t.LastCustomerResponseAt), ct);
        var me = portal.Me.Id;
        var confirm = await mine.CountAsync(t => t.AwaitingCustomerConfirmation && t.ContactId == me, ct);
        return new PortalTicketStatsDto(open, awaitingYou, confirm);
    }

    private async Task<List<PortalTicketListItemDto>> MapListAsync(IReadOnlyList<Ticket> rows, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        var contactIds = rows.Where(t => t.ContactId.HasValue).Select(t => t.ContactId!.Value).Distinct().ToList();
        var contacts = await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => contactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);
        var locationIds = rows.Where(t => t.LocationId.HasValue).Select(t => t.LocationId!.Value).Distinct().ToList();
        var locations = await db.CustomerLocations.AsNoTracking().Where(l => locationIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var labels = await StatusLabelsAsync(ct);
        var me = portal.Me.Id;
        return rows.Select(t => new PortalTicketListItemDto(
            t.Id, t.Number, lookups.Display(t.Number), t.Subject, Label(labels, t.StatusId, t.StatusCategory), t.StatusCategory.ToString(), t.StatusCategory.IsOpen(),
            t.Priority.ToString(), t.CreatedAt, t.LastActivityAt, t.ContactId, t.ContactId is { } c ? contacts.GetValueOrDefault(c) : null, t.ContactId == me,
            t.LocationId is { } l ? locations.GetValueOrDefault(l) : null, lookups.Product(t.ProductId), t.AwaitingCustomerConfirmation,
            t.StatusCategory.IsOpen() && t.LastAgentResponseAt is not null && (t.LastCustomerResponseAt is null || t.LastAgentResponseAt > t.LastCustomerResponseAt))).ToList();
    }

    private async Task<Dictionary<Guid, string?>> StatusLabelsAsync(CancellationToken ct) =>
        await db.TicketStatuses.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.CustomerLabel, ct);

    /// <summary>Customer-facing status label (never the internal status name).</summary>
    public static string Label(IReadOnlyDictionary<Guid, string?> labels, Guid statusId, StatusCategory category) =>
        labels.GetValueOrDefault(statusId) is { Length: > 0 } label ? label : category switch
        {
            StatusCategory.Open => "Received",
            StatusCategory.Resolved => "Resolved",
            StatusCategory.Closed => "Closed",
            _ => "In progress"
        };

    // ───────────────────────────── Detail ─────────────────────────────

    public async Task<PortalTicketDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var t = await Visible(ScopeOrganisation).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Ticket", id);
        return await MapDetailAsync(t, ct);
    }

    private async Task<PortalTicketDetailDto> MapDetailAsync(Ticket t, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        var s = await settings.TicketsAsync(ct);
        var labels = await StatusLabelsAsync(ct);
        var now = db.Clock.UtcNow;
        var contact = t.ContactId is { } cid ? await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking().Where(c => c.Id == cid).Select(c => c.FullName).FirstOrDefaultAsync(ct) : null;
        var customer = t.CustomerId is { } cu ? await db.Customers.AsNoTracking().Where(c => c.Id == cu).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var location = t.LocationId is { } lo ? await db.CustomerLocations.AsNoTracking().Where(l => l.Id == lo).Select(l => l.Name).FirstOrDefaultAsync(ct) : null;
        var department = await db.Departments.AsNoTracking().Where(d => d.Id == t.DepartmentId && d.VisibleInPortal).Select(d => d.Name).FirstOrDefaultAsync(ct);
        var category = t.CategoryId is { } ca ? await db.TicketCategories.AsNoTracking().Where(c => c.Id == ca && c.VisibleInPortal).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var installation = t.InstallationId is { } inst ? await db.Installations.AsNoTracking().Where(i => i.Id == inst).Select(i => i.Name).FirstOrDefaultAsync(ct) : null;
        var fields = await PortalFieldsAsync(t, ct);
        var attachments = await PublicAttachmentsAsync(t.Id, null, ct);
        var mine = t.ContactId == portal.Me.Id;
        var open = t.StatusCategory.IsOpen();
        var since = t.ClosedAt ?? t.ResolvedAt;
        DateTime? reopenUntil = !open && s.AllowCustomerReopen && t.MergedIntoTicketId is null && since is { } at ? at.AddDays(s.ReopenWindowDays) : null;
        var canReopen = reopenUntil is { } until && until >= now;
        var actions = new PortalTicketActionsDto(
            Reply: t.MergedIntoTicketId is null && (open || canReopen),
            Reopen: canReopen,
            Confirm: mine && t.AwaitingCustomerConfirmation && t.StatusCategory == StatusCategory.Resolved,
            Rate: mine && s.CsatEnabled && !open && t.CsatScore is null);
        return new PortalTicketDetailDto(t.Id, t.Number, lookups.Display(t.Number), t.Subject, t.DescriptionHtml, Label(labels, t.StatusId, t.StatusCategory),
            t.StatusCategory.ToString(), open, t.Priority.ToString(), t.Channel.ToString(), t.CreatedAt, t.LastActivityAt, t.ResolvedAt, t.ClosedAt, t.ContactId,
            contact, mine, customer, location, department, category, lookups.Product(t.ProductId), installation, fields, attachments, t.AwaitingCustomerConfirmation,
            t.CsatScore, mine ? t.CsatComment : null, actions, reopenUntil);
    }

    private async Task<List<PortalFieldValueDto>> PortalFieldsAsync(Ticket t, CancellationToken ct)
    {
        var defs = await db.CustomFields.AsNoTracking().Where(f => f.Entity == "Ticket" && f.IsActive && f.VisibleInPortal).OrderBy(f => f.SortOrder).ToListAsync(ct);
        var values = Json.Object(t.CustomFieldsJson);
        var list = new List<PortalFieldValueDto>();
        foreach (var def in defs)
        {
            if (!values.TryGetValue(def.Key, out var v)) continue;
            list.Add(new PortalFieldValueDto(def.Key, def.Label, Display(def, v)));
        }
        return list;
    }

    private static string? Display(CustomFieldDefinition def, JsonElement v)
    {
        var options = Json.DeserializeOr(def.OptionsJson, new List<JsonElement>())
            .Where(o => o.ValueKind == JsonValueKind.Object).Select(o => new
            {
                Value = o.TryGetProperty("value", out var x) ? x.ToString() : "",
                Label = o.TryGetProperty("label", out var l) ? l.ToString() : ""
            }).ToList();
        string Label(string raw) => options.FirstOrDefault(o => o.Value == raw)?.Label is { Length: > 0 } l ? l : raw;
        return v.ValueKind switch
        {
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(e => Label(e.ToString()))),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => def.FieldType is CustomFieldType.Picklist ? Label(v.ToString()) : v.ToString()
        };
    }

    private async Task<List<PortalAttachmentDto>> PublicAttachmentsAsync(Guid ticketId, IReadOnlyCollection<Guid>? threadIds, CancellationToken ct)
    {
        var q = db.TicketAttachments.AsNoTracking().Where(a => a.TicketId == ticketId && a.Visibility == Visibility.Public);
        q = threadIds is null ? q.Where(a => a.ThreadId == null) : q.Where(a => a.ThreadId != null && threadIds.Contains(a.ThreadId.Value));
        var rows = await q.Join(db.StoredFiles.AsNoTracking(), a => a.FileId, f => f.Id, (a, f) => new { a, f.ScanStatus })
            .Where(x => x.ScanStatus != Domain.Platform.ScanStatus.Infected && x.ScanStatus != Domain.Platform.ScanStatus.Blocked)
            .OrderBy(x => x.a.CreatedAt).Select(x => x.a).ToListAsync(ct);
        return rows.Select(a => new PortalAttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes, $"{portal.ApiBase}/tickets/{ticketId}/attachments/{a.Id}", a.CreatedAt))
            .ToList();
    }

    public async Task<List<PortalMessageDto>> MessagesAsync(Guid id, CancellationToken ct = default)
    {
        var t = await Visible(ScopeOrganisation).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Ticket", id);
        var threads = await db.TicketThreads.AsNoTracking()
            .Where(x => x.TicketId == t.Id && x.Visibility == Visibility.Public && PublicKinds.Contains(x.Kind))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Seq).ToListAsync(ct);
        return await MapMessagesAsync(t.Id, threads, ct);
    }

    private async Task<List<PortalMessageDto>> MapMessagesAsync(Guid ticketId, IReadOnlyList<TicketThread> threads, CancellationToken ct)
    {
        var ids = threads.Select(x => x.Id).ToList();
        var attachments = (await PublicAttachmentsAsync(ticketId, ids, ct)).ToDictionary(a => a.Id);
        var byThread = (await db.TicketAttachments.AsNoTracking().Where(a => a.ThreadId != null && ids.Contains(a.ThreadId.Value)).Select(a => new { a.Id, a.ThreadId }).ToListAsync(ct))
            .Where(a => attachments.ContainsKey(a.Id)).GroupBy(a => a.ThreadId!.Value).ToDictionary(g => g.Key, g => g.Select(a => attachments[a.Id]).ToList());
        var me = portal.Me.Id;
        return threads.Select(x => new PortalMessageDto(x.Id, x.Seq,
            x.AuthorType == AuthorType.Customer ? "customer" : x.AuthorType == AuthorType.System ? "system" : "agent",
            x.AuthorType == AuthorType.Customer ? x.AuthorName : x.AuthorName ?? "Support team", x.AuthorContactId == me,
            x.BodyHtml ?? TextNormalization.TextToHtml(x.BodyText), x.CreatedAt, byThread.GetValueOrDefault(x.Id) ?? [])).ToList();
    }

    public async Task<(Domain.Tickets.TicketAttachment Attachment, Stream Content)> OpenAttachmentAsync(Guid ticketId, Guid attachmentId, CancellationToken ct = default)
    {
        var t = await Visible(ScopeOrganisation).FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        var att = await db.TicketAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == t.Id && a.Visibility == Visibility.Public, ct)
                  ?? throw new NotFoundException("Attachment", attachmentId);
        if (att.ThreadId is { } threadId && !await db.TicketThreads.AnyAsync(x => x.Id == threadId && x.Visibility == Visibility.Public && PublicKinds.Contains(x.Kind), ct))
            throw new NotFoundException("Attachment", attachmentId);
        var (_, stream) = await files.OpenAsync(att.FileId, ct);
        return (att, stream);
    }

    // ───────────────────────────── Create ─────────────────────────────

    public async Task<PortalTicketFormDto> FormAsync(CancellationToken ct = default)
    {
        var me = portal.Me;
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive && d.VisibleInPortal).OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new PortalOptionDto(d.Id, d.Name, d.Description)).ToListAsync(ct);
        var deptIds = departments.Select(d => d.Id).ToList();
        var categories = await db.TicketCategories.AsNoTracking()
            .Where(c => c.IsActive && c.VisibleInPortal && (c.DepartmentId == null || deptIds.Contains(c.DepartmentId.Value)))
            .OrderBy(c => c.SortOrder).Select(c => new PortalCategoryOptionDto(c.Id, c.ParentId, c.Name, c.DepartmentId)).ToListAsync(ct);
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).Select(p => new PortalOptionDto(p.Id, p.Name, p.Description)).ToListAsync(ct);
        var installations = me.CustomerId is { } cust
            ? await db.Installations.AsNoTracking().Where(i => i.CustomerId == cust && i.Status == Domain.Products.InstallationStatus.Active).OrderBy(i => i.Name)
                .Select(i => new PortalInstallationOptionDto(i.Id, i.Name, i.ProductId, db.Products.Where(p => p.Id == i.ProductId).Select(p => p.Name).First(),
                    db.ProductVersions.Where(v => v.Id == i.VersionId).Select(v => v.Version).FirstOrDefault(),
                    db.CustomerLocations.Where(l => l.Id == i.LocationId).Select(l => l.Name).FirstOrDefault(), i.Environment.ToString()))
                .ToListAsync(ct)
            : [];
        var defs = await db.CustomFields.AsNoTracking().Where(f => f.Entity == "Ticket" && f.IsActive && f.VisibleInPortal).OrderBy(f => f.SortOrder).ToListAsync(ct);
        var fields = defs.Select(d => new PortalFieldDefDto(d.Key, d.Label, d.FieldType.ToString(), d.IsRequired, d.EditableInPortal, d.HelpText, Options(d.OptionsJson),
            Json.GuidList(d.DepartmentIdsJson), d.DefaultValue, d.MinValue, d.MaxValue, d.MaxLength)).ToList();
        var layout = await db.FormLayouts.AsNoTracking()
            .Where(l => l.Entity == "Ticket" && l.Kind == LayoutKind.PortalForm && l.IsActive && (l.BrandId == null || l.BrandId == portal.Brand.Id))
            .OrderByDescending(l => l.BrandId != null).ThenByDescending(l => l.IsDefault).FirstOrDefaultAsync(ct);
        var sections = Sections(layout?.SectionsJson, fields);
        var plan = await limits.GetLimitsAsync(ct);
        return new PortalTicketFormDto(departments, categories, products, installations, fields, sections, PortalPriorities, plan.MaxAttachmentMb ?? FileService.DefaultMaxMb);
    }

    private static List<OptionDto> Options(string json) =>
        Json.DeserializeOr(json, new List<JsonElement>()).Select(o => o.ValueKind == JsonValueKind.Object
                ? new OptionDto(o.TryGetProperty("value", out var v) ? v.ToString() : "", o.TryGetProperty("label", out var l) ? l.ToString() : "")
                : new OptionDto(o.ToString(), o.ToString()))
            .Where(o => o.Value.Length > 0).ToList();

    private static List<PortalFormSectionDto> Sections(string? json, IReadOnlyList<PortalFieldDefDto> fields)
    {
        var standard = new[] { "departmentId", "categoryId", "productId", "installationId", "priority" };
        var editable = fields.Where(f => f.Editable).Select(f => "cf:" + f.Key).ToList();
        var sections = new List<PortalFormSectionDto>();
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    var keys = s.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array
                        ? f.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("key", out var k) ? k.GetString() : x.GetString())
                            .Where(k => k is not null && (standard.Contains(k) || editable.Contains(k))).Select(k => k!).ToList()
                        : [];
                    if (keys.Count == 0) continue;
                    sections.Add(new PortalFormSectionDto(s.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                        s.TryGetProperty("columns", out var c) && c.TryGetInt32(out var cols) ? Math.Clamp(cols, 1, 3) : 2, keys));
                }
            }
            catch (JsonException) { sections.Clear(); }
            catch (InvalidOperationException) { sections.Clear(); }
        }
        if (sections.Count > 0)
        {
            var missing = editable.Where(k => !sections.Any(s => s.Fields.Contains(k))).ToList();
            if (missing.Count > 0) sections.Add(new PortalFormSectionDto("Additional information", 2, missing));
            return sections;
        }
        sections.Add(new PortalFormSectionDto("About your request", 2, standard.ToList()));
        if (editable.Count > 0) sections.Add(new PortalFormSectionDto("Additional information", 2, editable));
        return sections;
    }

    public async Task<PortalTicketCreatedDto> CreateAsync(PortalCreateTicketRequest r, CancellationToken ct = default)
    {
        var me = portal.Me;
        var errors = new Dictionary<string, string>();
        var subject = (r.Subject ?? "").Trim();
        if (subject.Length < 3) errors["subject"] = "Tell us briefly what the problem is.";
        else if (subject.Length > 250) errors["subject"] = "At most 250 characters.";
        if (r.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d && x.IsActive && x.VisibleInPortal, ct)) errors["departmentId"] = "Choose one of the listed departments.";
        if (r.CategoryId is { } c && !await db.TicketCategories.AnyAsync(x => x.Id == c && x.IsActive && x.VisibleInPortal, ct)) errors["categoryId"] = "Choose one of the listed categories.";
        if (r.SubcategoryId is { } sc && !await db.TicketCategories.AnyAsync(x => x.Id == sc && x.IsActive && x.VisibleInPortal && x.ParentId == r.CategoryId, ct))
            errors["subcategoryId"] = "Choose one of the listed sub-categories.";
        if (r.ProductId is { } p && !await db.Products.AnyAsync(x => x.Id == p && x.IsActive, ct)) errors["productId"] = "Choose one of the listed products.";
        if (r.InstallationId is { } i && (me.CustomerId is null || !await db.Installations.AnyAsync(x => x.Id == i && x.CustomerId == me.CustomerId, ct)))
            errors["installationId"] = "Choose one of your installations.";
        var priority = string.IsNullOrWhiteSpace(r.Priority) ? "Medium" : PortalPriorities.FirstOrDefault(x => x.Equals(r.Priority.Trim(), StringComparison.OrdinalIgnoreCase));
        if (priority is null) errors["priority"] = "Choose Low, Medium or High.";
        if (r.SourceFlowId is { } flow && !await db.TroubleshootingFlows.AnyAsync(f => f.Id == flow, ct)) errors["sourceFlowId"] = "Unknown guide.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var ticket = await tickets.CreateAsync(new CreateTicketRequest
        {
            Subject = subject,
            DescriptionHtml = r.DescriptionHtml,
            Channel = nameof(Channel.Portal),
            BrandId = portal.Brand.Id,
            DepartmentId = r.DepartmentId ?? (r.CategoryId is { } cat ? await db.TicketCategories.Where(x => x.Id == cat).Select(x => x.DepartmentId).FirstOrDefaultAsync(ct) : null),
            CategoryId = r.CategoryId,
            SubcategoryId = r.SubcategoryId,
            ProductId = r.ProductId,
            InstallationId = r.InstallationId,
            Priority = priority,
            CustomFields = r.CustomFields,
            AttachmentFileIds = r.AttachmentFileIds,
            ClientRequestId = IdempotencyKey(me.Id, r.ClientRequestId),
            Origin = r.SourceFlowId is { } source ? new ExternalReferenceInput("troubleshooting", "flow", source, null, null, null) : null
        }, new TicketCreationSource(CreateInitialThread: true, PortalSubmission: true, AuthorName: me.FullName, FromAddress: me.Email), ct);
        await lookups.LoadAsync(ct);
        return new PortalTicketCreatedDto(ticket.Id, ticket.Number, lookups.Display(ticket.Number));
    }

    // ───────────────────────────── Conversation & lifecycle ─────────────────────────────

    public async Task<PortalMessageDto> ReplyAsync(Guid id, PortalReplyRequest r, CancellationToken ct = default)
    {
        var t = await Visible(ScopeOrganisation).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Ticket", id);
        var detail = await MapDetailAsync(t, ct);
        if (!detail.Actions.Reply)
            throw new DomainException("ticket_closed", "This ticket is closed. Please submit a new ticket and mention its number.", 422);
        return await AddMessageAsync(t, r.BodyHtml, r.AttachmentFileIds, r.ClientRequestId, ct);
    }

    private async Task<PortalMessageDto> AddMessageAsync(Ticket t, string? bodyHtml, IReadOnlyList<Guid>? fileIds, string? clientRequestId, CancellationToken ct)
    {
        var me = portal.Me;
        if (string.IsNullOrWhiteSpace(TextNormalization.HtmlToText(bodyHtml)) && fileIds is not { Count: > 0 })
            throw new ValidationException(new Dictionary<string, string> { ["bodyHtml"] = "Write a message first." });
        if (fileIds is { Count: > 0 })
        {
            var ids = fileIds.Distinct().ToList();
            var own = await db.StoredFiles.CountAsync(f => ids.Contains(f.Id) && f.UploadedByContactId == me.Id, ct);
            if (own != ids.Count) throw new ForbiddenException("You can only attach files you uploaded.");
        }
        var thread = await conversations.AddCustomerMessageAsync(t.Id, new CustomerMessageInput(bodyHtml, null, Channel.Portal, me.Id, me.FullName, me.Email,
                AttachmentFileIds: fileIds, ClientRequestId: IdempotencyKey(me.Id, clientRequestId)), ct)
            ?? throw new DomainException("ticket_closed", "This ticket is closed. Please submit a new ticket and mention its number.", 422);
        return (await MapMessagesAsync(t.Id, [thread], ct))[0];
    }

    public async Task<PortalTicketDetailDto> ReopenAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var t = await Visible(ScopeOrganisation).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Ticket", id);
        var detail = await MapDetailAsync(t, ct);
        if (!detail.Actions.Reopen)
            throw new DomainException("reopen_not_allowed", t.StatusCategory.IsOpen()
                ? "The ticket is already open."
                : "This ticket can no longer be reopened. Please submit a new ticket and mention its number.", 422);
        var text = string.IsNullOrWhiteSpace(reason) ? "The issue is not resolved." : reason.Trim();
        await AddMessageAsync(t, TextNormalization.TextToHtml("Reopened: " + text), null, null, ct);
        return await GetAsync(id, ct);
    }

    public async Task<PortalTicketDetailDto> ConfirmAsync(Guid id, PortalConfirmRequest r, CancellationToken ct = default)
    {
        var visible = await Visible(ScopeOrganisation).AnyAsync(x => x.Id == id, ct);
        if (!visible) throw new NotFoundException("Ticket", id);
        var t = await db.Tickets.FirstAsync(x => x.Id == id, ct);
        if (t.ContactId != portal.Me.Id) throw new ForbiddenException("Only the person who raised the ticket can confirm the resolution.");
        if (!t.AwaitingCustomerConfirmation || t.StatusCategory != StatusCategory.Resolved)
            throw new DomainException("confirmation_not_requested", "This ticket is not waiting for your confirmation.", 422);
        if (!r.Resolved)
        {
            db.ChangeTracker.Clear();
            var text = string.IsNullOrWhiteSpace(r.Comment) ? "The issue is not resolved." : r.Comment.Trim();
            await AddMessageAsync(t, TextNormalization.TextToHtml(text), null, null, ct);
            return await GetAsync(id, ct);
        }
        var closed = await tickets.StatusForCategoryAsync(StatusCategory.Closed, ct);
        var comment = string.IsNullOrWhiteSpace(r.Comment) ? null : TextNormalization.Truncate(r.Comment.Trim(), 2000);
        await tickets.ApplyStatusAsync(t, closed, null, comment is null ? "Resolution confirmed by the customer" : $"Resolution confirmed by the customer: {comment}",
            bypassRules: true, ct);
        t.AwaitingCustomerConfirmation = false;
        // The comment is kept in the activity history (a customer message would reopen the ticket).
        history.Record(t, TicketEventTypes.CustomerConfirmed, comment is null ? "Customer confirmed the resolution" : $"Customer confirmed the resolution: {comment}",
            null, new { comment });
        await tickets.SaveAsync(t, ct);
        return await GetAsync(id, ct);
    }

    public async Task<PortalTicketDetailDto> RateAsync(Guid id, PortalSatisfactionRequest r, CancellationToken ct = default)
    {
        if (r.Rating is < 1 or > 5) throw new ValidationException(new Dictionary<string, string> { ["rating"] = "Rate from 1 to 5." });
        if (!await Visible(ScopeOrganisation).AnyAsync(x => x.Id == id, ct)) throw new NotFoundException("Ticket", id);
        var t = await db.Tickets.FirstAsync(x => x.Id == id, ct);
        var s = await settings.TicketsAsync(ct);
        if (t.ContactId != portal.Me.Id) throw new ForbiddenException("Only the person who raised the ticket can rate it.");
        if (!s.CsatEnabled || t.StatusCategory.IsOpen()) throw new DomainException("rating_not_available", "You can rate the support once the ticket is resolved.", 422);
        if (t.CsatScore is not null) throw new DomainException("already_rated", "You have already rated this ticket. Thank you!", 409);
        var now = db.Clock.UtcNow;
        var comment = string.IsNullOrWhiteSpace(r.Comment) ? null : TextNormalization.Truncate(TextNormalization.HtmlToText(r.Comment).Trim(), 2000);
        t.CsatScore = r.Rating;
        t.CsatComment = comment;
        var survey = await db.SatisfactionResponses.Where(x => x.TicketId == t.Id && x.RespondedAt == null).OrderByDescending(x => x.SentAt).FirstOrDefaultAsync(ct);
        if (survey is null)
        {
            survey = new SatisfactionResponse
            {
                TicketId = t.Id, ContactId = portal.Me.Id, AgentUserId = t.AssigneeId, TokenHash = TextNormalization.Sha256Hex(TextNormalization.RandomToken()),
                Channel = Channel.Portal, SentAt = now, ExpiresAt = now
            };
            db.SatisfactionResponses.Add(survey);
        }
        survey.Rating = r.Rating;
        survey.Comment = comment;
        survey.RespondedAt = now;
        history.Record(t, TicketEventTypes.Satisfaction, $"Customer rated the support {r.Rating}/5", [new FieldChange("csatScore", null, r.Rating.ToString(CultureInfo.InvariantCulture))]);
        events.Publish(EventTypes.SatisfactionReceived, "Ticket", t.Id, new TicketEventPayload(t.Id, t.Number, [new FieldChange("csatScore", null, r.Rating.ToString(CultureInfo.InvariantCulture))]));
        await tickets.SaveAsync(t, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Client idempotency keys are namespaced per contact so one customer can never collide with another.</summary>
    private static string? IdempotencyKey(Guid contactId, string? clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey)) return null;
        var key = clientKey.Trim();
        return $"portal:{contactId:N}:{(key.Length > 100 ? key[..100] : key)}";
    }

    // ───────────────────────────── Files ─────────────────────────────

    public async Task<UploadedFileDto> UploadAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default)
    {
        _ = portal.Me;
        if (!ctx.IsContact) throw new ForbiddenException("Sign in to upload files.");
        var stored = await files.UploadAsync(content, fileName, contentType, "portal-attachment", ct);
        return new UploadedFileDto(stored.Id, stored.FileName, stored.ContentType, stored.SizeBytes, stored.ScanStatus.ToString());
    }
}
