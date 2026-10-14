using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportDesk.Application.Integrations.Collaboration;
using SupportDesk.Application.Integrations.Engineering;
using SupportDesk.Domain;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Tickets;
using SupportDesk.Integrations.Engineering;

namespace SupportDesk.UnitTests.Engineering;

internal sealed class NullCallLog : IEngineeringCallLog
{
    public List<string> Operations { get; } = [];

    public Task RecordAsync(Guid tenantId, Guid integrationId, string system, string operation, string? url, int? status, bool success, int durationMs,
        string? request, string? response, CancellationToken ct = default)
    {
        Operations.Add($"{operation}:{status}:{success}");
        return Task.CompletedTask;
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        return respond(request);
    }

    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}

public sealed class TrackerAdapterTests
{
    private static TrackerConnection Ado(string authType = "Pat") => new(Guid.NewGuid(), Guid.NewGuid(), EngineeringProvider.AzureDevOps, "ADO",
        "https://tfs.example.test/tfs/DefaultCollection", TrackerSettings.Defaults(EngineeringProvider.AzureDevOps) with { Project = "ERP", AuthType = authType },
        new TrackerSecrets("my-pat", null, "hook-secret"));

    private static TrackerConnection GitHub() => new(Guid.NewGuid(), Guid.NewGuid(), EngineeringProvider.GitHub, "GitHub", "https://api.github.com",
        TrackerSettings.Defaults(EngineeringProvider.GitHub) with { Owner = "acme", Repository = "erp" }, new TrackerSecrets("ghp_x", null, "s"));

    private static WorkItemDraft Draft(EngineeringItemType type = EngineeringItemType.Bug) => new(type, type == EngineeringItemType.Bug ? "Bug" : "User Story",
        "Export times out", "<p>Steps</p>", TicketSeverity.Critical, TicketPriority.High, "ERP\\Sales", null, ["SupportDesk", "export"], null, 3, null, "#1042",
        "https://desk.example.test/tickets/1");

    [Fact]
    public async Task Azure_DevOps_creates_work_items_with_a_json_patch_document_and_PAT_auth()
    {
        var stub = new StubHandler(_ => StubHandler.Json(new
        {
            id = 77, fields = new Dictionary<string, object> { ["System.Title"] = "Export times out", ["System.State"] = "New", ["System.WorkItemType"] = "Bug", ["System.TeamProject"] = "ERP" },
            _links = new { html = new { href = "https://tfs.example.test/tfs/DefaultCollection/ERP/_workitems/edit/77" } }
        }));
        var log = new NullCallLog();
        var tracker = new AzureDevOpsTracker(new HttpClient(stub), Ado(), log);

        var created = await tracker.CreateAsync(Draft(), default);

        Assert.Equal("77", created.ExternalId);
        Assert.Equal("New", created.State);
        Assert.Equal(EngineeringItemType.Bug, created.ItemType);
        var (request, body) = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://tfs.example.test/tfs/DefaultCollection/ERP/_apis/wit/workitems/$Bug?api-version=7.1", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(":my-pat")), request.Headers.Authorization.Parameter);
        Assert.Equal("application/json-patch+json", request.Content!.Headers.ContentType!.MediaType);
        using var doc = JsonDocument.Parse(body);
        var ops = doc.RootElement.EnumerateArray().ToDictionary(o => o.GetProperty("path").GetString()!, o => o.GetProperty("value"));
        Assert.Equal("Export times out", ops["/fields/System.Title"].GetString());
        Assert.Equal("<p>Steps</p>", ops["/fields/Microsoft.VSTS.TCM.ReproSteps"].GetString());
        Assert.Equal("1 - Critical", ops["/fields/Microsoft.VSTS.Common.Severity"].GetString());
        Assert.Equal(2, ops["/fields/Microsoft.VSTS.Common.Priority"].GetInt32());
        Assert.Equal("SupportDesk; export", ops["/fields/System.Tags"].GetString());
        Assert.Equal("ERP\\Sales", ops["/fields/System.AreaPath"].GetString());
        Assert.Equal("Hyperlink", ops["/relations/-"].GetProperty("rel").GetString());
        Assert.Contains("workitem.create:200:True", log.Operations);
    }

    [Fact]
    public async Task Azure_DevOps_features_use_the_description_field_and_OAuth_uses_bearer()
    {
        var stub = new StubHandler(_ => StubHandler.Json(new { id = 5, fields = new Dictionary<string, object> { ["System.Title"] = "x", ["System.WorkItemType"] = "User Story" } }));
        var tracker = new AzureDevOpsTracker(new HttpClient(stub), Ado("OAuth"), new NullCallLog());
        var created = await tracker.CreateAsync(Draft(EngineeringItemType.Feature), default);
        Assert.Equal(EngineeringItemType.Feature, created.ItemType);
        var (request, body) = Assert.Single(stub.Calls);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.EndsWith("/_apis/wit/workitems/$User%20Story?api-version=7.1", request.RequestUri!.AbsoluteUri);
        Assert.Contains("/fields/System.Description", body);
        Assert.DoesNotContain("Severity", body);
    }

    [Fact]
    public async Task Azure_DevOps_reports_missing_and_moved_items_without_throwing()
    {
        var stub = new StubHandler(_ => StubHandler.Json(new
        {
            count = 3,
            value = new object?[]
            {
                new { id = 1, fields = new Dictionary<string, object> { ["System.Title"] = "Stays", ["System.State"] = "Active", ["System.TeamProject"] = "ERP" } },
                null,
                new { id = 3, fields = new Dictionary<string, object> { ["System.Title"] = "Moved", ["System.State"] = "Active", ["System.TeamProject"] = "POS" } }
            }
        }));
        var tracker = new AzureDevOpsTracker(new HttpClient(stub), Ado(), new NullCallLog());
        var result = await tracker.GetAsync(["1", "2", "3"], default);
        Assert.Equal(TrackerLookupStatus.Found, result.Single(r => r.ExternalId == "1").Status);
        Assert.Equal(TrackerLookupStatus.NotFound, result.Single(r => r.ExternalId == "2").Status);
        Assert.Equal(TrackerLookupStatus.Moved, result.Single(r => r.ExternalId == "3").Status);
        Assert.Contains("errorPolicy=omit", Assert.Single(stub.Calls).Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Azure_DevOps_maps_credential_failures_to_a_clear_error()
    {
        var tracker = new AzureDevOpsTracker(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))), Ado(), new NullCallLog());
        var ex = await Assert.ThrowsAsync<TrackerException>(() => tracker.TestAsync(default));
        Assert.Contains("credentials", ex.Message);
        Assert.False(ex.Transient);
        var down = new AzureDevOpsTracker(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))), Ado(), new NullCallLog());
        Assert.True((await Assert.ThrowsAsync<TrackerException>(() => down.GetAsync(["1"], default))).Transient);
    }

    [Fact]
    public async Task GitHub_creates_labelled_issues_and_detects_deleted_transferred_and_released_issues()
    {
        var stub = new StubHandler(r =>
        {
            var url = r.RequestUri!.ToString();
            if (r.Method == HttpMethod.Post)
                return StubHandler.Json(new { number = 45, title = "Export times out", state = "open", html_url = "https://github.com/acme/erp/issues/45", labels = new[] { new { name = "bug" } } },
                    HttpStatusCode.Created);
            if (url.EndsWith("/issues/45")) return StubHandler.Json(new
            {
                number = 45, title = "Export times out", state = "closed", state_reason = "completed", html_url = "https://github.com/acme/erp/issues/45",
                labels = new[] { new { name = "bug" }, new { name = "severity:critical" } }, milestone = new { title = "v8.0.1" }, assignee = new { login = "devk" }
            });
            if (url.Contains("/issues/45/timeline")) return StubHandler.Json(new object[]
            {
                new { @event = "cross-referenced", source = new { issue = new { number = 51, title = "Page the export", html_url = "https://github.com/acme/erp/pull/51", state = "closed",
                    pull_request = new { merged_at = "2026-10-01T00:00:00Z" } } } }
            });
            if (url.EndsWith("/issues/46")) return new HttpResponseMessage(HttpStatusCode.Gone);
            if (url.EndsWith("/issues/47")) return new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            if (url.EndsWith("/issues/48")) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (url.Contains("/releases")) return StubHandler.Json(new object[]
            {
                new { tag_name = "v8.0.1", name = "8.0.1", draft = false, published_at = "2026-10-10T10:00:00Z", html_url = "https://github.com/acme/erp/releases/v8.0.1" }
            });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var tracker = new GitHubTracker(new HttpClient(stub), GitHub(), new NullCallLog(), new GitHubAppTokenCache());

        var created = await tracker.CreateAsync(Draft() with { TrackerType = "bug" }, default);
        Assert.Equal("45", created.ExternalId);
        using (var body = JsonDocument.Parse(stub.Calls[0].Body))
        {
            var labels = body.RootElement.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ToList();
            Assert.Contains("bug", labels);
            Assert.Contains("severity:critical", labels);
            Assert.Contains("priority:high", labels);
            Assert.Contains("#1042", body.RootElement.GetProperty("body").GetString());
        }
        Assert.Equal("Bearer", stub.Calls[0].Request.Headers.Authorization!.Scheme);
        Assert.Equal("2022-11-28", stub.Calls[0].Request.Headers.GetValues("X-GitHub-Api-Version").Single());

        var lookups = await tracker.GetAsync(["45", "46", "47", "48"], default);
        var found = lookups.Single(l => l.ExternalId == "45");
        Assert.Equal(TrackerLookupStatus.Found, found.Status);
        Assert.Equal("closed", found.Item!.State);
        Assert.Equal("critical", found.Item.Severity);
        Assert.Equal("devk", found.Item.AssignedTo);
        Assert.True(found.Item.Released);
        Assert.Equal("v8.0.1", found.Item.ReleaseName);
        Assert.Contains(found.Item.Artifacts, a => a.Kind == "PullRequest" && a.Id == "51" && a.State == "merged");
        Assert.Equal(TrackerLookupStatus.Deleted, lookups.Single(l => l.ExternalId == "46").Status);
        Assert.Equal(TrackerLookupStatus.Moved, lookups.Single(l => l.ExternalId == "47").Status);
        Assert.Equal(TrackerLookupStatus.NotFound, lookups.Single(l => l.ExternalId == "48").Status);
    }

    [Fact]
    public void GitHub_app_jwt_is_RS256_signed_with_the_app_id_as_issuer()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var now = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
        var jwt = GitHubTracker.AppJwt("123456", pem, now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] B64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        using var payload = JsonDocument.Parse(B64(parts[1]));
        Assert.Equal("123456", payload.RootElement.GetProperty("iss").GetString());
        Assert.True(payload.RootElement.GetProperty("exp").GetInt64() - payload.RootElement.GetProperty("iat").GetInt64() <= 600);
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Theory]
    [InlineData("123", "123")]
    [InlineData("#123", "123")]
    [InlineData("AB#123", "123")]
    [InlineData("https://dev.azure.com/acme/ERP/_workitems/edit/987", "987")]
    [InlineData("https://tfs.local/tfs/DefaultCollection/ERP/_workitems/edit/55?fullScreen=true", "55")]
    [InlineData("not an id", null)]
    public void Azure_DevOps_references_are_parsed(string input, string? expected) => Assert.Equal(expected, AzureDevOpsMapping.ParseReference(input));

    [Theory]
    [InlineData("45", "45")]
    [InlineData("#45", "45")]
    [InlineData("acme/erp#45", "45")]
    [InlineData("https://github.com/acme/erp/issues/45", "45")]
    [InlineData("https://github.com/other/repo/issues/45", null)]
    [InlineData("other/repo#45", null)]
    public void GitHub_references_must_belong_to_the_configured_repository(string input, string? expected) =>
        Assert.Equal(expected, GitHubMapping.ParseReference(input, "acme", "erp"));

    [Theory]
    [InlineData("ENG-42", "42")]
    [InlineData("42", "42")]
    [InlineData("/engineering?item=42", "42")]
    [InlineData("ENG-", null)]
    public void Local_references_are_parsed(string input, string? expected) => Assert.Equal(expected, LocalTracker.ParseLocalReference(input));

    [Fact]
    public void State_matching_supports_states_labels_and_versions()
    {
        Assert.True(StateMatcher.Matches(["Resolved", "Done"], "resolved", []));
        Assert.False(StateMatcher.Matches(["Resolved"], "Active", ["resolved"]));
        Assert.True(StateMatcher.Matches(["label:released"], "closed", ["bug", "Released"]));
        Assert.True(StateMatcher.Matches(["tag:shipped"], null, ["shipped"]));
        Assert.True(StateMatcher.SameVersion("v8.0.1", "8.0.1"));
        Assert.False(StateMatcher.SameVersion("8.0.1", "8.0.10"));
        Assert.Equal(new[] { "12", "7" }, GitHubMapping.ReferencedIssues("Fixes #12, see #7 and acme/erp#9 (not ours)"));
    }

    [Fact]
    public void Webhook_signatures_are_verified()
    {
        var body = Encoding.UTF8.GetBytes("{\"action\":\"closed\"}");
        var sig = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), body)).ToLowerInvariant();
        Assert.True(EngineeringWebhookService.VerifyGitHub("s3cret", new EngineeringWebhookRequest(new Dictionary<string, string> { ["X-Hub-Signature-256"] = sig }, body)));
        Assert.False(EngineeringWebhookService.VerifyGitHub("other", new EngineeringWebhookRequest(new Dictionary<string, string> { ["X-Hub-Signature-256"] = sig }, body)));
        Assert.False(EngineeringWebhookService.VerifyGitHub(null, new EngineeringWebhookRequest(new Dictionary<string, string> { ["X-Hub-Signature-256"] = sig }, body)));
        Assert.False(EngineeringWebhookService.VerifyGitHub("s3cret", new EngineeringWebhookRequest(new Dictionary<string, string>(), body)));

        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ado:s3cret"));
        Assert.True(EngineeringWebhookService.VerifyAzureDevOps("s3cret", new EngineeringWebhookRequest(new Dictionary<string, string> { ["Authorization"] = basic }, body)));
        Assert.True(EngineeringWebhookService.VerifyAzureDevOps("s3cret", new EngineeringWebhookRequest(new Dictionary<string, string> { ["X-SupportDesk-Secret"] = "s3cret" }, body)));
        Assert.False(EngineeringWebhookService.VerifyAzureDevOps("s3cret", new EngineeringWebhookRequest(new Dictionary<string, string> { ["X-SupportDesk-Secret"] = "nope" }, body)));
        Assert.False(EngineeringWebhookService.VerifyAzureDevOps("s3cret", new EngineeringWebhookRequest(new Dictionary<string, string>(), body)));
    }
}

public sealed class CollaborationUnitTests
{
    [Theory]
    [InlineData("https://127.0.0.1/hook", false, false)]
    [InlineData("https://127.0.0.1/hook", true, true)]
    [InlineData("https://10.1.2.3/hook", false, false)]
    [InlineData("https://192.168.1.10/hook", false, false)]
    [InlineData("https://localhost/hook", false, false)]
    [InlineData("https://169.254.169.254/latest", true, false)]
    [InlineData("https://52.10.20.30/services/T1/B2/x", false, true)]
    [InlineData("http://52.10.20.30/hook", false, false)]
    [InlineData("http://127.0.0.1:5000/hook", true, true)]
    [InlineData("https://user:pw@52.10.20.30/hook", false, false)]
    [InlineData("not a url", true, false)]
    public void Webhook_urls_to_private_networks_are_blocked_unless_allowed(string url, bool allowPrivate, bool allowed) =>
        Assert.Equal(allowed, CollaborationUrlPolicy.Validate(url, allowPrivate) is null);

    [Fact]
    public async Task Host_names_resolving_to_private_addresses_are_blocked()
    {
        Task<IPAddress[]> Resolve(string host, CancellationToken ct) => Task.FromResult(host == "intranet.example" ? new[] { IPAddress.Parse("10.0.0.8") } : [IPAddress.Parse("52.1.1.1")]);
        Assert.NotNull(await CollaborationUrlPolicy.CheckAsync("https://intranet.example/hook", false, Resolve));
        Assert.Null(await CollaborationUrlPolicy.CheckAsync("https://intranet.example/hook", true, Resolve));
        Assert.Null(await CollaborationUrlPolicy.CheckAsync("https://hooks.slack.example/services/x", false, Resolve));
    }

    [Fact]
    public void Teams_and_Slack_payloads_carry_the_deep_link()
    {
        var message = new CollaborationMessage("SLA breached on #1042", "Billing <down> & out", [new CollaborationFact("Customer", "Coastal Care")], "https://desk.example/tickets/abc", "critical");
        using var teams = JsonDocument.Parse(CollaborationPayloads.Build(CollaborationProvider.Teams, message));
        var card = teams.RootElement.GetProperty("attachments")[0].GetProperty("content");
        Assert.Equal("1.4", card.GetProperty("version").GetString());
        Assert.Equal("Attention", card.GetProperty("body")[0].GetProperty("color").GetString());
        Assert.Equal("FactSet", card.GetProperty("body")[2].GetProperty("type").GetString());
        Assert.Equal("https://desk.example/tickets/abc", card.GetProperty("actions")[0].GetProperty("url").GetString());

        using var slack = JsonDocument.Parse(CollaborationPayloads.Build(CollaborationProvider.Slack, message));
        var blocks = slack.RootElement.GetProperty("blocks");
        Assert.Equal("Billing &lt;down&gt; &amp; out", blocks[1].GetProperty("text").GetProperty("text").GetString());
        Assert.Equal("https://desk.example/tickets/abc", blocks[3].GetProperty("elements")[0].GetProperty("url").GetString());
        Assert.Equal("danger", blocks[3].GetProperty("elements")[0].GetProperty("style").GetString());
        Assert.Equal("hooks.slack.com/…xYz9", CollaborationUrlPolicy.Hint("https://hooks.slack.com/services/T000/B000/abcxYz9"));
    }
}
