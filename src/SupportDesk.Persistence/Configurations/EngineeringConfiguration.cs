using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportDesk.Domain.Integration;

namespace SupportDesk.Persistence.Configurations;

/// <summary>Built-in engineering tracker work items (kanban board).</summary>
public sealed class LocalWorkItemConfiguration : IEntityTypeConfiguration<LocalWorkItem>
{
    public void Configure(EntityTypeBuilder<LocalWorkItem> e)
    {
        e.ToTable("LocalWorkItems");
        e.Property(x => x.Title).HasMaxLength(500);
        e.Property(x => x.AreaPath).HasMaxLength(300);
        e.Property(x => x.IterationPath).HasMaxLength(300);
        e.Property(x => x.FixVersion).HasMaxLength(100);
        e.Property(x => x.TagsJson).HasMaxLength(2000);
        e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        e.HasIndex(x => new { x.TenantId, x.IntegrationId, x.State, x.Rank });
        e.HasIndex(x => new { x.TenantId, x.UpdatedAt, x.Id });
        e.HasIndex(x => x.ReleaseId);
    }
}

/// <summary>Built-in engineering tracker releases.</summary>
public sealed class LocalReleaseConfiguration : IEntityTypeConfiguration<LocalRelease>
{
    public void Configure(EntityTypeBuilder<LocalRelease> e)
    {
        e.ToTable("LocalReleases");
        e.Property(x => x.Name).HasMaxLength(100);
        e.HasIndex(x => new { x.TenantId, x.IntegrationId, x.Name }).IsUnique();
    }
}

/// <summary>Shared last-known state of linked work items (one row per tracker item).</summary>
public sealed class EngineeringItemConfiguration : IEntityTypeConfiguration<EngineeringItem>
{
    public void Configure(EntityTypeBuilder<EngineeringItem> e)
    {
        e.ToTable("EngineeringItems");
        e.Property(x => x.ExternalId).HasMaxLength(200);
        e.Property(x => x.TrackerType).HasMaxLength(100);
        e.Property(x => x.Url).HasMaxLength(1000);
        e.Property(x => x.Project).HasMaxLength(300);
        e.Property(x => x.State).HasMaxLength(100);
        e.Property(x => x.AssignedTo).HasMaxLength(300);
        e.Property(x => x.AreaPath).HasMaxLength(300);
        e.Property(x => x.IterationPath).HasMaxLength(300);
        e.Property(x => x.FixVersion).HasMaxLength(100);
        e.Property(x => x.ReleaseName).HasMaxLength(200);
        e.Property(x => x.OrphanReason).HasMaxLength(40);
        e.Property(x => x.LastSyncError).HasMaxLength(1000);
        e.HasIndex(x => new { x.TenantId, x.IntegrationId, x.ExternalId }).IsUnique();
        // Polling picks items that are still being worked on.
        e.HasIndex(x => new { x.TenantId, x.IntegrationId, x.LastSyncedAt }).HasFilter("[IsOrphaned] = 0");
    }
}

/// <summary>Inbound tracker webhook deliveries (dedupe by delivery id).</summary>
public sealed class EngineeringWebhookDeliveryConfiguration : IEntityTypeConfiguration<EngineeringWebhookDelivery>
{
    public void Configure(EntityTypeBuilder<EngineeringWebhookDelivery> e)
    {
        e.ToTable("EngineeringWebhookDeliveries");
        e.Property(x => x.DeliveryId).HasMaxLength(200);
        e.Property(x => x.EventType).HasMaxLength(120);
        e.Property(x => x.ExternalId).HasMaxLength(200);
        e.Property(x => x.Detail).HasMaxLength(1000);
        e.HasIndex(x => new { x.IntegrationId, x.DeliveryId }).IsUnique();
        e.HasIndex(x => new { x.TenantId, x.IntegrationId, x.ReceivedAt });
    }
}

/// <summary>Microsoft Teams / Slack incoming-webhook destinations.</summary>
public sealed class CollaborationChannelConfiguration : IEntityTypeConfiguration<CollaborationChannel>
{
    public void Configure(EntityTypeBuilder<CollaborationChannel> e)
    {
        e.ToTable("CollaborationChannels");
        e.Property(x => x.Name).HasMaxLength(200);
        e.Property(x => x.UrlHint).HasMaxLength(200);
        e.Property(x => x.EventsJson).HasMaxLength(2000);
        e.Property(x => x.DepartmentIdsJson).HasMaxLength(4000);
        e.Property(x => x.LastError).HasMaxLength(1000);
        e.HasIndex(x => new { x.TenantId, x.IsActive });
    }
}
