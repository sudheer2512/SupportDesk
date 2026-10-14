namespace SupportDesk.Domain.Configuration;

public enum CustomFieldType
{
    Text, TextArea, Number, Decimal, Currency, Date, DateTime, Checkbox, Picklist, MultiPicklist, Url, Email, Phone, Lookup
}

/// <summary>Tenant-defined field on tickets, contacts or customers. Values are stored in the entity's CustomFieldsJson.</summary>
public class CustomFieldDefinition : AuditedEntity
{
    /// <summary>Ticket, Contact, Customer</summary>
    public string Entity { get; set; } = "Ticket";
    /// <summary>Stable machine key (snake_case), unique per entity.</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public CustomFieldType FieldType { get; set; }
    /// <summary>Picklist options: [{"value":"fin","label":"Finance"}]; Lookup: {"entity":"Product"}.</summary>
    public string OptionsJson { get; set; } = "[]";
    public bool IsRequired { get; set; }
    public string? DefaultValue { get; set; }
    public string? HelpText { get; set; }
    public string? ValidationRegex { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public int? MaxLength { get; set; }
    public bool VisibleInPortal { get; set; }
    public bool EditableInPortal { get; set; }
    /// <summary>Departments where the field applies; empty = all.</summary>
    public string DepartmentIdsJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public enum LayoutKind { AgentForm, PortalForm }

/// <summary>
/// Form layout (form builder output). SectionsJson: [{"title":"Product","columns":2,"fields":[{"key":"productId","required":true},{"key":"cf:erp_module"}]}]
/// Standard field keys are plain names; custom fields use the "cf:" prefix.
/// </summary>
public class FormLayout : AuditedEntity
{
    public string Entity { get; set; } = "Ticket";
    public string Name { get; set; } = "";
    public LayoutKind Kind { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BrandId { get; set; }
    public string SectionsJson { get; set; } = "[]";
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}
