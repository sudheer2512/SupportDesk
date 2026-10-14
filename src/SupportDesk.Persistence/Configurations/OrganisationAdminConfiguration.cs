using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportDesk.Domain.Identity;
using SupportDesk.Domain.Organisation;

namespace SupportDesk.Persistence.Configurations;

/// <summary>Agent invitations (one-time set-password codes; only hashes stored).</summary>
public sealed class AgentInvitationConfiguration : IEntityTypeConfiguration<AgentInvitation>
{
    public void Configure(EntityTypeBuilder<AgentInvitation> e)
    {
        e.ToTable("AgentInvitations");
        e.Property(x => x.Email).HasMaxLength(320);
        e.Property(x => x.CodeHash).HasMaxLength(64);
        e.HasIndex(x => new { x.TenantId, x.UserId, x.CreatedAt });
        e.HasIndex(x => new { x.TenantId, x.Email });
        e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Routing fallback queue and organisation colour/description columns added by the organisation module.</summary>
public sealed class TeamRoutingConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> e)
    {
        e.HasOne<Team>().WithMany().HasForeignKey(x => x.FallbackTeamId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ShiftDisplayConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> e)
    {
        e.Property(x => x.Color).HasMaxLength(16);
        e.Property(x => x.TimeZone).HasMaxLength(64);
    }
}

public sealed class AgentLeaveConfiguration : IEntityTypeConfiguration<AgentLeave>
{
    public void Configure(EntityTypeBuilder<AgentLeave> e)
    {
        e.HasIndex(x => new { x.TenantId, x.Status, x.StartsAt });
        e.HasIndex(x => x.ApprovalRequestId);
    }
}
