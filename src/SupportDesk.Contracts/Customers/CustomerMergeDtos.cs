using System.Text.Json;

namespace SupportDesk.Contracts.Customers;

/// <summary>
/// POST /api/v1/customers/merge (customers.merge): folds <c>MergeId</c> into <c>KeepId</c>. The kept customer keeps its
/// own values and fills blanks from the merged one; <c>PreferMergedFields</c> lists fields whose merged value wins.
/// </summary>
public sealed record MergeCustomersRequest(Guid KeepId, Guid MergeId, IReadOnlyList<string>? PreferMergedFields = null);

/// <summary>Outcome of an organisation merge. <c>Moved</c> counts moved records per kind (contacts, tickets, locations…).</summary>
public sealed record CustomerMergeResultDto(Guid SurvivorId, Guid MergedId, bool AlreadyMerged, IReadOnlyDictionary<string, int> Moved,
    IReadOnlyList<string> FieldsCopied, Guid? LogId);

/// <summary>One entry of a customer's merge history (as survivor or as the merged customer).</summary>
public sealed record CustomerMergeLogDto(Guid Id, Guid SurvivorCustomerId, string? SurvivorName, Guid MergedCustomerId, string? MergedCustomerName,
    Guid? MergedById, string? MergedByName, DateTime MergedAt, JsonElement Snapshot);
