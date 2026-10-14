using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportDesk.Domain.Communication;

namespace SupportDesk.Persistence.Configurations;

/// <summary>
/// Mobile push registrations (agents and portal contacts). The (TenantId, Token) unique index lives in
/// ModelConfiguration; these indexes serve "devices of this user/contact" lookups by the push dispatchers.
/// </summary>
public sealed class PushDeviceConfiguration : IEntityTypeConfiguration<PushDevice>
{
    public void Configure(EntityTypeBuilder<PushDevice> e)
    {
        e.Property(x => x.Token).HasMaxLength(450);
        e.Property(x => x.AppVersion).HasMaxLength(50);
        e.Property(x => x.DeactivatedReason).HasMaxLength(200);
        e.HasIndex(x => new { x.TenantId, x.UserId }).HasFilter("[UserId] IS NOT NULL AND [IsActive] = 1");
        e.HasIndex(x => new { x.TenantId, x.ContactId }).HasFilter("[ContactId] IS NOT NULL AND [IsActive] = 1");
    }
}
