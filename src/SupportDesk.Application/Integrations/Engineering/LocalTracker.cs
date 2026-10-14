using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Infrastructure;

namespace SupportDesk.Application.Integrations.Engineering;

/// <summary>
/// The built-in engineering tracker: work items and releases stored in SupportDesk (LocalWorkItems / LocalReleases).
/// External ids are the per-tenant item numbers ("42" → ENG-42); deleted items report <see cref="TrackerLookupStatus.Deleted"/>.
/// <see cref="CreateAsync"/> adds the row to the caller's unit of work (saved with the ticket link).
/// </summary>
public sealed partial class LocalTracker(TrackerConnection connection, DeskDbContext db, TenantCounterService counters, HtmlSanitization html) : IEngineeringTracker
{
    public const string CounterName = "engineering-workitem";
    public const string KeyPrefix = "ENG-";

    public EngineeringProvider Provider => EngineeringProvider.Local;

    public Task<TrackerTestResult> TestAsync(CancellationToken ct) =>
        Task.FromResult(new TrackerTestResult(true, "The built-in tracker is ready.", []));

    public Task<IReadOnlyList<TrackerProjectDto>> ProjectsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TrackerProjectDto>>([]);

    public async Task<TrackerWorkItem> CreateAsync(WorkItemDraft draft, CancellationToken ct)
    {
        string? assigneeName = null;
        if (draft.AssigneeUserId is { } uid)
        {
            assigneeName = await db.Users.AsNoTracking().Where(u => u.Id == uid && u.IsActive).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
                           ?? throw new Tickets.ValidationException(new Dictionary<string, string> { ["assigneeUserId"] = "Choose an active engineer." });
        }
        var number = (int)await counters.NextAsync(CounterName, 1, ct);
        var rank = await db.Set<LocalWorkItem>().Where(w => w.IntegrationId == connection.IntegrationId && w.State == LocalWorkItemState.New)
            .MaxAsync(w => (double?)w.Rank, ct) ?? 0;
        var item = new LocalWorkItem
        {
            IntegrationId = connection.IntegrationId, Number = number, Type = draft.Type, Title = TextNormalization.Truncate(draft.Title, 500),
            DescriptionHtml = string.IsNullOrWhiteSpace(draft.DescriptionHtml) ? null : html.Sanitize(draft.DescriptionHtml), Severity = draft.Severity,
            Priority = draft.Priority, AssigneeUserId = draft.AssigneeUserId, AreaPath = draft.AreaPath, IterationPath = draft.IterationPath,
            TagsJson = Json.Serialize(draft.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList()), CustomerImpactCount = draft.CustomerImpactCount,
            ProductId = draft.ProductId, Rank = rank + 1
        };
        db.Set<LocalWorkItem>().Add(item);
        return Snapshot(item, null, assigneeName);
    }

    public async Task<IReadOnlyList<TrackerLookup>> GetAsync(IReadOnlyCollection<string> externalIds, CancellationToken ct)
    {
        var numbers = externalIds.Select(id => int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
            .Where(n => n is not null).Select(n => n!.Value).Distinct().ToList();
        var items = await db.Set<LocalWorkItem>().IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(w => w.IntegrationId == connection.IntegrationId && numbers.Contains(w.Number)).ToListAsync(ct);
        var releaseIds = items.Where(i => i.ReleaseId != null).Select(i => i.ReleaseId!.Value).Distinct().ToList();
        var releases = await db.Set<LocalRelease>().AsNoTracking().Where(r => releaseIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var userIds = items.Where(i => i.AssigneeUserId != null).Select(i => i.AssigneeUserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var result = new List<TrackerLookup>();
        foreach (var id in externalIds)
        {
            var item = int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? items.FirstOrDefault(i => i.Number == n) : null;
            if (item is null) result.Add(TrackerLookup.Missing(id, TrackerLookupStatus.NotFound, "The work item does not exist in the built-in tracker."));
            else if (item.IsDeleted) result.Add(TrackerLookup.Missing(id, TrackerLookupStatus.Deleted, "The work item was deleted from the built-in tracker."));
            else
                result.Add(TrackerLookup.Found(Snapshot(item, item.ReleaseId is { } rid ? releases.GetValueOrDefault(rid) : null,
                    item.AssigneeUserId is { } a ? users.GetValueOrDefault(a) : null)));
        }
        return result;
    }

    public string? ParseReference(string reference) => ParseLocalReference(reference);

    public static string? ParseLocalReference(string reference)
    {
        var m = LocalRefRegex().Match(reference.Trim());
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : null;
    }

    public string DisplayId(string externalId) => KeyPrefix + externalId;

    public static string Url(int number) => $"/engineering?item={number}";

    public static TrackerWorkItem Snapshot(LocalWorkItem item, LocalRelease? release, string? assigneeName)
    {
        var released = release?.ReleasedAt is not null;
        return new TrackerWorkItem(
            item.Number.ToString(CultureInfo.InvariantCulture), item.Title, Url(item.Number), item.Type.ToString(), item.Type, item.State.ToString(), assigneeName,
            item.AreaPath, item.IterationPath, release?.Name ?? item.FixVersion, Json.StringList(item.TagsJson), item.Severity?.ToString(), item.Priority.ToString(),
            null, item.UpdatedAt == default ? null : item.UpdatedAt, [],
            ReleaseName: released ? release!.Name : null, ReleasedAt: release?.ReleasedAt, DeployedAt: release?.DeployedAt, Released: released,
            Resolved: item.State is LocalWorkItemState.Resolved or LocalWorkItemState.Closed);
    }

    /// <summary>"42", "#42", "ENG-42", "/engineering?item=42".</summary>
    [GeneratedRegex(@"^(?:.*[?&]item=|ENG-|#)?(\d{1,9})$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalRefRegex();
}
