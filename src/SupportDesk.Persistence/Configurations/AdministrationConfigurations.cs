using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportDesk.Domain.Administration;
using SupportDesk.Domain.Configuration;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Platform;

namespace SupportDesk.Persistence.Configurations;

/// <summary>E-mail template overrides (administration → notifications). One row per tenant and catalog key.</summary>
public sealed class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> e)
    {
        e.ToTable("NotificationTemplates");
        e.Property(x => x.Key).HasMaxLength(100);
        e.Property(x => x.Subject).HasMaxLength(500);
        e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
    }
}

/// <summary>Administration additions to form layouts (channel-specific layouts).</summary>
public sealed class FormLayoutAdministrationConfiguration : IEntityTypeConfiguration<FormLayout>
{
    public void Configure(EntityTypeBuilder<FormLayout> e) => e.Property(x => x.Channel).HasMaxLength(40);
}

/// <summary>Brand assets and footer (administration → brands &amp; portals).</summary>
public sealed class BrandAdministrationConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> e) => e.Property(x => x.FaviconUrl).HasMaxLength(450);
}

/// <summary>Tenant-wide active-session listing (administration → security → sessions).</summary>
public sealed class UserSessionAdministrationConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> e) => e.HasIndex(x => new { x.TenantId, x.PrincipalKind, x.LastSeenAt });
}
