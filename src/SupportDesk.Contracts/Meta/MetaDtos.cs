using System.Text.Json;
using SupportDesk.Contracts.Common;

namespace SupportDesk.Contracts.Meta;

/// <summary>Everything a ticket form/filter needs in one call (GET /api/v1/meta/ticket-form).</summary>
public sealed record TicketFormMetaDto(
    IReadOnlyList<StatusDto> Statuses,
    IReadOnlyList<string> Priorities,
    IReadOnlyList<string> Severities,
    IReadOnlyList<string> Channels,
    IReadOnlyList<DepartmentRefDto> Departments,
    IReadOnlyList<TeamRefDto> Teams,
    IReadOnlyList<AgentRefDto> Agents,
    IReadOnlyList<CategoryRefDto> Categories,
    IReadOnlyList<IdName> IssueTypes,
    IReadOnlyList<ProductRefDto> Products,
    IReadOnlyList<CustomFieldDto> CustomFields,
    IReadOnlyList<LayoutDto> Layouts,
    IReadOnlyList<LookupDto> Lookups,
    IReadOnlyList<IdName> Brands,
    IReadOnlyList<string> Tags,
    string TicketNumberPrefix);

public sealed record StatusDto(Guid Id, string Name, string Category, string Color, int SortOrder, bool IsDefault, bool PausesSla,
    bool IsActive, string? CustomerLabel, IReadOnlyList<Guid> AllowedNext, IReadOnlyList<string> Substatuses);

public sealed record DepartmentRefDto(Guid Id, string Name, string Code, bool VisibleInPortal);

public sealed record TeamRefDto(Guid Id, string Name, Guid DepartmentId);

public sealed record AgentRefDto(Guid UserId, string Name, string? Email, string? AvatarUrl, string Availability, bool IsLightAgent,
    IReadOnlyList<Guid> DepartmentIds, IReadOnlyList<Guid> TeamIds, int OpenTickets, int Capacity);

public sealed record CategoryRefDto(Guid Id, string Name, Guid? ParentId, Guid? DepartmentId, bool VisibleInPortal);

public sealed record ProductRefDto(Guid Id, string Name, string Code, IReadOnlyList<IdName> Modules, IReadOnlyList<IdName> Versions);

public sealed record CustomFieldDto(Guid Id, string Entity, string Key, string Label, string FieldType, JsonElement Options,
    bool IsRequired, string? DefaultValue, string? HelpText, string? ValidationRegex, bool VisibleInPortal, bool EditableInPortal,
    IReadOnlyList<Guid> DepartmentIds, int SortOrder, bool IsActive);

public sealed record LayoutDto(Guid Id, string Entity, string Name, string Kind, Guid? DepartmentId, Guid? BrandId, JsonElement Sections,
    bool IsDefault);

public sealed record LookupDto(string Type, string Value, string Label);
