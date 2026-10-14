using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Common;
using SupportDesk.Application.Seed;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Automation;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Automation;

/// <summary>
/// Demo automation for tenant "wings": event rules (VIP escalation, GST classification, reopen note), scheduled rules
/// (waiting-on-customer reminder and auto-close, unassigned escalation), an inbound ERP webhook rule, an example
/// outbound webhook rule (inactive), the "Defect escalation" blueprint for Product Development and a disabled example
/// webhook subscription. Everything is created through the real services (validation, versions, audit).
/// </summary>
public sealed class AutomationDemoSeed : IDemoSeedContributor
{
    /// <summary>Secret of the demo inbound webhook rule "ERP payment received" (header X-Webhook-Secret).</summary>
    public const string DemoWebhookSecret = "wings-erp-demo-webhook-secret";
    public const string DemoWebhookKey = "wings-erp-payments";

    public int Order => 50;

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value, AutomationJson.Options);

    private static object Cond(string field, string op, object? value = null) => value is null ? new { field, op } : new { field, op, value };

    private static object Act(string type, object? @params = null) => @params is null ? new { type } : new { type, @params };

    public async Task SeedAsync(DemoSeedContext c, CancellationToken ct)
    {
        c.At(c.Anchor.AddHours(-2));
        Guid reminderTemplate = Guid.Empty;
        Dictionary<string, Guid> roles = new();
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name, r => r.Id, ct);
            var template = new ReplyTemplate
            {
                Name = "Waiting on customer — gentle reminder", Category = "Follow-up", Visibility = TemplateVisibility.Global, OwnerUserId = c.Users["admin"],
                Subject = "Reminder: we're waiting for your reply",
                BodyHtml = "<p>Hi {{contact.name}},</p><p>We're still waiting for the information we requested on ticket {{ticket.number}} " +
                           "(<em>{{ticket.subject}}</em>). Could you reply when you have a moment? If we don't hear back within 4 days, " +
                           "we'll close the ticket — you can always reopen it by replying.</p><p>Regards,<br>Wings Software Support</p>"
            };
            db.ReplyTemplates.Add(template);
            await db.SaveChangesAsync(ct);
            reminderTemplate = template.Id;
        });

        var support = c.Departments["support"];
        var engineering = c.Departments["engineering"];
        var waiting = c.Statuses["Waiting on Customer"];
        var closed = c.Statuses["Closed"];
        var closedCategories = new[] { "Resolved", "Closed" };

        await c.AsAgentAsync("admin", async sp =>
        {
            var rules = sp.GetRequiredService<RuleService>();

            async Task<RuleDetailDto> Rule(string name, string description, string trigger, object definition, int order, bool stop = false, int? interval = null,
                bool active = true) =>
                await rules.CreateAsync(new SaveRuleRequest
                {
                    Name = name, Description = description, Trigger = trigger, Definition = J(definition), ExecutionOrder = order, StopProcessing = stop,
                    ScheduleIntervalMinutes = interval, IsActive = active, Publish = true, ChangeNote = "Demo rule"
                }, ct);

            await Rule("VIP or Platinum + High → Critical and notify supervisor",
                "High-priority tickets from VIP contacts or Platinum customers are raised to Critical, tagged and the department supervisors are alerted.",
                RuleTriggers.TicketCreated,
                new
                {
                    conditions = new
                    {
                        logic = "and",
                        conditions = new[] { Cond("priority", ConditionOps.Equals, "High"), Cond("statusCategory", ConditionOps.NotIn, closedCategories) },
                        groups = new[] { new { logic = "or", conditions = new[] { Cond("contact.isVip", ConditionOps.Equals, true), Cond("customer.tier", ConditionOps.Equals, "Platinum") } } }
                    },
                    actions = new[]
                    {
                        Act(ActionTypes.SetPriority, new { priority = "Critical" }),
                        Act(ActionTypes.AddTags, new { tags = new[] { "vip" } }),
                        Act(ActionTypes.NotifyAgents, new
                        {
                            recipients = new[] { "supervisors" }, title = "VIP ticket {{ticket.number}} raised to Critical",
                            message = "{{ticket.subject}} — {{customer.name}} ({{contact.name}})", severity = "Warning"
                        })
                    }
                }, 10);

            var gstWords = new[] { "gst", "gstr", "e-invoice", "e-way", "cgst", "sgst", "igst" };
            await Rule("GST keywords → tag gst + category Calculation mismatch",
                "Support tickets mentioning GST, GSTR, e-invoice or e-way bill are tagged 'gst' and classified as Application Error › Calculation mismatch.",
                RuleTriggers.TicketCreated,
                new
                {
                    conditions = new
                    {
                        logic = "and",
                        conditions = new[] { Cond("departmentId", ConditionOps.Equals, support.ToString()), Cond("statusCategory", ConditionOps.NotIn, closedCategories) },
                        groups = new[] { new { logic = "or", conditions = new[] { Cond("subject", ConditionOps.ContainsAny, gstWords), Cond("description", ConditionOps.ContainsAny, gstWords) } } }
                    },
                    actions = new[]
                    {
                        Act(ActionTypes.AddTags, new { tags = new[] { "gst" } }),
                        Act(ActionTypes.SetCategory, new { categoryId = c.Categories["Application Error"], subcategoryId = c.Categories["Calculation mismatch"] })
                    }
                }, 20);

            await Rule("Customer replied on resolved ticket → reopen note",
                "When a customer reply reopens a resolved or closed ticket, leave a note for the agent and notify the assignee.",
                RuleTriggers.StatusChanged,
                new
                {
                    conditions = new
                    {
                        logic = "and",
                        conditions = new[]
                        {
                            Cond("status", ConditionOps.ChangedFrom, new[] { "Resolved", "Closed" }),
                            Cond("statusCategory", ConditionOps.In, new[] { "Open", "Assigned", "InProgress" }),
                            Cond("lastCustomerResponseAt", ConditionOps.WithinLastMinutes, 10)
                        }
                    },
                    actions = new[]
                    {
                        Act(ActionTypes.AddNote, new { bodyHtml = "<p>Reopened because {{contact.name}} replied after the ticket was resolved. Review the latest customer message before replying.</p>" }),
                        Act(ActionTypes.NotifyAgents, new { recipients = new[] { "assignee" }, title = "{{ticket.number}} was reopened by the customer", message = "{{ticket.subject}}" })
                    }
                }, 10);

            await Rule("Customer replied → clear reminder tag",
                "A customer reply cancels the waiting-on-customer reminder cycle.",
                RuleTriggers.CustomerReplied,
                new
                {
                    conditions = new { logic = "and", conditions = new[] { Cond("tags", ConditionOps.Contains, "awaiting-reply-reminder") } },
                    actions = new[] { Act(ActionTypes.RemoveTags, new { tags = new[] { "awaiting-reply-reminder" } }) }
                }, 10);

            await Rule("Waiting on Customer for 3 days → reminder",
                "Sends the reminder template and notes it when a ticket has waited 3 days for the customer.",
                RuleTriggers.Scheduled,
                new
                {
                    conditions = new
                    {
                        logic = "and",
                        conditions = new[]
                        {
                            Cond("statusId", ConditionOps.Equals, waiting.ToString()),
                            Cond("lastActivityAt", ConditionOps.OlderThanMinutes, 3 * 24 * 60),
                            Cond("tags", ConditionOps.NotContains, "awaiting-reply-reminder")
                        }
                    },
                    actions = new[]
                    {
                        Act(ActionTypes.ReplyWithTemplate, new { templateId = reminderTemplate }),
                        Act(ActionTypes.AddTags, new { tags = new[] { "awaiting-reply-reminder" } }),
                        Act(ActionTypes.AddNote, new { bodyHtml = "<p>Automatic reminder sent — no customer response for 3 days. The ticket closes automatically after 7 days without a reply.</p>" })
                    }
                }, 10, interval: 60);

            await Rule("Waiting on Customer for 7 days → auto-close",
                "Closes tickets that stayed unanswered for 4 more days after the reminder (7 days in total).",
                RuleTriggers.Scheduled,
                new
                {
                    conditions = new
                    {
                        logic = "and",
                        conditions = new[]
                        {
                            Cond("statusId", ConditionOps.Equals, waiting.ToString()),
                            Cond("tags", ConditionOps.Contains, "awaiting-reply-reminder"),
                            Cond("lastActivityAt", ConditionOps.OlderThanMinutes, 4 * 24 * 60)
                        }
                    },
                    actions = new[]
                    {
                        Act(ActionTypes.AddNote, new { bodyHtml = "<p>Closed automatically: no customer response for 7 days.</p>" }),
                        Act(ActionTypes.SetStatus, new { statusId = closed, comment = "Auto-closed after 7 days without a customer reply" })
                    }
                }, 20, interval: 60);

            await Rule("Unassigned for 2 hours → escalate to supervisor",
                "Open tickets nobody picked up within 2 hours are escalated and a department supervisor is added.",
                RuleTriggers.Scheduled,
                new
                {
                    conditions = new
                    {
                        logic = "and",
                        conditions = new[]
                        {
                            Cond("isUnassigned", ConditionOps.Equals, true),
                            Cond("statusCategory", ConditionOps.Equals, "Open"),
                            Cond("isEscalated", ConditionOps.Equals, false),
                            Cond("createdAt", ConditionOps.OlderThanMinutes, 120)
                        }
                    },
                    actions = new[] { Act(ActionTypes.Escalate, new { reason = "Unassigned for more than 2 hours", toSupervisor = true }) }
                }, 30, interval: 15);

            var erp = await Rule("ERP payment received → note on ticket",
                $"Inbound webhook for Wings ERP: POST {RuleService.InboundWebhookPath}{DemoWebhookKey} with header X-Webhook-Secret: {DemoWebhookSecret} " +
                "and a JSON body {\"ticketNumber\":1042,\"event\":\"payment.received\",\"reference\":\"RCPT-2291\",\"amount\":\"₹48,000\"}.",
                RuleTriggers.Webhook,
                new
                {
                    conditions = new { logic = "and", conditions = new[] { Cond("webhook.event", ConditionOps.Equals, "payment.received") } },
                    actions = new[]
                    {
                        Act(ActionTypes.AddNote, new { bodyHtml = "<p>Wings ERP: payment <strong>{{webhook.reference}}</strong> of {{webhook.amount}} was received.</p>" }),
                        Act(ActionTypes.AddTags, new { tags = new[] { "payment-received" } })
                    }
                }, 10);

            await Rule("Critical ticket → alert ERP service desk (example)",
                "Example outbound API call (inactive): posts a signed JSON alert when a ticket becomes Critical.",
                RuleTriggers.TicketUpdated,
                new
                {
                    trigger = new { fields = new[] { "priority" } },
                    conditions = new { logic = "and", conditions = new[] { Cond("priority", ConditionOps.ChangedTo, "Critical") } },
                    actions = new[]
                    {
                        Act(ActionTypes.InvokeWebhook, new
                        {
                            method = "POST", url = "https://erp.example.com/api/support-alerts", timeoutSeconds = 10, secret = "example-signing-secret-0001",
                            headers = new Dictionary<string, string> { ["X-Source"] = "SupportDesk" },
                            body = "{\"ticket\":\"{{ticket.number}}\",\"subject\":\"{{ticket.subject}}\",\"customer\":\"{{customer.name}}\",\"priority\":\"{{ticket.priority}}\"}"
                        })
                    }
                }, 50, active: false);

            // Known demo credentials for the inbound webhook (a real tenant rotates the secret from the console).
            var db = sp.GetRequiredService<DeskDbContext>();
            var rule = await db.AutomationRules.FirstAsync(r => r.Id == erp.Rule.Id, ct);
            rule.WebhookKey = DemoWebhookKey;
            rule.WebhookSecretHash = TextNormalization.Sha256Hex(DemoWebhookSecret);
            await db.SaveChangesAsync(ct);

            // Blueprint for engineering defects
            var blueprints = sp.GetRequiredService<BlueprintService>();
            var escalated = c.Statuses["Escalated to Engineering"];
            object State(string key, string name, Guid status, string? substatus, double x, double y, bool initial = false, bool final = false) =>
                new { key, name, statusId = status, substatus, x, y, isInitial = initial, isFinal = final };
            var supervisorRole = roles["Supervisor"];
            var agentRole = roles["Agent"];
            await blueprints.CreateAsync(new SaveBlueprintRequest
            {
                Name = "Defect escalation", DepartmentId = engineering, Publish = true, ChangeNote = "Demo blueprint",
                Description = "Product defects escalated from support: investigation → engineering → fix → release → customer notified → closed. " +
                              "Confirming a defect needs the Engineering Manager's approval and severity + affected version.",
                Definition = J(new
                {
                    states = new[]
                    {
                        State("investigation", "Investigation", escalated, "Investigation", 80, 160, initial: true),
                        State("engineering", "Engineering review", escalated, "Investigation", 300, 160),
                        State("fix_in_progress", "Fix in progress", escalated, "Fix in progress", 520, 160),
                        State("awaiting_release", "Awaiting release", escalated, "Awaiting release", 740, 160),
                        State("customer_notified", "Customer notified", waiting, null, 960, 160),
                        State("closed", "Closed", closed, null, 1180, 160, final: true)
                    },
                    transitions = new object[]
                    {
                        new
                        {
                            key = "confirm_defect", name = "Confirm defect", from = "investigation", to = "engineering", requireComment = true,
                            requiredFields = new[] { "severity", "versionId" }, allowedRoleIds = new[] { supervisorRole, agentRole },
                            approval = new { userId = c.Users["lakshmi"], note = "Engineering Manager approval" }
                        },
                        new
                        {
                            key = "not_a_defect", name = "Not a defect", from = "investigation", to = "customer_notified", requireComment = true,
                            actions = new[] { Act(ActionTypes.AddNote, new { bodyHtml = "<p>Engineering concluded this is not a product defect. Explain the outcome to the customer.</p>" }) }
                        },
                        new
                        {
                            key = "start_fix", name = "Start fix", from = "engineering", to = "fix_in_progress",
                            actions = new[] { Act(ActionTypes.CreateTask, new { title = "Add a regression test for {{ticket.number}}", dueInHours = 72 }) }
                        },
                        new { key = "fix_merged", name = "Fix merged", from = "fix_in_progress", to = "awaiting_release", requiredFields = new[] { "rootCause" } },
                        new
                        {
                            key = "release_shipped", name = "Release shipped", from = "awaiting_release", to = "customer_notified", requireComment = true,
                            actions = new[]
                            {
                                Act(ActionTypes.AddNote, new { bodyHtml = "<p>The fix has been released. Share the release details with {{contact.name}}.</p>" }),
                                Act(ActionTypes.NotifyAgents, new { recipients = new[] { "assignee", "watchers" }, title = "Fix released for {{ticket.number}}", message = "{{ticket.subject}}" })
                            }
                        },
                        new { key = "still_affected", name = "Customer still affected", from = "customer_notified", to = "investigation", requireComment = true },
                        new
                        {
                            key = "close", name = "Close", from = "customer_notified", to = "closed", requiredFields = new[] { "resolutionCode" },
                            allowedRoleIds = new[] { supervisorRole }
                        }
                    }
                })
            }, ct);

            // Example outbound webhook subscription (disabled — point it at a real endpoint and enable it).
            await sp.GetRequiredService<WebhookService>().CreateAsync(new SaveWebhookRequest
            {
                Name = "ERP sync (example)", Url = "https://erp.example.com/hooks/supportdesk", IsActive = false,
                Events = [EventTypes.TicketCreated, EventTypes.TicketStatusChanged, EventTypes.TicketCustomerReplied]
            }, ct);
        });

        // A defect escalated to Product Development: the blueprint is applied when the ticket.created event is dispatched.
        await c.AsSystemAsync(async sp =>
        {
            await sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
            {
                Subject = "POS freezes when more than 30 items are scanned (defect)",
                DescriptionHtml = "<p>Escalated from support ticket on Coastal Hospitals' pharmacy counter: Wings POS 3.2 freezes after the 30th scanned item. " +
                                  "Reproduced in the lab with the customer's item master.</p>",
                Channel = "Internal", DepartmentId = engineering, CustomerId = c.Customers["coastal"], ContactId = c.Contacts["rekha"],
                ProductId = c.Products["pos"], Priority = "High", CategoryId = c.Categories["Bug"], SkipRouting = true
            }, TicketCreationSource.Agent with { EnforceRequiredFields = false }, ct);
        });
    }
}
