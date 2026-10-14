using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportDesk.Domain.Analytics;

namespace SupportDesk.Persistence.Configurations;

/// <summary>
/// Analytics access paths in addition to the base keys in ModelConfiguration: period scans by created/resolved instant
/// (UTC ranges computed on the tenant calendar), the open working set (backlog/ageing) and stale-row detection.
/// </summary>
public sealed class TicketMetricConfiguration : IEntityTypeConfiguration<TicketMetric>
{
    public void Configure(EntityTypeBuilder<TicketMetric> e)
    {
        e.HasIndex(x => new { x.TenantId, x.CreatedAt }).HasDatabaseName("IX_TicketMetrics_Tenant_CreatedAt");
        e.HasIndex(x => new { x.TenantId, x.ResolvedAt }).HasDatabaseName("IX_TicketMetrics_Tenant_ResolvedAt");
        e.HasIndex(x => new { x.TenantId, x.DepartmentId, x.CreatedAt }).HasDatabaseName("IX_TicketMetrics_Tenant_Department_CreatedAt");
        e.HasIndex(x => new { x.TenantId, x.StatusCategory }).HasFilter("[StatusCategory] < 5 AND [Excluded] = 0").HasDatabaseName("IX_TicketMetrics_Open");
        e.Property(x => x.SourceVersion).HasMaxLength(8);
        e.Property(x => x.CustomerTier).HasMaxLength(60);
        e.Property(x => x.RootCause).HasMaxLength(100);
        e.Property(x => x.ResolutionCode).HasMaxLength(100);
        e.Property(x => x.Cost).HasPrecision(18, 4);
    }
}

public sealed class ScheduledReportRunConfiguration : IEntityTypeConfiguration<ScheduledReportRun>
{
    public void Configure(EntityTypeBuilder<ScheduledReportRun> e)
    {
        e.ToTable("ScheduledReportRuns");
        e.HasIndex(x => new { x.TenantId, x.ScheduledReportId, x.CreatedAt });
        // One automatic run per schedule slot (manual "run now" rows are exempt).
        e.HasIndex(x => new { x.ScheduledReportId, x.ScheduledFor }).IsUnique().HasFilter("[Manual] = 0");
        e.HasOne<ScheduledReport>().WithMany().HasForeignKey(x => x.ScheduledReportId).OnDelete(DeleteBehavior.Cascade);
        e.Property(x => x.FileName).HasMaxLength(200);
        e.Property(x => x.Error).HasMaxLength(1000);
    }
}

public sealed class ReportExportConfiguration : IEntityTypeConfiguration<ReportExport>
{
    public void Configure(EntityTypeBuilder<ReportExport> e)
    {
        e.ToTable("ReportExports");
        e.HasIndex(x => new { x.TenantId, x.RequestedById, x.CreatedAt });
        e.Property(x => x.ReportKey).HasMaxLength(60);
        e.Property(x => x.Title).HasMaxLength(200);
        e.Property(x => x.FileName).HasMaxLength(200);
        e.Property(x => x.Error).HasMaxLength(1000);
    }
}

public sealed class ScheduledReportConfiguration : IEntityTypeConfiguration<ScheduledReport>
{
    public void Configure(EntityTypeBuilder<ScheduledReport> e)
    {
        e.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        e.Property(x => x.Name).HasMaxLength(200);
        e.Property(x => x.ReportKey).HasMaxLength(60);
        e.Property(x => x.TimeZone).HasMaxLength(100);
        e.Property(x => x.LastStatus).HasMaxLength(1000);
    }
}

public sealed class DashboardConfiguration : IEntityTypeConfiguration<Dashboard>
{
    public void Configure(EntityTypeBuilder<Dashboard> e)
    {
        e.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        e.Property(x => x.Name).HasMaxLength(200);
        e.Property(x => x.Audience).HasMaxLength(40);
        e.Property(x => x.Period).HasMaxLength(40);
    }
}
