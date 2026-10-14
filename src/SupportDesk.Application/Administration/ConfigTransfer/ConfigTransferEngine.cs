using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Contracts.ConfigTransfer;
using SupportDesk.Domain;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Administration.ConfigTransfer;

/// <summary>
/// Export, diff and apply of configuration bundles for the current tenant. Bundle format (version 1):
/// <c>{"format":"supportdesk.config","version":1,"exportedAt":…,"sourceTenant":…,"sections":{"statuses":[{"key":"Open","name":"Open","data":{…},"extras":…}]}}</c>.
/// </summary>
public sealed class ConfigTransferEngine(DeskDbContext db)
{
    public const int MaxItemsPerSection = 5000;

    public sealed record LiveItem(ConfigSection Section, Entity Entity, string Key);

    public sealed class LiveConfig
    {
        public ConfigRefs Refs { get; } = new();
        public Dictionary<string, Guid> KeyToId { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, LiveItem>> Sections { get; } = new(StringComparer.Ordinal);
    }

    public sealed record IncomingItem(string Section, string Key, string Name, JsonObject Data, JsonNode? Extras);

    public async Task<LiveConfig> LoadAsync(CancellationToken ct)
    {
        var live = new LiveConfig();
        foreach (var (kind, load) in ConfigSections.ReferenceKinds)
        {
            foreach (var (id, key) in await load(db, ct))
            {
                if (string.IsNullOrEmpty(key) || live.KeyToId.ContainsKey($"{kind}/{key}")) continue;
                live.Refs.Add(id, kind, key);
                live.KeyToId[$"{kind}/{key}"] = id;
            }
        }
        foreach (var section in ConfigSections.All)
        {
            var entities = await section.Load(db, ct);
            var byId = entities.ToDictionary(e => e.Id);
            var items = new Dictionary<string, LiveItem>(StringComparer.Ordinal);
            foreach (var e in entities.OrderBy(e => e.Id))
            {
                var key = section.NaturalKey(e, byId);
                var unique = key;
                for (var n = 2; items.ContainsKey(unique); n++) unique = $"{key} #{n}";
                items[unique] = new LiveItem(section, e, unique);
                live.Refs.Add(e.Id, section.Key, unique);
                live.KeyToId[$"{section.Key}/{unique}"] = e.Id;
            }
            live.Sections[section.Key] = items;
        }
        return live;
    }

    public async Task<JsonObject> ExportItemAsync(LiveItem item, ConfigRefs refs, CancellationToken ct)
    {
        var data = ConfigCodec.ExportObject(item.Entity, refs, item.Section.Exclude);
        var o = new JsonObject { ["key"] = item.Key, ["name"] = DisplayName(item.Section, data, item.Key), ["data"] = data };
        if (item.Section.ExportExtras is { } extras) o["extras"] = await extras(db, item.Entity, refs, ct);
        return o;
    }

    public async Task<JsonObject> ExportAsync(LiveConfig live, string? sourceTenant, DateTime now, IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        var sections = new JsonObject();
        foreach (var section in ConfigSections.All)
        {
            if (only is { Count: > 0 } && !only.Contains(section.Key)) continue;
            var list = new JsonArray();
            foreach (var item in live.Sections[section.Key].Values.OrderBy(i => i.Key, StringComparer.Ordinal))
                list.Add(await ExportItemAsync(item, live.Refs, ct));
            sections[section.Key] = list;
        }
        return new JsonObject
        {
            ["format"] = ConfigSections.Format, ["version"] = ConfigSections.FormatVersion, ["exportedAt"] = now.ToString("O"),
            ["sourceTenant"] = sourceTenant, ["sections"] = sections
        };
    }

    /// <summary>Validates and reads a bundle. Unknown sections are reported as warnings and ignored.</summary>
    public static (List<IncomingItem> Items, List<string> Warnings, string? SourceTenant, int Version) Parse(JsonNode? bundle)
    {
        if (bundle is not JsonObject root) throw new DomainException("invalid_bundle", "The configuration file is not a JSON object.", 400);
        if (root["format"]?.GetValue<string>() != ConfigSections.Format) throw new DomainException("invalid_bundle", "This is not a SupportDesk configuration export.", 400);
        var version = root["version"]?.GetValue<int>() ?? 0;
        if (version is < 1 or > ConfigSections.FormatVersion)
            throw new DomainException("unsupported_version", $"Configuration format version {version} is not supported (max {ConfigSections.FormatVersion}).", 400);
        if (root["sections"] is not JsonObject sections) throw new DomainException("invalid_bundle", "The file has no sections.", 400);
        var items = new List<IncomingItem>();
        var warnings = new List<string>();
        foreach (var (key, value) in sections)
        {
            if (ConfigSections.Find(key) is null)
            {
                warnings.Add($"Section '{key}' is not supported by this version and was ignored.");
                continue;
            }
            if (value is not JsonArray list) throw new DomainException("invalid_bundle", $"Section '{key}' must be a list.", 400);
            if (list.Count > MaxItemsPerSection) throw new DomainException("invalid_bundle", $"Section '{key}' has more than {MaxItemsPerSection} items.", 400);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in list)
            {
                if (node is not JsonObject item || item["data"] is not JsonObject data || item["key"]?.GetValue<string>() is not { Length: > 0 } itemKey)
                    throw new DomainException("invalid_bundle", $"Section '{key}' contains an item without key or data.", 400);
                if (!seen.Add(itemKey)) throw new DomainException("invalid_bundle", $"Section '{key}' contains the key '{itemKey}' twice.", 400);
                items.Add(new IncomingItem(key, itemKey, item["name"]?.GetValue<string>() ?? itemKey, (JsonObject)data.DeepClone(), item["extras"]?.DeepClone()));
            }
        }
        return (items, warnings, root["sourceTenant"]?.GetValue<string>(), version);
    }

    public async Task<ConfigDiffDto> DiffAsync(JsonNode? bundle, IReadOnlyDictionary<string, ConfigResolutionDto> resolutions, CancellationToken ct)
    {
        var (items, warnings, _, _) = Parse(bundle);
        var live = await LoadAsync(ct);
        var sections = new List<ConfigDiffSectionDto>();
        int created = 0, changed = 0, unchanged = 0;
        foreach (var section in ConfigSections.All)
        {
            var list = new List<ConfigDiffItemDto>();
            foreach (var item in items.Where(i => i.Section == section.Key))
            {
                var key = $"{section.Key}/{item.Key}";
                List<ConfigFieldChangeDto> changes = [];
                string status;
                if (!live.Sections[section.Key].TryGetValue(item.Key, out var current))
                {
                    status = "new";
                    created++;
                }
                else
                {
                    var currentItem = await ExportItemAsync(current, live.Refs, ct);
                    changes = ConfigCodec.Compare((JsonObject)currentItem["data"]!, item.Data)
                        .Select(c => new ConfigFieldChangeDto(c.Field, c.Current, c.Incoming)).ToList();
                    if (section.ExportExtras is not null && !JsonNode.DeepEquals(currentItem["extras"], item.Extras))
                        changes.Add(new ConfigFieldChangeDto(section.Key is "rules" or "workflows" ? "definition" : "children",
                            ConfigCodec.Short(currentItem["extras"]), ConfigCodec.Short(item.Extras)));
                    status = changes.Count == 0 ? "unchanged" : "changed";
                    if (status == "changed") changed++; else unchanged++;
                }
                var (action, newName) = Resolve(section, status, resolutions.GetValueOrDefault(key));
                list.Add(new ConfigDiffItemDto(key, section.Key, item.Key, item.Name, status, action, newName, changes));
            }
            if (list.Count > 0) sections.Add(new ConfigDiffSectionDto(section.Key, section.Label, list));
        }
        return new ConfigDiffDto(sections, created, changed, unchanged, warnings);
    }

    /// <summary>The effective action for an item: the stored resolution when valid for its status, else the default.</summary>
    public static (string Action, string? NewName) Resolve(ConfigSection section, string status, ConfigResolutionDto? r)
    {
        var fallback = status switch { "new" => "create", "changed" => "overwrite", _ => "skip" };
        if (r is null) return (fallback, null);
        var action = r.Action?.Trim().ToLowerInvariant();
        var allowed = status switch
        {
            "new" => action is "create" or "skip" || (action == "rename" && section.AllowRename),
            "changed" => action is "overwrite" or "skip" || (action == "rename" && section.AllowRename),
            _ => action is "skip" || (action == "rename" && section.AllowRename)
        };
        if (!allowed) return (fallback, null);
        return action == "rename" ? (string.IsNullOrWhiteSpace(r.NewName) ? (fallback, null) : ("rename", r.NewName.Trim())) : (action!, null);
    }

    /// <summary>
    /// Applies a bundle in three phases (scalars → references/JSON → children and versioned definitions), so items may
    /// reference each other in any order. With <paramref name="deactivateMissing"/> (rollback), live items absent from the
    /// bundle are deactivated (never deleted). The caller saves inside its transaction.
    /// </summary>
    public async Task<ConfigApplyResultDto> ApplyAsync(JsonNode? bundle, IReadOnlyDictionary<string, ConfigResolutionDto> resolutions, bool deactivateMissing,
        ConfigApplyContext ctx, CancellationToken ct)
    {
        var (items, warnings, _, _) = Parse(bundle);
        ctx.Warnings.AddRange(warnings);
        var live = await LoadAsync(ct);
        foreach (var (k, id) in live.KeyToId) ctx.Resolver[k] = id;
        var applied = new List<(ConfigSection Section, Entity Entity, IncomingItem Item, bool Created)>();
        int created = 0, updated = 0, skipped = 0, deactivated = 0;

        foreach (var section in ConfigSections.All)
        {
            var current = live.Sections[section.Key];
            foreach (var item in items.Where(i => i.Section == section.Key))
            {
                current.TryGetValue(item.Key, out var existing);
                string status = "new";
                if (existing is not null)
                {
                    var currentItem = await ExportItemAsync(existing, live.Refs, ct);
                    var same = ConfigCodec.Compare((JsonObject)currentItem["data"]!, item.Data).Count == 0
                               && (section.ExportExtras is null || JsonNode.DeepEquals(currentItem["extras"], item.Extras));
                    status = same ? "unchanged" : "changed";
                }
                var (action, newName) = Resolve(section, status, resolutions.GetValueOrDefault($"{section.Key}/{item.Key}"));
                if (action == "skip")
                {
                    skipped++;
                    continue;
                }
                if (action == "overwrite" && existing is not null)
                {
                    ConfigCodec.ApplyObject(existing.Entity, item.Data, ctx, section.Exclude, refsOnly: false);
                    applied.Add((section, existing.Entity, item, false));
                    ctx.Resolver[$"{section.Key}/{item.Key}"] = existing.Entity.Id;
                    updated++;
                    continue;
                }
                var entity = (Entity)Activator.CreateInstance(section.EntityType)!;
                ConfigCodec.ApplyObject(entity, item.Data, ctx, section.Exclude, refsOnly: false);
                if (action == "rename")
                {
                    var renamedKey = ConfigSections.RenameKey(item.Key, newName!);
                    if (current.ContainsKey(renamedKey))
                    {
                        ctx.Warnings.Add($"{section.Label}: '{renamedKey}' already exists; '{item.Key}' was skipped.");
                        skipped++;
                        continue;
                    }
                    section.EntityType.GetProperty(section.NameProperty, BindingFlags.Public | BindingFlags.Instance)!.SetValue(entity, newName);
                    ctx.Resolver[$"{section.Key}/{renamedKey}"] = entity.Id;
                }
                section.OnCreate?.Invoke(entity);
                db.Add(entity);
                ctx.Resolver[$"{section.Key}/{item.Key}"] = entity.Id;
                applied.Add((section, entity, item, true));
                created++;
            }
        }

        // Phase 2: references and embedded JSON (every target now exists).
        foreach (var (section, entity, item, _) in applied)
            ConfigCodec.ApplyObject(entity, item.Data, ctx, section.Exclude, refsOnly: true);

        // Phase 3: children and versioned definitions.
        foreach (var (section, entity, item, wasCreated) in applied)
        {
            if (section.ApplyExtras is { } apply && item.Extras is not null) await apply(db, entity, item.Extras, ctx, ct);
            if (section.BeforeSave is { } before) await before(db, entity, wasCreated, ct);
        }

        if (deactivateMissing)
        {
            foreach (var section in ConfigSections.All)
            {
                var incoming = items.Where(i => i.Section == section.Key).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var item in live.Sections[section.Key].Values.Where(i => !incoming.Contains(i.Key)))
                {
                    var active = section.EntityType.GetProperty("IsActive");
                    var system = section.EntityType.GetProperty("IsSystem");
                    if (active is null || active.GetValue(item.Entity) is not true || system?.GetValue(item.Entity) is true) continue;
                    active.SetValue(item.Entity, false);
                    deactivated++;
                }
            }
        }

        NormalizeDefaults(live, applied);
        return new ConfigApplyResultDto(created, updated, skipped, deactivated, ctx.Warnings.Distinct().Take(200).ToList());
    }

    /// <summary>Keeps exactly one default per section (statuses, SLA policies, calendars): the imported default wins.</summary>
    private static void NormalizeDefaults(LiveConfig live, List<(ConfigSection Section, Entity Entity, IncomingItem Item, bool Created)> applied)
    {
        foreach (var section in ConfigSections.All.Where(s => s.UniqueDefault is not null))
        {
            var prop = section.EntityType.GetProperty(section.UniqueDefault!)!;
            var all = live.Sections[section.Key].Values.Select(i => i.Entity).Concat(applied.Where(a => a.Section == section && a.Created).Select(a => a.Entity)).Distinct().ToList();
            var defaults = all.Where(e => prop.GetValue(e) is true).ToList();
            if (defaults.Count <= 1) continue;
            var keep = applied.Where(a => a.Section == section).Select(a => a.Entity).FirstOrDefault(e => prop.GetValue(e) is true) ?? defaults[0];
            foreach (var e in defaults.Where(e => e != keep)) prop.SetValue(e, false);
        }
    }

    private static string DisplayName(ConfigSection section, JsonObject data, string key) =>
        data[section.DisplayProperty ?? section.NameProperty] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : key;

    public static IReadOnlyList<ConfigSectionInfoDto> Summarize(JsonNode? bundle)
    {
        if (bundle?["sections"] is not JsonObject sections) return [];
        return ConfigSections.All.Where(s => sections[s.Key] is JsonArray)
            .Select(s => new ConfigSectionInfoDto(s.Key, s.Label, ((JsonArray)sections[s.Key]!).Count)).ToList();
    }
}
