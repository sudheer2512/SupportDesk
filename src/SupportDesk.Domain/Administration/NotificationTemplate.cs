namespace SupportDesk.Domain.Administration;

/// <summary>
/// A tenant override of a built-in e-mail template (customer acknowledgement, agent notification, digest…).
/// Keys come from the template catalog; a missing row means the built-in default is used.
/// Subject and body use {{placeholder}} syntax; values are HTML-encoded when rendered.
/// </summary>
public class NotificationTemplate : AuditedEntity, IConcurrencyVersioned
{
    /// <summary>Catalog key, e.g. "customer.ticket_acknowledgement".</summary>
    public string Key { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>Sanitised HTML body.</summary>
    public string BodyHtml { get; set; } = "";
    /// <summary>When false the built-in default is used (the override is kept for later).</summary>
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}
