using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Contracts.Portal;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Customers;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.IntegrationTests.Portal;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Knowledge;

[Collection("api")]
public sealed class KnowledgeTests(DeskApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions J = PortalTestKit.J;

    private static SaveArticleRequest Draft(string title, string body = "<p>Step one. Step two.</p>", string visibility = "Public") =>
        new() { Title = title, BodyHtml = body, ArticleType = "HowTo", Visibility = visibility };

    private static async Task<T> Ok<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>(J))!;
    }

    [Fact]
    public async Task Workflow_transitions_are_permission_checked_and_versions_are_immutable()
    {
        var t = await factory.CreateTenantAsync();
        var (_, authorEmail) = await t.CreateAgentAsync("Author", "Agent");
        var (_, managerEmail) = await t.CreateAgentAsync("Manager", "Knowledge Manager");
        var (_, lightEmail) = await t.CreateAgentAsync("Light", "Light Agent");
        var author = await t.AgentClientAsync(authorEmail);
        var manager = await t.AgentClientAsync(managerEmail);
        var light = await t.AgentClientAsync(lightEmail);

        Assert.Equal(HttpStatusCode.Forbidden, (await light.PostAsJsonAsync("/api/v1/kb/articles", Draft("Nope"), J)).StatusCode);
        var v1 = await Ok<ArticleDetailDto>(await author.PostAsJsonAsync("/api/v1/kb/articles", Draft("Configure printers", "<p>Original body</p>"), J));
        Assert.Equal("Draft", v1.Status);
        Assert.Equal(1, v1.CurrentVersionNumber);

        // Authors cannot approve or publish.
        Assert.Equal(HttpStatusCode.Forbidden, (await author.PostAsJsonAsync($"/api/v1/kb/articles/{v1.Id}/transition", new ArticleTransitionRequest("publish"), J)).StatusCode);
        var submitted = await Ok<ArticleDetailDto>(await author.PostAsJsonAsync($"/api/v1/kb/articles/{v1.Id}/transition", new ArticleTransitionRequest("submit"), J));
        Assert.Equal("InReview", submitted.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await author.PostAsJsonAsync($"/api/v1/kb/articles/{v1.Id}/transition", new ArticleTransitionRequest("approve"), J)).StatusCode);
        // Rejecting needs a note; then back to Draft with the reviewer note.
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync($"/api/v1/kb/articles/{v1.Id}/transition", new ArticleTransitionRequest("reject"), J)).StatusCode);
        var rejected = await Ok<ArticleDetailDto>(await manager.PostAsJsonAsync($"/api/v1/kb/articles/{v1.Id}/transition", new ArticleTransitionRequest("reject", "Add a screenshot"), J));
        Assert.Equal("Draft", rejected.Status);
        Assert.Equal("Add a screenshot", rejected.LastReviewNote);
        await Ok<ArticleDetailDto>(await author.PostAsJsonAsync($"/api/v1/kb/articles/{v1.Id}/transition", new ArticleTransitionRequest("submit"), J));
        var approved = await Ok<ArticleDetailDto>(await manager.PostAsJsonAsync($"/api/v1/kb/articles/{v1.Id}/transition", new ArticleTransitionRequest("approve", "Looks good"), J));
        Assert.Equal("Approved", approved.Status);
        var published = await Ok<ArticleDetailDto>(await manager.PostAsJsonAsync($"/api/v1/kb/articles/{v1.Id}/transition", new ArticleTransitionRequest("publish"), J));
        Assert.True(published.IsLive);
        Assert.Equal(1, published.PublishedVersionNumber);

        // Editing creates version 2 (the live version stays 1); stale edit tokens are rejected.
        var edit = Draft("Configure printers", "<p>Changed body</p>") with { RowVersion = published.RowVersion };
        var v2 = await Ok<ArticleDetailDto>(await author.PutAsJsonAsync($"/api/v1/kb/articles/{v1.Id}", edit, J));
        Assert.Equal(2, v2.CurrentVersionNumber);
        Assert.Equal(1, v2.PublishedVersionNumber);
        Assert.True(v2.HasPendingChanges);
        Assert.Equal(HttpStatusCode.Conflict, (await author.PutAsJsonAsync($"/api/v1/kb/articles/{v1.Id}", edit with { BodyHtml = "<p>Third</p>" }, J)).StatusCode);

        var original = await author.GetFromJsonAsync<ArticleVersionContentDto>($"/api/v1/kb/articles/{v1.Id}/versions/1", J);
        Assert.Contains("Original body", original!.BodyHtml);
        var diff = await author.GetFromJsonAsync<ArticleDiffDto>($"/api/v1/kb/articles/{v1.Id}/diff?from=1&to=2", J);
        Assert.Contains(diff!.Body, l => l.Op == "delete" && l.Text.Contains("Original body"));
        Assert.Contains(diff.Body, l => l.Op == "insert" && l.Text.Contains("Changed body"));

        var restored = await Ok<ArticleDetailDto>(await author.PostAsJsonAsync($"/api/v1/kb/articles/{v1.Id}/versions/1/restore", new RestoreVersionRequest("Undo"), J));
        Assert.Equal(3, restored.CurrentVersionNumber);
        Assert.Contains("Original body", restored.BodyHtml);
        var versions = await author.GetFromJsonAsync<List<ArticleVersionDto>>($"/api/v1/kb/articles/{v1.Id}/versions", J);
        Assert.Equal([3, 2, 1], versions!.Select(v => v.VersionNumber));
        Assert.Contains("Changed body", (await author.GetFromJsonAsync<ArticleVersionContentDto>($"/api/v1/kb/articles/{v1.Id}/versions/2", J))!.BodyHtml);

        // Archive takes it off the portal.
        Assert.Equal(HttpStatusCode.Forbidden, (await author.PostAsJsonAsync($"/api/v1/kb/articles/{v1.Id}/transition", new ArticleTransitionRequest("archive"), J)).StatusCode);
        var archived = await Ok<ArticleDetailDto>(await manager.PostAsJsonAsync($"/api/v1/kb/articles/{v1.Id}/transition", new ArticleTransitionRequest("archive"), J));
        Assert.False(archived.IsLive);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/api/v1/portal/{t.Slug}/kb/articles/{v1.Slug}")).StatusCode);
    }

    [Fact]
    public async Task Translation_becomes_outdated_when_the_source_is_republished()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var a = await admin.PublishedArticleAsync("Reset your password", "<p>Click Forgot password.</p>");
        await Ok<ArticleTranslationDto>(await admin.PutAsJsonAsync($"/api/v1/kb/articles/{a.Id}/translations/hi",
            new SaveTranslationRequest("पासवर्ड रीसेट करें", "<p>पासवर्ड भूल गए पर क्लिक करें।</p>", null), J));
        var tr = await Ok<ArticleTranslationDto>(await admin.PostAsJsonAsync($"/api/v1/kb/articles/{a.Id}/translations/hi/transition",
            new TranslationTransitionRequest("publish"), J));
        Assert.Equal("Published", tr.Status);
        Assert.False(tr.IsOutdated);

        var portalHi = await factory.CreateClient().GetFromJsonAsync<PortalArticleDto>($"/api/v1/portal/{t.Slug}/kb/articles/{a.Slug}?locale=hi", J);
        Assert.Equal("hi", portalHi!.Locale);
        Assert.False(portalHi.TranslationOutdated);

        var current = await admin.GetFromJsonAsync<ArticleDetailDto>($"/api/v1/kb/articles/{a.Id}", J);
        await Ok<ArticleDetailDto>(await admin.PutAsJsonAsync($"/api/v1/kb/articles/{a.Id}",
            Draft("Reset your password", "<p>Click Forgot password, then enter the code.</p>") with { RowVersion = current!.RowVersion }, J));
        await Ok<ArticleDetailDto>(await admin.PostAsJsonAsync($"/api/v1/kb/articles/{a.Id}/transition", new ArticleTransitionRequest("publish"), J));

        var translations = await admin.GetFromJsonAsync<List<ArticleTranslationDto>>($"/api/v1/kb/articles/{a.Id}/translations", J);
        var hi = Assert.Single(translations!);
        Assert.Equal("Outdated", hi.Status);
        Assert.True(hi.IsOutdated);
        portalHi = await factory.CreateClient().GetFromJsonAsync<PortalArticleDto>($"/api/v1/portal/{t.Slug}/kb/articles/{a.Slug}?locale=hi", J);
        Assert.True(portalHi!.TranslationOutdated);
        // Unknown locale falls back to the primary language.
        var fr = await factory.CreateClient().GetFromJsonAsync<PortalArticleDto>($"/api/v1/portal/{t.Slug}/kb/articles/{a.Slug}?locale=fr", J);
        Assert.Equal("en", fr!.Locale);
        Assert.True(fr.TranslationFallback);
    }

    [Fact]
    public async Task Restricted_articles_are_only_visible_to_permitted_customers()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var platinum = await factory.CustomerAsync(t, "Platinum Co", "Platinum");
        var gold = await factory.CustomerAsync(t, "Gold Co", "Gold");
        var (_, vip) = await factory.ContactAsync(t, platinum.Id, platinum.HeadOffice, PortalAccessScope.Own, "Vip");
        var (_, regular) = await factory.ContactAsync(t, gold.Id, gold.HeadOffice, PortalAccessScope.Own, "Regular");
        var article = await admin.PublishedArticleAsync("Priority hotline zephyrine", "<p>Call the zephyrine hotline.</p>", "Restricted",
            [new ArticleAccessRuleInput(null, null, "Platinum", null)]);
        await factory.DrainOutboxAsync();

        var url = $"/api/v1/portal/{t.Slug}/kb/articles/{article.Slug}";
        Assert.Equal(HttpStatusCode.OK, (await vip.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await regular.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync(url)).StatusCode);

        var vipSearch = await vip.GetFromJsonAsync<PortalSearchResultDto>($"/api/v1/portal/{t.Slug}/search?q=zephyrine", J);
        Assert.Contains(vipSearch!.Articles, a => a.Id == article.Id);
        var regularSearch = await regular.GetFromJsonAsync<PortalSearchResultDto>($"/api/v1/portal/{t.Slug}/search?q=zephyrine", J);
        Assert.DoesNotContain(regularSearch!.Articles, a => a.Id == article.Id);
        var feedback = await regular.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/kb/articles/{article.Id}/feedback", new SubmitArticleFeedbackRequest(true), J);
        Assert.Equal(HttpStatusCode.NotFound, feedback.StatusCode);

        // Restricted without rules is rejected.
        var invalid = await admin.PostAsJsonAsync("/api/v1/kb/articles", Draft("No rules", visibility: "Restricted"), J);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Search_respects_visibility_and_tenant()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var adminA = await a.AdminClientAsync();
        var adminB = await b.AdminClientAsync();
        var word = "quixotry" + Guid.NewGuid().ToString("N")[..6];
        var publicA = await adminA.PublishedArticleAsync($"How to {word} invoices", $"<p>The {word} feature explained.</p>");
        var internalA = await adminA.PublishedArticleAsync($"Internal {word} playbook", $"<p>{word} steps for engineers.</p>", "Internal");
        var publicB = await adminB.PublishedArticleAsync($"Tenant B {word}", $"<p>{word} in tenant B.</p>");
        await factory.DrainOutboxAsync();

        var anonA = await factory.CreateClient().GetFromJsonAsync<PortalSearchResultDto>($"/api/v1/portal/{a.Slug}/search?q={word}", J);
        Assert.Contains(anonA!.Articles, x => x.Id == publicA.Id);
        Assert.DoesNotContain(anonA.Articles, x => x.Id == internalA.Id);
        Assert.DoesNotContain(anonA.Articles, x => x.Id == publicB.Id);
        Assert.Contains("<mark>", anonA.Articles.First(x => x.Id == publicA.Id).SnippetHtml ?? "");

        var agentA = await adminA.GetFromJsonAsync<GlobalSearchResultDto>($"/api/v1/search?q={word}", J);
        Assert.Contains(agentA!.Hits, h => h.Id == publicA.Id);
        Assert.Contains(agentA.Hits, h => h.Id == internalA.Id);
        Assert.DoesNotContain(agentA.Hits, h => h.Id == publicB.Id);
        // Prefix search (search-as-you-type).
        var prefix = await adminA.GetFromJsonAsync<GlobalSearchResultDto>($"/api/v1/search?q={word[..7]}", J);
        Assert.Contains(prefix!.Hits, h => h.Id == publicA.Id);

        // Agents only see tickets in their data scope.
        var ticket = await Ok<TicketDetailDto>(await adminA.PostAsJsonAsync("/api/v1/tickets",
            new CreateTicketRequest { Subject = $"Billing {word} dispute", Channel = "Phone", DepartmentId = a.BillingDepartmentId, SkipRouting = true }, J));
        await factory.DrainOutboxAsync();
        var (_, supportEmail) = await a.CreateAgentAsync("Support Only", "Agent", a.SupportDepartmentId);
        var support = await a.AgentClientAsync(supportEmail);
        var supportHits = await support.GetFromJsonAsync<GlobalSearchResultDto>($"/api/v1/search?q={word}", J);
        Assert.DoesNotContain(supportHits!.Hits, h => h.Id == ticket.Id);
        Assert.Contains((await adminA.GetFromJsonAsync<GlobalSearchResultDto>($"/api/v1/search?q={word}&types=Ticket", J))!.Hits, h => h.Id == ticket.Id);
    }

    [Fact]
    public async Task Resolved_ticket_becomes_an_internal_draft_article_without_internal_notes()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = await Ok<TicketDetailDto>(await admin.PostAsJsonAsync("/api/v1/tickets",
            new CreateTicketRequest { Subject = "Re: Scanner not detected", DescriptionHtml = "<p>USB scanner not detected after update.</p>", Channel = "Phone", SkipRouting = true }, J));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"/api/v1/kb/articles/from-ticket/{ticket.Id}", new ArticleFromTicketRequest(), J)).StatusCode);
        await Ok<ThreadDto>(await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/notes", new NoteRequest { BodyHtml = "<p>SECRET-NOTE customer password is hunter2</p>" }, J));
        await Ok<ThreadDto>(await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/replies", new ReplyRequest { BodyHtml = "<p>Reinstall the scanner driver in HID mode.</p>", Force = true }, J));
        Assert.True((await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/resolve", new ResolveTicketRequest("fixed", null, null), J)).IsSuccessStatusCode);

        var draft = await Ok<ArticleDetailDto>(await admin.PostAsJsonAsync($"/api/v1/kb/articles/from-ticket/{ticket.Id}", new ArticleFromTicketRequest(), J));
        Assert.Equal("Draft", draft.Status);
        Assert.Equal("Internal", draft.Visibility);
        Assert.Equal(ticket.Id, draft.SourceTicketId);
        Assert.Equal("Scanner not detected", draft.Title);
        Assert.Contains("HID mode", draft.BodyHtml);
        Assert.DoesNotContain("SECRET-NOTE", draft.BodyHtml);
        Assert.False(draft.IsLive);
    }

    [Fact]
    public async Task Flows_need_a_valid_tree_to_publish_and_record_portal_usage()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var broken = new FlowDefinitionDto("q1",
        [
            new FlowNodeDto("q1", "question", "Start", "Is it plugged in?", null, [new FlowOptionDto("Yes", "missing")]),
            new FlowNodeDto("orphan", "solution", "Orphan", "Never reached", null, [])
        ]);
        var flow = await Ok<FlowDetailDto>(await admin.PostAsJsonAsync("/api/v1/kb/flows", new SaveFlowRequest("Power issue", null, "Public", null, null, broken), J));
        Assert.Contains(flow.Issues, i => i.Message.Contains("missing step"));
        Assert.Contains(flow.Issues, i => i.NodeId == "orphan");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"/api/v1/kb/flows/{flow.Id}/transition", new FlowTransitionRequest("publish"), J)).StatusCode);

        var valid = new FlowDefinitionDto("q1",
        [
            new FlowNodeDto("q1", "question", "Start", "Is it plugged in?", null, [new FlowOptionDto("Yes", "e1"), new FlowOptionDto("No", "s1")]),
            new FlowNodeDto("s1", "solution", "Plug in", "<p>Plug it in.</p>", null, []),
            new FlowNodeDto("e1", "escalate", "Ticket", "We will help.", null, [])
        ]);
        flow = await Ok<FlowDetailDto>(await admin.PutAsJsonAsync($"/api/v1/kb/flows/{flow.Id}", new SaveFlowRequest("Power issue", null, "Public", null, null, valid), J));
        Assert.Empty(flow.Issues);
        await Ok<FlowDetailDto>(await admin.PostAsJsonAsync($"/api/v1/kb/flows/{flow.Id}/transition", new FlowTransitionRequest("publish"), J));

        var anon = factory.CreateClient();
        var portalFlow = await anon.GetFromJsonAsync<PortalFlowDto>($"/api/v1/portal/{t.Slug}/flows/{flow.Id}", J);
        Assert.Equal("q1", portalFlow!.Definition.StartNodeId);
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/flows/{flow.Id}/usage", new FlowUsageRequest("started"), J)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/flows/{flow.Id}/usage", new FlowUsageRequest("escalated"), J)).StatusCode);
        var stats = await admin.GetFromJsonAsync<FlowDetailDto>($"/api/v1/kb/flows/{flow.Id}", J);
        Assert.Equal(1, stats!.Started);
        Assert.Equal(1, stats.Escalated);
    }
}
