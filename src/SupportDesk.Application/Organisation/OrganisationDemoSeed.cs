using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Common;
using SupportDesk.Application.Seed;
using SupportDesk.Contracts.Organisation;
using SupportDesk.Domain.Organisation;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Organisation;

/// <summary>
/// Demo data for the organisation module (tenant "wings"): shift patterns and assignments, an "India 2026" holiday
/// calendar (used by Implementation), extra skills, reporting lines, routing options (fallback queue, shift-aware L2,
/// territory routing for Customer Success), one agent on approved leave with a delegate, and a pending leave request
/// in the supervisor's approvals inbox.
/// </summary>
public sealed class OrganisationDemoSeedContributor : IDemoSeedContributor
{
    public int Order => 10;

    public async Task SeedAsync(DemoSeedContext c, CancellationToken ct)
    {
        c.At(c.Anchor);
        await c.AsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            if (await db.Shifts.AnyAsync(ct)) return;
            var now = c.Anchor;
            var today = DateOnly.FromDateTime(now);

            // Shifts (IST)
            string Weekly(params (DayOfWeek Day, string Start, string End)[] slots) =>
                WorkingCalendar.WeeklyToJson(slots.GroupBy(s => s.Day).ToDictionary(g => g.Key,
                    g => (IReadOnlyList<WorkInterval>)g.Select(s => new WorkInterval(WorkingCalendar.ParseClock(s.Start), WorkingCalendar.ParseClock(s.End))).ToList()));
            var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
            var day = new Shift
            {
                Name = "India day", Description = "Mon–Fri 09:00–18:00 IST", TimeZone = "Asia/Kolkata", Color = "#2563eb",
                WeeklyHoursJson = Weekly(weekdays.Select(d => (d, "09:00", "18:00")).ToArray())
            };
            var evening = new Shift
            {
                Name = "India evening", Description = "Mon–Fri 14:00–23:00 IST (US overlap)", TimeZone = "Asia/Kolkata", Color = "#7c3aed",
                WeeklyHoursJson = Weekly(weekdays.Select(d => (d, "14:00", "23:00")).ToArray())
            };
            var weekend = new Shift
            {
                Name = "Weekend cover", Description = "Sat–Sun 10:00–16:00 IST on-call", TimeZone = "Asia/Kolkata", Color = "#16a34a",
                WeeklyHoursJson = Weekly((DayOfWeek.Saturday, "10:00", "16:00"), (DayOfWeek.Sunday, "10:00", "16:00"))
            };
            db.Shifts.AddRange(day, evening, weekend);
            var from = today.AddDays(-60);
            foreach (var key in new[] { "priya", "arjun", "meera", "rahul", "divya", "sana", "vikram", "lakshmi" })
                db.AgentShiftAssignments.Add(new AgentShiftAssignment { UserId = c.Users[key], ShiftId = day.Id, EffectiveFrom = from });
            db.AgentShiftAssignments.Add(new AgentShiftAssignment { UserId = c.Users["karthik"], ShiftId = evening.Id, EffectiveFrom = from });

            // India 2026 public holidays (indicative gazetted list) on a dedicated calendar used by Implementation.
            var india = new BusinessCalendar
            {
                Name = "India 2026 (Mon–Fri 09:30–18:30 IST)", Description = "Indian business hours with the 2026 public holiday list", TimeZone = "Asia/Kolkata",
                WeeklyHoursJson = Weekly(weekdays.Select(d => (d, "09:30", "18:30")).ToArray())
            };
            db.BusinessCalendars.Add(india);
            (int M, int D, string Name)[] holidays2026 =
            [
                (1, 26, "Republic Day"), (3, 4, "Holi"), (3, 21, "Id-ul-Fitr"), (3, 26, "Ram Navami"), (3, 31, "Mahavir Jayanti"), (4, 3, "Good Friday"),
                (5, 1, "Buddha Purnima"), (5, 27, "Id-ul-Zuha (Bakrid)"), (6, 26, "Muharram"), (8, 15, "Independence Day"), (8, 26, "Milad-un-Nabi"),
                (9, 4, "Janmashtami"), (10, 2, "Gandhi Jayanti"), (10, 20, "Dussehra"), (11, 8, "Diwali"), (11, 24, "Guru Nanak Jayanti"), (12, 25, "Christmas")
            ];
            foreach (var (m, d, name) in holidays2026)
                db.CalendarHolidays.Add(new CalendarHoliday { CalendarId = india.Id, Date = new DateOnly(2026, m, d), Name = name });
            var implementation = await db.Departments.FirstAsync(d => d.Id == c.Departments["implementation"], ct);
            implementation.BusinessCalendarId = india.Id;

            // Extra skills
            var skills = new[]
            {
                new Skill { Name = "Tally migration", Category = "Implementation", Description = "Moving customers from Tally to Wings ERP" },
                new Skill { Name = "Hindi support", Category = "Language" },
                new Skill { Name = "Tamil support", Category = "Language" },
                new Skill { Name = "Escalation handling", Category = "Soft skills", Description = "Calming upset customers and managing executive escalations" }
            };
            db.Skills.AddRange(skills);
            db.AgentSkills.Add(new AgentSkill { UserId = c.Users["rahul"], SkillId = skills[0].Id, Level = 4 });
            db.AgentSkills.Add(new AgentSkill { UserId = c.Users["divya"], SkillId = skills[0].Id, Level = 5 });
            db.AgentSkills.Add(new AgentSkill { UserId = c.Users["karthik"], SkillId = skills[1].Id, Level = 4 });
            db.AgentSkills.Add(new AgentSkill { UserId = c.Users["meera"], SkillId = skills[2].Id, Level = 5 });
            db.AgentSkills.Add(new AgentSkill { UserId = c.Users["priya"], SkillId = skills[3].Id, Level = 5 });
            db.AgentSkills.Add(new AgentSkill { UserId = c.Users["vikram"], SkillId = skills[3].Id, Level = 4 });

            // Reporting lines
            foreach (var key in new[] { "arjun", "meera", "karthik" })
                (await db.Agents.FirstAsync(a => a.UserId == c.Users[key], ct)).ReportsToUserId = c.Users["priya"];
            (await db.Agents.FirstAsync(a => a.UserId == c.Users["rahul"], ct)).ReportsToUserId = c.Users["divya"];

            // Routing configuration: L1 overflows to L2; L2 is shift-aware with a capacity cap; Customer Success routes by territory.
            var l1 = await db.Teams.FirstAsync(t => t.Id == c.Teams["support"], ct);
            l1.FallbackTeamId = c.Teams["support-l2"];
            var l2 = await db.Teams.FirstAsync(t => t.Id == c.Teams["support-l2"], ct);
            l2.ConsiderShifts = true;
            l2.MaxOpenTicketsPerAgent = 20;
            var success = await db.Teams.FirstAsync(t => t.Id == c.Teams["success"], ct);
            success.RoutingStrategy = RoutingStrategy.Territory;
            db.AgentRoutingAttributes.Add(new AgentRoutingAttribute { UserId = c.Users["vikram"], Kind = RoutingAttributeKind.Territory, Value = "North" });

            // Rahul is on approved leave this week; Divya covers for him.
            var leaveStart = now.Date.AddDays(-1).AddHours(-5.5);
            var leaveEnd = now.Date.AddDays(4).AddHours(-5.5);
            db.AgentLeaves.Add(new AgentLeave
            {
                UserId = c.Users["rahul"], StartsAt = leaveStart, EndsAt = leaveEnd, Type = LeaveType.Leave, Status = LeaveStatus.Approved,
                Reason = "Family function in Jaipur", ApprovedById = c.Users["divya"], DecidedAt = now.AddDays(-6), DecisionNote = "Approved — Divya covers"
            });
            db.AgentDelegations.Add(new AgentDelegation
            {
                UserId = c.Users["rahul"], DelegateUserId = c.Users["divya"], StartsAt = leaveStart, EndsAt = leaveEnd,
                Reason = "Annual leave", ReassignOpenTickets = false, IncludeApprovals = true
            });
            await db.SaveChangesAsync(ct);
        });

        // A pending leave request in Priya's approvals inbox (created through the real service as Arjun).
        await c.AsAgentAsync("arjun", async sp =>
        {
            var start = c.Anchor.Date.AddDays(10).AddHours(-5.5);
            await sp.GetRequiredService<WorkforceService>().RequestLeaveAsync(
                new CreateLeaveRequest(null, start, start.AddDays(2), "Training", "Wings ERP 26.1 certification workshop"), ct);
        });
    }
}
