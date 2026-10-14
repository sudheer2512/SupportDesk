using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportDesk.Domain.Knowledge;

namespace SupportDesk.Persistence.Configurations;

/// <summary>Knowledge search log (KB analytics: top searches, searches without results).</summary>
public sealed class KnowledgeSearchLogConfiguration : IEntityTypeConfiguration<KnowledgeSearchLog>
{
    public void Configure(EntityTypeBuilder<KnowledgeSearchLog> e)
    {
        e.ToTable("KnowledgeSearchLogs");
        e.Property(x => x.Query).HasMaxLength(300);
        e.Property(x => x.NormalizedQuery).HasMaxLength(300);
        e.Property(x => x.Source).HasMaxLength(20);
        e.Property(x => x.Locale).HasMaxLength(16);
        e.HasIndex(x => new { x.TenantId, x.CreatedAt });
        e.HasIndex(x => new { x.TenantId, x.NormalizedQuery });
    }
}
