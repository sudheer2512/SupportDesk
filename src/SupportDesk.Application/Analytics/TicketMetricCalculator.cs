using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Sla;
using SupportDesk.Domain.Tickets;

namespace SupportDesk.Application.Analytics;

/// <summary>A conversation entry relevant to metrics.</summary>
public sealed record ThreadFact(ThreadKind Kind, Visibility Visibility, Guid? AuthorUserId, DateTime CreatedAt);

/// <summary>A ticket activity record relevant to metrics (TicketEvent type + time).</summary>
public sealed record ActivityFact(string Type, DateTime OccurredAt);

/// <summary>An SLA milestone outcome.</summary>
public sealed record MilestoneFact(SlaMilestoneKind Kind, MilestoneStatus Status, DateTime? CompletedAt, DateTime? BreachedAt);

/// <summary>Everything the calculator needs about one ticket (loaded by <see cref="TicketMetricBuilder"/>).</summary>
public sealed record TicketFacts(
    Ticket Ticket,
    IReadOnlyList<ThreadFact> Threads,
    IReadOnlyList<ActivityFact> Activity,
    IReadOnlyList<MilestoneFact> Milestones,
    int LoggedMinutes,
    int BillableMinutes,
    decimal Cost,
    string? CustomerTier);

/// <summary>
/// Pure computation of a <see cref="TicketMetric"/> row from ticket facts (definitions in docs/METRICS.md):
/// calendar minutes use SQL DATEDIFF(minute) semantics (minute boundaries crossed) so they equal ad-hoc SQL over
/// tickets; business minutes are working minutes of the ticket's business calendar between the same instants.
/// </summary>
public static class TicketMetricCalculator
{
    public static void Apply(TicketMetric m, TicketFacts f, WorkingCalendar calendar, TimeZoneInfo tz, DateTime now)
    {
        var t = f.Ticket;
        m.TicketId = t.Id;
        m.TenantId = t.TenantId;
        m.TicketNumber = t.Number;
        m.CreatedAt = t.CreatedAt;
        m.CreatedDay = AnalyticsPeriods.LocalDay(t.CreatedAt, tz);
        m.Channel = t.Channel;
        m.Priority = t.Priority;
        m.StatusCategory = t.StatusCategory;
        m.DepartmentId = t.DepartmentId;
        m.TeamId = t.TeamId;
        m.AssigneeId = t.AssigneeId;
        m.CustomerId = t.CustomerId;
        m.CustomerTier = f.CustomerTier;
        m.ContactId = t.ContactId;
        m.LocationId = t.LocationId;
        m.ProductId = t.ProductId;
        m.ModuleId = t.ModuleId;
        m.VersionId = t.VersionId;
        m.CategoryId = t.CategoryId;
        m.SubcategoryId = t.SubcategoryId;
        m.IssueTypeId = t.IssueTypeId;
        m.RootCause = Trim(t.RootCause, 100);
        m.ResolutionCode = Trim(t.ResolutionCode, 100);
        m.Sentiment = t.Sentiment;
        m.Excluded = t.IsDeleted || t.IsSpam || t.MergedIntoTicketId != null;

        // First response: the ticket's first public agent reply.
        var firstReply = f.Threads.Where(x => x.Kind == ThreadKind.AgentReply && x.Visibility == Visibility.Public).OrderBy(x => x.CreatedAt).FirstOrDefault();
        m.FirstResponseAt = t.FirstResponseAt ?? firstReply?.CreatedAt;
        m.FirstResponderId = firstReply?.AuthorUserId;
        m.FirstResponseMinutes = m.FirstResponseAt is { } fr ? CalendarMinutes(t.CreatedAt, fr) : null;
        m.FirstResponseBusinessMinutes = m.FirstResponseAt is { } fr2 ? BusinessMinutes(calendar, t.CreatedAt, fr2) : null;

        // Resolution: created → final resolution (ResolvedAt is cleared on reopen and set again on the next resolution).
        m.ResolvedAt = t.StatusCategory.IsResolvedOrClosed() ? t.ResolvedAt ?? t.ClosedAt : null;
        m.ResolvedDay = m.ResolvedAt is { } r ? AnalyticsPeriods.LocalDay(r, tz) : null;
        m.ClosedAt = t.StatusCategory == StatusCategory.Closed ? t.ClosedAt : null;
        m.ResolutionMinutes = m.ResolvedAt is { } r1 ? CalendarMinutes(t.CreatedAt, r1) : null;
        m.ResolutionBusinessMinutes = m.ResolvedAt is { } r2 ? BusinessMinutes(calendar, t.CreatedAt, r2) : null;

        m.ReopenCount = Math.Max(t.ReopenCount, f.Activity.Count(a => a.Type == TicketEventTypes.Reopened));
        var publicAgentReplies = f.Threads.Count(x => x.Kind == ThreadKind.AgentReply && x.Visibility == Visibility.Public);
        m.AgentReplies = Math.Max(t.AgentReplyCount, publicAgentReplies);
        m.CustomerReplies = Math.Max(t.CustomerReplyCount, f.Threads.Count(x => x.Kind == ThreadKind.CustomerMessage) - 1);
        m.Handoffs = Math.Max(0, t.AssignmentCount - 1);
        m.Transfers = f.Activity.Count(a => a.Type == TicketEventTypes.Transferred);
        m.FirstContactResolution = m.ResolvedAt is not null && m.ReopenCount == 0 && m.AgentReplies <= 1;

        // SLA outcome per milestone across the ticket's SLA instances.
        m.HasSla = f.Milestones.Count > 0 || t.SlaPolicyId is not null && t.SlaState != SlaState.None;
        var first = f.Milestones.Where(x => x.Kind == SlaMilestoneKind.FirstResponse).ToList();
        var resolution = f.Milestones.Where(x => x.Kind == SlaMilestoneKind.Resolution).ToList();
        var next = f.Milestones.Where(x => x.Kind == SlaMilestoneKind.NextResponse).ToList();
        m.FirstResponseBreached = first.Any(Breached);
        m.FirstResponseSlaMet = !m.FirstResponseBreached && first.Any(x => x.Status == MilestoneStatus.Met);
        m.ResolutionBreached = resolution.Any(Breached);
        m.ResolutionSlaMet = !m.ResolutionBreached && resolution.Any(x => x.Status == MilestoneStatus.Met);
        m.NextResponseBreaches = next.Count(Breached);
        m.NextResponseMet = next.Count(x => x.Status == MilestoneStatus.Met && !Breached(x));
        m.SlaBreached = m.FirstResponseBreached || m.ResolutionBreached || m.NextResponseBreaches > 0 || t.SlaState == SlaState.Breached;

        m.EscalationCount = f.Activity.Count(a => a.Type == TicketEventTypes.Escalated);
        m.IsEscalated = t.IsEscalated || m.EscalationCount > 0;
        m.CsatScore = t.CsatScore;
        m.TimeSpentMinutes = f.LoggedMinutes;
        m.BillableMinutes = f.BillableMinutes;
        m.Cost = f.Cost;
        m.SourceVersion = t.RowVersion is { Length: > 0 } rv ? rv : null;
        m.UpdatedAt = now;
    }

    private static bool Breached(MilestoneFact x) => x.BreachedAt is not null || x.Status == MilestoneStatus.Breached;

    /// <summary>SQL Server DATEDIFF(minute, from, to): the number of minute boundaries crossed.</summary>
    public static int CalendarMinutes(DateTime from, DateTime to)
    {
        var diff = to.Ticks / TimeSpan.TicksPerMinute - from.Ticks / TimeSpan.TicksPerMinute;
        return (int)Math.Clamp(diff, 0, int.MaxValue);
    }

    public static int BusinessMinutes(WorkingCalendar calendar, DateTime from, DateTime to) =>
        (int)Math.Round(calendar.WorkingMinutesBetween(from, to), MidpointRounding.AwayFromZero);

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
