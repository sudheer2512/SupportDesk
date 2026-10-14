using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SupportDesk.Application.Integrations.Engineering;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Domain.Tickets;

namespace SupportDesk.Integrations.Engineering;

/// <summary>
/// Azure DevOps Services and Azure DevOps Server / TFS (REST 7.x; collection URL as base): PAT (Basic) or OAuth
/// (Bearer) authentication, work item creation with type mapping (JSON patch: title, repro steps HTML, severity,
/// priority, area/iteration, tags, hyperlink back to the ticket), batch reads with relations (changesets, PRs,
/// builds) and project listing. Deleted items come back as missing; items moved to another project as moved.
/// </summary>
public sealed class AzureDevOpsTracker : IEngineeringTracker
{
    private readonly TrackerConnection _c;
    private readonly TrackerHttp _http;
    private readonly string _base;
    private readonly string _api;

    public AzureDevOpsTracker(HttpClient http, TrackerConnection connection, IEngineeringCallLog log)
    {
        _c = connection;
        _http = new TrackerHttp(http, connection, log, "Azure DevOps");
        _base = (connection.BaseUrl ?? throw new TrackerException("No Azure DevOps collection URL is configured.")).TrimEnd('/');
        _api = string.IsNullOrWhiteSpace(connection.Settings.ApiVersion) ? "7.1" : connection.Settings.ApiVersion;
    }

    public EngineeringProvider Provider => EngineeringProvider.AzureDevOps;

    private string Project => _c.Settings.Project ?? throw new TrackerException("No Azure DevOps project is configured.");

    private void Auth(HttpRequestMessage m)
    {
        var token = _c.Secrets.Token ?? throw new TrackerException("No Azure DevOps token is configured.");
        m.Headers.Authorization = string.Equals(_c.Settings.AuthType, "OAuth", StringComparison.OrdinalIgnoreCase)
            ? new AuthenticationHeaderValue("Bearer", token)
            : new AuthenticationHeaderValue("Basic", TrackerHttp.Basic("", token));
        m.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<TrackerTestResult> TestAsync(CancellationToken ct)
    {
        var projects = await ProjectsAsync(ct);
        var configured = _c.Settings.Project;
        var found = configured is null || projects.Any(p => string.Equals(p.Name, configured, StringComparison.OrdinalIgnoreCase));
        return new TrackerTestResult(found,
            found ? $"Connected to Azure DevOps ({projects.Count} project{(projects.Count == 1 ? "" : "s")} visible)."
                  : $"Connected, but the project '{configured}' is not visible to this token.", projects);
    }

    public async Task<IReadOnlyList<TrackerProjectDto>> ProjectsAsync(CancellationToken ct)
    {
        var r = await _http.SendAsync(HttpMethod.Get, $"{_base}/_apis/projects?$top=500&api-version={_api}", Auth, "projects.list", ct);
        return r.Json().Prop("value").Items()
            .Select(p => new TrackerProjectDto(p.Str("id") ?? "", p.Str("name") ?? "", $"{_base}/{Uri.EscapeDataString(p.Str("name") ?? "")}"))
            .Where(p => p.Name.Length > 0).OrderBy(p => p.Name).ToList();
    }

    public async Task<TrackerWorkItem> CreateAsync(WorkItemDraft draft, CancellationToken ct)
    {
        var patch = JsonSerializer.Serialize(AzureDevOpsMapping.CreatePatch(draft, _c.Settings));
        var url = $"{_base}/{Uri.EscapeDataString(Project)}/_apis/wit/workitems/${Uri.EscapeDataString(draft.TrackerType)}?api-version={_api}";
        var r = await _http.SendAsync(HttpMethod.Post, url, m =>
        {
            Auth(m);
            m.Content = new StringContent(patch, Encoding.UTF8, "application/json-patch+json");
        }, "workitem.create", ct, Truncate(patch, 1500));
        return AzureDevOpsMapping.FromWorkItem(r.Json(), _c.Settings, _base);
    }

    public async Task<IReadOnlyList<TrackerLookup>> GetAsync(IReadOnlyCollection<string> externalIds, CancellationToken ct)
    {
        var ids = externalIds.Where(id => int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _)).Distinct().ToList();
        var result = new List<TrackerLookup>();
        foreach (var chunk in ids.Chunk(200))
        {
            var url = $"{_base}/_apis/wit/workitems?ids={string.Join(",", chunk)}&$expand=relations&errorPolicy=omit&api-version={_api}";
            var r = await _http.SendAsync(HttpMethod.Get, url, Auth, "workitem.get", ct);
            var found = r.Json().Prop("value").Items().Where(v => v.ValueKind == JsonValueKind.Object).ToList();
            foreach (var id in chunk)
            {
                var wi = found.FirstOrDefault(v => v.Str("id") == id);
                if (wi.ValueKind != JsonValueKind.Object)
                {
                    result.Add(TrackerLookup.Missing(id, TrackerLookupStatus.NotFound, $"Work item {id} does not exist or was deleted."));
                    continue;
                }
                var item = AzureDevOpsMapping.FromWorkItem(wi, _c.Settings, _base);
                if (_c.Settings.Project is { } project && item.Project is { } actual && !string.Equals(actual, project, StringComparison.OrdinalIgnoreCase))
                    result.Add(new TrackerLookup(id, TrackerLookupStatus.Moved, item, $"Work item {id} moved to project {actual}."));
                else result.Add(TrackerLookup.Found(item));
            }
        }
        foreach (var bad in externalIds.Except(ids)) result.Add(TrackerLookup.Missing(bad, TrackerLookupStatus.NotFound, "Not an Azure DevOps work item id."));
        return result;
    }

    public string? ParseReference(string reference) => AzureDevOpsMapping.ParseReference(reference);

    public string DisplayId(string externalId) => "#" + externalId;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
