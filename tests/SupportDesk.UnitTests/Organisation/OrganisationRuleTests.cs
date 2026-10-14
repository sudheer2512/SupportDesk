using SupportDesk.Application.Organisation;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Organisation;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Rules;

namespace SupportDesk.UnitTests.Organisation;

public sealed class OrganisationRuleTests
{
    private static DateTime Ist(int month, int day, int hour, int minute = 0) => new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    private static Shift EveningShift() => new()
    {
        Name = "Evening", TimeZone = "Asia/Kolkata",
        WeeklyHoursJson = WeeklyHours.ToJson(new Dictionary<string, IReadOnlyList<WorkIntervalDto>>
        {
            ["Monday"] = [new WorkIntervalDto("14:00", "23:00")], ["Tuesday"] = [new WorkIntervalDto("14:00", "23:00")]
        })
    };

    [Fact]
    public void Shift_is_evaluated_in_its_own_time_zone()
    {
        var shift = EveningShift();
        Assert.False(ShiftSchedule.IsOnShift(shift, Ist(10, 12, 10)));   // Monday 10:00 IST
        Assert.True(ShiftSchedule.IsOnShift(shift, Ist(10, 12, 14)));    // Monday 14:00 IST
        Assert.True(ShiftSchedule.IsOnShift(shift, Ist(10, 12, 22, 59)));
        Assert.False(ShiftSchedule.IsOnShift(shift, Ist(10, 14, 15)));   // Wednesday: no hours
    }

    [Fact]
    public void Missing_or_inactive_shift_means_always_on_shift()
    {
        Assert.True(ShiftSchedule.IsOnShift(null, Ist(10, 12, 3)));
        var inactive = EveningShift();
        inactive.IsActive = false;
        Assert.True(ShiftSchedule.IsOnShift(inactive, Ist(10, 12, 3)));
        Assert.True(ShiftSchedule.IsOnShift(new Shift { WeeklyHoursJson = "{}" }, Ist(10, 12, 3)));
    }

    [Fact]
    public void Effective_assignment_is_the_latest_started_within_its_window()
    {
        var day = new DateOnly(2026, 10, 12);
        var a = new AgentShiftAssignment { ShiftId = Guid.NewGuid(), EffectiveFrom = new DateOnly(2026, 1, 1) };
        var b = new AgentShiftAssignment { ShiftId = Guid.NewGuid(), EffectiveFrom = new DateOnly(2026, 10, 1), EffectiveTo = new DateOnly(2026, 10, 31) };
        var future = new AgentShiftAssignment { ShiftId = Guid.NewGuid(), EffectiveFrom = new DateOnly(2026, 11, 1) };
        Assert.Same(b, ShiftSchedule.EffectiveAssignment([a, b, future], day));
        Assert.Same(future, ShiftSchedule.EffectiveAssignment([a, b, future], new DateOnly(2026, 11, 2)));
        Assert.Same(a, ShiftSchedule.EffectiveAssignment([a, b], new DateOnly(2026, 11, 2)));
        Assert.Null(ShiftSchedule.EffectiveAssignment([future], day));
    }

    [Fact]
    public void Weekly_hours_validation_rejects_overlaps_and_bad_times()
    {
        Assert.Throws<ValidationException>(() => WeeklyHours.ToJson(new Dictionary<string, IReadOnlyList<WorkIntervalDto>>
        {
            ["Monday"] = [new WorkIntervalDto("09:00", "13:00"), new WorkIntervalDto("12:00", "18:00")]
        }));
        Assert.Throws<ValidationException>(() => WeeklyHours.ToJson(new Dictionary<string, IReadOnlyList<WorkIntervalDto>> { ["Monday"] = [new WorkIntervalDto("18:00", "09:00")] }));
        Assert.Throws<ValidationException>(() => WeeklyHours.ToJson(new Dictionary<string, IReadOnlyList<WorkIntervalDto>> { ["Funday"] = [new WorkIntervalDto("09:00", "10:00")] }));
        Assert.Throws<ValidationException>(() => WeeklyHours.ToJson(new Dictionary<string, IReadOnlyList<WorkIntervalDto>> { ["Monday"] = [new WorkIntervalDto("9am", "10:00")] }));

        var json = WeeklyHours.ToJson(new Dictionary<string, IReadOnlyList<WorkIntervalDto>>
        {
            ["saturday"] = [new WorkIntervalDto("14:00", "24:00"), new WorkIntervalDto("08:00", "12:00")]
        });
        var round = WeeklyHours.FromJson(json);
        Assert.Equal(7, round.Count);
        Assert.Equal(["08:00", "14:00"], round["Saturday"].Select(i => i.Start));
        Assert.Equal("24:00", round["Saturday"][1].End);
        Assert.Equal(14, WeeklyHours.TotalHours(json));
    }

    [Fact]
    public void Business_time_skips_weekend_and_holidays()
    {
        var calendar = WorkingCalendar.WeekdaysCalendar("Asia/Kolkata", TimeSpan.FromHours(9), TimeSpan.FromHours(18), [new DateOnly(2026, 10, 19)]);
        // Friday 17:00 → Tuesday 10:00 IST with Monday a holiday: 60 + 60 minutes.
        Assert.Equal(120, calendar.WorkingMinutesBetween(Ist(10, 16, 17), Ist(10, 20, 10)));
        Assert.Equal(Ist(10, 20, 9, 30), calendar.AddWorkingMinutes(Ist(10, 16, 17), 90));
    }
}
