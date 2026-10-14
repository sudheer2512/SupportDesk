using SupportDesk.Domain;
using SupportDesk.Domain.Rules;

namespace SupportDesk.UnitTests.Domain;

public sealed class BusinessTimeTests
{
    // Monday–Friday 09:00–18:00 India Standard Time (UTC+05:30, no DST).
    private static readonly WorkingCalendar Ist = WorkingCalendar.WeekdaysCalendar("Asia/Kolkata", TimeSpan.FromHours(9), TimeSpan.FromHours(18));

    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void Adds_minutes_within_the_same_working_day()
    {
        // Monday 12 Oct 2026 10:00 IST = 04:30 UTC; +60 working minutes → 11:00 IST.
        Assert.Equal(Utc(2026, 10, 12, 5, 30), Ist.AddWorkingMinutes(Utc(2026, 10, 12, 4, 30), 60));
    }

    [Fact]
    public void Rolls_over_the_evening_and_the_weekend()
    {
        // Friday 16 Oct 17:00 IST (11:30 UTC) + 2 h → 1 h on Friday, 1 h on Monday → Monday 10:00 IST (04:30 UTC).
        Assert.Equal(Utc(2026, 10, 19, 4, 30), Ist.AddWorkingMinutes(Utc(2026, 10, 16, 11, 30), 120));
    }

    [Fact]
    public void Starting_outside_hours_begins_at_next_opening()
    {
        // Saturday → Monday 09:00 IST + 30 min = 09:30 IST = 04:00 UTC.
        Assert.Equal(Utc(2026, 10, 19, 4, 0), Ist.AddWorkingMinutes(Utc(2026, 10, 17, 6, 0), 30));
        Assert.False(Ist.IsWorkingTime(Utc(2026, 10, 17, 6, 0)));
        Assert.Equal(Utc(2026, 10, 19, 3, 30), Ist.NextWorkingTime(Utc(2026, 10, 17, 6, 0)));
    }

    [Fact]
    public void Holidays_are_skipped()
    {
        var withHoliday = WorkingCalendar.WeekdaysCalendar("Asia/Kolkata", TimeSpan.FromHours(9), TimeSpan.FromHours(18), [new DateOnly(2026, 10, 19)]);
        // Friday 17:00 IST + 2 h with Monday a holiday → Tuesday 10:00 IST.
        Assert.Equal(Utc(2026, 10, 20, 4, 30), withHoliday.AddWorkingMinutes(Utc(2026, 10, 16, 11, 30), 120));
    }

    [Fact]
    public void Working_minutes_between_counts_only_open_hours()
    {
        // Friday 17:00 IST → Monday 10:00 IST = 60 + 60 working minutes.
        Assert.Equal(120, Ist.WorkingMinutesBetween(Utc(2026, 10, 16, 11, 30), Utc(2026, 10, 19, 4, 30)), 3);
        Assert.Equal(0, Ist.WorkingMinutesBetween(Utc(2026, 10, 17, 4, 0), Utc(2026, 10, 18, 4, 0)), 3);
    }

    [Fact]
    public void Add_and_between_are_inverse()
    {
        var start = Utc(2026, 10, 14, 9, 17);
        foreach (var minutes in new[] { 1, 59, 540, 541, 1440, 7777 })
        {
            var due = Ist.AddWorkingMinutes(start, minutes);
            Assert.Equal(minutes, Ist.WorkingMinutesBetween(start, due), 3);
        }
    }

    [Fact]
    public void Always_open_calendar_is_wall_clock()
    {
        var start = Utc(2026, 10, 17, 23, 0);
        Assert.Equal(start.AddMinutes(240), WorkingCalendar.AlwaysOpen.AddWorkingMinutes(start, 240));
        Assert.Equal(90, WorkingCalendar.AlwaysOpen.WorkingMinutesBetween(start, start.AddMinutes(90)), 3);
    }

    [Fact]
    public void Daylight_saving_transitions_are_respected()
    {
        // London weekdays 09:00–17:00. Clocks go forward on Sunday 29 Mar 2026: 09:00 BST = 08:00 UTC on Monday.
        var london = WorkingCalendar.WeekdaysCalendar("Europe/London", TimeSpan.FromHours(9), TimeSpan.FromHours(17));
        // Friday 27 Mar 16:00 GMT (16:00 UTC) + 2 h → 1 h Friday + 1 h Monday from 09:00 BST → 10:00 BST = 09:00 UTC.
        Assert.Equal(Utc(2026, 3, 30, 9, 0), london.AddWorkingMinutes(Utc(2026, 3, 27, 16, 0), 120));
    }

    [Fact]
    public void Weekly_hours_round_trip_through_json()
    {
        var json = WorkingCalendar.WeeklyToJson(Ist.Weekly);
        var parsed = WorkingCalendar.ParseWeekly(json);
        Assert.Equal(5, parsed.Count);
        Assert.Equal(new WorkInterval(TimeSpan.FromHours(9), TimeSpan.FromHours(18)), parsed[DayOfWeek.Wednesday][0]);
        Assert.Throws<DomainException>(() => new WorkingCalendar("UTC", false, new Dictionary<DayOfWeek, IReadOnlyList<WorkInterval>>(), [], []));
    }
}
