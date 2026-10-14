using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Sla;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Organisation;
using SupportDesk.Contracts.Sla;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;

namespace SupportDesk.IntegrationTests.Organisation;

/// <summary>
/// Organisation module: invitations, deactivation, plan seats, routing eligibility (leave, shifts, capacity),
/// delegation of approvals, business-time preview, SLA policy re-targeting and escalation, monitor data scope and
/// tenant isolation. Times are IST (UTC+05:30) unless noted.
/// </summary>
[Collection("api")]
public sealed class OrganisationTests(DeskApiFactory factory)
{
    private static readonly JsonSerializerOptions J = DeskApiFactory.Json;

    private static DateTime Ist(int month, int day, int hour, int minute = 0) => new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, J)!;
    }

    private static async Task<string> CodeOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? "" : body;
    }

    private Task<Ticket> CreateTicketAsync(Guid tenant, string subject, string priority = "Medium", Guid? department = null, Guid? assignee = null, bool route = false) =>
        factory.InTenantAsync(tenant, sp => sp.GetRequiredService<TicketService>().CreateAsync(new CreateTicketRequest
        {
            Subject = subject, Channel = "Email", Priority = priority, DepartmentId = department, AssigneeId = assignee, SkipRouting = !route,
            NewContact = new NewContactDto("Customer", $"c{Guid.NewGuid():N}@x.test", null, null)
        }, new TicketCreationSource(CreateInitialThread: true, EnforceRequiredFields: false), default));

    private async Task AssertJobsSucceededAsync(Guid tenant, string type)
    {
        var jobs = await factory.InTenantAsync(tenant, sp => sp.GetRequiredService<DeskDbContext>().BackgroundJobs.AsNoTracking()
            .Where(j => j.TenantId == tenant && j.Type == type).Select(j => new { j.Status, j.Error, j.RunAt }).ToListAsync());
        Assert.NotEmpty(jobs);
        Assert.All(jobs, j => Assert.True(j.Status == Domain.Platform.JobStatus.Succeeded, $"{type}: {j.Status} {j.Error} (run at {j.RunAt:O})"));
    }

    private Task<Ticket> LoadTicketAsync(Guid tenant, Guid id) =>
        factory.InTenantAsync(tenant, sp => sp.GetRequiredService<DeskDbContext>().Tickets.AsNoTracking().FirstAsync(t => t.Id == id));

    // ───────────────────────────── Invitations ─────────────────────────────

    [Fact]
    public async Task Invitation_flow_sets_password_and_allows_sign_in()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<TenantSettingsService>().SetAsync(SettingKeys.Security, new SecuritySettings { DemoMode = true }));
        var admin = await tenant.AdminClientAsync();
        var email = $"new.agent.{Guid.NewGuid():N}"[..20] + $"@{tenant.Slug}.test";

        var invite = await Ok<InviteResultDto>(await admin.PostAsJsonAsync("/api/v1/org/agents", new InviteAgentRequest
        {
            Email = email, Name = "Nina Joseph", Title = "Support Engineer", DepartmentIds = [tenant.SupportDepartmentId]
        }, J));
        Assert.Equal("Invited", invite.Agent.Status);
        Assert.False(string.IsNullOrEmpty(invite.DemoCode));
        Assert.Contains("accept-invite", invite.AcceptUrl);
        Assert.Contains(invite.Agent.Roles, r => r.Name == "Agent");

        // Cannot sign in before accepting.
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.LoginAsync(tenant.Slug, email, DeskApiFactory.Password));

        var anonymous = factory.CreateClient();
        var wrong = await anonymous.PostAsJsonAsync("/api/v1/org/public/invitations/accept", new AcceptInvitationRequest(tenant.Slug, email, "AAAA-AAAA", DeskApiFactory.Password), J);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("invitation_invalid", await CodeOf(wrong));

        var weak = await anonymous.PostAsJsonAsync("/api/v1/org/public/invitations/accept", new AcceptInvitationRequest(tenant.Slug, email, invite.DemoCode!, "short"), J);
        Assert.False(weak.IsSuccessStatusCode);

        var accepted = await Ok<AcceptInvitationResultDto>(await anonymous.PostAsJsonAsync("/api/v1/org/public/invitations/accept",
            new AcceptInvitationRequest(tenant.Slug, email.ToUpperInvariant(), invite.DemoCode!.ToLowerInvariant(), DeskApiFactory.Password, "Nina J."), J));
        Assert.Equal("Nina J.", accepted.Name);

        var token = await factory.LoginAsync(tenant.Slug, email);
        Assert.False(string.IsNullOrEmpty(token));
        var detail = await Ok<AgentDetailDto>(await admin.GetAsync($"/api/v1/org/agents/{invite.Agent.UserId}"));
        Assert.Equal("Active", detail.Status);
        Assert.Null(detail.Invitation);

        // The code is single-use.
        var again = await anonymous.PostAsJsonAsync("/api/v1/org/public/invitations/accept", new AcceptInvitationRequest(tenant.Slug, email, invite.DemoCode!, DeskApiFactory.Password), J);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    // ───────────────────────────── Deactivation ─────────────────────────────

    [Fact]
    public async Task Deactivated_agent_cannot_sign_in_and_open_tickets_are_reassigned()
    {
        var tenant = await factory.CreateTenantAsync();
        var (leaver, leaverEmail) = await tenant.CreateAgentAsync("Leaver");
        var (heir, _) = await tenant.CreateAgentAsync("Heir");
        var leaverToken = await factory.LoginAsync(tenant.Slug, leaverEmail);
        var t1 = await CreateTicketAsync(tenant.Id, "Owned 1", assignee: leaver);
        var t2 = await CreateTicketAsync(tenant.Id, "Owned 2", assignee: leaver);
        var admin = await tenant.AdminClientAsync();

        var result = await Ok<DeactivateAgentResultDto>(await admin.PostAsJsonAsync($"/api/v1/org/agents/{leaver}/deactivate",
            new DeactivateAgentRequest("reassign", heir, "Left the company"), J));
        Assert.Equal("Deactivated", result.Agent.Status);
        Assert.Equal(2, result.OpenTickets);

        // Existing sessions are revoked and new sign-ins fail.
        var stale = factory.ClientWithToken(leaverToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/v1/me")).StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.LoginAsync(tenant.Slug, leaverEmail));

        await factory.DrainJobsAsync();
        await AssertJobsSucceededAsync(tenant.Id, "org.agent-offboard");
        Assert.Equal(heir, (await LoadTicketAsync(tenant.Id, t1.Id)).AssigneeId);
        Assert.Equal(heir, (await LoadTicketAsync(tenant.Id, t2.Id)).AssigneeId);

        // Reactivation restores access.
        await Ok<AgentDetailDto>(await admin.PostAsync($"/api/v1/org/agents/{leaver}/reactivate", null));
        Assert.False(string.IsNullOrEmpty(await factory.LoginAsync(tenant.Slug, leaverEmail)));
    }

    // ───────────────────────────── Plan limits ─────────────────────────────

    [Fact]
    public async Task Plan_limits_cap_agents_and_light_agents()
    {
        var tenant = await factory.CreateTenantAsync();
        await tenant.CreateAgentAsync("Second");
        await factory.InTenantAsync(tenant.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var sub = await db.Subscriptions.FirstAsync();
            sub.LimitOverridesJson = "{\"MaxAgents\":2,\"MaxLightAgents\":0}";
            await db.SaveChangesAsync();
        });
        var admin = await tenant.AdminClientAsync();
        var seats = await Ok<SeatUsageDto>(await admin.GetAsync("/api/v1/org/agents/seats"));
        Assert.Equal(2, seats.Agents);
        Assert.Equal(2, seats.MaxAgents);
        Assert.Equal(0, seats.MaxLightAgents);

        var full = await admin.PostAsJsonAsync("/api/v1/org/agents", new InviteAgentRequest
        {
            Email = $"third@{tenant.Slug}.test", Name = "Third Agent", DepartmentIds = [tenant.SupportDepartmentId]
        }, J);
        Assert.Equal(HttpStatusCode.PaymentRequired, full.StatusCode);
        Assert.Equal("limit_exceeded", await CodeOf(full));

        var light = await admin.PostAsJsonAsync("/api/v1/org/agents", new InviteAgentRequest
        {
            Email = $"light@{tenant.Slug}.test", Name = "Light Agent", IsLightAgent = true, DepartmentIds = [tenant.SupportDepartmentId]
        }, J);
        Assert.Equal(HttpStatusCode.PaymentRequired, light.StatusCode);
    }

    // ───────────────────────────── Routing eligibility ─────────────────────────────

    [Fact]
    public async Task Routing_skips_agents_on_leave_off_shift_and_at_capacity()
    {
        var tenant = await factory.CreateTenantAsync();
        var clock = factory.Clock;
        try
        {
            clock.Pin(Ist(10, 12, 10)); // Monday 10:00 IST
            var (onLeave, _) = await tenant.CreateAgentAsync("On Leave");
            var (offShift, _) = await tenant.CreateAgentAsync("Off Shift");
            var (full, _) = await tenant.CreateAgentAsync("Full");
            var (free, _) = await tenant.CreateAgentAsync("Free");
            var admin = await tenant.AdminClientAsync();

            // Capacity: "Full" may hold one ticket and already has one.
            await factory.InTenantAsync(tenant.Id, async sp =>
            {
                var db = sp.GetRequiredService<DeskDbContext>();
                (await db.Agents.FirstAsync(a => a.UserId == full)).MaxOpenTickets = 1;
                await db.SaveChangesAsync();
            });
            await CreateTicketAsync(tenant.Id, "Existing load", assignee: full);

            // Leave: recorded (and approved) by the administrator.
            var leave = await Ok<LeaveDto>(await admin.PostAsJsonAsync("/api/v1/org/leaves",
                new CreateLeaveRequest(onLeave, Ist(10, 12, 0), Ist(10, 14, 0), "Leave", "Holiday"), J));
            Assert.Equal("Approved", leave.Status);

            // Shift: evenings only.
            var shift = await Ok<ShiftDto>(await admin.PostAsJsonAsync("/api/v1/org/shifts", new SaveShiftRequest
            {
                Name = "Evening", TimeZone = "Asia/Kolkata",
                WeeklyHours = new Dictionary<string, IReadOnlyList<WorkIntervalDto>> { ["Monday"] = [new WorkIntervalDto("18:00", "23:00")] }
            }, J));
            await Ok<List<ShiftAssignmentDto>>(await admin.PostAsJsonAsync("/api/v1/org/shift-assignments",
                new AssignShiftRequest(shift.Id, [offShift], new DateOnly(2026, 10, 1), null), J));

            // The department's routing team considers shifts (load balanced).
            var teams = await Ok<List<TeamDto>>(await admin.GetAsync($"/api/v1/org/teams?departmentId={tenant.SupportDepartmentId}"));
            var team = teams.Single(t => t.IsDepartmentDefault);
            await Ok<TeamDto>(await admin.PutAsJsonAsync($"/api/v1/org/teams/{team.Id}/routing",
                new { mode = "LoadBalanced", onlyOnlineAgents = true, considerShifts = true, maxOpenTicketsPerAgent = (int?)null, fallbackTeamId = (Guid?)null, rowVersion = team.RowVersion }, J));

            // The routing preview endpoint works (real clock) …
            await Ok<RoutingPreviewDto>(await admin.PostAsJsonAsync("/api/v1/org/routing/preview", new RoutingPreviewRequest(tenant.SupportDepartmentId), J));
            // … and on Monday 10:00 IST (pinned clock: services are called in this flow) it explains every exclusion.
            var preview = await factory.InTenantAsync(tenant.Id, sp =>
                sp.GetRequiredService<Application.Organisation.TeamService>().PreviewAsync(new RoutingPreviewRequest(tenant.SupportDepartmentId)));
            Assert.Equal(free, preview.AssigneeId);
            Assert.Equal("On leave", preview.Candidates.Single(c => c.UserId == onLeave).ExcludedReason);
            Assert.Equal("Off shift", preview.Candidates.Single(c => c.UserId == offShift).ExcludedReason);
            Assert.StartsWith("At capacity", preview.Candidates.Single(c => c.UserId == full).ExcludedReason);

            var routed = await CreateTicketAsync(tenant.Id, "Route me", route: true);
            Assert.Equal(free, routed.AssigneeId);

            // In the evening the off-shift agent becomes eligible (lowest load wins).
            clock.Pin(Ist(10, 12, 19));
            var evening = await CreateTicketAsync(tenant.Id, "Evening ticket", route: true);
            Assert.Equal(offShift, evening.AssigneeId);
        }
        finally
        {
            clock.Unpin();
        }
    }

    // ───────────────────────────── Delegation ─────────────────────────────

    [Fact]
    public async Task Delegation_routes_leave_approvals_to_the_delegate()
    {
        var tenant = await factory.CreateTenantAsync();
        var (supervisor, supervisorEmail) = await tenant.CreateAgentAsync("Sup Ervisor", "Supervisor");
        var (requester, requesterEmail) = await tenant.CreateAgentAsync("Req Uester");
        var (delegateId, delegateEmail) = await tenant.CreateAgentAsync("Dele Gate");
        await factory.InTenantAsync(tenant.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            (await db.Agents.FirstAsync(a => a.UserId == requester)).ReportsToUserId = supervisor;
            await db.SaveChangesAsync();
        });
        var now = factory.Clock.UtcNow;

        var sup = await tenant.AgentClientAsync(supervisorEmail);
        var delegation = await Ok<DelegationDto>(await sup.PostAsJsonAsync("/api/v1/org/delegations",
            new SaveDelegationRequest(null, delegateId, now.AddHours(-1), now.AddDays(3), "Conference", false), J));
        Assert.Equal("Active", delegation.State);

        var req = await tenant.AgentClientAsync(requesterEmail);
        var leave = await Ok<LeaveDto>(await req.PostAsJsonAsync("/api/v1/org/leaves",
            new CreateLeaveRequest(null, now.AddDays(7), now.AddDays(8), "Leave", "Wedding"), J));
        Assert.Equal("Requested", leave.Status);
        Assert.NotNull(leave.ApprovalRequestId);
        await factory.DrainOutboxAsync();

        var approver = await factory.InTenantAsync(tenant.Id, sp =>
            sp.GetRequiredService<DeskDbContext>().ApprovalRequests.AsNoTracking().Where(a => a.Id == leave.ApprovalRequestId).Select(a => a.ApproverUserId).FirstAsync());
        Assert.Equal(delegateId, approver);

        // The delegate (a plain agent) can decide because the request is now addressed to them.
        var del = await tenant.AgentClientAsync(delegateEmail);
        var inbox = await Ok<List<JsonElement>>(await del.GetAsync("/api/v1/approvals?status=Pending"));
        Assert.Contains(inbox, a => a.GetProperty("id").GetGuid() == leave.ApprovalRequestId);
        var decided = await Ok<LeaveDto>(await del.PostAsJsonAsync($"/api/v1/org/leaves/{leave.Id}/approve", new DecideLeaveRequest("Enjoy"), J));
        Assert.Equal("Approved", decided.Status);

        // The requester cannot approve their own leave.
        var own = await req.PostAsJsonAsync($"/api/v1/org/leaves/{leave.Id}/reject", new DecideLeaveRequest(null), J);
        Assert.False(own.IsSuccessStatusCode);
    }

    // ───────────────────────────── Calendars ─────────────────────────────

    [Fact]
    public async Task Business_time_preview_skips_weekend_and_holiday()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await tenant.AdminClientAsync();
        var weekdays = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" }
            .ToDictionary(d => d, _ => (IReadOnlyList<WorkIntervalDto>)[new WorkIntervalDto("09:00", "18:00")]);
        var cal = await Ok<BusinessCalendarDto>(await admin.PostAsJsonAsync("/api/v1/sla/calendars",
            new SaveBusinessCalendarRequest { Name = "IST weekdays", TimeZone = "Asia/Kolkata", WeeklyHours = weekdays }, J));
        Assert.Equal(45, cal.WeeklyHoursTotal);

        var import = await Ok<ImportHolidaysResultDto>(await admin.PostAsJsonAsync($"/api/v1/sla/calendars/{cal.Id}/holidays/import",
            new ImportHolidaysRequest("date,name,recurring\n2026-10-19,Regional holiday,no\n19/10/2026,Duplicate,no\nnot-a-date,Bad"), J));
        Assert.Equal(1, import.Added);
        Assert.Equal(2, import.Errors.Count);

        // Friday 16 Oct 17:00 → Tuesday 20 Oct 10:00: Friday 60 min + (weekend, Monday holiday) + Tuesday 60 min.
        var preview = await Ok<BusinessTimePreviewDto>(await admin.PostAsJsonAsync($"/api/v1/sla/calendars/{cal.Id}/preview",
            new BusinessTimePreviewRequest(Ist(10, 16, 17), Ist(10, 20, 10), 90), J));
        Assert.Equal(120, preview.BusinessMinutes);
        Assert.Equal(Ist(10, 20, 9, 30), preview.DueAt);
        Assert.Single(preview.HolidaysInRange);
        Assert.Equal(2, preview.Intervals.Count);
    }

    // ───────────────────────────── SLA policies ─────────────────────────────

    private static SaveSlaPolicyRequest ToRequest(SlaPolicyDto p) => new()
    {
        Name = p.Name, Description = p.Description, IsActive = p.IsActive, IsDefault = p.IsDefault, Conditions = p.Conditions, CalendarId = p.CalendarId,
        UseContractCalendar = p.UseContractCalendar, PauseStatusIds = p.PauseStatusIds, Targets = p.Targets, Escalations = p.Escalations, RowVersion = p.RowVersion
    };

    [Fact]
    public async Task Changing_a_policy_retargets_open_tickets_keeping_elapsed_time()
    {
        var tenant = await factory.CreateTenantAsync();
        var clock = factory.Clock;
        try
        {
            clock.Pin(Ist(10, 12, 10)); // Monday 10:00 — High: 60 min first response, 480 business minutes resolution
            var ticket = await CreateTicketAsync(tenant.Id, "Retarget me", "High");
            Assert.Equal(Ist(10, 12, 18), ticket.DueAt);

            clock.Pin(Ist(10, 12, 10, 30));
            var admin = await tenant.AdminClientAsync();
            var policy = await Ok<SlaPolicyDto>(await admin.GetAsync($"/api/v1/sla/policies/{tenant.Provisioned.DefaultSlaPolicyId}"));
            var request = ToRequest(policy) with
            {
                Targets = policy.Targets.Select(t => t.Priority == "High" ? t with { FirstResponseMinutes = 120, ResolutionMinutes = 240 } : t).ToList()
            };
            // Saved through the service so the re-target job is queued at the pinned time (the HTTP host uses the real clock).
            var saved = await factory.InTenantAsync(tenant.Id, sp => sp.GetRequiredService<SlaPolicyAdminService>().UpdateAsync(policy.Id, request));
            Assert.Equal(policy.Version + 1, saved.Policy.Version);
            Assert.True(saved.RetargetQueued >= 1);

            // Stale rowVersion → 409.
            var stale = await admin.PutAsJsonAsync($"/api/v1/sla/policies/{policy.Id}", request, J);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

            await factory.DrainJobsAsync();
            await AssertJobsSucceededAsync(tenant.Id, "sla.retarget");
            var after = await LoadTicketAsync(tenant.Id, ticket.Id);
            Assert.Equal(Ist(10, 12, 12), after.FirstResponseDueAt); // 30 elapsed + 90 remaining
            Assert.Equal(Ist(10, 12, 14), after.DueAt);              // 30 elapsed + 210 remaining
            var events = await factory.InTenantAsync(tenant.Id, sp =>
                sp.GetRequiredService<DeskDbContext>().SlaEvents.AsNoTracking().Where(e => e.TicketId == ticket.Id).Select(e => e.Type).ToListAsync());
            Assert.Contains(SlaEventTypes.PolicyChanged, events);

            // Simulation reports the same due dates for the saved policy.
            var sim = await Ok<SlaSimulationDto>(await admin.PostAsJsonAsync("/api/v1/sla/simulate", new SimulateSlaRequest(ticket.Id, null), J));
            Assert.Equal(policy.Id, sim.SelectedPolicyId);
            Assert.False(sim.WouldChange);
        }
        finally
        {
            clock.Unpin();
        }
    }

    [Fact]
    public async Task Escalation_levels_fire_at_percent_of_target()
    {
        var tenant = await factory.CreateTenantAsync();
        var clock = factory.Clock;
        try
        {
            var (backup, _) = await tenant.CreateAgentAsync("Backup");
            var admin = await tenant.AdminClientAsync();
            var policy = await Ok<SlaPolicyDto>(await admin.GetAsync($"/api/v1/sla/policies/{tenant.Provisioned.DefaultSlaPolicyId}"));
            var request = ToRequest(policy) with
            {
                Escalations =
                [
                    new SlaEscalationDto("FirstResponse", 1, 0, 80, "NotifyAndReassign", JsonDocument.Parse("{\"notifySupervisors\":true}").RootElement, backup, null),
                    new SlaEscalationDto("Resolution", 1, 0, null, "Notify", JsonDocument.Parse("{}").RootElement, null, null)
                ]
            };
            await Ok<SaveSlaPolicyResultDto>(await admin.PutAsJsonAsync($"/api/v1/sla/policies/{policy.Id}", request, J));

            clock.Pin(Ist(10, 13, 11)); // Critical: 15 min first response, 24x7
            var ticket = await CreateTicketAsync(tenant.Id, "Escalate me", "Critical");

            clock.Pin(Ist(10, 13, 11, 10)); // 67 % — nothing yet
            await factory.RunRecurringAsync("sla-monitor");
            Assert.False((await LoadTicketAsync(tenant.Id, ticket.Id)).IsEscalated);

            clock.Pin(Ist(10, 13, 11, 13)); // 87 % — level 1 fires before the breach
            await factory.RunRecurringAsync("sla-monitor");
            var escalated = await LoadTicketAsync(tenant.Id, ticket.Id);
            Assert.True(escalated.IsEscalated);
            Assert.Equal(1, escalated.EscalationLevel);
            Assert.Equal(backup, escalated.AssigneeId);
            var types = await factory.InTenantAsync(tenant.Id, sp =>
                sp.GetRequiredService<DeskDbContext>().SlaEvents.AsNoTracking().Where(e => e.TicketId == ticket.Id).Select(e => e.Type).ToListAsync());
            Assert.Contains(SlaEventTypes.Escalated, types);
        }
        finally
        {
            clock.Unpin();
        }
    }

    // ───────────────────────────── Monitor & isolation ─────────────────────────────

    [Fact]
    public async Task Monitor_respects_data_scope()
    {
        var tenant = await factory.CreateTenantAsync();
        var (_, supervisorEmail) = await tenant.CreateAgentAsync("Scope Supervisor", "Supervisor", tenant.SupportDepartmentId);
        var (billingAgent, _) = await tenant.CreateAgentAsync("Billing Agent", "Agent", tenant.BillingDepartmentId);
        var (_, agentEmail) = await tenant.CreateAgentAsync("Plain Agent");
        await CreateTicketAsync(tenant.Id, "Support queue", department: tenant.SupportDepartmentId);
        await CreateTicketAsync(tenant.Id, "Billing queue", department: tenant.BillingDepartmentId);

        var sup = await tenant.AgentClientAsync(supervisorEmail);
        var snapshot = await Ok<MonitorSnapshotDto>(await sup.GetAsync("/api/v1/org/monitor"));
        Assert.Equal([tenant.SupportDepartmentId], snapshot.Departments.Select(d => d.Id).ToList());
        Assert.All(snapshot.Queues, q => Assert.Equal(tenant.SupportDepartmentId, q.DepartmentId));
        Assert.Equal(1, snapshot.OpenTickets);
        Assert.DoesNotContain(snapshot.Agents, a => a.UserId == billingAgent);

        Assert.Equal(HttpStatusCode.NotFound, (await sup.GetAsync($"/api/v1/org/monitor?departmentId={tenant.BillingDepartmentId}")).StatusCode);
        var agent = await tenant.AgentClientAsync(agentEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/v1/org/monitor")).StatusCode);

        var admin = await tenant.AdminClientAsync();
        var all = await Ok<MonitorSnapshotDto>(await admin.GetAsync("/api/v1/org/monitor"));
        Assert.Equal(2, all.OpenTickets);
        Assert.Contains(all.Agents, a => a.UserId == billingAgent);
    }

    [Fact]
    public async Task Organisation_data_is_isolated_between_tenants()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var adminA = await a.AdminClientAsync();
        var adminB = await b.AdminClientAsync();
        var skill = await Ok<SkillDto>(await adminA.PostAsJsonAsync("/api/v1/org/skills", new SaveSkillRequest("Payroll", "Product", null), J));
        var (agentA, _) = await a.CreateAgentAsync("Tenant A Agent");

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/org/departments/{a.SupportDepartmentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/sla/policies/{a.Provisioned.DefaultSlaPolicyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/sla/calendars/{a.Provisioned.DefaultCalendarId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"/api/v1/org/skills/{skill.Id}", new SaveSkillRequest("Hijack", null, null), J)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/org/agents/{agentA}")).StatusCode);
        var listB = await Ok<PagedResult<AgentListItemDto>>(await adminB.GetAsync("/api/v1/org/agents"));
        Assert.DoesNotContain(listB.Items, x => x.UserId == agentA);
        Assert.DoesNotContain(await Ok<List<SkillDto>>(await adminB.GetAsync("/api/v1/org/skills")), s => s.Id == skill.Id);
        var monitorB = await Ok<MonitorSnapshotDto>(await adminB.GetAsync("/api/v1/org/monitor"));
        Assert.DoesNotContain(monitorB.Agents, x => x.UserId == agentA);
    }

    [Fact]
    public async Task Department_archive_requires_reassignment_of_open_tickets()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await tenant.AdminClientAsync();
        var created = await Ok<DepartmentDto>(await admin.PostAsJsonAsync("/api/v1/org/departments", new SaveDepartmentRequest { Name = "Field Service" }, J));
        Assert.NotNull(created.DefaultTeamId);
        var ticket = await CreateTicketAsync(tenant.Id, "Field visit", department: created.Id);

        var refused = await admin.PostAsJsonAsync($"/api/v1/org/departments/{created.Id}/archive", new ArchiveDepartmentRequest(null, null), J);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("department_has_open_tickets", await CodeOf(refused));

        var archived = await Ok<ArchiveDepartmentResultDto>(await admin.PostAsJsonAsync($"/api/v1/org/departments/{created.Id}/archive",
            new ArchiveDepartmentRequest(tenant.SupportDepartmentId, null), J));
        Assert.False(archived.Department.IsActive);
        await factory.DrainJobsAsync();
        Assert.Equal(tenant.SupportDepartmentId, (await LoadTicketAsync(tenant.Id, ticket.Id)).DepartmentId);
    }
}
