using System.Text.Json;

namespace SupportDesk.Domain.Rules;

/// <summary>A working interval within a local day. End may be 24:00.</summary>
public readonly record struct WorkInterval(TimeSpan Start, TimeSpan End);

/// <summary>
/// Working calendar used for SLA business-hour arithmetic. Weekly hours are expressed in the calendar's
/// time zone; DST transitions are handled by converting each local interval to UTC individually.
/// Holidays remove whole local days.
/// </summary>
public sealed class WorkingCalendar
{
    private const int MaxDaysScanned = 3 * 366;

    public string TimeZoneId { get; }
    public bool Is24x7 { get; }
    public IReadOnlyDictionary<DayOfWeek, IReadOnlyList<WorkInterval>> Weekly { get; }
    public IReadOnlySet<DateOnly> Holidays { get; }
    public IReadOnlySet<(int Month, int Day)> RecurringHolidays { get; }
    private readonly TimeZoneInfo _tz;

    public static readonly WorkingCalendar AlwaysOpen = new("UTC", true, new Dictionary<DayOfWeek, IReadOnlyList<WorkInterval>>(), [], []);

    public WorkingCalendar(string timeZoneId, bool is24x7,
        IReadOnlyDictionary<DayOfWeek, IReadOnlyList<WorkInterval>> weekly,
        IEnumerable<DateOnly> holidays, IEnumerable<(int Month, int Day)> recurringHolidays)
    {
        TimeZoneId = timeZoneId;
        Is24x7 = is24x7;
        Weekly = weekly;
        Holidays = holidays.ToHashSet();
        RecurringHolidays = recurringHolidays.ToHashSet();
        _tz = ResolveTimeZone(timeZoneId);
        if (!is24x7 && !weekly.Values.Any(v => v.Any(i => i.End > i.Start)))
            throw new DomainException("calendar_empty", "A business calendar must have at least one working interval.");
    }

    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz) ? tz : TimeZoneInfo.Utc;
    }

    /// <summary>Parses {"Monday":[{"start":"09:00","end":"18:00"}],…}. Day names are case-insensitive.</summary>
    public static Dictionary<DayOfWeek, IReadOnlyList<WorkInterval>> ParseWeekly(string? json)
    {
        var result = new Dictionary<DayOfWeek, IReadOnlyList<WorkInterval>>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!Enum.TryParse<DayOfWeek>(prop.Name, true, out var day) || prop.Value.ValueKind != JsonValueKind.Array) continue;
            var list = new List<WorkInterval>();
            foreach (var item in prop.Value.EnumerateArray())
            {
                var start = ParseClock(item.GetProperty("start").GetString());
                var end = ParseClock(item.GetProperty("end").GetString());
                if (end > start) list.Add(new WorkInterval(start, end));
            }
            result[day] = list.OrderBy(i => i.Start).ToList();
        }
        return result;
    }

    public static string WeeklyToJson(IReadOnlyDictionary<DayOfWeek, IReadOnlyList<WorkInterval>> weekly) =>
        JsonSerializer.Serialize(weekly.ToDictionary(k => k.Key.ToString(),
            v => v.Value.Select(i => new { start = FormatClock(i.Start), end = FormatClock(i.End) })));

    public static TimeSpan ParseClock(string? hhmm)
    {
        if (string.IsNullOrWhiteSpace(hhmm)) return TimeSpan.Zero;
        var parts = hhmm.Split(':');
        var h = int.Parse(parts[0]);
        var m = parts.Length > 1 ? int.Parse(parts[1]) : 0;
        return h >= 24 ? TimeSpan.FromHours(24) : new TimeSpan(h, m, 0);
    }

    public static string FormatClock(TimeSpan t) => t >= TimeSpan.FromHours(24) ? "24:00" : $"{t.Hours:00}:{t.Minutes:00}";

    /// <summary>Standard Monday–Friday calendar.</summary>
    public static WorkingCalendar WeekdaysCalendar(string tz, TimeSpan start, TimeSpan end, IEnumerable<DateOnly>? holidays = null)
    {
        var day = new List<WorkInterval> { new(start, end) };
        var weekly = new Dictionary<DayOfWeek, IReadOnlyList<WorkInterval>>
        {
            [DayOfWeek.Monday] = day, [DayOfWeek.Tuesday] = day, [DayOfWeek.Wednesday] = day,
            [DayOfWeek.Thursday] = day, [DayOfWeek.Friday] = day
        };
        return new WorkingCalendar(tz, false, weekly, holidays ?? [], []);
    }

    public bool IsHoliday(DateOnly localDate) =>
        Holidays.Contains(localDate) || RecurringHolidays.Contains((localDate.Month, localDate.Day));

    /// <summary>Working intervals (UTC, ordered, non-overlapping) starting at or after <paramref name="fromUtc"/>.</summary>
    public IEnumerable<(DateTime Start, DateTime End)> IntervalsFrom(DateTime fromUtc)
    {
        fromUtc = EnsureUtc(fromUtc);
        if (Is24x7)
        {
            yield return (fromUtc, DateTime.MaxValue);
            yield break;
        }

        var localStart = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, _tz);
        var date = DateOnly.FromDateTime(localStart);
        for (var i = 0; i < MaxDaysScanned; i++, date = date.AddDays(1))
        {
            if (IsHoliday(date) || !Weekly.TryGetValue(date.DayOfWeek, out var intervals)) continue;
            foreach (var iv in intervals)
            {
                var s = LocalToUtc(date, iv.Start);
                var e = LocalToUtc(date, iv.End);
                if (e <= fromUtc) continue;
                if (s < fromUtc) s = fromUtc;
                if (e > s) yield return (s, e);
            }
        }
    }

    /// <summary>Adds working minutes to a UTC instant and returns the UTC due time.</summary>
    public DateTime AddWorkingMinutes(DateTime startUtc, double minutes)
    {
        startUtc = EnsureUtc(startUtc);
        if (minutes <= 0) return startUtc;
        if (Is24x7) return startUtc.AddMinutes(minutes);
        var remaining = minutes;
        foreach (var (s, e) in IntervalsFrom(startUtc))
        {
            var available = (e - s).TotalMinutes;
            if (remaining <= available) return s.AddMinutes(remaining);
            remaining -= available;
        }
        throw new DomainException("calendar_exhausted", "The business calendar has no working time within the next three years.");
    }

    /// <summary>Working minutes elapsed between two UTC instants (0 if to &lt;= from).</summary>
    public double WorkingMinutesBetween(DateTime fromUtc, DateTime toUtc)
    {
        fromUtc = EnsureUtc(fromUtc);
        toUtc = EnsureUtc(toUtc);
        if (toUtc <= fromUtc) return 0;
        if (Is24x7) return (toUtc - fromUtc).TotalMinutes;
        double total = 0;
        foreach (var (s, e) in IntervalsFrom(fromUtc))
        {
            if (s >= toUtc) break;
            total += ((e < toUtc ? e : toUtc) - s).TotalMinutes;
        }
        return total;
    }

    public bool IsWorkingTime(DateTime utc)
    {
        if (Is24x7) return true;
        var first = IntervalsFrom(utc).FirstOrDefault();
        return first != default && first.Start <= EnsureUtc(utc);
    }

    /// <summary>The next instant (UTC) at which the calendar is working (the instant itself if already working).</summary>
    public DateTime NextWorkingTime(DateTime utc)
    {
        var first = IntervalsFrom(utc).FirstOrDefault();
        if (first == default) throw new DomainException("calendar_exhausted", "No upcoming working time.");
        return first.Start;
    }

    private DateTime LocalToUtc(DateOnly date, TimeSpan clock)
    {
        var local = date.ToDateTime(TimeOnly.MinValue).Add(clock);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Spring-forward gap: shift to the first valid local instant.
        while (_tz.IsInvalidTime(local)) local = local.AddMinutes(15);
        return TimeZoneInfo.ConvertTimeToUtc(local, _tz);
    }

    private static DateTime EnsureUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc)
    };
}
