using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Ai.Agents;
using SupportDesk.Application.Ai.Anomalies;
using SupportDesk.Application.Ai.Features;
using SupportDesk.Application.Ai.Retrieval;
using SupportDesk.Application.Common;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Knowledge;

namespace SupportDesk.Application.Ai;

/// <summary>
/// Demo AI setup for tenant "wings": features on, an approved "password reset how-to" auto-reply scenario, four agents
/// (Triage in suggest mode, Info collector, Knowledge resolver, Auto-responder for the approved scenario only), the
/// knowledge index backfilled, and a few showcase tickets so proposals, executions, summaries and anomalies have content.
/// </summary>
public sealed class AiDemoSeedContributor(ILogger<AiDemoSeedContributor> logger) : IDemoSeedContributor
{
    public int Order => 900;

    public const string PasswordScenario = "password_reset_howto";

    public async Task SeedAsync(DemoSeedContext c, CancellationToken ct)
    {
        if (c.TenantSlug != "wings") return;
        c.At(c.Anchor.AddHours(-6));
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<Persistence.DeskDbContext>();
            var config = await sp.GetRequiredService<AiConfigurationService>().GetOrCreateEntityAsync(ct);
            var features = AiFeatures.All.ToDictionary(f => f, f => new AiFeatureSettings(true, [], false, f == AiFeatures.Agents ? 0.75 : 0.7));
            config.FeaturesJson = AiConfigurationService.SerializeFeatures(features);
            config.MonthlyBudget = 50m;
            config.BudgetAction = BudgetAction.Block;
            config.AutoReplyScenariosJson = AiConfigurationService.SerializeScenarios(
            [
                new AutoReplyScenario(PasswordScenario, "Password reset how-to", "Customer asks how to reset, change or recover a forgotten password.",
                    ["reset password", "forgot password", "password reset", "forgotten password", "change password", "recover password"], [], [],
                    ["Email", "Portal", "WebForm", "Chat"], 0.75, true)
            ]);

            await EnsureArticlesAsync(db, c, ct);

            if (!await db.AiAgents.AnyAsync(ct))
            {
                db.AiAgents.AddRange(
                    new AiAgent
                    {
                        Name = "Triage assistant", Kind = AiAgentKind.Triage, IsEnabled = true, Mode = AiAgentMode.SuggestOnly,
                        ConfigJson = Json.Serialize(new { minConfidence = 0.7, allowedActions = new[] { "set_category", "set_priority", "add_tag" } }),
                        Instructions = "Suggest category, product and priority for new tickets. Never change the department automatically."
                    },
                    new AiAgent
                    {
                        Name = "Information collector", Kind = AiAgentKind.InfoCollector, IsEnabled = true, Mode = AiAgentMode.SuggestOnly,
                        ConfigJson = Json.Serialize(new
                        {
                            departments = new[] { c.Departments["support"] }, requiredInfo = InfoItems.Default, allowedActions = new[] { "request_info" }
                        })
                    },
                    new AiAgent
                    {
                        Name = "Knowledge resolver", Kind = AiAgentKind.KnowledgeResolver, IsEnabled = true, Mode = AiAgentMode.SuggestOnly,
                        ConfigJson = Json.Serialize(new { allowedActions = new[] { "suggest_article", "send_reply" } }),
                        Instructions = "Keep replies short and friendly; point to the article steps."
                    },
                    new AiAgent
                    {
                        Name = "Password reset auto-responder", Kind = AiAgentKind.AutoResponder, IsEnabled = true, Mode = AiAgentMode.AutoLowRisk,
                        ConfigJson = Json.Serialize(new
                        {
                            scenarios = new[] { PasswordScenario }, channels = new[] { "Email", "Portal", "WebForm", "Chat" }, minConfidence = 0.75,
                            maxRepliesPerTicket = 1, allowedActions = new[] { "auto_reply", "send_reply" }
                        })
                    });
            }
            await db.SaveChangesAsync(ct);
        });

        // Knowledge index for articles and every resolved demo ticket.
        await c.AsSystemAsync(async sp =>
        {
            var r = await sp.GetRequiredService<KnowledgeIndexer>().BackfillAsync(500, 2000, 0, ct);
            logger.LogInformation("AI demo: indexed {Articles} articles and {Tickets} resolved tickets", r.Articles, r.Tickets);
        });

        // Showcase tickets handled by the agents (created a few hours ago, so the outbox does not re-run agents on them).
        var showcase = new (string Contact, string Customer, string Channel, string Subject, string Body)[]
        {
            ("joseph", "abc", "Portal", "How do I reset my password?",
                "<p>Hi, I forgot my password for the Wings portal and cannot sign in this morning. How do I reset my password?</p>"),
            ("harpreet", "metro", "Email", "Error 'Invalid voucher date' when posting GRN",
                "<p>When I post a goods receipt note I get the error 'Invalid voucher date'. It started today and blocks our receiving team.</p>"),
            ("senthil", "pioneer", "Email", "Refund for duplicate AMC payment",
                "<p>We paid the AMC renewal twice last week (two bank transfers). Please refund the duplicate payment to our account.</p>")
        };
        var created = new List<Guid>();
        foreach (var (contact, customer, channel, subject, body) in showcase)
        {
            if (!c.Contacts.ContainsKey(contact)) continue;
            Guid ticketId = default;
            var request = new CreateTicketRequest
            {
                Subject = subject, DescriptionHtml = body, Channel = channel, ContactId = c.Contacts[contact],
                CustomerId = c.Customers.TryGetValue(customer, out var cust) ? cust : null, DepartmentId = c.Departments["support"]
            };
            try
            {
                if (channel == "Portal")
                    await c.AsContactAsync(contact, async sp => ticketId = (await sp.GetRequiredService<TicketService>().CreateAsync(request with { DepartmentId = null },
                        new TicketCreationSource(CreateInitialThread: true, PortalSubmission: true, EnforceRequiredFields: false), ct)).Id);
                else
                    await c.AsSystemAsync(async sp => ticketId = (await sp.GetRequiredService<TicketService>().CreateAsync(request,
                        new TicketCreationSource(CreateInitialThread: true, EnforceRequiredFields: false), ct)).Id);
                created.Add(ticketId);
                c.Tickets[$"ai-{contact}"] = ticketId;
                await c.AsSystemAsync(async sp =>
                {
                    await sp.GetRequiredService<AiSentimentService>().AnalyzeTicketAsync(ticketId, interactive: false, ct);
                    await sp.GetRequiredService<AiAgentRunner>().RunForTicketAsync(ticketId, AgentTriggers.Created, ActorType.Contact, ct);
                });
            }
            catch (DomainException ex)
            {
                logger.LogInformation("AI demo ticket '{Subject}' skipped: {Message}", subject, ex.Message);
            }
        }

        // Summaries, classification suggestions and a reply suggestion on story tickets so the execution log has content.
        await c.AsSystemAsync(async sp =>
        {
            foreach (var key in new[] { "gst", "vip", "export", "migration" })
            {
                if (!c.Tickets.TryGetValue(key, out var id)) continue;
                try { await sp.GetRequiredService<AiSummaryService>().SummarizeAsync(id, interactive: false, ct); }
                catch (DomainException ex) { logger.LogInformation("AI demo summary skipped: {Message}", ex.Message); }
            }
            foreach (var key in new[] { "pharmacy", "perf" })
            {
                if (!c.Tickets.TryGetValue(key, out var id)) continue;
                try
                {
                    var loader = sp.GetRequiredService<AiTicketLoader>();
                    var (ticket, snapshot) = await loader.LoadAsync(id, internalView: false, ct);
                    await sp.GetRequiredService<AiClassificationService>().ClassifyAsync(ticket, snapshot, interactive: false, AiFeatures.Classification, null, ct);
                }
                catch (DomainException ex) { logger.LogInformation("AI demo classification skipped: {Message}", ex.Message); }
            }
            var anomalies = sp.GetRequiredService<AnomalyDetectionService>();
            var today = DateOnly.FromDateTime(c.Anchor);
            for (var d = 0; d <= 7; d++) await anomalies.DetectAsync(today.AddDays(-d), ct);
        });
    }

    /// <summary>
    /// Customer-visible articles the AI can ground answers in. Added only when the knowledge base has no article on the
    /// subject yet (the knowledge module seeds its own content when installed).
    /// </summary>
    private static async Task EnsureArticlesAsync(Persistence.DeskDbContext db, DemoSeedContext c, CancellationToken ct)
    {
        var articles = new (string Slug, string Title, string Summary, KbVisibility Visibility, string Body, string Probe)[]
        {
            ("how-to-reset-your-password", "How to reset your Wings password", "Reset a forgotten or expired password yourself in a minute.", KbVisibility.Public,
                "<p>If you forgot your password or it has expired, you can reset it yourself.</p><ol>" +
                "<li>Open the Wings sign-in page and click <strong>Forgot password?</strong>.</li>" +
                "<li>Enter your registered e-mail address and click <strong>Send reset link</strong>.</li>" +
                "<li>Open the e-mail from Wings and click the reset link within 30 minutes.</li>" +
                "<li>Choose a new password with at least 10 characters, including upper- and lower-case letters and a digit.</li>" +
                "<li>Sign in again with the new password. On shared warehouse terminals, sign out and back in so saved credentials are updated.</li></ol>" +
                "<p>No e-mail after 5 minutes? Check your spam folder or ask your Wings administrator to reset the password from <em>Administration › Users</em>.</p>",
                "password"),
            ("gst-rounding-differences-on-invoices", "Fixing GST rounding differences on invoices", "Why invoice totals differ by a few paise and how to configure rounding.",
                KbVisibility.Public,
                "<p>A difference of a few paise between the CGST + SGST total and the invoice total is caused by the rounding method.</p><ol>" +
                "<li>Go to <strong>Settings › Taxation › GST</strong>.</li><li>Set <em>Tax rounding</em> to <strong>Invoice level</strong> instead of line level.</li>" +
                "<li>Re-save the affected invoices (posted invoices need a credit/debit note).</li></ol><p>Applies to Wings ERP 7.x and 8.x.</p>",
                "rounding"),
            ("report-export-timeouts", "Report export times out for large date ranges", "Export large registers reliably.", KbVisibility.Authenticated,
                "<p>Exports of more than about 150,000 rows can time out on version 7.3.</p><ol><li>Export one quarter or a few branches at a time.</li>" +
                "<li>Or upgrade to version 8.0.1 or later, which pages the export in the background.</li></ol>",
                "export")
        };
        var now = db.Clock.UtcNow;
        var author = c.Users.GetValueOrDefault("neha", c.Users["admin"]);
        foreach (var a in articles)
        {
            if (await db.Articles.AnyAsync(x => x.Slug == a.Slug || (x.Status == ArticleStatus.Published && x.Title.Contains(a.Probe)), ct)) continue;
            var article = new Article
            {
                Slug = a.Slug, Title = a.Title, Summary = a.Summary, Status = ArticleStatus.Published, Visibility = a.Visibility, ArticleType = "HowTo",
                OwnerUserId = author, LatestVersionNumber = 1, PrimaryLocale = "en", PublishedAt = now, ProductId = c.Products.GetValueOrDefault("erp"),
                TagsJson = Json.Serialize(new[] { a.Probe })
            };
            var version = new ArticleVersion
            {
                ArticleId = article.Id, VersionNumber = 1, Locale = "en", Title = a.Title, BodyHtml = a.Body, Summary = a.Summary, Status = ArticleStatus.Published,
                AuthorId = author, CreatedAt = now, ChangeNote = "Initial version"
            };
            article.CurrentVersionId = version.Id;
            article.PublishedVersionId = version.Id;
            db.Articles.Add(article);
            db.ArticleVersions.Add(version);
        }
    }
}
