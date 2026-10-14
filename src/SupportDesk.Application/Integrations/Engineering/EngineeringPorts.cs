using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Domain;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Engineering;

/// <summary>
/// Port to an engineering tracker (Azure DevOps Services/Server, GitHub, the built-in local tracker): create work items
/// from tickets, read their state (fix version, linked changesets/PRs, releases) and list projects/repositories.
/// HTTP adapters live in SupportDesk.Integrations; the local tracker is database-backed (<see cref="LocalTracker"/>).
/// </summary>
public interface IEngineeringTracker
{
    EngineeringProvider Provider { get; }
    /// <summary>Checks connectivity and credentials; returns the projects/repositories visible to the credentials.</summary>
    Task<TrackerTestResult> TestAsync(CancellationToken ct);
    Task<IReadOnlyList<TrackerProjectDto>> ProjectsAsync(CancellationToken ct);
    Task<TrackerWorkItem> CreateAsync(WorkItemDraft draft, CancellationToken ct);
    /// <summary>Current state of the given items. Missing, deleted or moved items come back with that status (never thrown).</summary>
    Task<IReadOnlyList<TrackerLookup>> GetAsync(IReadOnlyCollection<string> externalIds, CancellationToken ct);
    /// <summary>Parses "123", "#123", "AB#123", "ENG-12" or a work item / issue URL into an external id (null = not recognised).</summary>
    string? ParseReference(string reference);
    string DisplayId(string externalId);
}

public interface IEngineeringTrackerFactory
{
    IEngineeringTracker Create(TrackerConnection connection);
}

public sealed record TrackerTestResult(bool Ok, string Message, IReadOnlyList<TrackerProjectDto> Projects);

/// <summary>What SupportDesk asks the tracker to create (built from the ticket by the escalation service).</summary>
public sealed record WorkItemDraft(
    EngineeringItemType Type,
    string TrackerType,
    string Title,
    string? DescriptionHtml,
    TicketSeverity? Severity,
    TicketPriority Priority,
    string? AreaPath,
    string? IterationPath,
    IReadOnlyList<string> Tags,
    Guid? AssigneeUserId,
    int CustomerImpactCount,
    Guid? ProductId,
    string? SourceReference,
    string? SourceUrl);

public sealed record TrackerArtifact(string Kind, string Id, string? Title, string? Url, string? State);

/// <summary>A tracker's view of one work item. Null release fields mean "unknown" (kept from earlier syncs).</summary>
public sealed record TrackerWorkItem(
    string ExternalId,
    string Title,
    string? Url,
    string? TrackerType,
    EngineeringItemType ItemType,
    string? State,
    string? AssignedTo,
    string? AreaPath,
    string? IterationPath,
    string? FixVersion,
    IReadOnlyList<string> Tags,
    string? Severity,
    string? Priority,
    string? Project,
    DateTime? ChangedAt,
    IReadOnlyList<TrackerArtifact> Artifacts,
    string? ReleaseName = null,
    DateTime? ReleasedAt = null,
    DateTime? DeployedAt = null,
    bool? Released = null,
    bool? Resolved = null);

public enum TrackerLookupStatus { Found, NotFound, Deleted, Moved }

public sealed record TrackerLookup(string ExternalId, TrackerLookupStatus Status, TrackerWorkItem? Item, string? Detail = null)
{
    public static TrackerLookup Found(TrackerWorkItem item) => new(item.ExternalId, TrackerLookupStatus.Found, item);
    public static TrackerLookup Missing(string id, TrackerLookupStatus status, string? detail = null) => new(id, status, null, detail);
}

/// <summary>A tracker call failed (connectivity, credentials, validation). Shown to admins/agents; never contains secrets.</summary>
public sealed class TrackerException(string message, bool transient = false, int? statusCode = null) : Exception(message)
{
    public bool Transient { get; } = transient;
    public int? StatusCode { get; } = statusCode;
}

public static class TrackerProviders
{
    public const string AzureDevOps = "AzureDevOps";
    public const string GitHub = "GitHub";
    public const string Local = "Local";

    public static readonly string[] All = [AzureDevOps, GitHub, Local];
    public static readonly string[] IntegrationTypeNames = [IntegrationTypes.AzureDevOps, IntegrationTypes.GitHub, IntegrationTypes.LocalEngineering];

    public static EngineeringProvider Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "azuredevops" or "ado" or "tfs" or "azure-devops" => EngineeringProvider.AzureDevOps,
        "github" => EngineeringProvider.GitHub,
        "local" or "localengineering" => EngineeringProvider.Local,
        _ => throw new Tickets.ValidationException(new Dictionary<string, string> { ["provider"] = "Provider must be AzureDevOps, GitHub or Local." })
    };

    public static string IntegrationType(EngineeringProvider p) => p switch
    {
        EngineeringProvider.AzureDevOps => IntegrationTypes.AzureDevOps,
        EngineeringProvider.GitHub => IntegrationTypes.GitHub,
        EngineeringProvider.Local => IntegrationTypes.LocalEngineering,
        _ => throw new Tickets.ValidationException(new Dictionary<string, string> { ["provider"] = "Jira is not supported." })
    };

    public static EngineeringProvider FromIntegrationType(string type) => type switch
    {
        IntegrationTypes.AzureDevOps => EngineeringProvider.AzureDevOps,
        IntegrationTypes.GitHub => EngineeringProvider.GitHub,
        _ => EngineeringProvider.Local
    };

    public static string Name(EngineeringProvider p) => p switch
    {
        EngineeringProvider.AzureDevOps => AzureDevOps,
        EngineeringProvider.GitHub => GitHub,
        EngineeringProvider.Jira => "Jira",
        _ => Local
    };
}

/// <summary>Non-secret tracker options (Integration.SettingsJson).</summary>
public sealed record TrackerSettings
{
    public string? Project { get; init; }
    public string? Owner { get; init; }
    public string? Repository { get; init; }
    /// <summary>ADO: Pat | OAuth. GitHub: Token | App. Local: None.</summary>
    public string AuthType { get; init; } = "None";
    public string? AppId { get; init; }
    public string? InstallationId { get; init; }
    public string DefaultItemType { get; init; } = "Bug";
    /// <summary>SupportDesk item type (Bug, Feature, Task) → tracker type (ADO work item type, GitHub label).</summary>
    public Dictionary<string, string> TypeMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public TrackerFields Fields { get; init; } = new();
    /// <summary>Tracker states (or "label:x" / "tag:x") that mean the fix is done.</summary>
    public List<string> ResolvedStates { get; init; } = [];
    /// <summary>Tracker states (or "label:x" / "tag:x") that mean the fix has shipped.</summary>
    public List<string> ReleasedStates { get; init; } = [];
    public int PollIntervalMinutes { get; init; } = 15;
    public string ApiVersion { get; init; } = "7.1";

    public static TrackerSettings Defaults(EngineeringProvider provider) => provider switch
    {
        EngineeringProvider.AzureDevOps => new TrackerSettings
        {
            AuthType = "Pat",
            TypeMap = new(StringComparer.OrdinalIgnoreCase) { ["Bug"] = "Bug", ["Feature"] = "User Story", ["Task"] = "Task" },
            Fields = new TrackerFields
            {
                ReproStepsField = "Microsoft.VSTS.TCM.ReproSteps", DescriptionField = "System.Description", SeverityField = "Microsoft.VSTS.Common.Severity",
                PriorityField = "Microsoft.VSTS.Common.Priority", FixVersionField = "Microsoft.VSTS.Build.IntegrationBuild", DefaultTags = ["SupportDesk"]
            },
            ResolvedStates = ["Resolved", "Done"],
            ReleasedStates = ["Closed"]
        },
        EngineeringProvider.GitHub => new TrackerSettings
        {
            AuthType = "Token",
            TypeMap = new(StringComparer.OrdinalIgnoreCase) { ["Bug"] = "bug", ["Feature"] = "enhancement", ["Task"] = "task" },
            Fields = new TrackerFields { DefaultTags = ["support"], SeverityLabelPrefix = "severity:", PriorityLabelPrefix = "priority:" },
            ResolvedStates = ["closed"],
            ReleasedStates = ["label:released"]
        },
        _ => new TrackerSettings
        {
            TypeMap = new(StringComparer.OrdinalIgnoreCase) { ["Bug"] = "Bug", ["Feature"] = "Feature", ["Task"] = "Task" },
            ResolvedStates = [nameof(LocalWorkItemState.Resolved), nameof(LocalWorkItemState.Closed)],
            PollIntervalMinutes = 0
        }
    };

    public string TrackerTypeFor(EngineeringItemType type) =>
        TypeMap.TryGetValue(type.ToString(), out var mapped) && !string.IsNullOrWhiteSpace(mapped) ? mapped : type.ToString();

    public EngineeringItemType ItemTypeFor(string? trackerType)
    {
        if (trackerType is null) return EngineeringItemType.Bug;
        foreach (var (k, v) in TypeMap)
            if (string.Equals(v, trackerType, StringComparison.OrdinalIgnoreCase) && Enum.TryParse<EngineeringItemType>(k, true, out var t)) return t;
        return Enum.TryParse<EngineeringItemType>(trackerType, true, out var direct) ? direct
            : trackerType.Contains("story", StringComparison.OrdinalIgnoreCase) || trackerType.Contains("feature", StringComparison.OrdinalIgnoreCase)
              || trackerType.Contains("enhancement", StringComparison.OrdinalIgnoreCase) ? EngineeringItemType.Feature
            : trackerType.Contains("task", StringComparison.OrdinalIgnoreCase) ? EngineeringItemType.Task : EngineeringItemType.Bug;
    }
}

public sealed record TrackerFields
{
    public string? ReproStepsField { get; init; }
    public string? DescriptionField { get; init; }
    public string? SeverityField { get; init; }
    public string? PriorityField { get; init; }
    public string? FixVersionField { get; init; }
    public string? CustomerImpactField { get; init; }
    public string? DefaultAreaPath { get; init; }
    public string? DefaultIterationPath { get; init; }
    public List<string> DefaultTags { get; init; } = [];
    public string? SeverityLabelPrefix { get; init; }
    public string? PriorityLabelPrefix { get; init; }
}

/// <summary>Secrets (Integration.SecretsProtected, AES-GCM): PAT/OAuth/GitHub token, GitHub App private key, inbound webhook secret.</summary>
public sealed record TrackerSecrets(string? Token, string? AppPrivateKey, string? WebhookSecret)
{
    public static readonly TrackerSecrets Empty = new(null, null, null);
}

/// <summary>Everything an adapter needs to talk to one configured tracker for one tenant.</summary>
public sealed record TrackerConnection(Guid TenantId, Guid IntegrationId, EngineeringProvider Provider, string Name, string? BaseUrl, TrackerSettings Settings,
    TrackerSecrets Secrets);

/// <summary>Matches tracker states and labels/tags against a configured list ("Resolved", "label:released", "tag:shipped").</summary>
public static class StateMatcher
{
    public static bool Matches(IEnumerable<string> configured, string? state, IEnumerable<string> tags)
    {
        var tagSet = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in configured)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var e = entry.Trim();
            var colon = e.IndexOf(':');
            if (colon > 0 && e[..colon].ToLowerInvariant() is "label" or "tag")
            {
                if (tagSet.Contains(e[(colon + 1)..].Trim())) return true;
            }
            else if (state is not null && string.Equals(e, state, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>"v8.0.1" and "8.0.1" name the same version.</summary>
    public static bool SameVersion(string? a, string? b)
    {
        static string N(string s) => s.Trim().TrimStart('v', 'V').Trim();
        return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Default factory when no HTTP adapters are installed: only the local tracker works.</summary>
public sealed class LocalOnlyTrackerFactory(IServiceProvider sp) : IEngineeringTrackerFactory
{
    public IEngineeringTracker Create(TrackerConnection connection) => connection.Provider == EngineeringProvider.Local
        ? ActivatorUtilities.CreateInstance<LocalTracker>(sp, connection)
        : throw new DomainException("integration_unavailable", $"No {TrackerProviders.Name(connection.Provider)} adapter is installed.");
}

/// <summary>Loads configured trackers for the current tenant and builds adapters for them.</summary>
public sealed class EngineeringTrackerResolver(DeskDbContext db, RequestContext ctx, IEngineeringTrackerFactory factory, ISecretProtector secrets)
{
    public IQueryable<Integration> Trackers() => db.Integrations.Where(i => TrackerProviders.IntegrationTypeNames.Contains(i.Type));

    public async Task<Integration> GetAsync(Guid trackerId, CancellationToken ct, bool track = false)
    {
        var q = Trackers().Where(i => i.Id == trackerId);
        if (!track) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Engineering tracker", trackerId);
    }

    public async Task<Integration> GetActiveAsync(Guid trackerId, CancellationToken ct)
    {
        var i = await GetAsync(trackerId, ct);
        if (!i.IsActive) throw new DomainException("integration_unavailable", $"The tracker '{i.Name}' is disabled.");
        return i;
    }

    public TrackerSettings Settings(Integration i) =>
        Json.DeserializeOr(i.SettingsJson, TrackerSettings.Defaults(TrackerProviders.FromIntegrationType(i.Type)));

    public TrackerSecrets Secrets(Integration i)
    {
        if (string.IsNullOrEmpty(i.SecretsProtected)) return TrackerSecrets.Empty;
        try { return Json.DeserializeOr(secrets.Unprotect(i.SecretsProtected), TrackerSecrets.Empty); }
        catch (System.Security.Cryptography.CryptographicException) { return TrackerSecrets.Empty; }
    }

    public TrackerConnection Connection(Integration i) =>
        new(ctx.TenantId, i.Id, TrackerProviders.FromIntegrationType(i.Type), i.Name, i.BaseUrl, Settings(i), Secrets(i));

    public IEngineeringTracker Create(Integration i) => factory.Create(Connection(i));
}

/// <summary>Writes IntegrationLog rows for tracker calls in their own scope, so the log survives a rolled-back business transaction.</summary>
public interface IEngineeringCallLog
{
    Task RecordAsync(Guid tenantId, Guid integrationId, string system, string operation, string? url, int? status, bool success, int durationMs,
        string? request, string? response, CancellationToken ct = default);
}

public sealed class EngineeringCallLog(IServiceScopeFactory scopes) : IEngineeringCallLog
{
    public async Task RecordAsync(Guid tenantId, Guid integrationId, string system, string operation, string? url, int? status, bool success, int durationMs,
        string? request, string? response, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().SetSystem(tenantId, "Engineering integration", ActorType.Integration);
        var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
        db.IntegrationLogs.Add(new IntegrationLog
        {
            IntegrationId = integrationId, System = system, Direction = MessageDirection.Outbound, Operation = Trim(operation, 200)!, Url = Trim(url, 450),
            StatusCode = status, Success = success, DurationMs = durationMs, RequestExcerpt = Trim(request, 4000), ResponseExcerpt = Trim(response, 4000),
            CreatedAt = db.Clock.UtcNow
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Tenant-wide engineering behaviour (TenantSetting "engineering").</summary>
public sealed record EngineeringSettings
{
    public const string Key = "engineering";
    public bool AddNoteOnResolved { get; init; } = true;
    public bool AddNoteOnReleased { get; init; } = true;
    public bool NotifyAssignee { get; init; } = true;
    public bool NotifyCustomerOnRelease { get; init; }
    public Guid? ReleaseReplyTemplateId { get; init; }
    public bool SetEngineeringSubstatus { get; init; } = true;
    public Guid? EscalationStatusId { get; init; }
    public Guid? DefaultTrackerId { get; init; }
}
