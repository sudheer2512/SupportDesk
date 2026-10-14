using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Common;
using SupportDesk.Application.Search;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Knowledge demo data for tenant "wings": a category tree, ~28 articles for Wings ERP / Books / POS (published,
/// internal-only, one restricted to Platinum customers, drafts in review), Hindi/Tamil translations (one outdated by a
/// republish), reader feedback and views, portal search history, community topics with accepted answers, two guided
/// troubleshooting flows, a ticket → article draft and a few portal tickets that show the Own / Location / Organisation
/// scopes. Everything goes through the real services. Ends with a search-index backfill.
/// </summary>
public sealed class KnowledgeDemoSeed : IDemoSeedContributor
{
    public int Order => 30;

    private sealed record Cat(string Key, string Name, string? Parent, string Icon, KbVisibility Visibility, string Description);

    private static readonly Cat[] Categories =
    [
        new("start", "Getting started", null, "rocket", KbVisibility.Public, "First steps with the Wings Business Suite."),
        new("erp", "Wings ERP", null, "package", KbVisibility.Public, "Finance, inventory, sales, payroll and compliance."),
        new("gst", "GST & compliance", "erp", "receipt", KbVisibility.Public, "GST calculation, e-invoicing, e-way bills and returns."),
        new("payroll", "Payroll", "erp", "wallet", KbVisibility.Public, "Payroll runs, PF, ESI and statutory deductions."),
        new("reports", "Reports & exports", "erp", "chart-column", KbVisibility.Public, "Standard reports, exports and scheduling."),
        new("books", "Wings Books", null, "book-open", KbVisibility.Public, "Accounting for small businesses."),
        new("banking", "Banking & reconciliation", "books", "credit-card", KbVisibility.Public, "Bank feeds, statement import and reconciliation."),
        new("pos", "Wings POS", null, "smartphone", KbVisibility.Public, "Billing counters, store sync and peripherals."),
        new("install", "Installation & upgrades", null, "server", KbVisibility.Authenticated, "Server setup, upgrades and database maintenance (customers only)."),
        new("account", "Account & sign-in", null, "key", KbVisibility.Public, "Passwords, users and access."),
        new("releases", "Release notes", null, "sparkles", KbVisibility.Public, "What's new in each release."),
        new("playbooks", "Support playbooks", null, "shield-check", KbVisibility.Internal, "Internal procedures for the support team.")
    ];

    private sealed record Doc(string Key, string Cat, string Title, string Summary, string Type, KbVisibility Visibility, string[] Tags, string? Product,
        double DaysAgo, string Author, string Body, string? Keywords = null);

    public async Task SeedAsync(DemoSeedContext c, CancellationToken ct)
    {
        // Brand: portal features used by the demo (anonymous tickets, self-registration, Hindi/Tamil).
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var brand = await db.Brands.FirstAsync(b => b.Id == c.BrandId, ct);
            brand.Name = "Wings Software";
            brand.PortalTitle = "Wings Help Center";
            brand.PortalWelcome = "Answers for Wings ERP, Wings Books and Wings POS — search the knowledge base, ask the community or raise a ticket.";
            brand.PrimaryColor = "#1d4ed8";
            brand.AccentColor = "#f59e0b";
            brand.SupportEmail = "support@wings.demo";
            brand.SupportPhone = "+91 44 4000 1000";
            brand.SupportedLocalesJson = Json.Serialize(new[] { "en", "hi", "ta" });
            brand.AllowAnonymousTickets = true;
            brand.AllowSelfRegistration = true;
            brand.CommunityEnabled = true;
            brand.KnowledgeBaseEnabled = true;
            brand.AiAssistantEnabled = true;
            await db.SaveChangesAsync(ct);
        });

        // Categories
        var cats = new Dictionary<string, Guid>();
        await c.AsAgentAsync("neha", async sp =>
        {
            var svc = sp.GetRequiredService<KbCategoryService>();
            var order = 0;
            foreach (var cat in Categories)
            {
                var dto = await svc.SaveAsync(null, new SaveKbCategoryRequest(cat.Parent is null ? null : cats[cat.Parent], cat.Name, null, cat.Description, cat.Icon,
                    cat.Visibility.ToString(), null, order++), ct);
                cats[cat.Key] = dto.Id;
            }
        });

        // Articles
        var ids = new Dictionary<string, Guid>();
        foreach (var doc in Docs)
        {
            c.At(c.DaysAgo(doc.DaysAgo, 11));
            await c.AsAgentAsync(doc.Author, async sp =>
            {
                var svc = sp.GetRequiredService<ArticleService>();
                var article = await svc.CreateCoreAsync(new SaveArticleRequest
                {
                    Title = doc.Title, Summary = doc.Summary, BodyHtml = doc.Body, Keywords = doc.Keywords, CategoryId = cats[doc.Cat], ArticleType = doc.Type,
                    Visibility = doc.Visibility.ToString(), Tags = doc.Tags, ProductId = doc.Product is null ? null : c.Products[doc.Product],
                    AccessRules = doc.Visibility == KbVisibility.Restricted ? [new ArticleAccessRuleInput(null, null, "Platinum", null)] : null,
                    ChangeNote = "First version"
                }, null, ct);
                ids[doc.Key] = article.Id;
            });
        }

        // Review & publish (Neha is the knowledge manager: reviewer + publisher).
        foreach (var doc in Docs.Where(d => !Unpublished.Contains(d.Key)))
        {
            c.At(c.DaysAgo(doc.DaysAgo, 15));
            await c.AsAgentAsync("neha", sp => sp.GetRequiredService<ArticleService>()
                .TransitionAsync(ids[doc.Key], new ArticleTransitionRequest("publish", "Reviewed for accuracy"), ct));
        }
        // A draft waiting for review (Meera) and a draft still being written (Karthik).
        c.At(c.DaysAgo(2, 12));
        await c.AsAgentAsync("meera", sp => sp.GetRequiredService<ArticleService>()
            .TransitionAsync(ids["stock-migration"], new ArticleTransitionRequest("submit", "Ready for review — covers the new warehouse import template."), ct));

        // Related articles, review/expiry dates.
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            void Related(string key, params string[] related)
            {
                var a = db.Articles.First(x => x.Id == ids[key]);
                a.RelatedArticleIdsJson = Json.Serialize(related.Select(r => ids[r]));
            }
            Related("gst-rounding", "einvoice", "eway-2172", "gstr-books");
            Related("eway-2172", "gst-rounding", "einvoice");
            Related("payroll-pf", "payroll-run", "pf-ceiling");
            Related("export-timeout", "scheduled-reports", "sql-maintenance");
            Related("pos-sync", "pos-offline", "pos-printer");
            Related("bank-reco", "bank-import");
            Related("password-reset", "account-locked", "user-access");
            Related("upgrade-8", "install-erp", "sql-maintenance", "release-801");
            var now = c.Anchor;
            foreach (var key in new[] { "account-locked", "faq-licence" })
                (await db.Articles.FirstAsync(x => x.Id == ids[key], ct)).ReviewDueAt = now.AddDays(-3);
            (await db.Articles.FirstAsync(x => x.Id == ids["pos-printer"], ct)).ReviewDueAt = now.AddDays(10);
            (await db.Articles.FirstAsync(x => x.Id == ids["release-801"], ct)).ExpiresAt = now.AddDays(10);
            await db.SaveChangesAsync(ct);
        });

        await SeedTranslationsAsync(c, ids, ct);
        await SeedReaderSignalsAsync(c, ids, ct);
        await SeedFlowsAsync(c, ids, ct);
        await SeedCommunityAsync(c, ids, ct);
        await SeedPortalTicketsAsync(c, ct);

        // Ticket → article: a reviewer-ready draft from the resolved payroll incident.
        if (c.Tickets.TryGetValue("payroll", out var payrollTicket))
        {
            c.At(c.DaysAgo(1, 10));
            try
            {
                await c.AsAgentAsync("neha", sp => sp.GetRequiredService<ArticleAuthoringService>()
                    .CreateFromTicketAsync(payrollTicket, new ArticleFromTicketRequest("PF computed on gross for imported employees", true, cats["payroll"]), ct));
            }
            catch (DomainException) { /* ticket not resolved in this demo run */ }
        }

        c.Clock.Unpin();
        await c.AsSystemAsync(sp => sp.GetRequiredService<SearchBackfill>().RunAsync(5000, ct));
    }

    private static readonly HashSet<string> Unpublished = ["stock-migration", "tally-import"];

    // ───────────────────────────── Translations ─────────────────────────────

    private static async Task SeedTranslationsAsync(DemoSeedContext c, Dictionary<string, Guid> ids, CancellationToken ct)
    {
        (string Key, string Locale, string Title, string Summary, string Body)[] translations =
        [
            ("password-reset", "hi", "Wings ERP का पासवर्ड कैसे रीसेट करें", "लॉगिन स्क्रीन से या अपने एडमिनिस्ट्रेटर की मदद से पासवर्ड रीसेट करें।",
                "<p>लॉगिन स्क्रीन पर <strong>पासवर्ड भूल गए?</strong> पर क्लिक करें और अपना पंजीकृत ई-मेल दर्ज करें।</p><ol><li>ई-मेल में मिला 6 अंकों का कोड दर्ज करें।</li><li>नया पासवर्ड बनाएं (कम से कम 10 अक्षर, एक बड़ा अक्षर और एक अंक)।</li><li>नए पासवर्ड से साइन इन करें।</li></ol><p>यदि ई-मेल नहीं मिलता है, तो अपने Wings एडमिनिस्ट्रेटर से <em>उपयोगकर्ता प्रबंधन</em> में पासवर्ड रीसेट करने को कहें।</p>"),
            ("password-reset", "ta", "Wings ERP கடவுச்சொல்லை மீட்டமைப்பது எப்படி", "உள்நுழைவு திரையிலிருந்து அல்லது நிர்வாகியின் உதவியுடன் கடவுச்சொல்லை மீட்டமைக்கவும்.",
                "<p>உள்நுழைவு திரையில் <strong>கடவுச்சொல் மறந்துவிட்டதா?</strong> என்பதைக் கிளிக் செய்து உங்கள் மின்னஞ்சலை உள்ளிடவும்.</p><ol><li>மின்னஞ்சலில் வந்த 6 இலக்க குறியீட்டை உள்ளிடவும்.</li><li>புதிய கடவுச்சொல்லை உருவாக்கவும்.</li><li>புதிய கடவுச்சொல்லுடன் உள்நுழையவும்.</li></ol>"),
            ("gst-rounding", "hi", "मल्टी-रेट इनवॉइस पर GST राउंडिंग अंतर ठीक करें", "जब CGST + SGST का योग इनवॉइस कुल से कुछ पैसे अलग हो।",
                "<p>यह अंतर तब आता है जब राउंडिंग <strong>लाइन स्तर</strong> पर की जाती है लेकिन इनवॉइस का कुल <strong>इनवॉइस स्तर</strong> पर राउंड होता है।</p><ol><li><em>सेटिंग्स → GST → राउंडिंग</em> खोलें।</li><li>\"इनवॉइस स्तर पर राउंड ऑफ\" चालू करें और राउंड-ऑफ लेजर चुनें।</li><li>प्रभावित इनवॉइस को दोबारा सेव करें।</li></ol>"),
            ("pos-sync", "ta", "Wings POS ஸ்டோர் ஒத்திசைவு: நின்ற வரிசையை சரிசெய்தல்", "கடைகளின் பில்கள் தலைமை அலுவலகத்திற்கு வராதபோது என்ன செய்வது.",
                "<p>ஒத்திசைவு நிலை <strong>Queue blocked</strong> எனக் காட்டினால்:</p><ol><li>இணைய இணைப்பைச் சரிபார்க்கவும்.</li><li><em>Wings Sync Service</em>-ஐ மறுதொடக்கம் செய்யவும்.</li><li>பிழை பட்டியலில் உள்ள பில்லைத் திருத்தி மீண்டும் அனுப்பவும்.</li></ol>")
        ];
        foreach (var t in translations)
        {
            c.At(c.DaysAgo(20, 12));
            await c.AsAgentAsync("neha", async sp =>
            {
                var svc = sp.GetRequiredService<ArticleTranslationService>();
                await svc.SaveAsync(ids[t.Key], t.Locale, new SaveTranslationRequest(t.Title, t.Body, t.Summary), ct);
                await svc.TransitionAsync(ids[t.Key], t.Locale, "publish", ct);
            });
        }
        // The POS sync article is republished with a new step → its Tamil translation becomes Outdated.
        c.At(c.DaysAgo(4, 11));
        await c.AsAgentAsync("neha", async sp =>
        {
            var svc = sp.GetRequiredService<ArticleService>();
            var current = await svc.GetAsync(ids["pos-sync"], ct);
            await svc.UpdateAsync(current.Id, new SaveArticleRequest
            {
                Title = current.Title, Summary = current.Summary, Keywords = current.Keywords, CategoryId = current.CategoryId, ArticleType = current.ArticleType,
                Visibility = current.Visibility, Tags = current.Tags, ProductId = current.ProductId, RowVersion = current.RowVersion,
                RelatedArticleIds = current.RelatedArticles.Select(r => r.Id).ToList(),
                BodyHtml = current.BodyHtml + "<h2>After upgrading to POS 3.2</h2><p>Version 3.2 moves the sync queue to <code>%ProgramData%\\Wings\\Sync</code>. If the queue still shows the old location after an upgrade, run <em>Wings POS Repair</em> once from the Start menu.</p>",
                ChangeNote = "Added the POS 3.2 queue location"
            }, ct);
            await svc.TransitionAsync(current.Id, new ArticleTransitionRequest("publish", "Reviewed the 3.2 change"), ct);
        });
    }

    // ───────────────────────────── Feedback, views, searches ─────────────────────────────

    private static async Task SeedReaderSignalsAsync(DemoSeedContext c, Dictionary<string, Guid> ids, CancellationToken ct)
    {
        (string Key, string Contact, bool Helpful, int? Rating, string? Comment, double DaysAgo)[] feedback =
        [
            ("gst-rounding", "ramesh", true, 5, "Invoice-level rounding fixed our month-end difference. Thanks!", 9),
            ("gst-rounding", "kavya", true, 4, null, 14),
            ("gst-rounding", "nikhil", false, 2, "We still see a 1 paisa difference on credit notes.", 3),
            ("export-timeout", "harpreet", false, 2, "Still times out for 14 branches — the workaround splits the export but that is slow.", 1),
            ("export-timeout", "anita", true, 4, null, 18),
            ("password-reset", "joseph", true, 5, null, 6),
            ("password-reset", "suresh", true, 4, "Clear steps.", 2),
            ("password-reset", "imran", true, null, null, 11),
            ("pos-sync", "rekha", false, 3, "Restarting the service helped only for a few hours.", 2),
            ("pos-sync", "lata", true, 5, "The queue path tip saved us.", 5),
            ("bank-reco", "manoj", true, 4, "Auto-match rules are great.", 25),
            ("payroll-pf", "kavya", true, 5, "Exactly our issue.", 2),
            ("eway-2172", "nikhil", true, 4, null, 5),
            ("faq-licence", "senthil", false, 3, "Please add the renewal price list.", 8)
        ];
        foreach (var f in feedback)
        {
            c.At(c.DaysAgo(f.DaysAgo, 16));
            await c.AsSystemAsync(sp => sp.GetRequiredService<KbFeedbackService>()
                .SubmitAsync(ids[f.Key], new SubmitArticleFeedbackRequest(f.Helpful, f.Rating, f.Comment, "en"), c.Contacts[f.Contact], null, ct));
        }

        var views = new Dictionary<string, int>
        {
            ["gst-rounding"] = 812, ["password-reset"] = 1240, ["export-timeout"] = 455, ["pos-sync"] = 390, ["eway-2172"] = 377, ["payroll-pf"] = 268,
            ["bank-reco"] = 301, ["einvoice"] = 222, ["upgrade-8"] = 189, ["account-locked"] = 410, ["release-801"] = 145, ["pos-offline"] = 133,
            ["scheduled-reports"] = 120, ["gstr-books"] = 98, ["install-erp"] = 87, ["bank-import"] = 76, ["pos-printer"] = 64, ["user-access"] = 59,
            ["payroll-run"] = 143, ["pf-ceiling"] = 71, ["sql-maintenance"] = 52, ["faq-licence"] = 166, ["getting-started"] = 520, ["platinum-hotline"] = 23
        };
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            foreach (var (key, count) in views)
                if (ids.TryGetValue(key, out var id))
                    await db.Articles.Where(a => a.Id == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.ViewCount, a => a.ViewCount + count), ct);
        });

        (string Query, int Results, double DaysAgo, string? Contact)[] searches =
        [
            ("gst rounding difference", 4, 1, "ramesh"), ("gst rounding", 5, 3, "kavya"), ("gst rounding", 5, 8, null), ("e-way bill 2172", 2, 2, "nikhil"),
            ("eway bill error", 3, 6, null), ("reset password", 3, 1, "joseph"), ("forgot password", 2, 4, null), ("pos not syncing", 3, 2, "rekha"),
            ("pos sync", 3, 9, "lata"), ("export timeout", 2, 1, "harpreet"), ("tally import", 0, 2, "senthil"), ("tally prime migration", 0, 5, null),
            ("tally import", 0, 7, null), ("whatsapp invoice sharing", 0, 3, "anita"), ("whatsapp invoice", 0, 10, null), ("bank reconciliation", 2, 12, "manoj"),
            ("pf calculation", 3, 2, "kavya"), ("upgrade 8.0", 2, 15, null), ("multi currency invoice", 0, 4, null), ("barcode scanner", 0, 1, "lata")
        ];
        foreach (var s in searches)
        {
            c.At(c.DaysAgo(s.DaysAgo, 13));
            await c.AsSystemAsync(sp => sp.GetRequiredService<KbFeedbackService>()
                .LogSearchAsync(s.Query, s.Results, "portal", c.BrandId, s.Contact is null ? null : c.Contacts[s.Contact], "en", ct));
        }
    }

    // ───────────────────────────── Guided troubleshooting ─────────────────────────────

    private static async Task SeedFlowsAsync(DemoSeedContext c, Dictionary<string, Guid> ids, CancellationToken ct)
    {
        static FlowNodeDto Q(string id, string title, string text, params (string Label, string Next)[] options) =>
            new(id, "question", title, text, null, options.Select(o => new FlowOptionDto(o.Label, o.Next)).ToList());
        static FlowNodeDto S(string id, string title, string text, params (string Label, string Next)[] options) =>
            new(id, "solution", title, text, null, options.Select(o => new FlowOptionDto(o.Label, o.Next)).ToList());
        static FlowNodeDto A(string id, string title, Guid article, params (string Label, string Next)[] options) =>
            new(id, "article", title, "", article, options.Select(o => new FlowOptionDto(o.Label, o.Next)).ToList());
        static FlowNodeDto E(string id, string title, string text) => new(id, "escalate", title, text, null, []);

        var gst = new SaveFlowRequest("Invoice GST total doesn't match", "Find out why CGST + SGST differs from the invoice total and fix it.",
            nameof(KbVisibility.Public), c.Products["erp"], null, new FlowDefinitionDto("q1",
            [
                Q("q1", "Several GST rates?", "Does the difference appear on invoices that have items with more than one GST rate?", ("Yes", "q2"), ("No", "q3")),
                Q("q2", "Invoice-level rounding?", "Open Settings → GST → Rounding. Is “Round off at invoice level” switched on?", ("Yes, it is on", "a1"), ("No, it is off", "s1")),
                Q("q3", "HSN code correct?", "Open the item master of an affected item. Is its HSN/SAC code correct?", ("No / not sure", "s2"), ("Yes, it is correct", "e1")),
                A("a1", "Rounding article", ids["gst-rounding"], ("Still not matching", "e1")),
                S("s1", "Enable invoice-level rounding", "<p>Switch on <strong>Round off at invoice level</strong>, choose the <em>Round-off</em> ledger and save. Then open the affected invoice and save it again — the totals are recalculated.</p>", ("Still not matching", "e1")),
                S("s2", "Fix the HSN code", "<p>Correct the HSN/SAC code in the item master (Inventory → Items), then re-save the invoice. The correct GST rate is picked up from the HSN master.</p>"),
                E("e1", "Talk to a GST specialist", "Our GST specialists will check your tax configuration. Create a ticket — the steps you tried are added automatically.")
            ]));
        var pos = new SaveFlowRequest("POS terminal is not syncing with head office", "Get bills from a store counter flowing to head office again.",
            nameof(KbVisibility.Public), c.Products["pos"], null, new FlowDefinitionDto("q1",
            [
                Q("q1", "Terminal online?", "Can the POS terminal open a website (is it connected to the internet)?", ("No", "s1"), ("Yes", "q2")),
                S("s1", "Work offline", "<p>Wings POS keeps billing offline. Fix the network (router, cable, Wi-Fi); the sync resumes automatically within 5 minutes of reconnecting.</p>", ("Online again but still not syncing", "q2")),
                Q("q2", "Queue blocked?", "Does the sync status (bottom-right) show “Queue blocked” or a red badge?", ("Yes", "a1"), ("No", "q3")),
                A("a1", "Unblock the queue", ids["pos-sync"], ("Queue is still blocked", "e1")),
                Q("q3", "After an update?", "Did the sync stop right after a Wings POS update?", ("Yes", "s2"), ("No", "e1")),
                S("s2", "Restart the sync service", "<p>Open <em>Services</em>, restart <strong>Wings Sync Service</strong>, then run <em>Wings POS Repair</em> once from the Start menu.</p>", ("Still not syncing", "e1")),
                E("e1", "Store sync support", "Our POS team will connect remotely and check the store's sync log. Create a ticket.")
            ]));
        var flowIds = new List<Guid>();
        c.At(c.DaysAgo(45, 11));
        await c.AsAgentAsync("neha", async sp =>
        {
            var svc = sp.GetRequiredService<TroubleshootingService>();
            foreach (var flow in new[] { gst, pos })
            {
                var saved = await svc.SaveAsync(null, flow, ct);
                await svc.TransitionAsync(saved.Id, "publish", ct);
                flowIds.Add(saved.Id);
            }
        });
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            await db.TroubleshootingFlows.Where(f => f.Id == flowIds[0]).ExecuteUpdateAsync(u => u.SetProperty(f => f.StartedCount, 48).SetProperty(f => f.ResolvedCount, 31).SetProperty(f => f.EscalatedCount, 9), ct);
            await db.TroubleshootingFlows.Where(f => f.Id == flowIds[1]).ExecuteUpdateAsync(u => u.SetProperty(f => f.StartedCount, 27).SetProperty(f => f.ResolvedCount, 15).SetProperty(f => f.EscalatedCount, 7), ct);
        });
    }

    // ───────────────────────────── Community ─────────────────────────────

    private static async Task SeedCommunityAsync(DemoSeedContext c, Dictionary<string, Guid> ids, CancellationToken ct)
    {
        TopicDetailDto? welcome = null;
        c.At(c.DaysAgo(60, 10));
        await c.AsAgentAsync("neha", async sp =>
        {
            var svc = sp.GetRequiredService<CommunityService>();
            welcome = await svc.CreateTopicAsync(new CreateTopicRequest("Welcome to the Wings community — please read first",
                "<p>Ask questions, share tips and vote for the answers that helped you. Our support engineers answer here every working day.</p><ul><li>Search before posting — your question may already be answered.</li><li>Never post licence keys, passwords or customer data.</li><li>For urgent production issues, raise a ticket instead.</li></ul>",
                false, ["announcement"]), c.BrandId, ct);
            await svc.ModerateTopicAsync(welcome.Id, new ModerateRequest("pin"), ct);
        });

        async Task<TopicDetailDto> Topic(string contact, double daysAgo, CreateTopicRequest r)
        {
            c.At(c.DaysAgo(daysAgo, 10));
            TopicDetailDto? t = null;
            await c.AsContactAsync(contact, async sp => t = await sp.GetRequiredService<CommunityService>().CreateTopicAsync(r, c.BrandId, ct));
            return t!;
        }
        async Task<TopicDetailDto> AgentReply(string agent, Guid topic, double daysAgo, string body)
        {
            c.At(c.DaysAgo(daysAgo, 14));
            TopicDetailDto? t = null;
            await c.AsAgentAsync(agent, async sp => t = await sp.GetRequiredService<CommunityService>().ReplyAsync(topic, new CreatePostRequest(body), c.BrandId, ct));
            return t!;
        }
        async Task<TopicDetailDto> ContactReply(string contact, Guid topic, double daysAgo, string body)
        {
            c.At(c.DaysAgo(daysAgo, 15));
            TopicDetailDto? t = null;
            await c.AsContactAsync(contact, async sp => t = await sp.GetRequiredService<CommunityService>().ReplyAsync(topic, new CreatePostRequest(body), c.BrandId, ct));
            return t!;
        }
        async Task Vote(string contact, Guid? topic, Guid? post) =>
            await c.AsContactAsync(contact, sp => sp.GetRequiredService<CommunityService>().VoteAsync(topic, post, 1, c.BrandId, ct));

        var t1 = await Topic("ramesh", 20, new CreateTopicRequest("Best way to handle GST rounding for mixed 5% and 18% items?",
            "<p>Our invoices mix 5% and 18% items and the CGST/SGST totals are sometimes off by a few paise from the grand total. Should we round per line or at invoice level? What do others do?</p>",
            true, ["gst", "rounding"]));
        var t1a = await AgentReply("meera", t1.Id, 19.8,
            $"<p>Hi Ramesh — for mixed-rate invoices we recommend <strong>invoice-level rounding</strong> with a dedicated round-off ledger. The step-by-step guide is here: <a href=\"/portal/wings/kb/fix-gst-rounding-differences-on-multi-rate-invoices\">Fix GST rounding differences on multi-rate invoices</a>.</p>");
        await ContactReply("anita", t1.Id, 19, "<p>We switched to invoice-level rounding last quarter — no differences since. Remember to re-save open invoices after the change.</p>");
        await c.AsContactAsync("ramesh", sp => sp.GetRequiredService<CommunityService>().AcceptAsync(t1.Id, t1a.Posts.First(p => p.IsAgentResponse).Id, c.BrandId, ct));
        await Vote("kavya", t1.Id, null);
        await Vote("anita", t1.Id, null);
        await Vote("kavya", null, t1a.Posts.First(p => p.IsAgentResponse).Id);

        var t2 = await Topic("anita", 12, new CreateTopicRequest("Can we schedule the stock valuation report every Monday?",
            "<p>Our warehouse head wants the stock valuation report in his inbox every Monday at 9 AM. Is that possible without exporting it manually?</p>", true, ["reports", "inventory"]));
        var t2a = await AgentReply("karthik", t2.Id, 11.8,
            "<p>Yes — open the report, choose <em>Schedule</em>, pick <strong>Weekly · Monday · 09:00</strong> and add the recipients. The report is sent as Excel. Details: <a href=\"/portal/wings/kb/schedule-reports-to-be-e-mailed-automatically\">Schedule reports to be e-mailed automatically</a>.</p>");
        await c.AsContactAsync("anita", sp => sp.GetRequiredService<CommunityService>().AcceptAsync(t2.Id, t2a.Posts.First(p => p.IsAgentResponse).Id, c.BrandId, ct));
        await Vote("ramesh", t2.Id, null);

        var t3 = await Topic("harpreet", 3, new CreateTopicRequest("Sales Register export timing out — any workaround?",
            "<p>Exporting the Sales Register for a full quarter across 14 branches times out after about two minutes. Smaller ranges work. Any tips while we wait for the upgrade?</p>", true, ["reports", "export"]));
        await AgentReply("meera", t3.Id, 2.7,
            "<p>Until you move to 8.0.1 (which streams large exports), export one month at a time or schedule the report overnight — scheduled exports run in the background without the 2-minute limit.</p>");
        await ContactReply("thomas", t3.Id, 2.2, "<p>The overnight schedule works well for us — we get the full quarter by morning.</p>");
        await Vote("kavya", t3.Id, null);

        var t4 = await Topic("kavya", 30, new CreateTopicRequest("Share your payroll-day checklist",
            "<p>Payroll day is always stressful. Here's what we check before running payroll: attendance import, new joiners' PF category, arrears. What's on your list?</p>",
            false, ["payroll"]));
        await ContactReply("thomas", t4.Id, 29, "<p>We also preview the bank transfer file and compare totals with last month before releasing salaries.</p>");
        await AgentReply("priya", t4.Id, 28.5, "<p>Great list! We'd add: run the <em>Statutory deductions</em> preview — it flags employees whose PF wage exceeds the ceiling.</p>");

        await Topic("lata", 8, new CreateTopicRequest("Idea: loyalty points on partial payments",
            "<p>Customers paying part cash and part card can't redeem loyalty points on the cash part. Would anyone else like loyalty redemption on partial payments?</p>",
            false, ["pos", "loyalty", "idea"]));
        await Topic("suresh", 0.5, new CreateTopicRequest("Warehouse terminal asks for password again after every shift",
            "<p>Since last week the warehouse terminal logs out at shift change and asks for the password again. Is there a setting for the session length?</p>", true, ["login"]));
        _ = welcome;
    }

    // ───────────────────────────── Portal tickets (scope demo) ─────────────────────────────

    private static async Task SeedPortalTicketsAsync(DemoSeedContext c, CancellationToken ct)
    {
        async Task<Guid> Portal(string contact, double daysAgo, string subject, string body, string product, string priority)
        {
            c.At(c.DaysAgo(daysAgo, 10));
            var id = Guid.Empty;
            await c.AsContactAsync(contact, async sp =>
            {
                var db = sp.GetRequiredService<DeskDbContext>();
                var contactRow = await db.Contacts.AsNoTracking().FirstAsync(x => x.Id == c.Contacts[contact], ct);
                id = (await sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
                {
                    Subject = subject, DescriptionHtml = body, Channel = nameof(Channel.Portal), BrandId = c.BrandId, ProductId = c.Products[product], Priority = priority
                }, new TicketCreationSource(CreateInitialThread: true, PortalSubmission: true, AuthorName: contactRow.FullName, FromAddress: contactRow.Email,
                    EnforceRequiredFields: false), ct)).Id;
            });
            return id;
        }
        var joseph = await Portal("joseph", 2, "Invoice print shows the old company address",
            "<p>After we moved office, printed sales invoices still show the old address in the header. The company master has the new address.</p>", "erp", "Medium");
        c.At(c.DaysAgo(1.5, 15));
        await c.AsAgentAsync("arjun", sp => sp.GetRequiredService<ConversationService>().ReplyAsync(joseph, new ReplyRequest
        {
            BodyHtml = "<p>Hi Joseph, the invoice header comes from the <em>print template</em>, not the company master. Could you share a screenshot of Settings → Print templates → Sales invoice?</p>",
            Force = true
        }, ct));
        await Portal("lata", 1, "Barcode scanner not detected on counter 3",
            "<p>The USB barcode scanner on counter 3 of our branch stopped working after Windows updates last night. Other counters are fine.</p>", "pos", "High");
        var ramesh = await Portal("ramesh", 6, "Unable to export GSTR-1 JSON for September",
            "<p>The GSTR-1 export fails with 'Invalid B2CS summary' for September. Filing is due on the 11th.</p>", "erp", "High");
        c.At(c.DaysAgo(5, 12));
        await c.AsAgentAsync("meera", sp => sp.GetRequiredService<ConversationService>().ReplyAsync(ramesh, new ReplyRequest
        {
            BodyHtml = "<p>Hi Ramesh, two B2CS invoices had a place of supply without a state code. We corrected them with your team and the JSON now validates on the GST portal. Could you confirm the filing went through?</p>",
            Force = true
        }, ct));
        await c.AsAgentAsync("meera", sp => sp.GetRequiredService<TicketService>().ResolveAsync(ramesh,
            new ResolveTicketRequest("fixed", "data_issue", "<p>Place-of-supply state codes corrected on two B2CS invoices; GSTR-1 JSON regenerated.</p>", true, RequestConfirmation: true), ct));
    }

    // ───────────────────────────── Articles ─────────────────────────────

    private static readonly Doc[] Docs =
    [
        new("getting-started", "start", "Getting started with the Wings Help Center",
            "How to find answers, raise tickets and follow them up.", "HowTo", KbVisibility.Public, ["help-center", "tickets"], null, 90, "neha",
            "<p>Welcome! This help center is the fastest way to get help with Wings ERP, Wings Books and Wings POS.</p><h2>Find an answer</h2><ul><li>Use the search box on the home page — it searches articles and community discussions.</li><li>Try a <strong>guided troubleshooter</strong> for common problems such as GST mismatches or POS sync.</li></ul><h2>Raise a ticket</h2><ol><li>Sign in with your work e-mail (password or e-mailed code).</li><li>Choose <em>Submit a ticket</em>, pick the product and installation, and describe the problem.</li><li>Attach screenshots or log files — they help us solve it faster.</li></ol><h2>Follow up</h2><p>Under <em>My tickets</em> you see every reply, can add information, and confirm when the issue is resolved. If your access allows it, you can also see your colleagues' tickets for your location or organisation.</p>"),
        new("gst-rounding", "gst", "Fix GST rounding differences on multi-rate invoices",
            "When CGST + SGST does not add up to the invoice total by a few paise.", "Troubleshooting", KbVisibility.Public, ["gst", "rounding", "invoice"], "erp", 60, "neha",
            "<p>On invoices with items at different GST rates, the sum of CGST and SGST can differ from the grand total by a few paise. This happens when tax is rounded <strong>per line</strong> while the total is rounded <strong>per invoice</strong>.</p><h2>Fix</h2><ol><li>Open <em>Settings → GST → Rounding</em>.</li><li>Switch on <strong>Round off at invoice level</strong>.</li><li>Select the <em>Round-off</em> ledger (create one under Indirect expenses if needed).</li><li>Open each affected invoice and save it again — totals are recalculated.</li></ol><h2>Check</h2><table><thead><tr><th>Line</th><th>Taxable</th><th>Rate</th><th>Tax</th></tr></thead><tbody><tr><td>Item A</td><td>1,234.57</td><td>5%</td><td>61.73</td></tr><tr><td>Item B</td><td>8,765.43</td><td>18%</td><td>1,577.78</td></tr></tbody></table><p>With invoice-level rounding the difference is posted to the round-off ledger and the GST totals match the e-invoice.</p><blockquote><p>Credit notes follow the same setting from release 8.0.1.</p></blockquote>",
            "gst mismatch rounding paise cgst sgst total difference"),
        new("eway-2172", "gst", "E-way bill error 2172: invalid HSN code — causes and fix",
            "The GST portal rejects the e-way bill because an item's HSN code is not valid.", "Troubleshooting", KbVisibility.Public, ["gst", "e-way-bill"], "erp", 55, "neha",
            "<p>Error <code>2172</code> means at least one item on the document has an HSN code that the GST portal does not recognise.</p><h2>Causes</h2><ul><li>HSN code shorter than the length required for your turnover (4 or 6 digits).</li><li>Old HSN codes that were retired in the latest notification.</li><li>Services (SAC codes) used on a goods movement.</li></ul><h2>Fix</h2><ol><li>Open the item master and correct the HSN code.</li><li>Use <em>Tools → GST → Validate HSN</em> to check all items at once.</li><li>Regenerate the e-way bill from the invoice.</li></ol>"),
        new("einvoice", "gst", "Generate e-invoices (IRN) directly from Wings ERP",
            "Set up the IRP connection and generate IRN and QR codes when you save an invoice.", "HowTo", KbVisibility.Public, ["gst", "e-invoice"], "erp", 70, "neha",
            "<p>Wings ERP connects to the Invoice Registration Portal (IRP) through your GSP credentials.</p><ol><li>Go to <em>Settings → GST → E-invoicing</em> and enter the GSP user name and password.</li><li>Choose <strong>Generate IRN on save</strong> for B2B invoices.</li><li>Save an invoice — the IRN, acknowledgement number and signed QR code are stored and printed automatically.</li></ol><p>Cancel an IRN within 24 hours from the invoice's <em>E-invoice</em> tab.</p>"),
        new("gstr-books", "gst", "Prepare GSTR-1 and GSTR-3B in Wings Books",
            "Review, reconcile and export your monthly GST returns.", "HowTo", KbVisibility.Public, ["gst", "returns"], "books", 65, "neha",
            "<p>Wings Books prepares GSTR-1 and GSTR-3B from your posted transactions.</p><ol><li>Open <em>GST → Returns</em> and select the period.</li><li>Review the <strong>exceptions</strong> list (missing GSTIN, invalid place of supply).</li><li>Export the JSON and upload it on the GST portal, or file directly with your GSP.</li></ol>"),
        new("payroll-pf", "payroll", "Payroll: PF calculated on gross instead of basic for new joiners",
            "Employees imported with the template get PF on gross pay. Fix the employee category.", "Troubleshooting", KbVisibility.Public, ["payroll", "pf"], "erp", 40, "neha",
            "<p>Employees created through the import template are assigned the <em>Default</em> category, whose PF rule uses gross pay.</p><h2>Fix</h2><ol><li>Open <em>Payroll → Employee categories</em> and check the PF rule of <strong>Default</strong>.</li><li>Move the imported employees to the correct category (bulk edit).</li><li>Recalculate the payroll for the month.</li></ol><p>From release 8.0.1 the import template has a <code>Category</code> column.</p>"),
        new("payroll-run", "payroll", "Run monthly payroll in Wings ERP (step by step)",
            "From attendance import to bank transfer file.", "HowTo", KbVisibility.Authenticated, ["payroll"], "erp", 80, "neha",
            "<ol><li>Import attendance (<em>Payroll → Attendance → Import</em>).</li><li>Review new joiners, exits and arrears.</li><li>Run <strong>Process payroll</strong> for the month.</li><li>Check the <em>Statutory deductions</em> preview.</li><li>Approve and lock the payroll.</li><li>Generate the bank transfer file and payslips.</li></ol>"),
        new("pf-ceiling", "payroll", "Configure the PF wage ceiling and employer contribution rules",
            "Reference for the PF wage ceiling, employer share and EPS split.", "Reference", KbVisibility.Public, ["payroll", "pf", "statutory"], "erp", 85, "neha",
            "<table><thead><tr><th>Setting</th><th>Default</th><th>Where</th></tr></thead><tbody><tr><td>PF wage ceiling</td><td>₹15,000</td><td>Payroll → Statutory → PF</td></tr><tr><td>Employee share</td><td>12%</td><td>Same</td></tr><tr><td>Employer EPS</td><td>8.33% (capped)</td><td>Same</td></tr></tbody></table><p>Enable <strong>Restrict to ceiling</strong> per employee category when contributions should not exceed the ceiling.</p>"),
        new("export-timeout", "reports", "Report export times out for large date ranges",
            "Exports of large registers stop after two minutes. Use these workarounds or upgrade to 8.0.1.", "Troubleshooting", KbVisibility.Public, ["reports", "export"], "erp", 30, "meera",
            "<p>In 7.3 and 8.0, interactive exports are limited to two minutes. Very large registers (over ~150,000 rows) can hit this limit.</p><h2>Workarounds</h2><ul><li>Export one month at a time.</li><li><strong>Schedule</strong> the report — scheduled exports run in the background without the limit.</li><li>Filter by branch.</li></ul><h2>Permanent fix</h2><p>Release 8.0.1 streams exports in pages, so any size completes.</p>"),
        new("scheduled-reports", "reports", "Schedule reports to be e-mailed automatically",
            "Send any report daily, weekly or monthly as Excel or PDF.", "HowTo", KbVisibility.Public, ["reports", "schedule"], "erp", 75, "karthik",
            "<ol><li>Open the report and set the filters.</li><li>Click <em>Schedule</em>.</li><li>Choose the frequency (e.g. <strong>Weekly · Monday · 09:00</strong>), format and recipients.</li><li>Save — you can pause or edit schedules under <em>Reports → Schedules</em>.</li></ol>"),
        new("password-reset", "account", "How to reset your Wings ERP password",
            "Reset your password from the sign-in screen or ask your administrator.", "HowTo", KbVisibility.Public, ["password", "login"], "erp", 100, "neha",
            "<p>On the sign-in screen click <strong>Forgot password?</strong> and enter your registered e-mail.</p><ol><li>Enter the 6-digit code from the e-mail.</li><li>Choose a new password (at least 10 characters, one capital letter and one number).</li><li>Sign in with the new password.</li></ol><p>No e-mail? Ask your Wings administrator to reset it under <em>User management</em>.</p>"),
        new("account-locked", "account", "Account locked after failed sign-ins",
            "Why accounts lock and how long it takes to unlock.", "FAQ", KbVisibility.Public, ["login", "security"], "erp", 95, "neha",
            "<h2>Why is my account locked?</h2><p>After five failed attempts your account locks for 15 minutes to protect it.</p><h2>How do I unlock it?</h2><p>Wait 15 minutes, or ask your administrator to unlock it immediately. Use <em>Forgot password?</em> if you are unsure of the password.</p>"),
        new("user-access", "account", "Give a user access to a new company or branch",
            "Administrators can grant company and branch access per user.", "HowTo", KbVisibility.Authenticated, ["users", "permissions"], "erp", 50, "karthik",
            "<ol><li>Open <em>Administration → Users</em> and select the user.</li><li>On the <strong>Access</strong> tab, add the company and branches.</li><li>Choose the role for each company.</li><li>Ask the user to sign out and in again.</li></ol>"),
        new("pos-sync", "pos", "Wings POS store sync: troubleshooting a stuck sync queue",
            "What to do when bills from a store stop reaching head office.", "Troubleshooting", KbVisibility.Public, ["pos", "sync"], "pos", 45, "karthik",
            "<p>Each Wings POS terminal queues bills locally and sends them to head office every minute. If the status shows <strong>Queue blocked</strong>:</p><ol><li>Check the internet connection of the terminal.</li><li>Restart the <em>Wings Sync Service</em>.</li><li>Open <em>Sync → Errors</em>, correct the bill that failed (usually a deleted item or customer) and click <strong>Retry</strong>.</li></ol>"),
        new("pos-offline", "pos", "Using Wings POS offline and syncing when the connection returns",
            "Billing continues offline; the queue is sent automatically.", "HowTo", KbVisibility.Public, ["pos", "offline"], "pos", 58, "karthik",
            "<p>Wings POS keeps working without internet: bills, returns and loyalty points are stored locally.</p><ul><li>Offline mode is shown by an amber badge.</li><li>Price changes from head office arrive after reconnecting.</li><li>The queue is sent automatically within five minutes.</li></ul>"),
        new("pos-printer", "pos", "Configure thermal receipt printers for Wings POS",
            "Set up 58 mm / 80 mm printers, logo and cash drawer.", "HowTo", KbVisibility.Public, ["pos", "printer", "hardware"], "pos", 120, "karthik",
            "<ol><li>Install the printer driver and print a Windows test page.</li><li>In Wings POS open <em>Settings → Devices → Receipt printer</em>.</li><li>Choose the paper width (58 or 80 mm) and the code page.</li><li>Enable <strong>Open cash drawer</strong> if the drawer is connected to the printer.</li></ol><pre><code>Test command: Tools → Devices → Print test receipt</code></pre>"),
        new("bank-reco", "banking", "Bank reconciliation in Wings Books: duplicates and auto-match rules",
            "Remove duplicate statement lines and let rules match recurring entries.", "HowTo", KbVisibility.Public, ["banking", "reconciliation"], "books", 35, "neha",
            "<p>Duplicate lines usually come from importing overlapping statement periods.</p><ol><li>Open <em>Banking → Reconcile</em>.</li><li>Filter <strong>Possible duplicates</strong> and delete the extra lines.</li><li>Create <em>auto-match rules</em> for recurring entries (rent, bank charges) — they match on amount, narration and date window.</li></ol>"),
        new("bank-import", "banking", "Import bank statements (CSV, MT940) into Wings Books",
            "Supported formats and column mapping.", "HowTo", KbVisibility.Public, ["banking", "import"], "books", 110, "neha",
            "<p>Wings Books imports CSV, Excel and MT940 statements.</p><ol><li>Open <em>Banking → Import statement</em>.</li><li>Choose the bank account and file.</li><li>Map date, narration, debit and credit columns (saved for next time).</li></ol>"),
        new("install-erp", "install", "Installing Wings ERP 8.0 on Windows Server",
            "System requirements and installation steps for on-premise servers.", "ProductDoc", KbVisibility.Authenticated, ["installation", "server"], "erp", 150, "karthik",
            "<h2>Requirements</h2><ul><li>Windows Server 2019 or 2022, 4+ cores, 16 GB RAM.</li><li>SQL Server 2019 or 2022 (Express for up to 10 users).</li><li>.NET runtime installed by the setup.</li></ul><h2>Steps</h2><ol><li>Run <code>WingsERP-Setup-8.0.exe</code> as administrator.</li><li>Choose the SQL Server instance and create the company database.</li><li>Install the licence file received by e-mail.</li><li>Install the client on each workstation.</li></ol>"),
        new("upgrade-8", "install", "Upgrade from Wings ERP 7.3 to 8.0: checklist",
            "Plan, back up, upgrade and verify.", "ProductDoc", KbVisibility.Authenticated, ["upgrade"], "erp", 25, "karthik",
            "<ol><li>Take a full SQL backup and copy the attachments folder.</li><li>Close all clients and stop the Wings services.</li><li>Run the 8.0 setup — the database is upgraded automatically.</li><li>Verify trial balance and stock valuation against the backup.</li><li>Update clients on all workstations.</li></ol><p>Allow 1–2 hours for databases larger than 20 GB.</p>"),
        new("sql-maintenance", "install", "Recommended SQL Server maintenance plan for Wings ERP",
            "Backups, index maintenance and statistics for good performance.", "Reference", KbVisibility.Authenticated, ["sql-server", "performance"], "erp", 130, "karthik",
            "<table><thead><tr><th>Task</th><th>Frequency</th></tr></thead><tbody><tr><td>Full backup</td><td>Daily</td></tr><tr><td>Log backup</td><td>Every 30 minutes</td></tr><tr><td>Index rebuild (&gt;30% fragmentation)</td><td>Weekly</td></tr><tr><td>Update statistics</td><td>Daily</td></tr></tbody></table>"),
        new("release-801", "releases", "Wings ERP 8.0.1 release notes",
            "Streaming exports, GST rounding on credit notes, payroll import category.", "ReleaseNote", KbVisibility.Public, ["release", "announcement"], "erp", 18, "neha",
            "<h2>Highlights</h2><ul><li><strong>Streaming exports</strong> — large reports export without timeouts.</li><li>GST invoice-level rounding now applies to credit notes.</li><li>Payroll import template has a <code>Category</code> column.</li></ul><h2>Fixes</h2><ul><li>E-way bill generation for multi-vehicle consignments.</li><li>Trial balance performance at month end.</li></ul>"),
        new("faq-licence", "start", "Licence renewal and AMC: frequently asked questions",
            "Renewal dates, quotes and what happens when an AMC expires.", "FAQ", KbVisibility.Public, ["licence", "amc", "billing"], null, 140, "neha",
            "<h2>When is my renewal due?</h2><p>See <em>Account → Contracts</em> in this portal.</p><h2>How do I get a quote?</h2><p>Raise a ticket in the Billing department or e-mail billing@wings.demo.</p><h2>What if the AMC expires?</h2><p>The software keeps working; support and updates pause until renewal.</p>"),
        new("platinum-hotline", "start", "Platinum support: 24×7 hotline and on-site escalation",
            "Exclusive escalation path for Platinum customers.", "Reference", KbVisibility.Restricted, ["platinum", "escalation"], null, 70, "neha",
            "<p>As a Platinum customer you have a <strong>24×7 hotline: +91 44 4000 2424</strong> for production-down incidents.</p><ul><li>Critical incidents: first response within 15 minutes, any time.</li><li>On-site engineer within 4 business hours in metro cities.</li><li>Your customer success manager joins every critical incident review.</li></ul>"),
        new("l2-gst", "playbooks", "L2 playbook: investigating GST calculation mismatches",
            "Internal checklist for support engineers.", "Reference", KbVisibility.Internal, ["gst", "playbook"], "erp", 60, "meera",
            "<ol><li>Ask for the invoice number and a screenshot of the tax summary.</li><li>Check <code>Settings → GST → Rounding</code> on the customer instance (remote session).</li><li>Run the <em>GST audit</em> report for the invoice date range.</li><li>If item HSN rates differ from the master, raise a data-correction task.</li><li>Escalate to Product Development only with a reproducible sample.</li></ol>"),
        new("pos-sql", "playbooks", "Internal: SQL scripts to unblock the POS sync queue",
            "Only for L2 engineers — always back up first.", "Reference", KbVisibility.Internal, ["pos", "sql", "playbook"], "pos", 44, "karthik",
            "<p><strong>Back up the store database first.</strong></p><pre><code class=\"language-sql\">-- Find the blocking bill\nSELECT TOP 10 * FROM SyncQueue WHERE Status = 'Failed' ORDER BY CreatedAt;\n-- Re-queue after fixing the master data\nUPDATE SyncQueue SET Status = 'Pending', Attempts = 0 WHERE Id = @id;</code></pre>"),
        new("payroll-escalation", "playbooks", "Escalation checklist for payroll-day incidents",
            "Critical payroll incidents: who to call and in which order.", "Reference", KbVisibility.Internal, ["payroll", "escalation", "playbook"], "erp", 38, "priya",
            "<ol><li>Set priority Critical and inform the shift supervisor.</li><li>Open a bridge call with the customer's HR and finance.</li><li>Engage the payroll specialist (Priya) within 15 minutes.</li><li>Post updates every 30 minutes until salaries are released.</li></ol>"),
        new("stock-migration", "erp", "Opening stock migration for a new warehouse",
            "Import opening quantities and values with the warehouse template.", "HowTo", KbVisibility.Public, ["inventory", "migration"], "erp", 2.5, "meera",
            "<ol><li>Create the warehouse under <em>Inventory → Warehouses</em>.</li><li>Download the opening stock template.</li><li>Fill item code, quantity, rate and batch.</li><li>Import and post the opening stock voucher.</li></ol>"),
        new("tally-import", "start", "Import masters from Tally",
            "Move ledgers, items and parties from Tally to Wings ERP.", "HowTo", KbVisibility.Public, ["tally", "migration"], "erp", 1, "karthik",
            "<p>Draft: export masters from Tally as XML and import them with <em>Tools → Import → Tally XML</em>. Steps for vouchers to follow.</p>")
    ];
}
