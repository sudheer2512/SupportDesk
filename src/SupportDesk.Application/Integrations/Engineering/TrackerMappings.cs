using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SupportDesk.Domain;
using SupportDesk.Domain.Tickets;

namespace SupportDesk.Application.Integrations.Engineering;

/// <summary>Small helpers for reading tracker JSON defensively (missing properties, nulls, numbers as strings).</summary>
public static class TrackerJson
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined ? v : null;

    public static string? Str(this JsonElement e, string name) => e.Prop(name) is { } v ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    } : null;

    public static DateTime? Date(this JsonElement e, string name) =>
        e.Str(name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static IEnumerable<JsonElement> Items(this JsonElement? e) => e is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];
}

/// <summary>
/// Azure DevOps Services / Server (TFS) REST 7.x mapping shared by the HTTP adapter and the service-hook receiver:
/// work item JSON → <see cref="TrackerWorkItem"/>, JSON-patch documents for creation, reference parsing.
/// </summary>
public static partial class AzureDevOpsMapping
{
    public static string WebUrl(string? baseUrl, string? project, string id) =>
        $"{(baseUrl ?? "").TrimEnd('/')}/{Uri.EscapeDataString(project ?? "")}/_workitems/edit/{id}";

    /// <summary>A work item resource ({id, fields, relations, _links}).</summary>
    public static TrackerWorkItem FromWorkItem(JsonElement wi, TrackerSettings s, string? baseUrl)
    {
        var id = wi.Str("id") ?? throw new TrackerException("Azure DevOps returned a work item without an id.");
        var fields = wi.Prop("fields") ?? default;
        var html = wi.Prop("_links")?.Prop("html")?.Str("href");
        return FromFields(id, fields, wi.Prop("relations"), s, html ?? WebUrl(baseUrl, fields.Str("System.TeamProject") ?? s.Project, id));
    }

    public static TrackerWorkItem FromFields(string id, JsonElement fields, JsonElement? relations, TrackerSettings s, string? webUrl)
    {
        var type = fields.Str("System.WorkItemType");
        var tags = (fields.Str("System.Tags") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var artifacts = new List<TrackerArtifact>();
        foreach (var rel in relations.Items())
        {
            if (rel.Str("rel") != "ArtifactLink" || rel.Str("url") is not { } url) continue;
            var name = rel.Prop("attributes")?.Str("name");
            var tail = Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
            var last = tail.Split('/').Last();
            if (url.Contains("/Git/PullRequestId/", StringComparison.OrdinalIgnoreCase)) artifacts.Add(new TrackerArtifact("PullRequest", last, name ?? "Pull request", null, null));
            else if (url.Contains("/VersionControl/Changeset/", StringComparison.OrdinalIgnoreCase)) artifacts.Add(new TrackerArtifact("Changeset", last, name ?? "Changeset", null, null));
            else if (url.Contains("/Git/Commit/", StringComparison.OrdinalIgnoreCase)) artifacts.Add(new TrackerArtifact("Commit", last.Length > 10 ? last[..10] : last, name ?? "Commit", null, null));
            else if (url.Contains("/Build/Build/", StringComparison.OrdinalIgnoreCase)) artifacts.Add(new TrackerArtifact("Build", last, name ?? "Build", null, null));
        }
        return new TrackerWorkItem(
            id, fields.Str("System.Title") ?? $"Work item {id}", webUrl, type, s.ItemTypeFor(type), fields.Str("System.State"), Identity(fields.Prop("System.AssignedTo")),
            fields.Str("System.AreaPath"), fields.Str("System.IterationPath"), s.Fields.FixVersionField is { Length: > 0 } fv ? fields.Str(fv) : null, tags,
            s.Fields.SeverityField is { Length: > 0 } sev ? fields.Str(sev) : null, s.Fields.PriorityField is { Length: > 0 } pri ? fields.Str(pri) : null,
            fields.Str("System.TeamProject"), fields.Date("System.ChangedDate"), artifacts);
    }

    /// <summary>Identity fields are objects ({displayName, uniqueName}) in 5.0+ and "Name &lt;mail&gt;" strings in older payloads.</summary>
    public static string? Identity(JsonElement? e) => e switch
    {
        { ValueKind: JsonValueKind.Object } o => o.Str("displayName") ?? o.Str("uniqueName"),
        { ValueKind: JsonValueKind.String } s => s.GetString()?.Split('<')[0].Trim(),
        _ => null
    };

    public static string SeverityValue(TicketSeverity s) => s switch
    {
        TicketSeverity.Critical => "1 - Critical",
        TicketSeverity.Major => "2 - High",
        TicketSeverity.Minor => "3 - Medium",
        _ => "4 - Low"
    };

    public static int PriorityValue(TicketPriority p) => p switch
    {
        TicketPriority.Critical => 1,
        TicketPriority.High => 2,
        TicketPriority.Medium => 3,
        _ => 4
    };

    /// <summary>JSON-patch document for POST …/_apis/wit/workitems/${type}.</summary>
    public static List<Dictionary<string, object?>> CreatePatch(WorkItemDraft d, TrackerSettings s)
    {
        var ops = new List<Dictionary<string, object?>>();
        void Add(string field, object? value)
        {
            if (value is null || value is string { Length: 0 }) return;
            ops.Add(new Dictionary<string, object?> { ["op"] = "add", ["path"] = $"/fields/{field}", ["value"] = value });
        }
        Add("System.Title", d.Title.Length > 255 ? d.Title[..255] : d.Title);
        var bodyField = d.Type == EngineeringItemType.Bug ? s.Fields.ReproStepsField ?? "Microsoft.VSTS.TCM.ReproSteps" : s.Fields.DescriptionField ?? "System.Description";
        Add(bodyField, d.DescriptionHtml);
        if (d.Severity is { } sev && d.Type == EngineeringItemType.Bug && s.Fields.SeverityField is { Length: > 0 } sf) Add(sf, SeverityValue(sev));
        if (s.Fields.PriorityField is { Length: > 0 } pf) Add(pf, PriorityValue(d.Priority));
        Add("System.AreaPath", d.AreaPath);
        Add("System.IterationPath", d.IterationPath);
        if (d.Tags.Count > 0) Add("System.Tags", string.Join("; ", d.Tags));
        if (s.Fields.CustomerImpactField is { Length: > 0 } cf && d.CustomerImpactCount > 0) Add(cf, d.CustomerImpactCount);
        if (d.SourceUrl is { } url)
            ops.Add(new Dictionary<string, object?>
            {
                ["op"] = "add", ["path"] = "/relations/-",
                ["value"] = new Dictionary<string, object?> { ["rel"] = "Hyperlink", ["url"] = url, ["attributes"] = new Dictionary<string, object?> { ["comment"] = $"SupportDesk ticket {d.SourceReference}" } }
            });
        return ops;
    }

    public static string? ParseReference(string reference)
    {
        var r = reference.Trim();
        var m = AdoUrlRegex().Match(r);
        if (m.Success) return m.Groups[1].Value;
        m = AdoIdRegex().Match(r);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"(?:_workitems/edit/|/_apis/wit/workitems/|workitems/edit/)(\d{1,10})", RegexOptions.IgnoreCase)]
    private static partial Regex AdoUrlRegex();

    [GeneratedRegex(@"^(?:AB)?#?(\d{1,10})$", RegexOptions.IgnoreCase)]
    private static partial Regex AdoIdRegex();
}

/// <summary>GitHub REST mapping shared by the HTTP adapter and the webhook receiver (issues, labels, milestones, releases).</summary>
public static partial class GitHubMapping
{
    public static TrackerWorkItem FromIssue(JsonElement issue, TrackerSettings s, string? repoFullName)
    {
        var number = issue.Str("number") ?? throw new TrackerException("GitHub returned an issue without a number.");
        var labels = issue.Prop("labels").Items().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : l.Str("name"))
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!).ToList();
        var state = issue.Str("state") ?? "open";
        if (state == "closed" && issue.Str("state_reason") == "not_planned") state = "closed (not planned)";
        var typeLabel = labels.FirstOrDefault(l => s.TypeMap.Values.Contains(l, StringComparer.OrdinalIgnoreCase));
        string? Prefixed(string? prefix) => string.IsNullOrWhiteSpace(prefix) ? null
            : labels.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } hit ? hit[prefix.Length..].Trim() : null;
        var assignee = issue.Prop("assignee")?.Str("login") ?? issue.Prop("assignees").Items().Select(a => a.Str("login")).FirstOrDefault(a => a is not null);
        var artifacts = new List<TrackerArtifact>();
        if (issue.Prop("pull_request") is { } pr)
            artifacts.Add(new TrackerArtifact("PullRequest", number, issue.Str("title"), issue.Str("html_url"), pr.Str("merged_at") is null ? state : "merged"));
        return new TrackerWorkItem(
            number, issue.Str("title") ?? $"Issue #{number}", issue.Str("html_url"), typeLabel ?? "issue", s.ItemTypeFor(typeLabel), state, assignee, null,
            null, issue.Prop("milestone")?.Str("title"), labels, Prefixed(s.Fields.SeverityLabelPrefix), Prefixed(s.Fields.PriorityLabelPrefix),
            repoFullName, issue.Date("updated_at"), artifacts);
    }

    /// <summary>Body of POST /repos/{owner}/{repo}/issues. GitHub renders the sanitised HTML subset used for repro steps.</summary>
    public static Dictionary<string, object?> CreateIssue(WorkItemDraft d, TrackerSettings s)
    {
        var labels = new List<string> { d.TrackerType };
        labels.AddRange(d.Tags);
        if (d.Severity is { } sev && !string.IsNullOrWhiteSpace(s.Fields.SeverityLabelPrefix)) labels.Add($"{s.Fields.SeverityLabelPrefix}{sev.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(s.Fields.PriorityLabelPrefix)) labels.Add($"{s.Fields.PriorityLabelPrefix}{d.Priority.ToString().ToLowerInvariant()}");
        var body = d.DescriptionHtml ?? "";
        if (d.CustomerImpactCount > 0) body += $"\n\n**Customer impact:** {d.CustomerImpactCount} customer(s)";
        if (d.SourceUrl is not null) body += $"\n\n_Escalated from SupportDesk ticket [{d.SourceReference}]({d.SourceUrl})._";
        return new Dictionary<string, object?>
        {
            ["title"] = d.Title.Length > 255 ? d.Title[..255] : d.Title,
            ["body"] = body,
            ["labels"] = labels.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    public static string? ParseReference(string reference, string? owner, string? repo)
    {
        var r = reference.Trim();
        var m = GhUrlRegex().Match(r);
        if (m.Success)
        {
            // Only issues of the configured repository can be linked through this tracker.
            if (owner is not null && repo is not null &&
                !(string.Equals(m.Groups[1].Value, owner, StringComparison.OrdinalIgnoreCase) && string.Equals(m.Groups[2].Value, repo, StringComparison.OrdinalIgnoreCase)))
                return null;
            return m.Groups[3].Value;
        }
        m = GhShortRegex().Match(r);
        if (!m.Success) return null;
        if (m.Groups[1].Success && owner is not null && repo is not null &&
            !string.Equals(m.Groups[1].Value, $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase)) return null;
        return m.Groups[2].Value;
    }

    /// <summary>Issue numbers referenced by closing keywords or plain #refs in a PR body ("Fixes #12").</summary>
    public static IReadOnlyList<string> ReferencedIssues(string? text) =>
        string.IsNullOrEmpty(text) ? [] : IssueRefRegex().Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();

    [GeneratedRegex(@"github[^/]*/([^/\s]+)/([^/\s]+)/(?:issues|pull)/(\d{1,9})", RegexOptions.IgnoreCase)]
    private static partial Regex GhUrlRegex();

    [GeneratedRegex(@"^(?:([\w.-]+/[\w.-]+))?#?(\d{1,9})$")]
    private static partial Regex GhShortRegex();

    [GeneratedRegex(@"(?<![\w/])#(\d{1,9})\b")]
    private static partial Regex IssueRefRegex();
}
