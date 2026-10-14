using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Approvals;
using SupportDesk.Application.Automation;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Automation;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;
using static SupportDesk.IntegrationTests.Automation.AutomationApi;

namespace SupportDesk.IntegrationTests.Automation;

[Collection("api")]
public sealed class BlueprintTests(DeskApiFactory factory)
{
    [Fact]
    public async Task Transition_refuses_missing_fields_and_waits_for_approval_before_completing()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var (supervisorId, supervisorEmail) = await t.CreateAgentAsync("Sam Supervisor", "Supervisor");
        var statuses = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketStatuses.AsNoTracking()
            .ToDictionaryAsync(s => s.Name, s => s.Id));

        var create = await admin.PostAsJsonAsync("/api/v1/automation/blueprints", new SaveBlueprintRequest
        {
            Name = "Incident handling", DepartmentId = t.SupportDepartmentId, Publish = true,
            Definition = Definition(new
            {
                states = new object[]
                {
                    new { key = "new", name = "New", statusId = statuses["Open"], isInitial = true },
                    new { key = "investigating", name = "Investigating", statusId = statuses["In Progress"] },
                    new { key = "done", name = "Done", statusId = statuses["Resolved"], isFinal = true }
                },
                transitions = new object[]
                {
                    new { key = "start", name = "Start investigation", from = "new", to = "investigating", requiredFields = new[] { "severity" } },
                    new
                    {
                        key = "resolve", name = "Resolve", from = "investigating", to = "done", requireComment = true,
                        approval = new { userId = supervisorId },
                        actions = new[] { Act(ActionTypes.AddNote, new { bodyHtml = "<p>Resolved through the blueprint for {{ticket.number}}.</p>" }) }
                    }
                }
            })
        }, J);
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());

        var ticket = await CreateTicketAsync(admin, "Database connection drops every hour");
        await factory.DrainOutboxAsync(); // ticket.created → blueprint assignment

        var bp = await admin.GetFromJsonAsync<TicketBlueprintDto>($"/api/v1/tickets/{ticket.Id}/blueprint", J);
        Assert.NotNull(bp);
        Assert.Equal("new", bp!.CurrentState.Key);
        var start = Assert.Single(bp.Transitions);
        Assert.Equal(["severity"], start.MissingFields);
        Assert.Equal("Open", (await GetTicketAsync(admin, ticket.Id)).Status);

        // Mandatory field missing → refused.
        var refused = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/blueprint/transitions/start", new ExecuteTransitionRequest(), J);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("missing_required_fields", await refused.Content.ReadAsStringAsync());

        // Supplying the field with the transition completes it.
        var started = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/blueprint/transitions/start",
            new ExecuteTransitionRequest { Fields = new Dictionary<string, JsonElement> { ["severity"] = JsonSerializer.SerializeToElement("Major") } }, J);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var startedResult = (await started.Content.ReadFromJsonAsync<TransitionResultDto>(J))!;
        Assert.Equal("completed", startedResult.Status);
        Assert.Equal("investigating", startedResult.StateKey);
        var detail = await GetTicketAsync(admin, ticket.Id);
        Assert.Equal("In Progress", detail.Status);
        Assert.Equal("Major", detail.Severity);

        // The approval-gated transition needs a comment, then stays pending.
        var noComment = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/blueprint/transitions/resolve", new ExecuteTransitionRequest(), J);
        Assert.Equal(HttpStatusCode.BadRequest, noComment.StatusCode);
        var requested = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/blueprint/transitions/resolve",
            new ExecuteTransitionRequest { Comment = "Connection pool size fixed" }, J);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var pending = (await requested.Content.ReadFromJsonAsync<TransitionResultDto>(J))!;
        Assert.Equal("pending_approval", pending.Status);
        Assert.NotNull(pending.ApprovalId);

        bp = await admin.GetFromJsonAsync<TicketBlueprintDto>($"/api/v1/tickets/{ticket.Id}/blueprint", J);
        Assert.Equal("investigating", bp!.CurrentState.Key);
        Assert.Equal(pending.ApprovalId, bp.PendingApproval?.ApprovalId);
        Assert.All(bp.Transitions, tr => Assert.False(tr.CanExecute));
        Assert.Equal("In Progress", (await GetTicketAsync(admin, ticket.Id)).Status);
        var again = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/blueprint/transitions/resolve", new ExecuteTransitionRequest { Comment = "again" }, J);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // The named approver approves → the transition completes (status, history, after-action).
        var supervisor = await t.AgentClientAsync(supervisorEmail);
        var decided = await supervisor.PostAsJsonAsync($"/api/v1/approvals/{pending.ApprovalId}/decide", new DecideApprovalRequest(true, "Looks good"), J);
        Assert.True(decided.StatusCode == HttpStatusCode.OK, await decided.Content.ReadAsStringAsync());

        bp = await admin.GetFromJsonAsync<TicketBlueprintDto>($"/api/v1/tickets/{ticket.Id}/blueprint", J);
        Assert.Equal("done", bp!.CurrentState.Key);
        Assert.Null(bp.PendingApproval);
        Assert.Empty(bp.Transitions);
        detail = await GetTicketAsync(admin, ticket.Id);
        Assert.Equal("Resolved", detail.StatusCategory);
        var timeline = await admin.GetFromJsonAsync<List<TimelineItemDto>>($"/api/v1/tickets/{ticket.Id}/timeline", J);
        Assert.Contains(timeline!, i => i.Event?.Type == TicketEventTypes.WorkflowTransition && i.Event.Summary!.Contains("approved by Sam Supervisor"));
        Assert.Contains(timeline!, i => i.Thread?.BodyText?.Contains("Resolved through the blueprint") == true);

        // Manual status changes are refused for blueprint tickets (for agents without admin.automation).
        var agent = await t.CreateAgentAsync("Ana Agent");
        var agentClient = await t.AgentClientAsync(agent.Email);
        var manual = await agentClient.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/status", new ChangeStatusRequest(statuses["Open"], null, null, null), J);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, manual.StatusCode);

        // Removing a ticket from its blueprint needs admin.automation.
        Assert.Equal(HttpStatusCode.Forbidden, (await agentClient.DeleteAsync($"/api/v1/tickets/{ticket.Id}/blueprint")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tickets/{ticket.Id}/blueprint")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.GetAsync($"/api/v1/tickets/{ticket.Id}/blueprint")).StatusCode);
    }

    [Fact]
    public async Task Publishing_an_invalid_blueprint_is_refused()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var response = await admin.PostAsJsonAsync("/api/v1/automation/blueprints", new SaveBlueprintRequest
        {
            Name = "Broken", Publish = true,
            Definition = Definition(new
            {
                states = new object[]
                {
                    new { key = "a", name = "A", statusId = Guid.NewGuid(), isInitial = true },
                    new { key = "b", name = "B", isFinal = true },
                    new { key = "island", name = "Island", isFinal = true }
                },
                transitions = new object[] { new { key = "go", name = "Go", from = "a", to = "b", allowedRoleIds = new[] { Guid.NewGuid() } } }
            })
        }, J);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("status does not exist", body);
        Assert.Contains("cannot be reached", body);
        Assert.Contains("allowedRoleIds", body);
    }
}
