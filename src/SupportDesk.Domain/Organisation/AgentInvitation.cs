using SupportDesk.Domain.Rules;

namespace SupportDesk.Domain.Organisation;

/// <summary>
/// Invitation for a new agent: the user row exists (active, no password) and the invitee proves possession of the
/// e-mailed code to choose a password. Only the SHA-256 hash of the code is stored; codes expire and are single-use.
/// </summary>
public class AgentInvitation : TenantEntity
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public Guid? InvitedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public int Attempts { get; set; }

    public bool IsPending(DateTime now) => AcceptedAt is null && RevokedAt is null && ExpiresAt > now;
}

/// <summary>
/// Shift arithmetic: whether an agent is on shift at an instant, given the shift's weekly hours in its own time zone.
/// Agents without a shift (or with an invalid/empty pattern) are treated as always on shift so routing never strands
/// tickets because of missing configuration.
/// </summary>
public static class ShiftSchedule
{
    public static WorkingCalendar? CalendarOf(Shift? shift)
    {
        if (shift is null || !shift.IsActive) return null;
        try
        {
            var weekly = WorkingCalendar.ParseWeekly(shift.WeeklyHoursJson);
            return weekly.Values.Any(v => v.Count > 0) ? new WorkingCalendar(shift.TimeZone, false, weekly, [], []) : null;
        }
        catch (Exception ex) when (ex is DomainException or System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        {
            return null;
        }
    }

    public static bool IsOnShift(Shift? shift, DateTime utc) => CalendarOf(shift)?.IsWorkingTime(utc) ?? true;

    /// <summary>The assignment effective on the agent's local date (latest EffectiveFrom wins), else null.</summary>
    public static AgentShiftAssignment? EffectiveAssignment(IEnumerable<AgentShiftAssignment> assignments, DateOnly date) =>
        assignments.Where(a => a.EffectiveFrom <= date && (a.EffectiveTo is null || a.EffectiveTo >= date))
            .OrderByDescending(a => a.EffectiveFrom).FirstOrDefault();
}
