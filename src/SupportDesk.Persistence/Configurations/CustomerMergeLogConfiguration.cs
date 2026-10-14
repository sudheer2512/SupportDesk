using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportDesk.Domain.Customers;

namespace SupportDesk.Persistence.Configurations;

/// <summary>Organisation merge history: looked up by survivor (Customer 360 history) and by merged customer (idempotency, redirects).</summary>
public sealed class CustomerMergeLogConfiguration : IEntityTypeConfiguration<CustomerMergeLog>
{
    public void Configure(EntityTypeBuilder<CustomerMergeLog> e)
    {
        e.HasIndex(x => new { x.TenantId, x.SurvivorCustomerId });
        e.HasIndex(x => new { x.TenantId, x.MergedCustomerId });
    }
}
