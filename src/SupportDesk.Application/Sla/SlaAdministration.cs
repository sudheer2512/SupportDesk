using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Organisation;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Organisation;
using SupportDesk.Contracts.Sla;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Sla;

internal static class SlaAdminAccess
{
    public static readonly string[] Read = [Permissions.AdminSla, Permissions.SupervisorMonitor, Permissions.AdminOrganisation];
    public static readonly string[] ManageCalendars = [Permissions.AdminSla, Permissions.AdminOrganisation];
}

/// <summary>
/// SLA policy administration: list/detail, create/update (targets per priority, calendar, pause statuses, escalation
/// levels by % of target or minutes around the breach, conditions using the shared condition model), evaluation order,
/// default policy, delete (only when unused) and simulation against an existing ticket. Saving a policy bumps its
/// version and queues a re-target of open tickets: the engine re-selects each ticket's policy and, where the policy or
/// its version changed, re-targets running milestones while keeping the elapsed time.
/// </summary>
public sealed class SlaPolicyAdminService(DeskDbContext db, RequestContext ctx, IAuditWriter audit, IJobQueue jobs)
{
    private static readonly HashSet<string> KnownFields =
        TicketConditionContext.FieldCatalog.Select(f => f.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<List<SlaPolicyListItemDto>> ListAsync(CancellationToken ct = default)
    {
        ctx.RequireAny(SlaAdminAccess.Read);
        var policies = await db.SlaPolicies.AsNoTracking().Include(p => p.Targets).Include(p => p.Escalations)
            .OrderByDescending(p => p.IsActive).ThenBy(p => p.IsDefault).ThenBy(p => p.EvaluationOrder).ThenBy(p => p.Name).ToListAsync(ct);
        var active = await ActiveCountsAsync(ct);
        var calendars = await db.BusinessCalendars.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return policies.Select(p => new SlaPolicyListItemDto(p.Id, p.Name, p.Description, p.IsActive, p.IsDefault, p.EvaluationOrder, p.Version, p.CalendarId,
            p.CalendarId is { } c ? calendars.GetValueOrDefault(c) : null, SafeParse(p.ConditionsJson).AllConditions().Count(), p.Targets.Count, p.Escalations.Count,
            active.GetValueOrDefault(p.Id), p.UpdatedAt)).ToList();
    }

    public async Task<SlaPolicyDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny(SlaAdminAccess.Read);
        var p = await db.SlaPolicies.AsNoTracking().Include(x => x.Targets).Include(x => x.Escalations).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("SLA policy", id);
        return await MapAsync(p, ct);
    }

    public async Task<SaveSlaPolicyResultDto> CreateAsync(SaveSlaPolicyRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminSla);
        var p = new SlaPolicy();
        await ValidateAsync(p, r, ct);
        p.EvaluationOrder = r.IsDefault ? 1000 : (await db.SlaPolicies.Where(x => !x.IsDefault).MaxAsync(x => (int?)x.EvaluationOrder, ct) ?? 0) + 10;
        Apply(p, r);
        db.SlaPolicies.Add(p);
        await SetDefaultAsync(p, r.IsDefault, ct);
        var queued = await QueueRetargetAsync(p.Id, ct);
        audit.Write(OrgAuditActions.SlaPolicyChanged, "SlaPolicy", p.Id, $"SLA policy '{p.Name}' created", new { targets = r.Targets.Count, escalations = r.Escalations.Count });
        await db.SaveChangesAsync(ct);
        return new SaveSlaPolicyResultDto(await GetAsync(p.Id, ct), queued);
    }

    public async Task<SaveSlaPolicyResultDto> UpdateAsync(Guid id, SaveSlaPolicyRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminSla);
        var p = await db.SlaPolicies.Include(x => x.Targets).Include(x => x.Escalations).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("SLA policy", id);
        OrgRowVersion.Apply(db, p, r.RowVersion, "SLA policy");
        await ValidateAsync(p, r, ct);
        var before = new { p.Name, p.IsActive, p.CalendarId, p.ConditionsJson, p.Version };
        Apply(p, r);
        p.Version++;
        await SetDefaultAsync(p, r.IsDefault, ct);
        var queued = await QueueRetargetAsync(p.Id, ct);
        audit.Write(OrgAuditActions.SlaPolicyChanged, "SlaPolicy", p.Id, $"SLA policy '{p.Name}' updated (v{before.Version} → v{p.Version})",
            new { before, after = new { p.Name, p.IsActive, p.CalendarId, p.ConditionsJson, p.Version } });
        await db.SaveChangesAsync(ct);
        return new SaveSlaPolicyResultDto(await GetAsync(p.Id, ct), queued);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminSla);
        var p = await db.SlaPolicies.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("SLA policy", id);
        if (p.IsDefault) throw new ConflictException("policy_is_default", "The default policy cannot be deleted. Make another policy the default first.");
        if (await db.SlaInstances.AnyAsync(i => i.PolicyId == id, ct))
            throw new ConflictException("policy_in_use", "Tickets have used this policy, so it is kept for reproducibility. Deactivate it instead.");
        if (await db.Contracts.AnyAsync(c => c.SlaPolicyId == id, ct))
            throw new ConflictException("policy_in_use", "Contracts reference this policy. Change those contracts first.");
        foreach (var d in await db.Departments.Where(d => d.DefaultSlaPolicyId == id).ToListAsync(ct)) d.DefaultSlaPolicyId = null;
        db.SlaPolicies.Remove(p);
        audit.Write(OrgAuditActions.SlaPolicyDeleted, "SlaPolicy", id, $"SLA policy '{p.Name}' deleted");
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<SlaPolicyListItemDto>> ReorderAsync(ReorderSlaPoliciesRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminSla);
        var ids = r.PolicyIds.Distinct().ToList();
        var policies = await db.SlaPolicies.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        if (policies.Count != ids.Count) throw new ValidationException(new Dictionary<string, string> { ["policyIds"] = "Unknown policy." });
        for (var i = 0; i < ids.Count; i++)
        {
            var p = policies.First(x => x.Id == ids[i]);
            if (!p.IsDefault) p.EvaluationOrder = (i + 1) * 10;
        }
        await QueueRetargetAsync(null, ct);
        audit.Write(OrgAuditActions.SlaPolicyChanged, "SlaPolicy", null, "SLA policy evaluation order changed", new { order = ids });
        await db.SaveChangesAsync(ct);
        return await ListAsync(ct);
    }

    /// <summary>Which policy would apply to the ticket (saved policies, or with an unsaved draft), the calendar, and the resulting due dates.</summary>
    public async Task<SlaSimulationDto> SimulateAsync(SimulateSlaRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(SlaAdminAccess.Read);
        var ticket = r.TicketId is { } tid
            ? await db.Tickets.AsNoTracking().Include(t => t.Tags).VisibleTo(ctx, db).FirstOrDefaultAsync(t => t.Id == tid, ct)
            : r.TicketNumber is { } num ? await db.Tickets.AsNoTracking().Include(t => t.Tags).VisibleTo(ctx, db).FirstOrDefaultAsync(t => t.Number == num, ct) : null;
        if (ticket is null) throw new NotFoundException("Ticket", (object?)r.TicketId ?? r.TicketNumber);

        var policies = await db.SlaPolicies.AsNoTracking().Include(p => p.Targets).Include(p => p.Escalations).ToListAsync(ct);
        Guid? unsavedId = null;
        if (r.Draft is { } draft)
        {
            var target = r.PolicyId is { } pid ? policies.FirstOrDefault(p => p.Id == pid) : null;
            var simulated = new SlaPolicy();
            if (target is not null) { simulated.Id = target.Id; simulated.EvaluationOrder = target.EvaluationOrder; policies.Remove(target); }
            else
            {
                unsavedId = simulated.Id;
                simulated.EvaluationOrder = (policies.Where(p => !p.IsDefault).Max(p => (int?)p.EvaluationOrder) ?? 0) + 10;
            }
            await ValidateAsync(simulated, draft, ct, forSimulation: true);
            Apply(simulated, draft);
            if (string.IsNullOrWhiteSpace(simulated.Name)) simulated.Name = "(unsaved policy)";
            if (draft.IsDefault) foreach (var p in policies) p.IsDefault = false;
            simulated.IsDefault = draft.IsDefault;
            policies.Add(simulated);
        }

        var context = await TicketConditionContext.BuildAsync(db, ticket, null, ct);
        var contract = ticket.ContractId is { } cid ? await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        var matches = new List<(SlaPolicy Policy, bool Matched, string Note)>();
        SlaPolicy? selected = null;
        string reason;
        if (contract?.SlaPolicyId is { } contractPolicy && policies.FirstOrDefault(p => p.Id == contractPolicy && p.IsActive) is { } cp)
        {
            selected = cp;
            reason = $"Contract {contract.Number} specifies this policy";
        }
        else reason = "";
        foreach (var p in policies.OrderBy(p => p.IsDefault).ThenBy(p => p.EvaluationOrder))
        {
            bool matched;
            string note;
            if (!p.IsActive) { matched = false; note = "Inactive"; }
            else if (p.IsDefault) { matched = true; note = "Default policy (fallback)"; }
            else
            {
                var group = SafeParse(p.ConditionsJson);
                matched = ConditionEvaluator.Evaluate(group, context);
                note = group.IsEmpty ? "No conditions — matches every ticket" : matched ? "Conditions match" : "Conditions do not match";
            }
            if (selected is null && matched && p.IsActive)
            {
                selected = p;
                reason = p.IsDefault ? "No other policy matched; the default policy applies" : $"First matching policy in evaluation order ({p.EvaluationOrder})";
            }
            matches.Add((p, matched, note));
        }
        var evaluated = matches.Select(m => new SlaPolicyMatchDto(m.Policy.Id == unsavedId ? null : m.Policy.Id, m.Policy.Name, m.Policy.EvaluationOrder,
            m.Policy.IsDefault, m.Policy.IsActive, m.Matched, ReferenceEquals(m.Policy, selected), m.Note)).ToList();
        if (selected is null) reason = "No active policy applies — the ticket has no SLA";

        // Calendar: contract support hours → policy calendar → department calendar → 24x7 (same order as the engine).
        Guid? calendarId = null;
        var source = "24x7";
        if (selected is not null)
        {
            if (selected.UseContractCalendar && contract?.CalendarId is { } cc) { calendarId = cc; source = "contract"; }
            else if (selected.CalendarId is { } pc) { calendarId = pc; source = "policy"; }
            else if (await db.Departments.AsNoTracking().Where(d => d.Id == ticket.DepartmentId).Select(d => d.BusinessCalendarId).FirstOrDefaultAsync(ct) is { } dc)
            { calendarId = dc; source = "department"; }
        }
        var calendarName = calendarId is { } calId ? await db.BusinessCalendars.AsNoTracking().Where(c => c.Id == calId).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var calendar = SlaSnapshot.Calendar(Json.Serialize(await SlaSnapshot.CaptureCalendarAsync(db, calendarId, ct)));

        var current = await db.SlaInstances.AsNoTracking().Include(i => i.Milestones).Where(i => i.TicketId == ticket.Id).OrderByDescending(i => i.StartedAt).FirstOrDefaultAsync(ct);
        var milestones = new List<SimulatedMilestoneDto>();
        var targetRow = selected?.Targets.FirstOrDefault(t => t.Priority == ticket.Priority);
        if (targetRow is not null)
        {
            void Add(SlaMilestoneKind kind, int? minutes, DateTime start)
            {
                if (minutes is not { } m) return;
                var cal = targetRow.BusinessHours ? calendar : WorkingCalendar.AlwaysOpen;
                var due = cal.AddWorkingMinutes(start, m);
                var warn = cal.AddWorkingMinutes(start, m * targetRow.WarningPercent / 100.0);
                var cur = current?.Milestones.Where(x => x.Kind == kind).OrderByDescending(x => x.Cycle).FirstOrDefault();
                milestones.Add(new SimulatedMilestoneDto(kind.ToString(), m, targetRow.BusinessHours, start, due, warn, cur?.DueAt, cur?.Status.ToString()));
            }
            Add(SlaMilestoneKind.FirstResponse, targetRow.FirstResponseMinutes, ticket.CreatedAt);
            if (ticket.FirstResponseAt is not null) Add(SlaMilestoneKind.NextResponse, targetRow.NextResponseMinutes, ticket.LastCustomerResponseAt ?? ticket.CreatedAt);
            Add(SlaMilestoneKind.Resolution, targetRow.ResolutionMinutes, ticket.CreatedAt);
        }
        else if (selected is not null) reason += $" — but it has no target for {ticket.Priority} priority";

        var currentName = current is null ? null : SlaSnapshot.Policy(current.PolicySnapshotJson).Name;
        // "Would change" = a different policy would apply than the one the ticket runs under now.
        var wouldChange = (selected is null ? null : (Guid?)selected.Id) != (current is { Status: SlaInstanceStatus.Active or SlaInstanceStatus.Paused } ? current.PolicyId : null);
        return new SlaSimulationDto(ticket.Id, ticket.Number, ticket.Subject, ticket.Priority.ToString(), ticket.StatusCategory.ToString(),
            selected is null || selected.Id == unsavedId ? null : selected.Id, selected?.Name, reason, source, calendarName, evaluated, milestones,
            current?.PolicyId, currentName, wouldChange);
    }

    // ───────────────────────────── Internals ─────────────────────────────

    private async Task ValidateAsync(SlaPolicy p, SaveSlaPolicyRequest r, CancellationToken ct, bool forSimulation = false)
    {
        var errors = new Dictionary<string, string>();
        var name = (r.Name ?? "").Trim();
        if (!forSimulation)
        {
            if (name.Length is < 2 or > 120) errors["name"] = "Enter a name of 2–120 characters.";
            else if (await db.SlaPolicies.AnyAsync(x => x.Name == name && x.Id != p.Id, ct)) errors["name"] = "Another policy has this name.";
        }
        ConditionGroup? group = null;
        try { group = r.Conditions is { ValueKind: JsonValueKind.Object } c ? ConditionGroup.Parse(c.GetRawText()) : new ConditionGroup(); }
        catch (JsonException) { errors["conditions"] = "The conditions are not valid."; }
        if (group is not null)
        {
            var unknown = group.AllConditions().Select(x => x.Field).Where(f => !f.StartsWith("cf:", StringComparison.Ordinal) && !KnownFields.Contains(f)).Distinct().ToList();
            if (unknown.Count > 0) errors["conditions"] = $"Unknown condition field(s): {string.Join(", ", unknown)}.";
            if (group.AllConditions().Any(x => !ConditionOps.All.Contains(x.Op))) errors["conditions"] = "Unknown condition operator.";
        }
        if (r.CalendarId is { } cal && !await db.BusinessCalendars.AnyAsync(x => x.Id == cal, ct)) errors["calendarId"] = "Unknown calendar.";
        var pauseIds = r.PauseStatusIds.Distinct().ToList();
        if (pauseIds.Count > 0 && await db.TicketStatuses.CountAsync(s => pauseIds.Contains(s.Id), ct) != pauseIds.Count) errors["pauseStatusIds"] = "Unknown status.";
        if (r.Targets.Count == 0) errors["targets"] = "Add at least one priority target.";
        var priorities = new HashSet<TicketPriority>();
        foreach (var (t, i) in r.Targets.Select((t, i) => (t, i)))
        {
            if (!Enum.TryParse<TicketPriority>(t.Priority, true, out var pr) || !Enum.IsDefined(pr)) { errors[$"targets[{i}].priority"] = "Unknown priority."; continue; }
            if (!priorities.Add(pr)) errors[$"targets[{i}].priority"] = "Each priority may appear once.";
            foreach (var (value, field) in new[] { (t.FirstResponseMinutes, "firstResponseMinutes"), (t.NextResponseMinutes, "nextResponseMinutes"), (t.ResolutionMinutes, "resolutionMinutes") })
                if (value is < 1 or > 525_600) errors[$"targets[{i}].{field}"] = "Use 1 minute to 365 days.";
            if (t.FirstResponseMinutes is null && t.NextResponseMinutes is null && t.ResolutionMinutes is null) errors[$"targets[{i}]"] = "Set at least one target.";
            if (t.FirstResponseMinutes is { } fr && t.ResolutionMinutes is { } res && fr > res) errors[$"targets[{i}].firstResponseMinutes"] = "First response cannot exceed resolution.";
            if (t.WarningPercent is < 1 or > 99) errors[$"targets[{i}].warningPercent"] = "Warn at 1–99 % of the target.";
        }
        var levels = new HashSet<(SlaMilestoneKind, int)>();
        foreach (var (e, i) in r.Escalations.Select((e, i) => (e, i)))
        {
            if (!Enum.TryParse<SlaMilestoneKind>(e.Milestone, true, out var kind) || !Enum.IsDefined(kind)) { errors[$"escalations[{i}].milestone"] = "Unknown milestone."; continue; }
            if (e.Level is < 1 or > 10) errors[$"escalations[{i}].level"] = "Levels are 1–10.";
            else if (!levels.Add((kind, e.Level))) errors[$"escalations[{i}].level"] = "Each milestone level may appear once.";
            if (!Enum.TryParse<EscalationAction>(e.Action, true, out var action) || !Enum.IsDefined(action)) errors[$"escalations[{i}].action"] = "Unknown action.";
            else if (action is EscalationAction.Reassign or EscalationAction.NotifyAndReassign && e.ReassignToUserId is null && e.ReassignToTeamId is null)
                errors[$"escalations[{i}].reassignTo"] = "Choose an agent or team to reassign to.";
            if (e.AtPercent is < 1 or > 1000) errors[$"escalations[{i}].atPercent"] = "Use 1–1000 %.";
            if (e.AtPercent is null && e.MinutesAfterBreach is < -43_200 or > 43_200) errors[$"escalations[{i}].minutesAfterBreach"] = "Use up to 30 days before or after the breach.";
            if (e.ReassignToUserId is { } u && !await db.Users.AnyAsync(x => x.Id == u && x.IsActive, ct)) errors[$"escalations[{i}].reassignToUserId"] = "Unknown or inactive agent.";
            if (e.ReassignToTeamId is { } tm && !await db.Teams.AnyAsync(x => x.Id == tm && x.IsActive, ct)) errors[$"escalations[{i}].reassignToTeamId"] = "Unknown team.";
            if (e.Notify.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null)) errors[$"escalations[{i}].notify"] = "Notify must be an object.";
        }
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private void Apply(SlaPolicy p, SaveSlaPolicyRequest r)
    {
        p.Name = (r.Name ?? "").Trim();
        p.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        p.IsActive = r.IsActive || r.IsDefault;
        p.ConditionsJson = (r.Conditions is { ValueKind: JsonValueKind.Object } c ? ConditionGroup.Parse(c.GetRawText()) : new ConditionGroup()).ToJson();
        p.CalendarId = r.CalendarId;
        p.UseContractCalendar = r.UseContractCalendar;
        p.PauseStatusIdsJson = Json.Serialize(r.PauseStatusIds.Distinct().ToList());

        // Targets / escalations are updated in place (unique keys per priority and per milestone level).
        var targets = r.Targets.Select(t => (Priority: Enum.Parse<TicketPriority>(t.Priority, true), t)).ToList();
        foreach (var old in p.Targets.Where(x => targets.All(t => t.Priority != x.Priority)).ToList())
        {
            p.Targets.Remove(old);
            if (db.Entry(old).State != EntityState.Detached) db.SlaTargets.Remove(old);
        }
        foreach (var (priority, t) in targets)
        {
            var row = p.Targets.FirstOrDefault(x => x.Priority == priority);
            if (row is null)
            {
                row = new SlaTarget { PolicyId = p.Id, Priority = priority };
                p.Targets.Add(row);
                if (db.Entry(p).State != EntityState.Detached && db.Entry(p).State != EntityState.Added) db.SlaTargets.Add(row);
            }
            row.FirstResponseMinutes = t.FirstResponseMinutes;
            row.NextResponseMinutes = t.NextResponseMinutes;
            row.ResolutionMinutes = t.ResolutionMinutes;
            row.BusinessHours = t.BusinessHours;
            row.WarningPercent = t.WarningPercent;
        }
        var escalations = r.Escalations.Select(e => (Kind: Enum.Parse<SlaMilestoneKind>(e.Milestone, true), e)).ToList();
        foreach (var old in p.Escalations.Where(x => escalations.All(e => e.Kind != x.Milestone || e.e.Level != x.Level)).ToList())
        {
            p.Escalations.Remove(old);
            if (db.Entry(old).State != EntityState.Detached) db.SlaEscalationLevels.Remove(old);
        }
        foreach (var (kind, e) in escalations)
        {
            var row = p.Escalations.FirstOrDefault(x => x.Milestone == kind && x.Level == e.Level);
            if (row is null)
            {
                row = new SlaEscalationLevel { PolicyId = p.Id, Milestone = kind, Level = e.Level };
                p.Escalations.Add(row);
                if (db.Entry(p).State != EntityState.Detached && db.Entry(p).State != EntityState.Added) db.SlaEscalationLevels.Add(row);
            }
            row.MinutesAfterBreach = e.AtPercent is null ? e.MinutesAfterBreach : 0;
            row.AtPercent = e.AtPercent;
            row.Action = Enum.Parse<EscalationAction>(e.Action, true);
            row.NotifyJson = e.Notify.ValueKind == JsonValueKind.Object ? e.Notify.GetRawText() : "{}";
            row.ReassignToUserId = row.Action is EscalationAction.Reassign or EscalationAction.NotifyAndReassign ? e.ReassignToUserId : null;
            row.ReassignToTeamId = row.Action is EscalationAction.Reassign or EscalationAction.NotifyAndReassign ? e.ReassignToTeamId : null;
        }
    }

    private async Task SetDefaultAsync(SlaPolicy p, bool makeDefault, CancellationToken ct)
    {
        if (makeDefault)
        {
            foreach (var other in await db.SlaPolicies.Where(x => x.IsDefault && x.Id != p.Id).ToListAsync(ct)) other.IsDefault = false;
            p.IsDefault = true;
            p.IsActive = true;
        }
        else if (p.IsDefault)
        {
            throw new ValidationException(new Dictionary<string, string> { ["isDefault"] = "Make another policy the default instead of unsetting it here." });
        }
    }

    /// <summary>Queues re-targeting of open tickets (policy selection may change for any ticket when conditions or order change).</summary>
    private async Task<int> QueueRetargetAsync(Guid? policyId, CancellationToken ct)
    {
        var count = await db.SlaInstances.CountAsync(i => i.Status == SlaInstanceStatus.Active || i.Status == SlaInstanceStatus.Paused, ct);
        jobs.Enqueue(SlaRetargetJobHandler.JobType, new SlaRetargetPayload(policyId, db.Clock.UtcNow), idempotencyKey: $"sla.retarget:{ctx.TenantId:N}:{Guid.NewGuid():N}");
        return count;
    }

    private async Task<Dictionary<Guid, int>> ActiveCountsAsync(CancellationToken ct) =>
        await db.SlaInstances.AsNoTracking().Where(i => i.Status == SlaInstanceStatus.Active || i.Status == SlaInstanceStatus.Paused)
            .GroupBy(i => i.PolicyId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private async Task<SlaPolicyDto> MapAsync(SlaPolicy p, CancellationToken ct)
    {
        var active = await db.SlaInstances.AsNoTracking().CountAsync(i => i.PolicyId == p.Id && (i.Status == SlaInstanceStatus.Active || i.Status == SlaInstanceStatus.Paused), ct);
        var calendar = p.CalendarId is { } c ? await db.BusinessCalendars.AsNoTracking().Where(x => x.Id == c).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var order = new[] { TicketPriority.Critical, TicketPriority.High, TicketPriority.Medium, TicketPriority.Low };
        return new SlaPolicyDto(p.Id, p.Name, p.Description, p.IsActive, p.IsDefault, p.EvaluationOrder, p.Version, Json.Element(SafeParse(p.ConditionsJson).ToJson()),
            p.CalendarId, calendar, p.UseContractCalendar, Json.GuidList(p.PauseStatusIdsJson),
            p.Targets.OrderBy(t => Array.IndexOf(order, t.Priority))
                .Select(t => new SlaTargetDto(t.Priority.ToString(), t.FirstResponseMinutes, t.NextResponseMinutes, t.ResolutionMinutes, t.BusinessHours, t.WarningPercent)).ToList(),
            p.Escalations.OrderBy(e => e.Milestone).ThenBy(e => e.Level)
                .Select(e => new SlaEscalationDto(e.Milestone.ToString(), e.Level, e.MinutesAfterBreach, e.AtPercent, e.Action.ToString(), Json.Element(e.NotifyJson),
                    e.ReassignToUserId, e.ReassignToTeamId)).ToList(),
            active, p.UpdatedAt, OrgRowVersion.Encode(p.RowVersion));
    }

    private static ConditionGroup SafeParse(string? json)
    {
        try { return ConditionGroup.Parse(json); }
        catch (JsonException) { return new ConditionGroup(); }
    }
}

public sealed record SlaRetargetPayload(Guid? PolicyId, DateTime RequestedAt);

/// <summary>
/// Re-evaluates the SLA of open tickets after a policy change (keyset batches, idempotent): the engine keeps elapsed
/// business time and only rewrites milestones whose policy, version, calendar or priority changed.
/// </summary>
public sealed class SlaRetargetJobHandler(DeskDbContext db, ISlaEngine engine, ILogger<SlaRetargetJobHandler> logger) : JobHandler<SlaRetargetPayload>
{
    public const string JobType = "sla.retarget";
    public override string Type => JobType;

    protected override async Task ExecuteAsync(SlaRetargetPayload p, BackgroundJob job, CancellationToken ct)
    {
        var last = Guid.Empty;
        for (var round = 0; round < 1000; round++)
        {
            var ticketIds = await db.SlaInstances.AsNoTracking()
                .Where(i => (i.Status == SlaInstanceStatus.Active || i.Status == SlaInstanceStatus.Paused) && i.TicketId.CompareTo(last) > 0)
                .Select(i => i.TicketId).Distinct().OrderBy(id => id).Take(100).ToListAsync(ct);
            if (ticketIds.Count == 0) return;
            foreach (var id in ticketIds)
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var ticket = await db.Tickets.Include(t => t.Tags).FirstOrDefaultAsync(t => t.Id == id, ct);
                        if (ticket is null || !ticket.StatusCategory.IsOpen()) break;
                        await engine.OnTicketChangedAsync(ticket, [new FieldChange("slaPolicy", null, p.PolicyId?.ToString())], ct);
                        await db.SaveChangesAsync(ct);
                        break;
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        logger.LogInformation("Ticket {Ticket} changed during SLA re-targeting; retrying", id);
                    }
                    finally
                    {
                        db.ChangeTracker.Clear();
                    }
                }
            }
            last = ticketIds[^1];
        }
    }
}

/// <summary>
/// Business calendars: weekly working hours per day in a time zone (or 24x7), holidays (one-off or recurring yearly),
/// CSV holiday import and a business-time preview. Used by SLA policies, departments and contracts.
/// </summary>
public sealed class BusinessCalendarService(DeskDbContext db, RequestContext ctx, IAuditWriter audit)
{
    public async Task<List<BusinessCalendarListItemDto>> ListAsync(CancellationToken ct = default)
    {
        ctx.RequireAny([.. SlaAdminAccess.Read, .. OrgPermissions.ReadStructure]);
        var rows = await db.BusinessCalendars.AsNoTracking().OrderByDescending(c => c.IsDefault).ThenBy(c => c.Name).ToListAsync(ct);
        var holidays = await db.CalendarHolidays.AsNoTracking().GroupBy(h => h.CalendarId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var usage = await UsageCountsAsync(ct);
        return rows.Select(c => new BusinessCalendarListItemDto(c.Id, c.Name, c.Description, c.TimeZone, c.Is24x7, c.IsDefault, holidays.GetValueOrDefault(c.Id),
            c.Is24x7 ? 168 : WeeklyHours.TotalHours(c.WeeklyHoursJson), usage.GetValueOrDefault(c.Id))).ToList();
    }

    public async Task<BusinessCalendarDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny([.. SlaAdminAccess.Read, .. OrgPermissions.ReadStructure]);
        var c = await db.BusinessCalendars.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Calendar", id);
        var holidays = await db.CalendarHolidays.AsNoTracking().Where(h => h.CalendarId == id).OrderBy(h => h.Date).ToListAsync(ct);
        var usedBy = new List<string>();
        usedBy.AddRange(await db.Departments.AsNoTracking().Where(d => d.BusinessCalendarId == id).Select(d => "Department: " + d.Name).ToListAsync(ct));
        usedBy.AddRange(await db.SlaPolicies.AsNoTracking().Where(p => p.CalendarId == id).Select(p => "SLA policy: " + p.Name).ToListAsync(ct));
        usedBy.AddRange(await db.Contracts.AsNoTracking().Where(k => k.CalendarId == id).OrderBy(k => k.Number).Take(20).Select(k => "Contract: " + k.Number).ToListAsync(ct));
        return new BusinessCalendarDto(c.Id, c.Name, c.Description, c.TimeZone, c.Is24x7, c.IsDefault, WeeklyHours.FromJson(c.WeeklyHoursJson),
            c.Is24x7 ? 168 : WeeklyHours.TotalHours(c.WeeklyHoursJson), holidays.Select(MapHoliday).ToList(), usedBy, OrgRowVersion.Encode(c.RowVersion));
    }

    public async Task<BusinessCalendarDto> SaveAsync(Guid? id, SaveBusinessCalendarRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(SlaAdminAccess.ManageCalendars);
        var cal = id is { } existing ? await db.BusinessCalendars.FirstOrDefaultAsync(c => c.Id == existing, ct) ?? throw new NotFoundException("Calendar", existing) : new BusinessCalendar();
        if (id is not null) OrgRowVersion.Apply(db, cal, r.RowVersion, "calendar");
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 120) throw new ValidationException(new Dictionary<string, string> { ["name"] = "Enter a name of 2–120 characters." });
        if (await db.BusinessCalendars.AnyAsync(c => c.Name == name && c.Id != cal.Id, ct))
            throw new ValidationException(new Dictionary<string, string> { ["name"] = "Another calendar has this name." });
        WeeklyHours.EnsureTimeZone(r.TimeZone);
        var json = r.Is24x7 ? "{}" : WeeklyHours.ToJson(r.WeeklyHours);
        if (!r.Is24x7 && WeeklyHours.TotalHours(json) <= 0)
            throw new ValidationException(new Dictionary<string, string> { ["weeklyHours"] = "Add working hours on at least one day (or mark the calendar 24x7)." });
        cal.Name = name;
        cal.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        cal.TimeZone = r.TimeZone;
        cal.Is24x7 = r.Is24x7;
        cal.WeeklyHoursJson = json;
        if (r.IsDefault && !cal.IsDefault)
            foreach (var other in await db.BusinessCalendars.Where(c => c.IsDefault && c.Id != cal.Id).ToListAsync(ct)) other.IsDefault = false;
        cal.IsDefault = r.IsDefault || (cal.IsDefault && !await db.BusinessCalendars.AnyAsync(c => c.IsDefault && c.Id != cal.Id, ct));
        if (id is null) db.BusinessCalendars.Add(cal);
        audit.Write(OrgAuditActions.CalendarChanged, "BusinessCalendar", cal.Id, $"Calendar '{cal.Name}' {(id is null ? "created" : "updated")}");
        await db.SaveChangesAsync(ct);
        return await GetAsync(cal.Id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.RequireAny(SlaAdminAccess.ManageCalendars);
        var cal = await db.BusinessCalendars.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Calendar", id);
        if (cal.IsDefault) throw new ConflictException("calendar_is_default", "The default calendar cannot be deleted.");
        if ((await UsageCountsAsync(ct)).GetValueOrDefault(id) > 0)
            throw new ConflictException("calendar_in_use", "Departments, SLA policies or contracts use this calendar. Change them first.");
        db.BusinessCalendars.Remove(cal);
        audit.Write(OrgAuditActions.CalendarChanged, "BusinessCalendar", id, $"Calendar '{cal.Name}' deleted");
        await db.SaveChangesAsync(ct);
    }

    public async Task<CalendarHolidayDto> SaveHolidayAsync(Guid calendarId, Guid? holidayId, SaveHolidayRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(SlaAdminAccess.ManageCalendars);
        if (!await db.BusinessCalendars.AnyAsync(c => c.Id == calendarId, ct)) throw new NotFoundException("Calendar", calendarId);
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 120) throw new ValidationException(new Dictionary<string, string> { ["name"] = "Enter the holiday's name." });
        if (await db.CalendarHolidays.AnyAsync(h => h.CalendarId == calendarId && h.Date == r.Date && h.Id != holidayId, ct))
            throw new ValidationException(new Dictionary<string, string> { ["date"] = "There is already a holiday on this date." });
        var h = holidayId is { } hid
            ? await db.CalendarHolidays.FirstOrDefaultAsync(x => x.Id == hid && x.CalendarId == calendarId, ct) ?? throw new NotFoundException("Holiday", hid)
            : new CalendarHoliday { CalendarId = calendarId };
        h.Date = r.Date;
        h.Name = name;
        h.Recurring = r.Recurring;
        if (holidayId is null) db.CalendarHolidays.Add(h);
        audit.Write(OrgAuditActions.CalendarChanged, "BusinessCalendar", calendarId, $"Holiday '{name}' on {r.Date:yyyy-MM-dd} saved");
        await db.SaveChangesAsync(ct);
        return MapHoliday(h);
    }

    public async Task DeleteHolidayAsync(Guid calendarId, Guid holidayId, CancellationToken ct = default)
    {
        ctx.RequireAny(SlaAdminAccess.ManageCalendars);
        var h = await db.CalendarHolidays.FirstOrDefaultAsync(x => x.Id == holidayId && x.CalendarId == calendarId, ct) ?? throw new NotFoundException("Holiday", holidayId);
        db.CalendarHolidays.Remove(h);
        audit.Write(OrgAuditActions.CalendarChanged, "BusinessCalendar", calendarId, $"Holiday '{h.Name}' removed");
        await db.SaveChangesAsync(ct);
    }

    public async Task<ImportHolidaysResultDto> ImportAsync(Guid calendarId, ImportHolidaysRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny(SlaAdminAccess.ManageCalendars);
        if (!await db.BusinessCalendars.AnyAsync(c => c.Id == calendarId, ct)) throw new NotFoundException("Calendar", calendarId);
        var lines = (r.Csv ?? "").Replace("\r", "").Split('\n');
        if (lines.Length > 2000) throw new ValidationException(new Dictionary<string, string> { ["csv"] = "Import at most 2000 lines at a time." });
        var parsed = new List<(DateOnly Date, string Name, bool Recurring)>();
        var errors = new List<string>();
        var skipped = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var cells = SplitCsv(line);
            if (i == 0 && cells.Count > 0 && cells[0].Trim().Equals("date", StringComparison.OrdinalIgnoreCase)) continue;
            if (cells.Count < 2) { errors.Add($"Line {i + 1}: expected date,name[,recurring]."); skipped++; continue; }
            if (!TryDate(cells[0], out var date)) { errors.Add($"Line {i + 1}: '{cells[0]}' is not a date (use yyyy-MM-dd)."); skipped++; continue; }
            var name = cells[1].Trim();
            if (name.Length is < 2 or > 120) { errors.Add($"Line {i + 1}: the name must be 2–120 characters."); skipped++; continue; }
            var recurring = cells.Count > 2 && cells[2].Trim().ToLowerInvariant() is "yes" or "y" or "true" or "1" or "recurring";
            if (parsed.Any(p => p.Date == date)) { errors.Add($"Line {i + 1}: duplicate date {date:yyyy-MM-dd} in the file."); skipped++; continue; }
            parsed.Add((date, name, recurring));
        }
        if (r.ReplaceExisting) await db.CalendarHolidays.Where(h => h.CalendarId == calendarId).ExecuteDeleteAsync(ct);
        var existing = await db.CalendarHolidays.Where(h => h.CalendarId == calendarId).ToListAsync(ct);
        int added = 0, updated = 0;
        foreach (var (date, name, recurring) in parsed)
        {
            var row = existing.FirstOrDefault(h => h.Date == date);
            if (row is null)
            {
                db.CalendarHolidays.Add(new CalendarHoliday { CalendarId = calendarId, Date = date, Name = name, Recurring = recurring });
                added++;
            }
            else if (row.Name != name || row.Recurring != recurring)
            {
                row.Name = name;
                row.Recurring = recurring;
                updated++;
            }
            else skipped++;
        }
        audit.Write(OrgAuditActions.CalendarChanged, "BusinessCalendar", calendarId, $"Holidays imported ({added} added, {updated} updated)",
            new { added, updated, skipped, r.ReplaceExisting });
        await db.SaveChangesAsync(ct);
        return new ImportHolidaysResultDto(added, updated, skipped, errors.Take(50).ToList());
    }

    /// <summary>Business minutes between two instants on the calendar, the working intervals involved and (optionally) a due time.</summary>
    public async Task<BusinessTimePreviewDto> PreviewAsync(Guid calendarId, BusinessTimePreviewRequest r, CancellationToken ct = default)
    {
        ctx.RequireAny([.. SlaAdminAccess.Read, .. OrgPermissions.ReadStructure]);
        var cal = await db.BusinessCalendars.AsNoTracking().FirstOrDefaultAsync(c => c.Id == calendarId, ct) ?? throw new NotFoundException("Calendar", calendarId);
        var from = DateTime.SpecifyKind(r.From, DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(r.To, DateTimeKind.Utc);
        if (to < from) throw new ValidationException(new Dictionary<string, string> { ["to"] = "The end is before the start." });
        if ((to - from).TotalDays > 366) throw new ValidationException(new Dictionary<string, string> { ["to"] = "Preview at most one year." });
        if (r.AddMinutes is < 0 or > 525_600) throw new ValidationException(new Dictionary<string, string> { ["addMinutes"] = "Use 0–525600 minutes." });
        var working = SlaSnapshot.Calendar(Json.Serialize(await SlaSnapshot.CaptureCalendarAsync(db, calendarId, ct)));
        var minutes = working.WorkingMinutesBetween(from, to);
        var intervals = new List<BusinessIntervalDto>();
        if (!working.Is24x7)
        {
            foreach (var (s, e) in working.IntervalsFrom(from))
            {
                if (s >= to || intervals.Count >= 60) break;
                var end = e < to ? e : to;
                intervals.Add(new BusinessIntervalDto(s, end, Math.Round((end - s).TotalMinutes, 1)));
            }
        }
        else intervals.Add(new BusinessIntervalDto(from, to, Math.Round((to - from).TotalMinutes, 1)));
        var tz = WorkingCalendar.ResolveTimeZone(cal.TimeZone);
        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(from, tz));
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(to, tz));
        var holidays = await db.CalendarHolidays.AsNoTracking().Where(h => h.CalendarId == calendarId).ToListAsync(ct);
        var inRange = new List<CalendarHolidayDto>();
        for (var d = firstDay; d <= lastDay && inRange.Count < 100; d = d.AddDays(1))
            foreach (var h in holidays.Where(h => h.Recurring ? h.Date.Month == d.Month && h.Date.Day == d.Day : h.Date == d))
                inRange.Add(new CalendarHolidayDto(h.Id, d, h.Name, h.Recurring));
        DateTime? due = r.AddMinutes is { } add ? working.AddWorkingMinutes(from, add) : null;
        return new BusinessTimePreviewDto(from, to, Math.Round(minutes, 1), Math.Round((to - from).TotalMinutes, 1), due, intervals, inRange, cal.TimeZone);
    }

    private async Task<Dictionary<Guid, int>> UsageCountsAsync(CancellationToken ct)
    {
        var result = new Dictionary<Guid, int>();
        void Add(IEnumerable<Guid?> ids) { foreach (var id in ids) if (id is { } g) result[g] = result.GetValueOrDefault(g) + 1; }
        Add(await db.Departments.AsNoTracking().Select(d => d.BusinessCalendarId).ToListAsync(ct));
        Add(await db.SlaPolicies.AsNoTracking().Select(p => p.CalendarId).ToListAsync(ct));
        Add(await db.Contracts.AsNoTracking().Where(c => c.CalendarId != null).Select(c => c.CalendarId).ToListAsync(ct));
        return result;
    }

    private static CalendarHolidayDto MapHoliday(CalendarHoliday h) => new(h.Id, h.Date, h.Name, h.Recurring);

    private static bool TryDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text.Trim(), ["yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy/MM/dd", "d MMM yyyy", "dd MMM yyyy", "MMM d yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if ((c == ',' || c == ';' || c == '\t') && !quoted) { cells.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        cells.Add(current.ToString());
        return cells;
    }
}

/// <summary>Live SLA risk: running milestones at risk (warning reached) or breached, within the caller's ticket visibility.</summary>
public sealed class SlaBreachService(DeskDbContext db, RequestContext ctx)
{
    public async Task<PagedResult<SlaBreachItemDto>> ListAsync(SlaBreachQuery q, CancellationToken ct = default)
    {
        ctx.RequireAny(SlaAdminAccess.Read);
        var now = db.Clock.UtcNow;
        var state = (q.State ?? "all").ToLowerInvariant();
        var visible = db.Tickets.AsNoTracking().VisibleTo(ctx, db).Where(t => t.StatusCategory < StatusCategory.Resolved);
        if (q.DepartmentId is { } dept) visible = visible.Where(t => t.DepartmentId == dept);
        var ms = db.SlaMilestones.AsNoTracking().Where(m => visible.Any(t => t.Id == m.TicketId));
        ms = state switch
        {
            "atrisk" => ms.Where(m => m.Status == MilestoneStatus.Active && (m.WarningRaised || m.WarningAt <= now)),
            "breached" => ms.Where(m => m.Status == MilestoneStatus.Breached && m.CompletedAt == null),
            _ => ms.Where(m => (m.Status == MilestoneStatus.Active && (m.WarningRaised || m.WarningAt <= now)) || (m.Status == MilestoneStatus.Breached && m.CompletedAt == null))
        };
        if (Enum.TryParse<SlaMilestoneKind>(q.Milestone, true, out var kind)) ms = ms.Where(m => m.Kind == kind);
        var cursor = DbExtensions.DecodeCursor(q.Cursor);
        if (cursor is not null)
        {
            var v = DateTime.Parse(cursor.V, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            ms = ms.Where(m => m.DueAt > v || (m.DueAt == v && m.Id.CompareTo(cursor.Id) > 0));
        }
        var limit = Math.Clamp(q.Limit ?? 50, 1, 200);
        int? total = cursor is null ? await ms.CountAsync(ct) : null;
        var page = await ms.OrderBy(m => m.DueAt).ThenBy(m => m.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);

        var ticketIds = page.Select(m => m.TicketId).Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Number, t.Subject, t.Priority, t.SlaState, t.AssigneeId, t.DepartmentId, t.EscalationLevel }).ToDictionaryAsync(t => t.Id, ct);
        var instanceIds = page.Select(m => m.InstanceId).Distinct().ToList();
        var instances = await db.SlaInstances.AsNoTracking().Where(i => instanceIds.Contains(i.Id))
            .Select(i => new { i.Id, i.PolicySnapshotJson, i.CalendarSnapshotJson }).ToDictionaryAsync(i => i.Id, ct);
        var deptIds = tickets.Values.Select(t => t.DepartmentId).Distinct().ToList();
        var depts = await db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var names = await OrgNames.UsersAsync(db, tickets.Values.Select(t => t.AssigneeId), ct);
        var items = page.Where(m => tickets.ContainsKey(m.TicketId)).Select(m =>
        {
            var t = tickets[m.TicketId];
            var inst = instances.GetValueOrDefault(m.InstanceId);
            var calendar = inst is null || !m.BusinessHours ? WorkingCalendar.AlwaysOpen : SafeCalendar(inst.CalendarSnapshotJson);
            var policy = inst is null ? null : SafePolicyName(inst.PolicySnapshotJson);
            return new SlaBreachItemDto(t.Id, m.Id, t.Number, t.Subject, t.Priority.ToString(), t.SlaState.ToString(), m.Kind.ToString(),
                m.Status == MilestoneStatus.Breached ? "Breached" : "AtRisk", m.DueAt, m.BreachedAt, m.TargetMinutes, Math.Round(SlaClock.Elapsed(m, now, calendar), 1),
                t.AssigneeId, t.AssigneeId is { } a ? names.GetValueOrDefault(a) : null, t.DepartmentId, depts.GetValueOrDefault(t.DepartmentId), policy, m.EscalationLevelReached);
        }).ToList();
        return new PagedResult<SlaBreachItemDto>(items,
            hasMore ? DbExtensions.EncodeCursor(page[^1].DueAt.ToString("O", CultureInfo.InvariantCulture), page[^1].Id) : null, total);
    }

    public async Task<SlaBreachSummaryDto> SummaryAsync(Guid? departmentId, CancellationToken ct = default)
    {
        ctx.RequireAny(SlaAdminAccess.Read);
        var now = db.Clock.UtcNow;
        var visible = db.Tickets.AsNoTracking().VisibleTo(ctx, db).Where(t => t.StatusCategory < StatusCategory.Resolved);
        if (departmentId is { } dept) visible = visible.Where(t => t.DepartmentId == dept);
        var counts = await visible.GroupBy(_ => 1).Select(g => new
        {
            AtRisk = g.Count(t => t.SlaState == SlaState.AtRisk), Breached = g.Count(t => t.SlaState == SlaState.Breached), Paused = g.Count(t => t.SlaState == SlaState.Paused)
        }).FirstOrDefaultAsync(ct);
        var dayStart = now.Date;
        var hour = now.AddHours(1);
        var breachedToday = await db.SlaMilestones.AsNoTracking().CountAsync(m => m.BreachedAt >= dayStart && visible.Any(t => t.Id == m.TicketId), ct);
        var dueNextHour = await db.SlaMilestones.AsNoTracking().CountAsync(m => m.Status == MilestoneStatus.Active && m.DueAt > now && m.DueAt <= hour && visible.Any(t => t.Id == m.TicketId), ct);
        return new SlaBreachSummaryDto(counts?.AtRisk ?? 0, counts?.Breached ?? 0, breachedToday, counts?.Paused ?? 0, dueNextHour);
    }

    private static WorkingCalendar SafeCalendar(string json)
    {
        try { return SlaSnapshot.Calendar(json); }
        catch (Exception ex) when (ex is JsonException or DomainException) { return WorkingCalendar.AlwaysOpen; }
    }

    private static string? SafePolicyName(string json)
    {
        try { return SlaSnapshot.Policy(json).Name; }
        catch (Exception ex) when (ex is JsonException or NullReferenceException) { return null; }
    }
}
