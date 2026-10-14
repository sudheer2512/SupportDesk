using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Automation;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Automation;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;
using static SupportDesk.IntegrationTests.Automation.AutomationApi;

namespace SupportDesk.IntegrationTests.Automation;

[Collection("api")]
public sealed class RuleEngineTests(DeskApiFactory factory)
{
    private Task<List<RuleExecution>> ExecutionsAsync(Guid tenantId, Guid ticketId) =>
        factory.InTenantAsync(tenantId, sp => sp.GetRequiredService<DeskDbContext>().RuleExecutions.AsNoTracking()
            .Where(e => e.EntityId == ticketId).OrderBy(e => e.StartedAt).ToListAsync());

    private Task<int> NotesAsync(Guid tenantId, Guid ticketId, string text) =>
        factory.InTenantAsync(tenantId, sp => sp.GetRequiredService<DeskDbContext>().TicketThreads.AsNoTracking()
            .CountAsync(x => x.TicketId == ticketId && x.Kind == ThreadKind.InternalNote && x.BodyText!.Contains(text)));

    [Fact]
    public async Task Rule_fires_once_even_when_the_event_is_dispatched_twice()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var rule = await CreateRuleAsync(admin, "Acknowledge new tickets", RuleTriggers.TicketCreated, new
        {
            actions = new[] { Act(ActionTypes.AddNote, new { bodyHtml = "<p>Auto-acknowledged {{ticket.number}}</p>" }), Act(ActionTypes.AddTags, new { tags = new[] { "auto" } }) }
        });
        var ticket = await CreateTicketAsync(admin, "Printer does not print invoices");
        await factory.DrainOutboxAsync();

        // Simulate a crash after the rule ran but before the dispatcher recorded the handler's success: the event is delivered again.
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var ev = await db.OutboxEvents.FirstAsync(e => e.AggregateId == ticket.Id && e.Type == "ticket.created");
            await db.OutboxDeliveries.Where(d => d.EventId == ev.Id).ExecuteDeleteAsync();
            await db.OutboxEvents.Where(e => e.Id == ev.Id).ExecuteUpdateAsync(u => u
                .SetProperty(e => e.Status, OutboxStatus.Pending).SetProperty(e => e.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
        });
        await factory.DrainOutboxAsync();

        var executions = (await ExecutionsAsync(t.Id, ticket.Id)).Where(e => e.RuleId == rule.Rule.Id).ToList();
        var single = Assert.Single(executions);
        Assert.Equal(RuleExecutionStatus.Succeeded, single.Status);
        Assert.Equal(1, await NotesAsync(t.Id, ticket.Id, "Auto-acknowledged"));
        var detail = await GetTicketAsync(admin, ticket.Id);
        Assert.Contains("auto", detail.Tags);

        // History records what the rule did, attributed to the rule.
        var timeline = await admin.GetFromJsonAsync<List<TimelineItemDto>>($"/api/v1/tickets/{ticket.Id}/timeline", J);
        var applied = Assert.Single(timeline!, i => i.Event?.Type == TicketEventTypes.RuleApplied);
        Assert.Contains("Acknowledge new tickets", applied.Event!.Summary);
        Assert.Equal("Automation", applied.Event.ActorType);

        // The execution history API shows the run with its per-action results.
        var page = await admin.GetFromJsonAsync<PageResult<RuleExecutionDto>>($"/api/v1/automation/executions?ruleId={rule.Rule.Id}", J);
        var run = Assert.Single(page!.Items);
        Assert.Equal(2, run.Actions.Count);
        Assert.All(run.Actions, a => Assert.Equal(ActionStatus.Succeeded, a.Status));
    }

    [Fact]
    public async Task Rules_that_trigger_each_other_stop_at_the_chain_depth_limit()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ping = await CreateRuleAsync(admin, "Ping: Medium → High", RuleTriggers.TicketUpdated, new
        {
            conditions = new { logic = "and", conditions = new[] { Cond("priority", ConditionOps.Equals, "Medium") } },
            actions = new[] { Act(ActionTypes.SetPriority, new { priority = "High" }) }
        }, order: 10);
        var pong = await CreateRuleAsync(admin, "Pong: High → Medium", RuleTriggers.TicketUpdated, new
        {
            conditions = new { logic = "and", conditions = new[] { Cond("priority", ConditionOps.Equals, "High") } },
            actions = new[] { Act(ActionTypes.SetPriority, new { priority = "Medium" }) }
        }, order: 20);
        var ticket = await CreateTicketAsync(admin, "Loop test", "Medium");
        await factory.DrainOutboxAsync();

        var fresh = await GetTicketAsync(admin, ticket.Id);
        var update = await admin.PatchAsJsonAsync($"/api/v1/tickets/{ticket.Id}", new UpdateTicketRequest { RowVersion = fresh.RowVersion, Subject = "Loop test (edited)" }, J);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        await factory.DrainOutboxAsync();

        var executions = (await ExecutionsAsync(t.Id, ticket.Id)).Where(e => e.RuleId == ping.Rule.Id || e.RuleId == pong.Rule.Id).ToList();
        var settings = new Application.Platform.AutomationSettings();
        var loopStop = Assert.Single(executions, e => e.Status == RuleExecutionStatus.SkippedLoop && e.Error!.Contains("chain depth"));
        Assert.True(loopStop.ChainDepth > settings.MaxChainDepth);
        Assert.InRange(executions.Count(e => e.Status == RuleExecutionStatus.Succeeded), 2, settings.MaxChainDepth * 2);
        // A rule never re-fires on the event its own action produced.
        Assert.Contains(executions, e => e.Status == RuleExecutionStatus.SkippedLoop && e.Error!.Contains("own actions"));

        // The chain is finished: nothing for this tenant is left in the outbox.
        var pending = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().OutboxEvents
            .CountAsync(e => e.TenantId == t.Id && (e.Status == OutboxStatus.Pending || e.Status == OutboxStatus.Failed)));
        Assert.Equal(0, pending);
    }

    [Fact]
    public async Task Simulation_reports_the_trace_and_actions_without_side_effects()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var refund = await CreateTicketAsync(admin, "Refund request for invoice 42");
        var other = await CreateTicketAsync(admin, "Password reset");
        await factory.DrainOutboxAsync();
        var rule = await CreateRuleAsync(admin, "Refunds are urgent", RuleTriggers.TicketCreated, new
        {
            conditions = new { logic = "and", conditions = new[] { Cond("subject", ConditionOps.Contains, "refund") } },
            actions = new[]
            {
                Act(ActionTypes.SetPriority, new { priority = "Critical" }),
                Act(ActionTypes.AddNote, new { bodyHtml = "<p>Refund for {{ticket.number}} — check billing.</p>" }),
                Act(ActionTypes.InvokeWebhook, new { method = "POST", url = "https://billing.example.com/refunds" })
            }
        }, publish: false);

        async Task<(byte[] RowVersion, int Threads, int Events, int Outbox)> SnapshotAsync() => await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var tk = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == refund.Id);
            return (tk.RowVersion, await db.TicketThreads.CountAsync(x => x.TicketId == refund.Id), await db.TicketEvents.CountAsync(x => x.TicketId == refund.Id),
                await db.OutboxEvents.CountAsync(e => e.TenantId == t.Id));
        });
        var before = await SnapshotAsync();

        var response = await admin.PostAsJsonAsync($"/api/v1/automation/rules/{rule.Rule.Id}/simulate", new SimulateRuleRequest { LastTickets = 10 }, J);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<SimulationResultDto>(J))!;
        Assert.Equal(2, result.Evaluated);
        Assert.Equal(1, result.Matched);
        var hit = Assert.Single(result.Results, r => r.Matched);
        Assert.Equal(refund.Id, hit.TicketId);
        Assert.Contains(hit.Trace, c => c.Field == "subject" && c.Result);
        Assert.Equal(3, hit.Actions.Count);
        Assert.All(hit.Actions, a => Assert.True(a.WouldRun));
        Assert.Contains($"#{refund.Number}", hit.Actions[1].Description);
        Assert.False(Assert.Single(result.Results, r => r.TicketId == other.Id).Matched);

        // No side effects: the ticket, its conversation/history and the outbox are untouched.
        var after = await SnapshotAsync();
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Equal(before.Threads, after.Threads);
        Assert.Equal(before.Events, after.Events);
        Assert.Equal(before.Outbox, after.Outbox);
        Assert.Equal("Medium", (await GetTicketAsync(admin, refund.Id)).Priority);
        var executions = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().RuleExecutions.AsNoTracking()
            .Where(e => e.RuleId == rule.Rule.Id).ToListAsync());
        Assert.Equal(2, executions.Count);
        Assert.All(executions, e => Assert.Equal(RuleExecutionStatus.Simulated, e.Status));
    }

    [Fact]
    public async Task Scheduled_rule_acts_on_an_aged_ticket_exactly_once()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = await CreateTicketAsync(admin, "Nobody picked this up");
        var rule = await CreateRuleAsync(admin, "Unassigned for 2 hours", RuleTriggers.Scheduled, new
        {
            conditions = new
            {
                logic = "and",
                conditions = new[]
                {
                    Cond("isUnassigned", ConditionOps.Equals, true), Cond("statusCategory", ConditionOps.Equals, "Open"),
                    Cond("createdAt", ConditionOps.OlderThanMinutes, 120)
                }
            },
            actions = new[] { Act(ActionTypes.AddNote, new { bodyHtml = "<p>Unassigned for more than 2 hours.</p>" }), Act(ActionTypes.AddTags, new { tags = new[] { "stale" } }) }
        }, interval: 60);

        // Too young: nothing happens.
        await factory.RunRecurringAsync(ScheduledRulesTask.TaskName);
        Assert.DoesNotContain(await ExecutionsAsync(t.Id, ticket.Id), e => e.RuleId == rule.Rule.Id);

        factory.Clock.Pin(DateTime.UtcNow.AddHours(3));
        try
        {
            await factory.RunRecurringAsync(ScheduledRulesTask.TaskName);
            // Force the rule to be due again within the same time bucket: still executes at most once per (rule, ticket, bucket).
            await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().AutomationRules.Where(r => r.Id == rule.Rule.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.NextScheduledRunAt, (DateTime?)null)));
            await factory.RunRecurringAsync(ScheduledRulesTask.TaskName);
        }
        finally
        {
            factory.Clock.Unpin();
        }

        var runs = (await ExecutionsAsync(t.Id, ticket.Id)).Where(e => e.RuleId == rule.Rule.Id).ToList();
        var run = Assert.Single(runs);
        Assert.Equal(RuleExecutionStatus.Succeeded, run.Status);
        Assert.Equal(RuleTriggers.Scheduled, run.Trigger);
        Assert.Equal(1, await NotesAsync(t.Id, ticket.Id, "Unassigned for more than 2 hours"));
        Assert.Contains("stale", (await GetTicketAsync(admin, ticket.Id)).Tags);
    }

    [Fact]
    public async Task Inbound_webhook_runs_the_rule_against_the_referenced_ticket()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = await CreateTicketAsync(admin, "Payment pending for AMC renewal");
        var rule = await CreateRuleAsync(admin, "ERP payment received", RuleTriggers.Webhook, new
        {
            conditions = new { logic = "and", conditions = new[] { Cond("webhook.event", ConditionOps.Equals, "payment.received") } },
            actions = new[] { Act(ActionTypes.AddNote, new { bodyHtml = "<p>Payment {{webhook.reference}} received.</p>" }) }
        });
        Assert.NotNull(rule.WebhookSecret);
        var url = rule.Rule.WebhookUrl!;
        var anonymous = factory.CreateClient();

        var wrong = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { ticketNumber = ticket.Number, @event = "payment.received" }) };
        wrong.Headers.Add("X-Webhook-Secret", "not-the-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(wrong)).StatusCode);

        for (var i = 0; i < 2; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { ticketNumber = ticket.Number, @event = "payment.received", reference = "RCPT-77" })
            };
            request.Headers.Add("X-Webhook-Secret", rule.WebhookSecret);
            request.Headers.Add("Idempotency-Key", "erp-delivery-1");
            var response = await anonymous.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<InboundWebhookResultDto>(J))!;
            Assert.Equal(ticket.Id, result.TicketId);
            Assert.Equal("Succeeded", result.Status);
        }
        Assert.Equal(1, await NotesAsync(t.Id, ticket.Id, "Payment RCPT-77 received"));
    }

    [Fact]
    public async Task Publishing_validates_the_definition_and_conflicts_are_reported()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var invalid = await admin.PostAsJsonAsync("/api/v1/automation/rules", new SaveRuleRequest
        {
            Name = "Broken", Trigger = RuleTriggers.TicketCreated, Publish = true,
            Definition = Definition(new { actions = new[] { Act(ActionTypes.SetStatus, new { statusId = Guid.NewGuid() }) } })
        }, J);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("statusId", await invalid.Content.ReadAsStringAsync());

        await CreateRuleAsync(admin, "High for phone", RuleTriggers.TicketCreated,
            new { conditions = new { conditions = new[] { Cond("channel", ConditionOps.Equals, "Phone") } }, actions = new[] { Act(ActionTypes.SetPriority, new { priority = "High" }) } }, order: 10);
        var low = await CreateRuleAsync(admin, "Low for everything", RuleTriggers.TicketCreated,
            new { actions = new[] { Act(ActionTypes.SetPriority, new { priority = "Low" }) } }, order: 20);
        Assert.Contains(low.Conflicts, c => c.Kind == ConflictKinds.FieldConflict && c.Field == "priority");
        var all = await admin.GetFromJsonAsync<List<RuleConflictDto>>("/api/v1/automation/conflicts", J);
        Assert.Single(all!);

        // Versions: a new definition creates a draft; publishing supersedes; roll back republishes v1 as v3.
        var updated = await admin.PutAsJsonAsync($"/api/v1/automation/rules/{low.Rule.Id}", new SaveRuleRequest
        {
            Name = "Low for everything", Trigger = RuleTriggers.TicketCreated, Definition = Definition(new { actions = new[] { Act(ActionTypes.SetPriority, new { priority = "Medium" }) } })
        }, J);
        var detail = (await updated.Content.ReadFromJsonAsync<RuleDetailDto>(J))!;
        Assert.Equal(2, detail.Rule.LatestVersionNumber);
        Assert.Equal(1, detail.Rule.PublishedVersionNumber);
        Assert.True(detail.Rule.HasUnpublishedDraft);
        var v1 = detail.Versions.Single(v => v.VersionNumber == 1).Id;
        var v2 = detail.Versions.Single(v => v.VersionNumber == 2).Id;
        detail = (await (await admin.PostAsJsonAsync($"/api/v1/automation/rules/{low.Rule.Id}/versions/{v2}/publish", new PublishVersionRequest(null), J))
            .Content.ReadFromJsonAsync<RuleDetailDto>(J))!;
        Assert.Equal(2, detail.Rule.PublishedVersionNumber);
        detail = (await (await admin.PostAsJsonAsync($"/api/v1/automation/rules/{low.Rule.Id}/versions/{v1}/rollback", new PublishVersionRequest(null), J))
            .Content.ReadFromJsonAsync<RuleDetailDto>(J))!;
        Assert.Equal(3, detail.Rule.PublishedVersionNumber);
        Assert.Equal("Superseded", detail.Versions.Single(v => v.VersionNumber == 2).State);

        var catalog = await admin.GetFromJsonAsync<AutomationCatalogDto>("/api/v1/automation/catalog", J);
        Assert.Contains(catalog!.Actions, a => a.Type == ActionTypes.InvokeWebhook && a.Params.Any(p => p.Name == "url" && p.Required));
        Assert.Contains(catalog.Fields, f => f.Field == "customer.tier");
    }
}
