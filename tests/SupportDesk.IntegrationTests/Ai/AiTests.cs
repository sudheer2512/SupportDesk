using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Ai;
using SupportDesk.Application.Ai.Retrieval;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Ai;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Ai;

[Collection("api")]
public sealed class AiTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = DeskApiFactory.Json;

    // ───────────────────────────── Helpers ─────────────────────────────

    private sealed record Catalog(Guid Payroll, Guid Invoicing, Guid Product);

    /// <summary>Categories, a product and labelled history so the tenant's local classifier has something to learn from.</summary>
    private async Task<Catalog> SeedHistoryAsync(TestTenant t)
    {
        var catalog = await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var payroll = new TicketCategory { Name = "Payroll", DepartmentId = t.SupportDepartmentId };
            var invoicing = new TicketCategory { Name = "Invoicing", DepartmentId = t.BillingDepartmentId };
            var product = new Domain.Products.Product { Name = "Wings ERP", Code = "ERP" };
            db.TicketCategories.AddRange(payroll, invoicing);
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return new Catalog(payroll.Id, invoicing.Id, product.Id);
        });
        string[] payrollTexts =
        [
            "Payroll run computes wrong PF deduction for new employees", "Salary slip shows incorrect provident fund amount",
            "Payslip PDF has the wrong month and PF is missing", "Employee salary not credited after payroll posting",
            "Payroll arrears calculation wrong for promoted staff", "PF ceiling not applied in this month's payroll"
        ];
        string[] invoiceTexts =
        [
            "GST total does not match the invoice amount", "Invoice copy needed for last quarter", "Credit note not reflected on the customer invoice",
            "Invoice numbering skipped after year end", "Duplicate invoice generated for the same order", "Invoice PDF missing the GSTIN of the buyer"
        ];
        foreach (var s in payrollTexts) await CreateTicketAsync(t, s, s + ".", categoryId: catalog.Payroll, productId: catalog.Product);
        foreach (var s in invoiceTexts) await CreateTicketAsync(t, s, s + ".", categoryId: catalog.Invoicing, productId: catalog.Product);
        return catalog;
    }

    /// <summary>Creates a ticket the way channel ingestion does (system actor, initial customer message).</summary>
    private Task<Guid> CreateTicketAsync(TestTenant t, string subject, string body, string channel = "Email", Guid? categoryId = null, Guid? productId = null,
        Guid? customerId = null) =>
        factory.InTenantAsync(t.Id, async sp =>
        {
            var ticket = await sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
            {
                Subject = subject, DescriptionHtml = $"<p>{body}</p>", Channel = channel, CategoryId = categoryId, ProductId = productId, CustomerId = customerId,
                NewContact = new NewContactDto("Pat Customer", $"pat.{Guid.NewGuid():N}@example.test", null, customerId)
            }, new TicketCreationSource(CreateInitialThread: true, EnforceRequiredFields: false));
            return ticket.Id;
        });

    private static async Task<Guid> CreateArticleAsync(IServiceProvider sp, string title, string bodyHtml, KbVisibility visibility = KbVisibility.Public)
    {
        var db = sp.GetRequiredService<DeskDbContext>();
        var now = db.Clock.UtcNow;
        var article = new Article
        {
            Slug = TextNormalization.Slugify(title) + "-" + Guid.NewGuid().ToString("N")[..6], Title = title, Status = ArticleStatus.Published,
            Visibility = visibility, PublishedAt = now, LatestVersionNumber = 1
        };
        var version = new ArticleVersion
        {
            ArticleId = article.Id, VersionNumber = 1, Locale = "en", Title = title, BodyHtml = bodyHtml, Status = ArticleStatus.Published, CreatedAt = now
        };
        article.CurrentVersionId = version.Id;
        article.PublishedVersionId = version.Id;
        db.Articles.Add(article);
        db.ArticleVersions.Add(version);
        await db.SaveChangesAsync();
        Assert.True(await sp.GetRequiredService<KnowledgeIndexer>().IndexArticleAsync(article.Id, default) > 0);
        return article.Id;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, J)!;
    }

    // ───────────────────────────── Tests ─────────────────────────────

    [Fact]
    public async Task Classification_suggestion_can_be_applied_and_is_logged_with_usage()
    {
        var t = await factory.CreateTenantAsync();
        var catalog = await SeedHistoryAsync(t);
        var admin = await t.AdminClientAsync();
        var ticketId = await CreateTicketAsync(t, "Wrong PF deduction in payroll for new joiners", "The payroll run deducted PF on gross salary for 23 new employees.");

        var suggestion = await ReadAsync<ClassificationSuggestionDto>(await admin.PostAsync($"/api/v1/ai/tickets/{ticketId}/classify", null));
        var category = suggestion.Fields.Single(f => f.Field == "category");
        Assert.Equal(catalog.Payroll.ToString(), category.Value);
        Assert.True(category.Confidence > 0.5, $"confidence {category.Confidence}");
        Assert.True(category.Differs);
        Assert.Equal("Local", suggestion.Provider);

        var applied = await ReadAsync<ClassificationApplyResultDto>(await admin.PostAsJsonAsync($"/api/v1/ai/tickets/{ticketId}/classify/apply",
            new ApplyClassificationRequest(suggestion.ExecutionId, ["category", "product"]), J));
        Assert.Contains("category", applied.AppliedFields);

        var detail = await admin.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{ticketId}", J);
        Assert.Equal(catalog.Payroll, detail!.CategoryId);
        Assert.Equal(catalog.Product, detail.ProductId);

        // Applying the same suggestion twice is refused.
        var again = await admin.PostAsJsonAsync($"/api/v1/ai/tickets/{ticketId}/classify/apply", new ApplyClassificationRequest(suggestion.ExecutionId), J);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var execution = await db.AiExecutions.SingleAsync(e => e.Id == suggestion.ExecutionId);
            Assert.Equal(AiFeatures.Classification, execution.Feature);
            Assert.Equal(AiExecutionStatus.Succeeded, execution.Status);
            Assert.Equal(AiOutcome.Applied, execution.Outcome);
            Assert.Equal(ticketId, execution.EntityId);
            Assert.False(string.IsNullOrEmpty(execution.InputHash));
            Assert.NotNull(execution.ReviewedById);
            var usage = await db.AiUsage.Where(u => u.Feature == AiFeatures.Classification).ToListAsync();
            Assert.Contains(usage, u => u.Requests >= 1 && u.InputTokens > 0 && u.PeriodStart == UsageMeter.PeriodOf(db.Clock.UtcNow));
            Assert.True(await db.TicketEvents.AnyAsync(e => e.TicketId == ticketId && e.Type == TicketEventTypes.AiAction));
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == AuditActions.AiActionApplied && a.EntityId == ticketId));
        });
    }

    [Fact]
    public async Task Exceeded_budget_blocks_paid_calls_before_any_provider_is_contacted()
    {
        var t = await factory.CreateTenantAsync();
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var config = await db.AiConfigurations.SingleAsync();
            config.Provider = AiProviderNames.OpenAICompatible;
            config.Model = "gpt-test";
            config.Endpoint = "https://ai.invalid/v1";
            config.ApiKeyProtected = sp.GetRequiredService<ISecretProtector>().Protect("sk-test");
            config.MonthlyBudget = 1m;
            config.BudgetAction = BudgetAction.Block;
            db.AiUsage.Add(new AiUsage
            {
                PeriodStart = UsageMeter.PeriodOf(db.Clock.UtcNow), Feature = AiFeatures.Summarization, Model = "OpenAICompatible:gpt-test", Requests = 40,
                InputTokens = 900_000, OutputTokens = 100_000, Cost = 5m, UpdatedAt = db.Clock.UtcNow
            });
            await db.SaveChangesAsync();
        });
        var admin = await t.AdminClientAsync();
        var ticketId = await CreateTicketAsync(t, "Report export fails", "Exporting the sales register times out.");

        var response = await admin.PostAsync($"/api/v1/ai/tickets/{ticketId}/summarize", null);
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Contains("ai_budget_exceeded", await response.Content.ReadAsStringAsync());

        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var refused = await db.AiExecutions.Where(e => e.EntityId == ticketId).ToListAsync();
            Assert.Contains(refused, e => e.Status == AiExecutionStatus.BudgetExceeded && e.Feature == AiFeatures.Summarization);
            Assert.DoesNotContain(refused, e => e.Status is AiExecutionStatus.Succeeded or AiExecutionStatus.Failed);
            Assert.Null((await db.Tickets.SingleAsync(x => x.Id == ticketId)).AiSummary);
        });
    }

    [Fact]
    public async Task Auto_responder_replies_only_inside_approved_scenarios()
    {
        var t = await factory.CreateTenantAsync();
        await factory.InTenantAsync(t.Id, async sp =>
        {
            await CreateArticleAsync(sp, "How to reset your password",
                "<p>If you forgot your password you can reset it yourself.</p><ol><li>Open the sign-in page and click <strong>Forgot password</strong>.</li>" +
                "<li>Enter your e-mail address and click <strong>Send reset link</strong>.</li><li>Open the e-mail and follow the reset link to choose a new password.</li></ol>");
            var db = sp.GetRequiredService<DeskDbContext>();
            var config = await db.AiConfigurations.SingleAsync();
            config.AutoReplyScenariosJson = AiConfigurationService.SerializeScenarios(
            [
                new AutoReplyScenario("password_reset", "Password reset how-to", "Customer asks how to reset a forgotten password.",
                    ["reset password", "forgot password", "password reset"], [], [], ["Email", "Portal"], 0.75, true)
            ]);
            db.AiAgents.Add(new AiAgent
            {
                Name = "Auto-responder", Kind = AiAgentKind.AutoResponder, IsEnabled = true, Mode = AiAgentMode.AutoLowRisk,
                ConfigJson = Json.Serialize(new { scenarios = new[] { "password_reset" }, minConfidence = 0.75, maxRepliesPerTicket = 1 })
            });
            await db.SaveChangesAsync();
        });

        var inScenario = await CreateTicketAsync(t, "Forgot password - how do I reset my password?",
            "I forgot my password and cannot sign in. How do I reset my password?");
        var refund = await CreateTicketAsync(t, "Refund our duplicate invoice payment", "We paid invoice INV-2207 twice. Please refund the duplicate payment.");
        var wrongChannel = await CreateTicketAsync(t, "Forgot password - how do I reset my password?",
            "I forgot my password and cannot sign in. How do I reset my password?", channel: "Phone");
        var unrelated = await CreateTicketAsync(t, "Stock valuation differs between reports", "The stock valuation report and the ledger show different totals.");
        await factory.DrainOutboxAsync();

        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            async Task<List<TicketThread>> AiReplies(Guid id) =>
                await db.TicketThreads.Where(x => x.TicketId == id && x.AuthorType == AuthorType.Ai && x.Kind == ThreadKind.AgentReply).ToListAsync();

            var reply = Assert.Single(await AiReplies(inScenario));
            Assert.Contains("Forgot password", reply.BodyText);
            Assert.Contains("[1]", reply.BodyText);
            Assert.Empty(await AiReplies(refund));
            Assert.Empty(await AiReplies(wrongChannel));
            Assert.Empty(await AiReplies(unrelated));

            // Outside the approved channel the matched answer waits for a person.
            var proposal = Assert.Single(await db.AiActionProposals.Where(p => p.TicketId == wrongChannel).ToListAsync());
            Assert.Equal("send_reply", proposal.ActionType);
            Assert.True(proposal.RequiresApproval);
            Assert.Equal(ProposalStatus.Proposed, proposal.Status);
            Assert.Contains("channel Phone", proposal.Rationale);
            // A refund request is flagged for a human decision; the AI never commits to it.
            Assert.Contains(await db.AiActionProposals.Where(p => p.TicketId == refund).ToListAsync(),
                p => p.ActionType == "consequential_review" && p.RequiresApproval && p.Risk == AiRiskLevel.High);
            // The automatic reply is audited and visible in the ticket history.
            Assert.True(await db.TicketEvents.AnyAsync(e => e.TicketId == inScenario && e.Type == TicketEventTypes.AiAction && e.ActorType == ActorType.Ai));
        });

        // Re-processing the same events never sends a second automatic reply.
        await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<Application.Ai.Agents.AiAgentRunner>()
            .RunForTicketAsync(inScenario, Application.Ai.Agents.AgentTriggers.CustomerReplied, ActorType.System, default));
        await factory.InTenantAsync(t.Id, async sp =>
            Assert.Equal(1, await sp.GetRequiredService<DeskDbContext>().TicketThreads.CountAsync(x => x.TicketId == inScenario && x.AuthorType == AuthorType.Ai)));
    }

    [Fact]
    public async Task Proposal_approve_apply_and_revert_restores_the_original_state()
    {
        var t = await factory.CreateTenantAsync();
        var catalog = await SeedHistoryAsync(t);
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            db.AiAgents.Add(new AiAgent
            {
                Name = "Triage", Kind = AiAgentKind.Triage, IsEnabled = true, Mode = AiAgentMode.SuggestOnly,
                ConfigJson = Json.Serialize(new { allowedActions = new[] { "set_category", "set_priority" } })
            });
            await db.SaveChangesAsync();
        });
        var ticketId = await CreateTicketAsync(t, "Payroll PF deduction wrong for new joiners", "This month's payroll computed PF on gross salary for new employees.");
        await factory.DrainOutboxAsync();

        var admin = await t.AdminClientAsync();
        var proposals = await admin.GetFromJsonAsync<List<AiProposalDto>>($"/api/v1/ai/proposals?ticketId={ticketId}", J);
        var proposal = proposals!.Single(p => p.ActionType == "set_classification");
        Assert.Equal("Proposed", proposal.Status);
        Assert.True(proposal.RequiresApproval);
        Assert.Equal(catalog.Payroll.ToString(), proposal.Payload.GetProperty("values").GetProperty("category").GetString());

        var before = await admin.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{ticketId}", J);
        Assert.Null(before!.CategoryId);

        var approved = await ReadAsync<AiProposalDto>(await admin.PostAsJsonAsync($"/api/v1/ai/proposals/{proposal.Id}/approve", new DecideProposalRequest("Looks right"), J));
        Assert.Equal("Applied", approved.Status);
        var applied = await admin.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{ticketId}", J);
        Assert.Equal(catalog.Payroll, applied!.CategoryId);
        Assert.Equal(catalog.Product, applied.ProductId);

        var reverted = await ReadAsync<AiProposalDto>(await admin.PostAsJsonAsync($"/api/v1/ai/proposals/{proposal.Id}/revert", new DecideProposalRequest(), J));
        Assert.Equal("Reverted", reverted.Status);
        var after = await admin.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{ticketId}", J);
        Assert.Null(after!.CategoryId);
        Assert.Null(after.SubcategoryId);
        Assert.Null(after.ProductId);
        Assert.Equal(before.Priority, after.Priority);
        Assert.Equal(before.DepartmentId, after.DepartmentId);

        // A reverted proposal cannot be reverted or approved again.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/ai/proposals/{proposal.Id}/revert", new DecideProposalRequest(), J)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/ai/proposals/{proposal.Id}/approve", new DecideProposalRequest(), J)).StatusCode);

        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == AuditActions.AiActionApplied && a.EntityId == ticketId));
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == AuditActions.AiActionReverted && a.EntityId == ticketId));
            Assert.True(await db.TicketEvents.CountAsync(e => e.TicketId == ticketId && e.Type == TicketEventTypes.AiAction) >= 2);
        });
    }

    [Fact]
    public async Task Agents_without_approval_permission_cannot_decide_proposals()
    {
        var t = await factory.CreateTenantAsync();
        var (_, email) = await t.CreateAgentAsync("Plain Agent");
        var agent = await t.AgentClientAsync(email);
        var ticketId = await CreateTicketAsync(t, "Cancel our AMC contract", "We want to cancel our AMC contract from next month.");
        var proposalId = await factory.InTenantAsync(t.Id, async sp =>
        {
            var p = await sp.GetRequiredService<Application.Ai.Agents.AiProposalService>().CreateAsync(ticketId, "consequential_review", new { kind = "contract" },
                "test", 0.9, AiRiskLevel.High, true, null, null, default);
            return p.Id;
        });
        var response = await agent.PostAsJsonAsync($"/api/v1/ai/proposals/{proposalId}/approve", new DecideProposalRequest(), J);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Retrieval_never_returns_chunks_of_another_tenant()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var articleA = await factory.InTenantAsync(a.Id, sp => CreateArticleAsync(sp, "Configure GST e-invoice API credentials",
            "<p>Open Settings, GST, e-invoice and enter the API user name and password issued by the GST portal. Test the e-invoice connection.</p>"));
        var articleB = await factory.InTenantAsync(b.Id, sp => CreateArticleAsync(sp, "Office opening hours",
            "<p>Our office is open Monday to Friday from 9 to 6. The help desk is closed on public holidays.</p>"));

        const string query = "How do I configure the GST e-invoice API credentials?";
        var fromB = await factory.InTenantAsync(b.Id, sp => sp.GetRequiredService<KnowledgeRetriever>()
            .SearchAsync(new RetrievalQuery(query, KnowledgeAudience.Agent, Take: 20, MinScore: 0), default));
        Assert.DoesNotContain(fromB, h => h.SourceId == articleA);
        Assert.All(fromB, h => Assert.Equal(articleB, h.SourceId));

        var fromA = await factory.InTenantAsync(a.Id, sp => sp.GetRequiredService<KnowledgeRetriever>()
            .SearchAsync(new RetrievalQuery(query, KnowledgeAudience.Agent, Take: 20), default));
        Assert.Equal(articleA, fromA[0].SourceId);

        // Same through the API, and the raw chunk table is tenant-filtered too.
        var clientB = await b.AdminClientAsync();
        var search = await ReadAsync<SemanticSearchResultDto>(await clientB.PostAsJsonAsync("/api/v1/ai/search", new SemanticSearchRequest(query, "articles", 50), J));
        Assert.DoesNotContain(search.Items, i => i.SourceId == articleA);
        await factory.InTenantAsync(b.Id, async sp =>
            Assert.DoesNotContain(await sp.GetRequiredService<DeskDbContext>().KnowledgeChunks.Select(c => c.SourceId).ToListAsync(), id => id == articleA));
    }

    [Fact]
    public async Task Public_portal_assistant_answers_only_from_public_knowledge()
    {
        var t = await factory.CreateTenantAsync();
        var (publicArticle, internalArticle) = await factory.InTenantAsync(t.Id, async sp => (
            await CreateArticleAsync(sp, "How to reset your password",
                "<p>Click Forgot password on the sign-in page, enter your e-mail address and follow the reset link to choose a new password.</p>"),
            await CreateArticleAsync(sp, "Internal: password reset override procedure",
                "<p>Agents can force a password reset from the admin console using the override procedure.</p>", KbVisibility.Internal)));
        var anonymous = factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/assistant", new PortalAssistantRequest("How do I reset my password?"), J);
        var answer = await ReadAsync<PortalAssistantResponse>(response);
        Assert.Contains(answer.Citations, c => c.SourceId == publicArticle);
        Assert.DoesNotContain(answer.Citations, c => c.SourceId == internalArticle);
        Assert.DoesNotContain("override procedure", answer.Text);

        var human = await ReadAsync<PortalAssistantResponse>(await anonymous.PostAsJsonAsync($"/api/v1/portal/{t.Slug}/assistant",
            new PortalAssistantRequest("I want to talk to a human agent please"), J));
        Assert.True(human.SuggestHandoff);
    }

    [Fact]
    public async Task Natural_language_analytics_returns_customers_with_most_sla_breaches()
    {
        var t = await factory.CreateTenantAsync();
        var (acme, beta) = await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var x = new Domain.Customers.Customer { Name = "Acme Breach Co" };
            var y = new Domain.Customers.Customer { Name = "Beta Calm Ltd" };
            db.Customers.AddRange(x, y);
            await db.SaveChangesAsync();
            return (x.Id, y.Id);
        });
        var breached = new List<Guid>();
        for (var i = 0; i < 3; i++) breached.Add(await CreateTicketAsync(t, $"Acme outage {i}", "System down.", customerId: acme));
        breached.Add(await CreateTicketAsync(t, "Beta question", "How to export?", customerId: beta));
        await CreateTicketAsync(t, "Beta second question", "How to print?", customerId: beta);
        await CreateTicketAsync(t, "Acme on track", "Minor question.", customerId: acme);
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            await db.Tickets.Where(x => breached.Contains(x.Id)).ExecuteUpdateAsync(u => u.SetProperty(x => x.SlaState, SlaState.Breached));
        });

        var admin = await t.AdminClientAsync();
        var response = await admin.PostAsJsonAsync("/api/v1/ai/analytics/ask", new AskAnalyticsRequest("Which customers had the most SLA violations this month?"), J);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("sla_breaches", root.GetProperty("intent").GetProperty("metric").GetString());
        Assert.Equal("customer", root.GetProperty("intent").GetProperty("dimension").GetString());
        Assert.Equal("bar", root.GetProperty("chart").GetProperty("type").GetString());
        var rows = root.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Acme Breach Co", rows[0].GetProperty("label").GetString());
        Assert.Equal(3, rows[0].GetProperty("value").GetDouble());
        Assert.Equal("Beta Calm Ltd", rows[1].GetProperty("label").GetString());
        Assert.Equal(1, rows[1].GetProperty("value").GetDouble());

        var unsupported = await admin.PostAsJsonAsync("/api/v1/ai/analytics/ask", new AskAnalyticsRequest("abc"), J);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
    }

    [Fact]
    public async Task Summaries_suggested_replies_and_admin_configuration_work_end_to_end()
    {
        var t = await factory.CreateTenantAsync();
        await factory.InTenantAsync(t.Id, sp => CreateArticleAsync(sp, "Report export times out for large date ranges",
            "<p>Exports of more than 150,000 rows can time out.</p><ol><li>Export one quarter at a time.</li><li>Or upgrade to version 8.0.1, which pages the export.</li></ol>"));
        var admin = await t.AdminClientAsync();
        var ticketId = await CreateTicketAsync(t, "Sales register export times out", "Exporting the sales register for Q2 to Excel times out after two minutes.");

        var summary = await ReadAsync<TicketSummaryDto>(await admin.PostAsync($"/api/v1/ai/tickets/{ticketId}/summarize", null));
        Assert.Contains("export", summary.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(summary.PendingActions);
        var detail = await admin.GetFromJsonAsync<TicketDetailDto>($"/api/v1/tickets/{ticketId}", J);
        Assert.NotNull(detail);

        var reply = await ReadAsync<SuggestedReplyDto>(await admin.PostAsJsonAsync($"/api/v1/ai/tickets/{ticketId}/suggest-reply", new SuggestReplyRequest(), J));
        Assert.True(reply.Grounded);
        Assert.Contains(reply.Citations, c => c.SourceType == "Article");
        Assert.Contains("[1]", reply.BodyText);

        var translation = await ReadAsync<TranslationDto>(await admin.PostAsJsonAsync("/api/v1/ai/translate", new TranslateRequest("Hello", "fr"), J));
        Assert.False(translation.Supported); // the built-in provider says so honestly

        var config = await admin.GetFromJsonAsync<AiConfigurationDto>("/api/v1/admin/ai/config", J);
        Assert.Equal("Local", config!.Provider);
        Assert.False(config.HasApiKey);
        var invalid = await admin.PutAsJsonAsync("/api/v1/admin/ai/config", new UpdateAiConfigurationRequest
        {
            Provider = "OpenAICompatible", Model = "llama3", Endpoint = "http://localhost:11434/v1"
        }, J);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); // private/insecure endpoints are refused by default
        var updated = await ReadAsync<AiConfigurationDto>(await admin.PutAsJsonAsync("/api/v1/admin/ai/config", new UpdateAiConfigurationRequest
        {
            Provider = "OpenAI", Model = "gpt-4o-mini", ApiKey = "sk-secret-value", MonthlyBudget = 25m, BudgetAction = "Warn"
        }, J));
        Assert.True(updated.HasApiKey);
        Assert.DoesNotContain("sk-secret-value", JsonSerializer.Serialize(updated, J));
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var stored = await sp.GetRequiredService<DeskDbContext>().AiConfigurations.SingleAsync();
            Assert.StartsWith("v1:", stored.ApiKeyProtected);
            Assert.DoesNotContain("sk-secret-value", stored.ApiKeyProtected);
        });

        var executions = await admin.GetFromJsonAsync<Contracts.Common.PageResult<AiExecutionDto>>($"/api/v1/admin/ai/executions?entityId={ticketId}", J);
        Assert.Contains(executions!.Items, e => e.Feature == AiFeatures.Summarization);
        Assert.Contains(executions.Items, e => e.Feature == AiFeatures.ReplySuggestion);
        var usage = await admin.GetFromJsonAsync<AiUsageSummaryDto>("/api/v1/admin/ai/usage", J);
        Assert.Contains(usage!.ByFeature, f => f.Key == AiFeatures.Summarization);

        // Context panel gets AI suggestions (articles + summary).
        var context = await admin.GetFromJsonAsync<TicketContextDto>($"/api/v1/tickets/{ticketId}/context", J);
        Assert.NotEmpty(context!.SuggestedArticles);
        Assert.Contains(context.AiRecommendations, r => r.Kind == "summary");
    }
}
