using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportDesk.Application.Integrations.Engineering;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Domain.Tickets;

namespace SupportDesk.Integrations.Engineering;

/// <summary>Caches GitHub App installation tokens (1 hour lifetime) per tracker/installation.</summary>
public sealed class GitHubAppTokenCache
{
    private readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresAt)> _tokens = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> GetAsync(string key, Func<Task<(string Token, DateTime ExpiresAt)>> issue, DateTime now)
    {
        if (_tokens.TryGetValue(key, out var t) && t.ExpiresAt > now.AddMinutes(5)) return t.Token;
        await _gate.WaitAsync();
        try
        {
            if (_tokens.TryGetValue(key, out t) && t.ExpiresAt > now.AddMinutes(5)) return t.Token;
            var fresh = await issue();
            _tokens[key] = fresh;
            return fresh.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(string key) => _tokens.TryRemove(key, out _);
}

/// <summary>
/// GitHub (github.com and GitHub Enterprise Server REST v3): issues with labels (type, severity, priority) and
/// milestones (fix version), cross-referenced pull requests from the issue timeline, releases (a published release whose
/// tag or name equals the milestone marks the fix as released). Authentication: a personal/fine-grained token or a
/// GitHub App installation token (RS256 JWT exchanged for an installation token, cached). Deleted issues return 410,
/// transferred issues 301 (redirects are not followed).
/// </summary>
public sealed class GitHubTracker : IEngineeringTracker
{
    private readonly TrackerConnection _c;
    private readonly TrackerHttp _http;
    private readonly GitHubAppTokenCache _tokens;
    private readonly Func<DateTime> _now;
    private readonly string _api;

    public GitHubTracker(HttpClient http, TrackerConnection connection, IEngineeringCallLog log, GitHubAppTokenCache tokens, Func<DateTime>? now = null)
    {
        _c = connection;
        _http = new TrackerHttp(http, connection, log, "GitHub");
        _tokens = tokens;
        _now = now ?? (() => DateTime.UtcNow);
        _api = (connection.BaseUrl ?? "https://api.github.com").TrimEnd('/');
    }

    public EngineeringProvider Provider => EngineeringProvider.GitHub;

    private string Repo => $"{Uri.EscapeDataString(_c.Settings.Owner ?? throw new TrackerException("No GitHub owner is configured."))}/" +
                           $"{Uri.EscapeDataString(_c.Settings.Repository ?? throw new TrackerException("No GitHub repository is configured."))}";

    private bool IsApp => string.Equals(_c.Settings.AuthType, "App", StringComparison.OrdinalIgnoreCase);

    private async Task<Action<HttpRequestMessage>> AuthAsync(CancellationToken ct)
    {
        var token = IsApp ? await InstallationTokenAsync(ct) : _c.Secrets.Token ?? throw new TrackerException("No GitHub token is configured.");
        return m =>
        {
            m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Defaults(m);
        };
    }

    private static void Defaults(HttpRequestMessage m)
    {
        m.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        m.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        m.Headers.UserAgent.ParseAdd("SupportDesk-Engineering/1.0");
    }

    private Task<string> InstallationTokenAsync(CancellationToken ct)
    {
        var appId = _c.Settings.AppId ?? throw new TrackerException("No GitHub App id is configured.");
        var installation = _c.Settings.InstallationId ?? throw new TrackerException("No GitHub App installation id is configured.");
        var key = _c.Secrets.AppPrivateKey ?? throw new TrackerException("No GitHub App private key is configured.");
        return _tokens.GetAsync($"{_c.IntegrationId:N}:{installation}", async () =>
        {
            var jwt = AppJwt(appId, key, _now());
            var r = await _http.SendAsync(HttpMethod.Post, $"{_api}/app/installations/{Uri.EscapeDataString(installation)}/access_tokens", m =>
            {
                m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                Defaults(m);
            }, "app.installation_token", ct);
            var json = r.Json();
            return (json.Str("token") ?? throw new TrackerException("GitHub did not return an installation token."), json.Date("expires_at") ?? _now().AddMinutes(50));
        }, _now());
    }

    /// <summary>RS256 JWT for GitHub App authentication (iat 60 s in the past, 9 minute lifetime).</summary>
    public static string AppJwt(string appId, string privateKeyPem, DateTime now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var iat = new DateTimeOffset(now.AddSeconds(-60)).ToUnixTimeSeconds();
        var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var payload = B64(Encoding.UTF8.GetBytes($"{{\"iat\":{iat},\"exp\":{iat + 540},\"iss\":\"{appId}\"}}"));
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(privateKeyPem); }
        catch (ArgumentException) { throw new TrackerException("The GitHub App private key is not a valid PEM RSA key."); }
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{B64(signature)}";
    }

    public async Task<TrackerTestResult> TestAsync(CancellationToken ct)
    {
        var auth = await AuthAsync(ct);
        var repo = (await _http.SendAsync(HttpMethod.Get, $"{_api}/repos/{Repo}", auth, "repo.get", ct)).Json();
        var issues = repo.Str("has_issues") != "false";
        var projects = await ProjectsAsync(ct);
        return new TrackerTestResult(issues,
            issues ? $"Connected to {repo.Str("full_name")} ({(repo.Str("private") == "true" ? "private" : "public")} repository)."
                   : $"Connected, but issues are disabled in {repo.Str("full_name")}.", projects);
    }

    public async Task<IReadOnlyList<TrackerProjectDto>> ProjectsAsync(CancellationToken ct)
    {
        var auth = await AuthAsync(ct);
        var r = await _http.SendAsync(HttpMethod.Get, IsApp ? $"{_api}/installation/repositories?per_page=100" : $"{_api}/user/repos?per_page=100&sort=updated",
            auth, "repo.list", ct);
        var json = r.Json();
        var list = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().ToList() : json.Prop("repositories").Items().ToList();
        return list.Select(x => new TrackerProjectDto(x.Str("full_name") ?? "", x.Str("full_name") ?? "", x.Str("html_url")))
            .Where(x => x.Name.Length > 0).OrderBy(x => x.Name).ToList();
    }

    public async Task<TrackerWorkItem> CreateAsync(WorkItemDraft draft, CancellationToken ct)
    {
        var auth = await AuthAsync(ct);
        var body = JsonSerializer.Serialize(GitHubMapping.CreateIssue(draft, _c.Settings));
        var r = await _http.SendAsync(HttpMethod.Post, $"{_api}/repos/{Repo}/issues", m =>
        {
            auth(m);
            m.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }, "issue.create", ct, body.Length > 1500 ? body[..1500] : body);
        return GitHubMapping.FromIssue(r.Json(), _c.Settings, $"{_c.Settings.Owner}/{_c.Settings.Repository}");
    }

    public async Task<IReadOnlyList<TrackerLookup>> GetAsync(IReadOnlyCollection<string> externalIds, CancellationToken ct)
    {
        var auth = await AuthAsync(ct);
        var repoName = $"{_c.Settings.Owner}/{_c.Settings.Repository}";
        var result = new List<TrackerLookup>();
        foreach (var id in externalIds.Distinct())
        {
            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                result.Add(TrackerLookup.Missing(id, TrackerLookupStatus.NotFound, "Not a GitHub issue number."));
                continue;
            }
            var r = await _http.SendAsync(HttpMethod.Get, $"{_api}/repos/{Repo}/issues/{id}", auth, "issue.get", ct, null,
                HttpStatusCode.NotFound, HttpStatusCode.Gone, HttpStatusCode.MovedPermanently);
            switch (r.Status)
            {
                case HttpStatusCode.NotFound:
                    result.Add(TrackerLookup.Missing(id, TrackerLookupStatus.NotFound, $"Issue #{id} was not found in {repoName}."));
                    continue;
                case HttpStatusCode.Gone:
                    result.Add(TrackerLookup.Missing(id, TrackerLookupStatus.Deleted, $"Issue #{id} was deleted."));
                    continue;
                case HttpStatusCode.MovedPermanently:
                    result.Add(TrackerLookup.Missing(id, TrackerLookupStatus.Moved, $"Issue #{id} was transferred to another repository."));
                    continue;
            }
            var item = GitHubMapping.FromIssue(r.Json(), _c.Settings, repoName);
            item = item with { Artifacts = [.. item.Artifacts, .. await LinkedPullRequestsAsync(id, auth, ct)] };
            result.Add(TrackerLookup.Found(item));
        }
        return await WithReleasesAsync(result, auth, ct);
    }

    /// <summary>Pull requests that cross-reference the issue (timeline "cross-referenced" events).</summary>
    private async Task<IReadOnlyList<TrackerArtifact>> LinkedPullRequestsAsync(string number, Action<HttpRequestMessage> auth, CancellationToken ct)
    {
        try
        {
            var r = await _http.SendAsync(HttpMethod.Get, $"{_api}/repos/{Repo}/issues/{number}/timeline?per_page=100", auth, "issue.timeline", ct);
            var json = r.Json();
            if (json.ValueKind != JsonValueKind.Array) return [];
            return json.EnumerateArray()
                .Where(e => e.Str("event") == "cross-referenced" && e.Prop("source")?.Prop("issue")?.Prop("pull_request") is not null)
                .Select(e => e.Prop("source")!.Value.Prop("issue")!.Value)
                .Select(pr => new TrackerArtifact("PullRequest", pr.Str("number") ?? "", pr.Str("title"), pr.Str("html_url"),
                    pr.Prop("pull_request")?.Str("merged_at") is not null ? "merged" : pr.Str("state")))
                .GroupBy(a => a.Id).Select(g => g.First()).Take(20).ToList();
        }
        catch (TrackerException)
        {
            return []; // the timeline is optional detail
        }
    }

    /// <summary>Marks items released when a published (non-draft) release's tag or name equals their milestone.</summary>
    private async Task<IReadOnlyList<TrackerLookup>> WithReleasesAsync(List<TrackerLookup> lookups, Action<HttpRequestMessage> auth, CancellationToken ct)
    {
        if (!lookups.Any(l => l.Item?.FixVersion is not null)) return lookups;
        var r = await _http.SendAsync(HttpMethod.Get, $"{_api}/repos/{Repo}/releases?per_page=50", auth, "release.list", ct);
        var json = r.Json();
        var releases = json.ValueKind == JsonValueKind.Array
            ? json.EnumerateArray().Where(x => x.Str("draft") != "true" && x.Str("published_at") is not null).ToList()
            : [];
        return lookups.Select(l =>
        {
            if (l.Item?.FixVersion is not { } version) return l;
            var rel = releases.FirstOrDefault(x => StateMatcher.SameVersion(x.Str("tag_name"), version) || StateMatcher.SameVersion(x.Str("name"), version));
            if (rel.ValueKind != JsonValueKind.Object) return l;
            var artifact = new TrackerArtifact("Release", rel.Str("tag_name") ?? version, rel.Str("name"), rel.Str("html_url"), "published");
            return l with
            {
                Item = l.Item with
                {
                    Released = true, ReleaseName = rel.Str("tag_name") ?? version, ReleasedAt = rel.Date("published_at"), Artifacts = [.. l.Item.Artifacts, artifact]
                }
            };
        }).ToList();
    }

    public string? ParseReference(string reference) => GitHubMapping.ParseReference(reference, _c.Settings.Owner, _c.Settings.Repository);

    public string DisplayId(string externalId) => "#" + externalId;
}
