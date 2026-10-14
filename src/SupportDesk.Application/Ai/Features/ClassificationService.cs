using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Ai;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Ai.Features;

public static class ClassificationFields
{
    public const string Category = "category";
    public const string Product = "product";
    public const string Priority = "priority";
    public const string Department = "department";
    public static readonly string[] All = [Category, Product, Priority, Department];
}

public sealed record FieldPrediction(string? Value, double Confidence);

public sealed record ClassificationResult(
    Guid ExecutionId,
    IReadOnlyDictionary<string, FieldPrediction> Fields,
    IReadOnlyList<string> Tags,
    string? Explanation,
    double Confidence,
    string Provider,
    string Model,
    IReadOnlyList<string> InjectionFlags);

/// <summary>
/// Per-tenant training corpus for the local classifier (recent labelled tickets). Checked for new tickets at most once a
/// minute and reloaded when they arrived (or after ten minutes); never shared across tenants (keyed by tenant id).
/// </summary>
public sealed class AiCorpusCache
{
    /// <summary>
    /// During a burst of new tickets every classification used to see a new corpus key, so each ticket reloaded 2,000
    /// examples and retrained the local model (the most expensive step of the outbox pipeline). A minute of staleness
    /// in the training examples is harmless.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, (string Key, DateTime LoadedAt, DateTime CheckedAt, IReadOnlyList<ClassificationExample> Examples)> _cache = new();

    public async Task<(string Key, IReadOnlyList<ClassificationExample> Examples)> GetAsync(DeskDbContext db, Guid tenantId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(tenantId, out var cached) && now - cached.CheckedAt < CheckInterval) return (cached.Key, cached.Examples);
        var probe = await db.Tickets.AsNoTracking().Where(t => t.CategoryId != null || t.ProductId != null)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Max = g.Max(t => t.CreatedAt) }).SingleOrDefaultAsync(ct); // one group at most
        var key = probe is null ? $"{tenantId:N}:0" : $"{tenantId:N}:{probe.Count}:{probe.Max.Ticks}";
        if (cached.Examples is not null && cached.Key == key && now - cached.LoadedAt < TimeSpan.FromMinutes(10))
        {
            _cache[tenantId] = cached with { CheckedAt = now };
            return (cached.Key, cached.Examples);
        }
        var rows = await db.Tickets.AsNoTracking()
            .Where(t => (t.CategoryId != null || t.ProductId != null) && !t.IsSpam && t.MergedIntoTicketId == null)
            .OrderByDescending(t => t.CreatedAt).Take(2000)
            .Select(t => new { t.Subject, t.DescriptionText, t.CategoryId, t.SubcategoryId, t.ProductId, t.Priority, t.DepartmentId })
            .ToListAsync(ct);
        var examples = rows.Select(r =>
        {
            var labels = new Dictionary<string, string>();
            if ((r.SubcategoryId ?? r.CategoryId) is { } c) labels[ClassificationFields.Category] = c.ToString();
            if (r.ProductId is { } p) labels[ClassificationFields.Product] = p.ToString();
            labels[ClassificationFields.Priority] = r.Priority.ToString();
            labels[ClassificationFields.Department] = r.DepartmentId.ToString();
            return new ClassificationExample($"{r.Subject}\n{TextNormalization.Truncate(r.DescriptionText, 600)}", labels);
        }).ToList();
        _cache[tenantId] = (key, now, now, examples);
        return (key, examples);
    }
}

/// <summary>
/// Suggests category/subcategory, product, priority and department (with confidence) and applies accepted suggestions
/// through the normal ticket services (validation, history, SLA re-targeting, events).
/// </summary>
public sealed class AiClassificationService(
    DeskDbContext db,
    RequestContext ctx,
    AiGateway gateway,
    AiTicketLoader loader,
    AiCorpusCache corpus,
    TicketService tickets,
    TicketHistory history,
    TicketLookups lookups,
    IAuditWriter audit)
{
    public async Task<ClassificationSuggestionDto> SuggestAsync(Guid ticketId, CancellationToken ct)
    {
        ctx.Require(Permissions.TicketsView);
        var (ticket, snapshot) = await loader.LoadAsync(ticketId, internalView: false, ct);
        var result = await ClassifyAsync(ticket, snapshot, interactive: true, AiFeatures.Classification, null, ct);
        return await ToDtoAsync(ticket, result, autoApplied: false, ct);
    }

    public async Task<ClassificationResult> ClassifyAsync(Ticket ticket, TicketSnapshot snapshot, bool interactive, string feature, Guid? agentId, CancellationToken ct)
    {
        var options = await OptionsAsync(ct);
        var (key, examples) = await corpus.GetAsync(db, ctx.TenantId, ct);
        var training = examples.Where(e => !e.Text.StartsWith(ticket.Subject + "\n", StringComparison.Ordinal)).ToList();
        var knownTags = await db.Tags.AsNoTracking().OrderBy(t => t.Name).Select(t => t.Name).Take(200).ToListAsync(ct);
        var text = AiTicketLoader.CustomerText(snapshot);
        var optionsText = new StringBuilder();
        foreach (var (field, list) in options)
        {
            optionsText.AppendLine($"{field}:");
            foreach (var o in list) optionsText.AppendLine($"  {o.Value} = {o.Name}");
        }
        var result = await gateway.CompleteAsync(new AiCall
        {
            Feature = feature, PromptKey = PromptKeys.Classification, EntityType = "Ticket", EntityId = ticket.Id, Interactive = interactive, AgentId = agentId,
            RequiresAgentsPlan = feature == AiFeatures.Agents,
            Values = new Dictionary<string, string> { ["options"] = optionsText.ToString(), ["tags"] = string.Join(", ", knownTags) },
            Untrusted = new Dictionary<string, string> { ["ticket"] = text },
            LocalTask = new ClassificationTask(text, options, training, key + ":" + training.Count, knownTags),
            MaxTokens = 500, Temperature = 0
        }, raw => Parse(raw, options, knownTags), ct);
        var value = result.Value;
        return value with { ExecutionId = result.ExecutionId, Provider = result.Provider, Model = result.Model, InjectionFlags = result.InjectionFlags };
    }

    private static AiOutput<ClassificationResult> Parse(string raw, IReadOnlyDictionary<string, IReadOnlyList<LabelOption>> options, IReadOnlyList<string> knownTags)
    {
        var json = AiJson.ParseObject(raw);
        var fieldsEl = AiJson.Prop(json, "fields") ?? json;
        var fields = new Dictionary<string, FieldPrediction>();
        foreach (var field in ClassificationFields.All)
        {
            if (AiJson.Prop(fieldsEl, field) is not { } f) continue;
            var value = f.ValueKind == System.Text.Json.JsonValueKind.Object ? AiJson.Str(f, "value") : f.ValueKind == System.Text.Json.JsonValueKind.String ? f.GetString() : null;
            var confidence = f.ValueKind == System.Text.Json.JsonValueKind.Object ? AiJson.Clamp01(AiJson.Num(f, "confidence")) : 0.5;
            if (value is null || value.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
            // Only whitelisted option values are accepted (ids are matched exactly, enum names case-insensitively).
            var match = options.TryGetValue(field, out var list) ? list.FirstOrDefault(o => o.Value.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) : null;
            if (match is null) continue;
            fields[field] = new FieldPrediction(match.Value, Math.Round(confidence, 4));
        }
        var tags = AiJson.StrList(json, "tags", 5, 50).Where(t => knownTags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        var explanation = AiJson.Str(json, "explanation");
        var overall = fields.Count == 0 ? 0 : fields.TryGetValue(ClassificationFields.Category, out var cat) ? cat.Confidence : fields.Values.Average(f => f.Confidence);
        var result = new ClassificationResult(Guid.Empty, fields, tags, explanation is null ? null : TextNormalization.Truncate(explanation, 500), Math.Round(overall, 4), "", "", []);
        return new AiOutput<ClassificationResult>(result, overall, Logged: new { fields, tags, explanation });
    }

    /// <summary>Whitelisted options per field: leaf categories ("Parent › Sub"), products, priorities, departments.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LabelOption>>> OptionsAsync(CancellationToken ct)
    {
        var categories = await db.TicketCategories.AsNoTracking().Where(c => c.IsActive).Select(c => new { c.Id, c.ParentId, c.Name }).ToListAsync(ct);
        var byId = categories.ToDictionary(c => c.Id);
        var categoryOptions = categories.Select(c =>
        {
            var name = c.ParentId is { } p && byId.TryGetValue(p, out var parent) ? $"{parent.Name} › {c.Name}" : c.Name;
            return new LabelOption(c.Id.ToString(), name, TextNormalization.Tokenize(name).Distinct().ToList());
        }).ToList();
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).Select(p => new { p.Id, p.Name, p.Code }).ToListAsync(ct);
        var productOptions = products.Select(p => new LabelOption(p.Id.ToString(), p.Name,
            TextNormalization.Tokenize(p.Name + " " + p.Code).Distinct().ToList())).ToList();
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive).Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var departmentOptions = departments.Select(d => new LabelOption(d.Id.ToString(), d.Name, TextNormalization.Tokenize(d.Name).Distinct().ToList())).ToList();
        var priorityOptions = new List<LabelOption>
        {
            new("Low", "Low", ["question", "how", "feature", "request", "suggestion", "minor", "cosmetic", "training"]),
            new("Medium", "Medium", ["issue", "problem", "error", "incorrect", "mismatch"]),
            new("High", "High", ["urgent", "asap", "blocking", "unable", "cannot", "fail", "failure", "month-end", "deadline", "stuck"]),
            new("Critical", "Critical", ["down", "outage", "production", "critical", "emergency", "all users", "nobody", "halted", "stopped", "data loss"])
        };
        return new Dictionary<string, IReadOnlyList<LabelOption>>
        {
            [ClassificationFields.Category] = categoryOptions,
            [ClassificationFields.Product] = productOptions,
            [ClassificationFields.Priority] = priorityOptions,
            [ClassificationFields.Department] = departmentOptions
        };
    }

    public async Task<ClassificationSuggestionDto> ToDtoAsync(Ticket t, ClassificationResult r, bool autoApplied, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        var current = CurrentValues(t);
        var fields = new List<ClassificationFieldDto>();
        foreach (var field in ClassificationFields.All)
        {
            r.Fields.TryGetValue(field, out var p);
            var cur = current.GetValueOrDefault(field);
            fields.Add(new ClassificationFieldDto(field, p?.Value, Label(field, p?.Value), p?.Confidence ?? 0, cur, Label(field, cur),
                p?.Value is not null && !string.Equals(p.Value, cur, StringComparison.OrdinalIgnoreCase)));
        }
        return new ClassificationSuggestionDto(r.ExecutionId, t.Id, fields, r.Confidence, r.Explanation, autoApplied, r.Provider, r.Model);
    }

    private string? Label(string field, string? value)
    {
        if (value is null) return null;
        if (field == ClassificationFields.Priority) return value;
        if (!Guid.TryParse(value, out var id)) return value;
        return field switch
        {
            ClassificationFields.Category => lookups.Category(id),
            ClassificationFields.Product => lookups.Product(id),
            ClassificationFields.Department => lookups.Department(id),
            _ => value
        };
    }

    public static Dictionary<string, string?> CurrentValues(Ticket t) => new()
    {
        [ClassificationFields.Category] = (t.SubcategoryId ?? t.CategoryId)?.ToString(),
        [ClassificationFields.Product] = t.ProductId?.ToString(),
        [ClassificationFields.Priority] = t.Priority.ToString(),
        [ClassificationFields.Department] = t.DepartmentId.ToString()
    };

    // ───────────────────────────── Apply ─────────────────────────────

    /// <summary>Applies a stored suggestion (values come from the execution record, never from the client).</summary>
    public async Task<ClassificationApplyResultDto> ApplyAsync(Guid ticketId, ApplyClassificationRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AiUse);
        ctx.Require(Permissions.TicketsEdit);
        var execution = await db.AiExecutions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == r.ExecutionId && e.EntityId == ticketId, ct)
                        ?? throw new NotFoundException("AI suggestion", r.ExecutionId);
        if (execution.Status != AiExecutionStatus.Succeeded || execution.Feature is not (AiFeatures.Classification or AiFeatures.Agents))
            throw new DomainException("validation_failed", "This AI execution is not a classification suggestion.", 400);
        if (execution.Outcome is AiOutcome.Applied or AiOutcome.Rejected)
            throw new ConflictException("already_decided", $"This suggestion was already {execution.Outcome.ToString().ToLowerInvariant()}.");
        var stored = AiJson.ParseObject(execution.OutputJson ?? "{}");
        var fieldsEl = AiJson.Prop(stored, "fields");
        var wanted = (r.Fields is { Count: > 0 } ? r.Fields : ClassificationFields.All).Select(f => f.ToLowerInvariant()).ToHashSet();
        var values = new Dictionary<string, string?>();
        foreach (var field in ClassificationFields.All.Where(wanted.Contains))
            if (fieldsEl is { } fe && AiJson.Prop(fe, field) is { } f && AiJson.Str(f, "value") is { } v) values[field] = v;
        if (values.Count == 0) throw new DomainException("validation_failed", "The suggestion has no values for the selected fields.", 400);
        var (applied, skipped, _) = await ApplyValuesAsync(ticketId, values, $"AI classification applied", execution.Id, ct);
        await gateway.MarkOutcomeAsync(execution.Id, applied.Count > 0 ? AiOutcome.Applied : AiOutcome.Rejected, ct);
        return new ClassificationApplyResultDto(ticketId, applied, skipped,
            applied.Count == 0 ? "Nothing to change: the ticket already has these values." : $"Applied {string.Join(", ", applied)}.");
    }

    /// <summary>
    /// Core apply used by suggestions, agents and proposals. Returns the fields changed, the fields skipped and the
    /// before-state (for reverting). Values: category (leaf category id), product (id), priority (name), department (id).
    /// </summary>
    public async Task<(List<string> Applied, List<string> Skipped, Dictionary<string, string?> Before)> ApplyValuesAsync(
        Guid ticketId, IReadOnlyDictionary<string, string?> values, string summary, Guid? executionId, CancellationToken ct)
    {
        var t = await tickets.LoadForUpdateAsync(ticketId, ct);
        var before = CurrentValues(t);
        before["categoryId"] = t.CategoryId?.ToString();
        before["subcategoryId"] = t.SubcategoryId?.ToString();
        var applied = new List<string>();
        var skipped = new List<string>();
        Guid? categoryId = null, subcategoryId = null, productId = null;
        string? priority = null;
        var clear = new List<string>();

        if (values.TryGetValue(ClassificationFields.Category, out var cv) && Guid.TryParse(cv, out var catId) && catId != (t.SubcategoryId ?? t.CategoryId))
        {
            var cat = await db.TicketCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == catId && c.IsActive, ct);
            if (cat is null) skipped.Add(ClassificationFields.Category);
            else
            {
                if (cat.ParentId is { } parent) { categoryId = parent; subcategoryId = cat.Id; }
                else { categoryId = cat.Id; if (t.SubcategoryId is not null) clear.Add("subcategoryId"); }
                applied.Add(ClassificationFields.Category);
            }
        }
        if (values.TryGetValue(ClassificationFields.Product, out var pv) && Guid.TryParse(pv, out var prodId) && prodId != t.ProductId)
        {
            if (await db.Products.AnyAsync(p => p.Id == prodId, ct)) { productId = prodId; applied.Add(ClassificationFields.Product); }
            else skipped.Add(ClassificationFields.Product);
        }
        if (values.TryGetValue(ClassificationFields.Priority, out var prv) && Enum.TryParse<TicketPriority>(prv, true, out var pr) && Enum.IsDefined(pr) && pr != t.Priority)
        {
            priority = pr.ToString();
            applied.Add(ClassificationFields.Priority);
        }
        if (categoryId is not null || productId is not null || priority is not null)
        {
            await tickets.UpdateAsync(t.Id, new UpdateTicketRequest
            {
                RowVersion = Convert.ToBase64String(t.RowVersion), CategoryId = categoryId, SubcategoryId = subcategoryId, ProductId = productId,
                Priority = priority, Clear = clear.Count > 0 ? clear : null
            }, ct);
        }
        if (values.TryGetValue(ClassificationFields.Department, out var dv) && Guid.TryParse(dv, out var deptId) && deptId != t.DepartmentId)
        {
            if (!(ctx.IsSystem || ctx.Has(Permissions.TicketsTransfer)) || !await db.Departments.AnyAsync(d => d.Id == deptId && d.IsActive, ct))
                skipped.Add(ClassificationFields.Department);
            else
            {
                var tracked = await tickets.LoadForUpdateAsync(t.Id, ct);
                await tickets.TransferCoreAsync(tracked, deptId, null, null, summary, ct);
                await tickets.SaveAsync(tracked, ct);
                applied.Add(ClassificationFields.Department);
            }
        }
        if (applied.Count > 0)
        {
            await lookups.LoadAsync(ct);
            var after = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == t.Id, ct);
            var changes = new List<FieldChange>();
            foreach (var f in applied)
            {
                var a = CurrentValues(after)[f];
                changes.Add(new FieldChange(f, Display(f, before[f]), Display(f, a)));
            }
            history.Record(after, TicketEventTypes.AiAction, $"{summary}: {string.Join(", ", changes.Select(c => $"{c.Field} → {c.To}"))}", changes,
                new { executionId, kind = "classification" });
            audit.Write(AuditActions.AiActionApplied, "Ticket", t.Id, $"{summary} on #{t.Number}", new { executionId, changes });
            await db.SaveChangesAsync(ct);
        }
        return (applied, skipped, before);
    }

    private string? Display(string field, string? value) => value is null ? null : field switch
    {
        ClassificationFields.Category => Guid.TryParse(value, out var c) ? lookups.Category(c) ?? value : value,
        ClassificationFields.Product => Guid.TryParse(value, out var p) ? lookups.Product(p) ?? value : value,
        ClassificationFields.Department => Guid.TryParse(value, out var d) ? lookups.Department(d) ?? value : value,
        _ => value
    };
}
