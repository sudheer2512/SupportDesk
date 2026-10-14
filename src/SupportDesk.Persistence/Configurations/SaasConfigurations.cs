using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportDesk.Domain.Platform;

namespace SupportDesk.Persistence.Configurations;

/// <summary>Daily usage history per tenant and metric (history charts, platform console).</summary>
public sealed class UsageSnapshotConfiguration : IEntityTypeConfiguration<UsageSnapshot>
{
    public void Configure(EntityTypeBuilder<UsageSnapshot> e)
    {
        e.Property(x => x.Metric).HasMaxLength(40);
        e.HasIndex(x => new { x.TenantId, x.Metric, x.Day }).IsUnique();
        e.HasIndex(x => new { x.Day, x.Metric });
    }
}

/// <summary>Pending self-service trial sign-ups (global; slug and e-mail are looked up case-normalised).</summary>
public sealed class TrialSignupConfiguration : IEntityTypeConfiguration<TrialSignup>
{
    public void Configure(EntityTypeBuilder<TrialSignup> e)
    {
        e.Property(x => x.Slug).HasMaxLength(64);
        e.Property(x => x.CompanyName).HasMaxLength(200);
        e.Property(x => x.AdminName).HasMaxLength(200);
        e.Property(x => x.AdminEmail).HasMaxLength(320);
        e.Property(x => x.AdminEmailNormalized).HasMaxLength(320);
        e.Property(x => x.PlanCode).HasMaxLength(40);
        e.Property(x => x.CodeHash).HasMaxLength(128);
        e.Property(x => x.IpAddress).HasMaxLength(64);
        e.HasIndex(x => new { x.Slug, x.Status });
        e.HasIndex(x => new { x.AdminEmailNormalized, x.CreatedAt });
        e.HasIndex(x => new { x.IpAddress, x.CreatedAt });
    }
}

/// <summary>Tenant-approved, time-boxed platform support access.</summary>
public sealed class SupportAccessGrantConfiguration : IEntityTypeConfiguration<SupportAccessGrant>
{
    public void Configure(EntityTypeBuilder<SupportAccessGrant> e)
    {
        e.Property(x => x.OperatorName).HasMaxLength(200);
        e.Property(x => x.DecidedByName).HasMaxLength(200);
        e.Property(x => x.Reason).HasMaxLength(1000);
        e.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
        e.HasIndex(x => new { x.OperatorId, x.Status });
    }
}

/// <summary>Imported configuration drafts (diff preview + resolutions until publish).</summary>
public sealed class ConfigurationDraftConfiguration : IEntityTypeConfiguration<ConfigurationDraft>
{
    public void Configure(EntityTypeBuilder<ConfigurationDraft> e)
    {
        e.Property(x => x.Label).HasMaxLength(200);
        e.Property(x => x.SourceTenant).HasMaxLength(64);
        e.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
    }
}

/// <summary>Invoice generation is idempotent: one live subscription invoice per subscription and period.</summary>
public sealed class BillingInvoicePeriodConfiguration : IEntityTypeConfiguration<BillingInvoice>
{
    public void Configure(EntityTypeBuilder<BillingInvoice> e)
    {
        e.HasIndex(x => new { x.TenantId, x.SubscriptionId, x.PeriodStart })
            .IsUnique()
            .HasFilter("[Kind] = 'Subscription' AND [Status] <> 'Void'")
            .HasDatabaseName("IX_BillingInvoices_OnePerPeriod");
        e.HasIndex(x => new { x.Status, x.DueAt });
        e.HasIndex(x => x.IssuedAt);
    }
}

/// <summary>Data-subject export downloads are looked up by the hash of their one-time token.</summary>
public sealed class DataRequestTokenConfiguration : IEntityTypeConfiguration<DataRequest>
{
    public void Configure(EntityTypeBuilder<DataRequest> e)
    {
        e.Property(x => x.DownloadTokenHash).HasMaxLength(128);
        e.HasIndex(x => x.DownloadTokenHash).IsUnique().HasFilter("[DownloadTokenHash] IS NOT NULL");
        e.HasIndex(x => new { x.TenantId, x.ContactId });
    }
}
