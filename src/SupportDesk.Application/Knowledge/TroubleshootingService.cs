using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Domain;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Guided troubleshooting decision trees: question / solution / article / escalate nodes. Drafts may be saved with
/// validation issues; publishing requires a valid tree (start node exists, every option points to an existing node,
/// every node is reachable, questions have answers, article nodes reference articles). Portal usage is counted
/// (started / resolved / escalated).
/// </summary>
public sealed class TroubleshootingService(DeskDbContext db, RequestContext ctx, KbAccess access, HtmlSanitization html, IAuditWriter audit)
{
    public static readonly string[] NodeTypes = ["question", "solution", "article", "escalate"];
    private const int MaxNodes = 200;

    // ───────────────────────────── Desk ─────────────────────────────

    public async Task<List<FlowListItemDto>> ListAsync(CancellationToken ct = default)
    {
        access.RequireAgentReader();
        var flows = await db.TroubleshootingFlows.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);
        var names = await NamesAsync(flows, ct);
        return flows.Select(f => new FlowListItemDto(f.Id, f.Name, f.Description, f.Status.ToString(), f.Visibility.ToString(), f.ProductId,
            Lookup(names.Products, f.ProductId), f.CategoryId, Lookup(names.Categories, f.CategoryId), f.Version, Parse(f.DefinitionJson).Nodes.Count,
            f.StartedCount, f.ResolvedCount, f.EscalatedCount, f.UpdatedAt)).ToList();
    }

    public async Task<FlowDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        access.RequireAgentReader();
        var flow = await db.TroubleshootingFlows.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Flow", id);
        return await MapAsync(flow, ct);
    }

    public async Task<FlowDetailDto> SaveAsync(Guid? id, SaveFlowRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbAuthor);
        var errors = new Dictionary<string, string>();
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 3 or > 200) errors["name"] = "Use a name of 3 to 200 characters.";
        if (!KbRules.TryVisibility(r.Visibility, out var visibility)) errors["visibility"] = "Use Public, Authenticated, Internal or Restricted.";
        if (r.ProductId is { } p && !await db.Products.AnyAsync(x => x.Id == p, ct)) errors["productId"] = "Unknown product.";
        if (r.CategoryId is { } c && !await db.KbCategories.AnyAsync(x => x.Id == c, ct)) errors["categoryId"] = "Unknown category.";
        if (r.Definition is null) errors["definition"] = "The decision tree is required.";
        else if ((r.Definition.Nodes?.Count ?? 0) > MaxNodes) errors["definition"] = $"At most {MaxNodes} steps.";
        if (errors.Count > 0) throw new ValidationException(errors);

        var flow = id is { } fid ? await db.TroubleshootingFlows.FirstOrDefaultAsync(f => f.Id == fid, ct) ?? throw new NotFoundException("Flow", fid) : new TroubleshootingFlow();
        var definition = Normalize(r.Definition!);
        var wasPublished = flow.Status == ArticleStatus.Published;
        flow.Name = name;
        flow.Description = string.IsNullOrWhiteSpace(r.Description) ? null : TextNormalization.Truncate(r.Description.Trim(), 1000);
        flow.Visibility = visibility;
        flow.ProductId = r.ProductId;
        flow.CategoryId = r.CategoryId;
        flow.DefinitionJson = Json.Serialize(definition);
        if (id is null) db.TroubleshootingFlows.Add(flow);
        else if (wasPublished)
        {
            // Editing a published flow keeps it live only while it stays valid.
            if ((await ValidateAsync(definition, ct)).Count > 0) flow.Status = ArticleStatus.Draft;
            else flow.Version++;
        }
        await db.SaveChangesAsync(ct);
        return await MapAsync(flow, ct);
    }

    public async Task<FlowDetailDto> TransitionAsync(Guid id, string action, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbPublish);
        var flow = await db.TroubleshootingFlows.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Flow", id);
        switch ((action ?? "").Trim().ToLowerInvariant())
        {
            case "publish":
                var issues = await ValidateAsync(Parse(flow.DefinitionJson), ct);
                if (issues.Count > 0)
                    throw new DomainException(ErrorCodes.InvalidTransition, $"Fix {issues.Count} problem(s) before publishing: {issues[0].Message}");
                if (flow.Status == ArticleStatus.Published) flow.Version++;
                flow.Status = ArticleStatus.Published;
                break;
            case "unpublish":
                flow.Status = ArticleStatus.Draft;
                break;
            case "archive":
                flow.Status = ArticleStatus.Archived;
                break;
            default:
                throw new ValidationException(new Dictionary<string, string> { ["action"] = "Use publish, unpublish or archive." });
        }
        audit.Write("kb.flow_" + action!.Trim().ToLowerInvariant(), "TroubleshootingFlow", flow.Id, $"{flow.Name}: {action}");
        await db.SaveChangesAsync(ct);
        return await MapAsync(flow, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbPublish);
        var flow = await db.TroubleshootingFlows.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Flow", id);
        db.TroubleshootingFlows.Remove(flow);
        audit.Write("kb.flow_deleted", "TroubleshootingFlow", id, $"Deleted troubleshooting flow '{flow.Name}'");
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Validation ─────────────────────────────

    public async Task<List<FlowValidationIssueDto>> ValidateAsync(FlowDefinitionDto? definition, CancellationToken ct = default)
    {
        var issues = new List<FlowValidationIssueDto>();
        if (definition is null || definition.Nodes.Count == 0)
        {
            issues.Add(new FlowValidationIssueDto(null, "Add at least one step."));
            return issues;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in definition.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id)) issues.Add(new FlowValidationIssueDto(null, "Every step needs an id."));
            else if (!ids.Add(n.Id)) issues.Add(new FlowValidationIssueDto(n.Id, $"Duplicate step id '{n.Id}'."));
        }
        if (string.IsNullOrWhiteSpace(definition.StartNodeId) || !ids.Contains(definition.StartNodeId))
            issues.Add(new FlowValidationIssueDto(null, "Choose the first step (the start node does not exist)."));
        var articleIds = definition.Nodes.Where(n => n.ArticleId is not null).Select(n => n.ArticleId!.Value).Distinct().ToList();
        var existingArticles = await db.Articles.AsNoTracking().Where(a => articleIds.Contains(a.Id)).Select(a => new { a.Id, a.PublishedVersionId }).ToListAsync(ct);
        foreach (var n in definition.Nodes)
        {
            var label = string.IsNullOrWhiteSpace(n.Title) ? TextNormalization.Truncate(TextNormalization.HtmlToText(n.Text), 40) : n.Title;
            if (!NodeTypes.Contains(n.Type)) { issues.Add(new FlowValidationIssueDto(n.Id, $"'{label}': unknown step type '{n.Type}'.")); continue; }
            if (n.Type != "article" && TextNormalization.HtmlToText(n.Text).Trim().Length == 0)
                issues.Add(new FlowValidationIssueDto(n.Id, $"Step '{n.Id}' has no text."));
            if (n.Type == "question" && n.Options.Count == 0)
                issues.Add(new FlowValidationIssueDto(n.Id, $"Question '{label}' needs at least one answer."));
            if (n.Type == "escalate" && n.Options.Count > 0)
                issues.Add(new FlowValidationIssueDto(n.Id, $"Escalation step '{label}' cannot have answers."));
            if (n.Type == "article")
            {
                var article = n.ArticleId is { } aid ? existingArticles.FirstOrDefault(a => a.Id == aid) : null;
                if (article is null) issues.Add(new FlowValidationIssueDto(n.Id, $"Article step '{label}' must link an existing article."));
                else if (article.PublishedVersionId is null) issues.Add(new FlowValidationIssueDto(n.Id, $"Article step '{label}' links an article that is not published."));
            }
            foreach (var o in n.Options)
            {
                if (string.IsNullOrWhiteSpace(o.Label)) issues.Add(new FlowValidationIssueDto(n.Id, $"An answer of '{label}' has no label."));
                if (string.IsNullOrWhiteSpace(o.Next) || !ids.Contains(o.Next))
                    issues.Add(new FlowValidationIssueDto(n.Id, $"Answer '{o.Label}' of '{label}' points to a missing step."));
                else if (o.Next == n.Id) issues.Add(new FlowValidationIssueDto(n.Id, $"Answer '{o.Label}' of '{label}' points back to the same step."));
            }
        }
        if (ids.Contains(definition.StartNodeId))
        {
            var byId = definition.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Id)).GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
            var seen = new HashSet<string> { definition.StartNodeId };
            var queue = new Queue<string>([definition.StartNodeId]);
            while (queue.Count > 0)
                foreach (var o in byId[queue.Dequeue()].Options)
                    if (byId.ContainsKey(o.Next) && seen.Add(o.Next)) queue.Enqueue(o.Next);
            foreach (var n in definition.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Id) && !seen.Contains(n.Id)))
                issues.Add(new FlowValidationIssueDto(n.Id, $"Step '{(string.IsNullOrWhiteSpace(n.Title) ? n.Id : n.Title)}' cannot be reached from the first step."));
        }
        return issues;
    }

    /// <summary>Validates a client-supplied definition (designer live validation).</summary>
    public Task<List<FlowValidationIssueDto>> ValidateDefinitionAsync(FlowDefinitionDto? definition, CancellationToken ct = default)
    {
        access.RequireAgentReader();
        return ValidateAsync(definition is null ? null : Normalize(definition), ct);
    }

    private FlowDefinitionDto Normalize(FlowDefinitionDto d) =>
        new((d.StartNodeId ?? "").Trim(), (d.Nodes ?? []).Where(n => n is not null).Select(n => new FlowNodeDto(
            (n.Id ?? "").Trim(),
            (n.Type ?? "").Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(n.Title) ? null : TextNormalization.Truncate(n.Title.Trim(), 120),
            html.Sanitize(n.Text ?? ""),
            n.ArticleId,
            (n.Options ?? []).Select(o => new FlowOptionDto(TextNormalization.Truncate((o.Label ?? "").Trim(), 200), (o.Next ?? "").Trim())).Take(12).ToList())).ToList());

    public static FlowDefinitionDto Parse(string? json) =>
        Json.DeserializeOr<FlowDefinitionDto?>(json, null) is { } d
            ? d with { Nodes = d.Nodes?.Select(n => n with { Options = n.Options ?? [] }).ToList() ?? [] }
            : new FlowDefinitionDto("", []);

    // ───────────────────────────── Portal ─────────────────────────────

    /// <summary>Published flows for a portal reader (Public for everyone; Authenticated/Restricted for signed-in contacts).</summary>
    public IQueryable<TroubleshootingFlow> PublishedFor(KbReader reader)
    {
        var q = db.TroubleshootingFlows.AsNoTracking().Where(f => f.Status == ArticleStatus.Published && f.Visibility != KbVisibility.Internal);
        if (reader.Audience == KnowledgeAudience.Public) q = q.Where(f => f.Visibility == KbVisibility.Public);
        return q;
    }

    public async Task RecordUsageAsync(Guid id, KbReader reader, string outcome, CancellationToken ct = default)
    {
        var flows = PublishedFor(reader).Where(f => f.Id == id);
        var affected = (outcome ?? "").Trim().ToLowerInvariant() switch
        {
            "started" => await db.TroubleshootingFlows.Where(f => flows.Any(x => x.Id == f.Id)).ExecuteUpdateAsync(u => u.SetProperty(f => f.StartedCount, f => f.StartedCount + 1), ct),
            "resolved" => await db.TroubleshootingFlows.Where(f => flows.Any(x => x.Id == f.Id)).ExecuteUpdateAsync(u => u.SetProperty(f => f.ResolvedCount, f => f.ResolvedCount + 1), ct),
            "escalated" => await db.TroubleshootingFlows.Where(f => flows.Any(x => x.Id == f.Id)).ExecuteUpdateAsync(u => u.SetProperty(f => f.EscalatedCount, f => f.EscalatedCount + 1), ct),
            _ => throw new ValidationException(new Dictionary<string, string> { ["outcome"] = "Use started, resolved or escalated." })
        };
        if (affected == 0) throw new NotFoundException("Flow", id);
    }

    // ───────────────────────────── Mapping ─────────────────────────────

    private async Task<FlowDetailDto> MapAsync(TroubleshootingFlow f, CancellationToken ct)
    {
        var names = await NamesAsync([f], ct);
        var definition = Parse(f.DefinitionJson);
        return new FlowDetailDto(f.Id, f.Name, f.Description, f.Status.ToString(), f.Visibility.ToString(), f.ProductId, Lookup(names.Products, f.ProductId),
            f.CategoryId, Lookup(names.Categories, f.CategoryId), f.Version, f.StartedCount, f.ResolvedCount, f.EscalatedCount, definition,
            await ValidateAsync(definition, ct), f.UpdatedAt);
    }

    private sealed record Names(Dictionary<Guid, string> Products, Dictionary<Guid, string> Categories);

    private async Task<Names> NamesAsync(IReadOnlyList<TroubleshootingFlow> flows, CancellationToken ct)
    {
        var productIds = flows.Where(f => f.ProductId.HasValue).Select(f => f.ProductId!.Value).Distinct().ToList();
        var categoryIds = flows.Where(f => f.CategoryId.HasValue).Select(f => f.CategoryId!.Value).Distinct().ToList();
        return new Names(
            await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct),
            await db.KbCategories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct));
    }

    private static string? Lookup(Dictionary<Guid, string> map, Guid? id) => id is { } g && map.TryGetValue(g, out var n) ? n : null;
}
