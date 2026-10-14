namespace SupportDesk.Domain.Customers;

/// <summary>
/// One organisation merge: the merged customer (now <see cref="CustomerStatus.Merged"/>, hidden and read-only) was folded
/// into the survivor. Keeps a snapshot of the merged customer, the moved record counts and the fields copied (audit and
/// idempotent replays of the same merge).
/// </summary>
public class CustomerMergeLog : TenantEntity
{
    public Guid SurvivorCustomerId { get; set; }
    public Guid MergedCustomerId { get; set; }
    public Guid? MergedById { get; set; }
    public DateTime MergedAt { get; set; }
    /// <summary>{"merged":{…snapshot…},"counts":{"contacts":3,…},"fieldsCopied":["phone"],"mergedBy":"Priya"}</summary>
    public string SnapshotJson { get; set; } = "{}";
}
