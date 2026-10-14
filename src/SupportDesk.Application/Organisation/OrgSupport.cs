using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Organisation;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Organisation;

public static class OrgAuditActions
{
    public const string DepartmentChanged = "org.department_changed";
    public const string DepartmentArchived = "org.department_archived";
    public const string TeamChanged = "org.team_changed";
    public const string AgentInvited = "user.invited";
    public const string InvitationAccepted = "user.invitation_accepted";
    public const string InvitationRevoked = "user.invitation_revoked";
    public const string AgentReactivated = "user.reactivated";
    public const string AgentAccessChanged = "user.access_changed";
    public const string AvailabilityChanged = "agent.availability_changed";
    public const string ShiftChanged = "org.shift_changed";
    public const string LeaveDecided = "org.leave_decided";
    public const string DelegationChanged = "org.delegation_changed";
    public const string CalendarChanged = "sla.calendar_changed";
    public const string SlaPolicyChanged = "sla.policy_changed";
    public const string SlaPolicyDeleted = "sla.policy_deleted";
}

/// <summary>Outbox events of the organisation module (internal; not part of the public webhook catalog).</summary>
public static class OrgEvents
{
    public const string AvailabilityChanged = "agent.availability_changed";
    public const string AgentDeactivated = "agent.deactivated";
    public const string AgentReactivated = "agent.reactivated";
    public const string LeaveChanged = "agent.leave_changed";
    public const string DelegationChanged = "agent.delegation_changed";
}

public sealed record AgentEventPayload(Guid UserId, string? Availability = null, string? Note = null);

/// <summary>Permission groups used by the organisation services (enforced in the services, mirrored by the UI).</summary>
public static class OrgPermissions
{
    /// <summary>May read the organisation structure (departments, teams, agents, skills, calendars).</summary>
    public static readonly string[] ReadStructure =
        [Permissions.AdminOrganisation, Permissions.AdminUsers, Permissions.AgentsManage, Permissions.SupervisorMonitor, Permissions.AdminSla];
}

internal static class OrgRowVersion
{
    public static string Encode(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    /// <summary>Applies the client's rowVersion as the original value (stale → 409 concurrency_conflict on save or immediately).</summary>
    public static void Apply<T>(DeskDbContext db, T entity, string? rowVersion, string what) where T : class, IConcurrencyVersioned
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
            throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "rowVersion is required for updates." });
        byte[] bytes;
        try { bytes = Convert.FromBase64String(rowVersion); }
        catch (FormatException) { throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "Invalid rowVersion." }); }
        if (!bytes.SequenceEqual(entity.RowVersion))
            throw new ConflictException("concurrency_conflict", $"The {what} was changed by someone else. Reload and try again.");
        db.Entry(entity).Property(x => x.RowVersion).OriginalValue = bytes;
    }
}

/// <summary>Weekly working-hours editing shared by shifts and business calendars.</summary>
public static class WeeklyHours
{
    public static readonly string[] Days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    /// <summary>Validates intervals (HH:mm, end after start, no overlaps) and returns the canonical JSON.</summary>
    public static string ToJson(IReadOnlyDictionary<string, IReadOnlyList<WorkIntervalDto>>? weekly, string field = "weeklyHours")
    {
        var errors = new Dictionary<string, string>();
        var result = new Dictionary<DayOfWeek, IReadOnlyList<WorkInterval>>();
        foreach (var (dayName, intervals) in weekly ?? new Dictionary<string, IReadOnlyList<WorkIntervalDto>>())
        {
            if (!Enum.TryParse<DayOfWeek>(dayName, true, out var day))
            {
                errors[$"{field}.{dayName}"] = "Unknown day.";
                continue;
            }
            var list = new List<WorkInterval>();
            foreach (var iv in intervals ?? [])
            {
                if (!TryClock(iv.Start, out var start) || !TryClock(iv.End, out var end))
                {
                    errors[$"{field}.{day}"] = "Times must be HH:mm (00:00–24:00).";
                    continue;
                }
                if (end <= start)
                {
                    errors[$"{field}.{day}"] = "Each interval must end after it starts.";
                    continue;
                }
                list.Add(new WorkInterval(start, end));
            }
            list = list.OrderBy(i => i.Start).ToList();
            for (var i = 1; i < list.Count; i++)
                if (list[i].Start < list[i - 1].End) errors[$"{field}.{day}"] = "Intervals overlap.";
            if (list.Count > 0) result[day] = list;
        }
        if (errors.Count > 0) throw new ValidationException(errors);
        return WorkingCalendar.WeeklyToJson(result);
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<WorkIntervalDto>> FromJson(string? json)
    {
        Dictionary<DayOfWeek, IReadOnlyList<WorkInterval>> parsed;
        try { parsed = WorkingCalendar.ParseWeekly(json); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { parsed = []; }
        return Days.ToDictionary(d => d, d =>
            (IReadOnlyList<WorkIntervalDto>)(parsed.TryGetValue(Enum.Parse<DayOfWeek>(d), out var list)
                ? list.Select(i => new WorkIntervalDto(WorkingCalendar.FormatClock(i.Start), WorkingCalendar.FormatClock(i.End))).ToList()
                : []));
    }

    public static double TotalHours(string? json)
    {
        try { return Math.Round(WorkingCalendar.ParseWeekly(json).Values.SelectMany(v => v).Sum(i => (i.End - i.Start).TotalHours), 2); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { return 0; }
    }

    private static bool TryClock(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m) || h < 0 || m < 0 || m > 59) return false;
        if (h == 24 && m == 0) { value = TimeSpan.FromHours(24); return true; }
        if (h > 23) return false;
        value = new TimeSpan(h, m, 0);
        return true;
    }

    public static void EnsureTimeZone(string? timeZone, string field = "timeZone")
    {
        if (string.IsNullOrWhiteSpace(timeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
            throw new ValidationException(new Dictionary<string, string> { [field] = "Unknown time zone." });
    }
}

/// <summary>Display-name lookups for users, departments and teams of the current tenant.</summary>
internal static class OrgNames
{
    public static async Task<Dictionary<Guid, string>> UsersAsync(DeskDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        if (list.Count == 0) return [];
        return await db.Users.AsNoTracking().IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).Where(u => list.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }
}
