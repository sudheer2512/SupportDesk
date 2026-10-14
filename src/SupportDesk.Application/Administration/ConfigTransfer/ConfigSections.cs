using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Configuration;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Products;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Administration.ConfigTransfer;

/// <summary>
/// One exportable configuration section. Items are matched across tenants by a natural key (never by id); every GUID
/// that points at another configuration item or a reference entity is written as <c>"@ref:kind/key"</c> and resolved to
/// the target tenant's id on import. Scalar properties are discovered by reflection, minus <see cref="Exclude"/>.
/// </summary>
public sealed class ConfigSection
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required Type EntityType { get; init; }
    /// <summary>The tenant's current items of this section (tracked, so they can be updated).</summary>
    public required Func<DeskDbContext, CancellationToken, Task<List<Entity>>> Load { get; init; }
    /// <summary>Natural key of a live entity (all items of the section are passed for parent lookups).</summary>
    public required Func<Entity, IReadOnlyDictionary<Guid, Entity>, string> NaturalKey { get; init; }
    /// <summary>Property that "rename" changes (and whose value is the last key segment).</summary>
    public string NameProperty { get; init; } = "Name";
    public string? DisplayProperty { get; init; }
    public bool AllowRename { get; init; } = true;
    public IReadOnlySet<string> Exclude { get; init; } = new HashSet<string>();
    /// <summary>Boolean property of which exactly one item should be true after an import (e.g. IsDefault).</summary>
    public string? UniqueDefault { get; init; }
    /// <summary>Child rows / versioned definitions exported alongside the item (compared and applied as one value).</summary>
    public Func<DeskDbContext, Entity, ConfigRefs, CancellationToken, Task<JsonNode?>>? ExportExtras { get; init; }
    public Func<DeskDbContext, Entity, JsonNode?, ConfigApplyContext, CancellationToken, Task>? ApplyExtras { get; init; }
    /// <summary>Called for newly created items (e.g. imported channels start disabled because secrets are never transferred).</summary>
    public Action<Entity>? OnCreate { get; init; }
    public Func<DeskDbContext, Entity, bool, CancellationToken, Task>? BeforeSave { get; init; }
}

/// <summary>Registry of exportable sections in dependency order (items may reference sections listed before them).</summary>
public static class ConfigSections
{
    public const int FormatVersion = 1;
    public const string Format = "supportdesk.config";

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);

    private static Func<DeskDbContext, CancellationToken, Task<List<Entity>>> LoadAll<T>(Func<IQueryable<T>, IQueryable<T>>? filter = null) where T : Entity =>
        async (db, ct) => (await (filter ?? (q => q))(db.Set<T>()).ToListAsync(ct)).Cast<Entity>().ToList();

    private static string Name<T>(Entity e, Func<T, string> f) where T : Entity => f((T)e);

    /// <summary>Reference-only kinds: matched by name, never exported or created by an import.</summary>
    public static readonly IReadOnlyList<(string Kind, Func<DeskDbContext, CancellationToken, Task<List<(Guid Id, string Key)>>> Load)> ReferenceKinds =
    [
        ("departments", async (db, ct) => (await db.Departments.Select(d => new { d.Id, d.Name }).ToListAsync(ct)).Select(x => (x.Id, x.Name)).ToList()),
        ("teams", async (db, ct) => (await db.Teams.Select(d => new { d.Id, d.Name }).ToListAsync(ct)).Select(x => (x.Id, x.Name)).ToList()),
        ("users", async (db, ct) => (await db.Users.Select(d => new { d.Id, d.EmailNormalized }).ToListAsync(ct)).Select(x => (x.Id, x.EmailNormalized)).ToList()),
        ("roles", async (db, ct) => (await db.Roles.Select(d => new { d.Id, d.Name }).ToListAsync(ct)).Select(x => (x.Id, x.Name)).ToList()),
        ("brands", async (db, ct) => (await db.Brands.Select(d => new { d.Id, d.Name }).ToListAsync(ct)).Select(x => (x.Id, x.Name)).ToList()),
        ("products", async (db, ct) => (await db.Products.Select(d => new { d.Id, d.Name }).ToListAsync(ct)).Select(x => (x.Id, x.Name)).ToList()),
        ("skills", async (db, ct) => (await db.Skills.Select(d => new { d.Id, d.Name }).ToListAsync(ct)).Select(x => (x.Id, x.Name)).ToList()),
    ];

    public static readonly IReadOnlyList<ConfigSection> All =
    [
        new()
        {
            Key = "calendars", Label = "Business calendars", EntityType = typeof(BusinessCalendar), Load = LoadAll<BusinessCalendar>(),
            NaturalKey = (e, _) => Name<BusinessCalendar>(e, x => x.Name), UniqueDefault = "IsDefault",
            ExportExtras = async (db, e, refs, ct) => new JsonArray((await db.CalendarHolidays.Where(h => h.CalendarId == e.Id).OrderBy(h => h.Date).ToListAsync(ct))
                .Select(h => (JsonNode)new JsonObject { ["date"] = h.Date.ToString("yyyy-MM-dd"), ["name"] = h.Name, ["recurring"] = h.Recurring }).ToArray()),
            ApplyExtras = async (db, e, extras, ctx, ct) =>
            {
                if (extras is not JsonArray list) return;
                var existing = await db.CalendarHolidays.Where(h => h.CalendarId == e.Id).ToListAsync(ct);
                db.CalendarHolidays.RemoveRange(existing);
                foreach (var h in list.OfType<JsonObject>())
                    if (DateOnly.TryParse(h["date"]?.GetValue<string>(), out var date))
                        db.CalendarHolidays.Add(new CalendarHoliday { CalendarId = e.Id, Date = date, Name = h["name"]?.GetValue<string>() ?? "", Recurring = h["recurring"]?.GetValue<bool>() ?? false });
            }
        },
        new()
        {
            Key = "statuses", Label = "Ticket statuses", EntityType = typeof(TicketStatus), Load = LoadAll<TicketStatus>(),
            NaturalKey = (e, _) => Name<TicketStatus>(e, x => x.Name), UniqueDefault = "IsDefault"
        },
        new()
        {
            Key = "issueTypes", Label = "Issue types", EntityType = typeof(IssueType), Load = LoadAll<IssueType>(),
            NaturalKey = (e, _) => Name<IssueType>(e, x => x.Name)
        },
        new()
        {
            Key = "lookups", Label = "Pick lists (resolution codes, root causes…)", EntityType = typeof(LookupValue), Load = LoadAll<LookupValue>(),
            NaturalKey = (e, _) => Name<LookupValue>(e, x => $"{x.Type}:{x.Value}"), NameProperty = "Value", DisplayProperty = "Label"
        },
        new()
        {
            Key = "categories", Label = "Ticket categories", EntityType = typeof(TicketCategory), Load = LoadAll<TicketCategory>(),
            NaturalKey = CategoryPath
        },
        new()
        {
            Key = "fields", Label = "Custom fields", EntityType = typeof(CustomFieldDefinition), Load = LoadAll<CustomFieldDefinition>(),
            NaturalKey = (e, _) => Name<CustomFieldDefinition>(e, x => $"{x.Entity}:{x.Key}"), NameProperty = "Key", DisplayProperty = "Label"
        },
        new()
        {
            Key = "layouts", Label = "Form layouts", EntityType = typeof(FormLayout), Load = LoadAll<FormLayout>(),
            NaturalKey = (e, _) => Name<FormLayout>(e, x => $"{x.Entity}:{x.Kind}:{x.Name}")
        },
        new()
        {
            Key = "sla", Label = "SLA policies", EntityType = typeof(SlaPolicy), Load = LoadAll<SlaPolicy>(), NaturalKey = (e, _) => Name<SlaPolicy>(e, x => x.Name),
            UniqueDefault = "IsDefault", Exclude = Set("Version"),
            ExportExtras = async (db, e, refs, ct) =>
            {
                var targets = await db.SlaTargets.Where(t => t.PolicyId == e.Id).OrderBy(t => t.Priority).ToListAsync(ct);
                var levels = await db.SlaEscalationLevels.Where(t => t.PolicyId == e.Id).OrderBy(t => t.Milestone).ThenBy(t => t.Level).ToListAsync(ct);
                return new JsonObject
                {
                    ["targets"] = new JsonArray(targets.Select(t => (JsonNode)ConfigCodec.ExportObject(t, refs, ChildExclude)).ToArray()),
                    ["escalations"] = new JsonArray(levels.Select(t => (JsonNode)ConfigCodec.ExportObject(t, refs, ChildExclude)).ToArray())
                };
            },
            ApplyExtras = async (db, e, extras, ctx, ct) =>
            {
                if (extras is not JsonObject o) return;
                db.SlaTargets.RemoveRange(await db.SlaTargets.Where(t => t.PolicyId == e.Id).ToListAsync(ct));
                db.SlaEscalationLevels.RemoveRange(await db.SlaEscalationLevels.Where(t => t.PolicyId == e.Id).ToListAsync(ct));
                foreach (var t in (o["targets"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var target = new SlaTarget { PolicyId = e.Id };
                    ConfigCodec.ApplyObject(target, t, ctx, ChildExclude, refsOnly: null);
                    db.SlaTargets.Add(target);
                }
                foreach (var t in (o["escalations"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var level = new SlaEscalationLevel { PolicyId = e.Id };
                    ConfigCodec.ApplyObject(level, t, ctx, ChildExclude, refsOnly: null);
                    db.SlaEscalationLevels.Add(level);
                }
                if (e is SlaPolicy p) p.Version++;
            }
        },
        new()
        {
            Key = "templates", Label = "Reply templates (shared)", EntityType = typeof(ReplyTemplate),
            Load = LoadAll<ReplyTemplate>(q => q.Where(t => t.Visibility != TemplateVisibility.Personal)),
            NaturalKey = (e, _) => Name<ReplyTemplate>(e, x => x.Name), Exclude = Set("UsageCount")
        },
        new()
        {
            Key = "kbCategories", Label = "Knowledge base categories", EntityType = typeof(KbCategory), Load = LoadAll<KbCategory>(),
            NaturalKey = (e, _) => Name<KbCategory>(e, x => x.Slug), NameProperty = "Slug", DisplayProperty = "Name"
        },
        new()
        {
            Key = "prompts", Label = "AI prompt templates", EntityType = typeof(AiPromptTemplate), Load = LoadAll<AiPromptTemplate>(q => q.Where(p => p.IsActive)),
            NaturalKey = (e, _) => Name<AiPromptTemplate>(e, x => x.Feature), NameProperty = "Feature", AllowRename = false, Exclude = Set("Version", "IsActive"),
            BeforeSave = async (db, e, created, ct) =>
            {
                var p = (AiPromptTemplate)e;
                var max = await db.AiPromptTemplates.Where(x => x.Feature == p.Feature && x.Id != p.Id).MaxAsync(x => (int?)x.Version, ct) ?? 0;
                p.Version = Math.Max(p.Version, max) + 1;
                p.IsActive = true;
            }
        },
        new()
        {
            Key = "channels", Label = "Channel definitions (without secrets)", EntityType = typeof(ChannelAccount), Load = LoadAll<ChannelAccount>(),
            NaturalKey = (e, _) => Name<ChannelAccount>(e, x => $"{x.Type}:{x.Name}"),
            Exclude = Set("SecretsProtected", "WebhookKey", "PollCursor", "LastPolledAt", "LastError", "Status", "IsActive"),
            OnCreate = e =>
            {
                var c = (ChannelAccount)e;
                c.IsActive = false;
                c.Status = ChannelAccountStatus.PendingVerification;
            }
        },
        new()
        {
            Key = "workflows", Label = "Blueprints", EntityType = typeof(Workflow), Load = LoadAll<Workflow>(), NaturalKey = (e, _) => Name<Workflow>(e, x => x.Name),
            Exclude = Set("PublishedVersionId", "LatestVersionNumber"),
            ExportExtras = async (db, e, refs, ct) =>
            {
                var w = (Workflow)e;
                var def = await db.WorkflowVersions.Where(v => v.WorkflowId == w.Id).OrderByDescending(v => v.Id == w.PublishedVersionId).ThenByDescending(v => v.VersionNumber)
                    .Select(v => v.DefinitionJson).FirstOrDefaultAsync(ct);
                return def is null ? null : ConfigCodec.ExportJson(def, refs);
            },
            ApplyExtras = async (db, e, extras, ctx, ct) =>
            {
                if (extras is null) return;
                var w = (Workflow)e;
                var json = ConfigCodec.ImportJson(extras, ctx);
                var number = (await db.WorkflowVersions.Where(v => v.WorkflowId == w.Id).MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0) + 1;
                await db.WorkflowVersions.Where(v => v.WorkflowId == w.Id && v.State == VersionState.Published)
                    .ExecuteUpdateAsync(u => u.SetProperty(v => v.State, VersionState.Superseded), ct);
                var version = new WorkflowVersion
                {
                    WorkflowId = w.Id, VersionNumber = number, DefinitionJson = json, State = VersionState.Published, ChangeNote = ctx.ChangeNote,
                    CreatedAt = ctx.Now, CreatedById = ctx.UserId, PublishedAt = ctx.Now, PublishedById = ctx.UserId
                };
                db.WorkflowVersions.Add(version);
                w.PublishedVersionId = version.Id;
                w.LatestVersionNumber = number;
            }
        },
        new()
        {
            Key = "rules", Label = "Automation rules", EntityType = typeof(AutomationRule), Load = LoadAll<AutomationRule>(),
            NaturalKey = (e, _) => Name<AutomationRule>(e, x => x.Name),
            Exclude = Set("PublishedVersionId", "LatestVersionNumber", "NextScheduledRunAt", "WebhookSecretHash", "WebhookKey", "ExecutionCount", "LastExecutedAt"),
            ExportExtras = async (db, e, refs, ct) =>
            {
                var r = (AutomationRule)e;
                var def = await db.RuleVersions.Where(v => v.RuleId == r.Id).OrderByDescending(v => v.Id == r.PublishedVersionId).ThenByDescending(v => v.VersionNumber)
                    .Select(v => v.DefinitionJson).FirstOrDefaultAsync(ct);
                return def is null ? null : ConfigCodec.ExportJson(def, refs);
            },
            ApplyExtras = async (db, e, extras, ctx, ct) =>
            {
                if (extras is null) return;
                var r = (AutomationRule)e;
                var json = ConfigCodec.ImportJson(extras, ctx);
                var number = (await db.RuleVersions.Where(v => v.RuleId == r.Id).MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0) + 1;
                await db.RuleVersions.Where(v => v.RuleId == r.Id && v.State == VersionState.Published)
                    .ExecuteUpdateAsync(u => u.SetProperty(v => v.State, VersionState.Superseded), ct);
                var version = new RuleVersion
                {
                    RuleId = r.Id, VersionNumber = number, DefinitionJson = json, State = VersionState.Published, ChangeNote = ctx.ChangeNote,
                    CreatedAt = ctx.Now, CreatedById = ctx.UserId, PublishedAt = ctx.Now, PublishedById = ctx.UserId
                };
                db.RuleVersions.Add(version);
                r.PublishedVersionId = version.Id;
                r.LatestVersionNumber = number;
            }
        },
    ];

    private static readonly IReadOnlySet<string> ChildExclude = new HashSet<string>(StringComparer.Ordinal) { "PolicyId" };

    public static ConfigSection? Find(string key) => All.FirstOrDefault(s => s.Key == key);

    private static string CategoryPath(Entity e, IReadOnlyDictionary<Guid, Entity> all)
    {
        var parts = new List<string>();
        var current = (TicketCategory?)e;
        var guard = 0;
        while (current is not null && guard++ < 20)
        {
            parts.Insert(0, current.Name);
            current = current.ParentId is { } pid && all.TryGetValue(pid, out var parent) ? (TicketCategory)parent : null;
        }
        return string.Join("/", parts);
    }

    /// <summary>Replaces the last key segment (after '/' or ':') with a new name.</summary>
    public static string RenameKey(string key, string newName)
    {
        var cut = Math.Max(key.LastIndexOf('/'), key.LastIndexOf(':'));
        return cut < 0 ? newName : key[..(cut + 1)] + newName;
    }
}
