using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Application.Customers;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Contracts.Erp;
using SupportDesk.Domain;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Engineering;

/// <summary>
/// Engineering administration (admin.integrations): tracker connections (Azure DevOps Services/Server, GitHub, local)
/// with write-only secrets, connection test and project/repository discovery, type/field/state mappings, inbound webhook
/// URL + secret (shown once, rotatable), sync now, sync/integration logs, and tenant-wide auto-notify settings.
/// </summary>
public sealed class EngineeringAdminService(
    DeskDbContext db,
    RequestContext ctx,
    EngineeringTrackerResolver resolver,
    EngineeringSyncService sync,
    ISecretProtector secrets,
    IAuditWriter audit,
    TenantSettingsService settings,
    LimitGuard limits,
    IConfiguration configuration,
    ILogger<EngineeringAdminService> logger)
{
    private string PublicBaseUrl =>
        (configuration["Communication:PublicBaseUrl"] is { Length: > 0 } a ? a : configuration["App:PublicUrl"] is { Length: > 0 } b ? b : "http://localhost:5830").TrimEnd('/');

    public string WebhookUrl(Integration i) =>
        $"{PublicBaseUrl}/api/v1/engineering/hooks/{(i.Type == IntegrationTypes.GitHub ? "github" : "azure-devops")}/{i.Id}";

    // ───────────────────────────── Trackers ─────────────────────────────

    public async Task<List<EngineeringTrackerDto>> ListAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var rows = await resolver.Trackers().AsNoTracking().OrderBy(i => i.Type).ThenBy(i => i.Name).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await db.EngineeringLinks.AsNoTracking().Where(l => l.IntegrationId != null && ids.Contains(l.IntegrationId.Value))
            .GroupBy(l => l.IntegrationId).Select(g => new { g.Key, Count = g.Select(x => x.ExternalId).Distinct().Count() }).ToListAsync(ct);
        return rows.Select(r => Map(r, counts.FirstOrDefault(c => c.Key == r.Id)?.Count ?? 0, null)).ToList();
    }

    public async Task<EngineeringTrackerDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var i = await resolver.GetAsync(id, ct);
        return Map(i, await LinkedCountAsync(i.Id, ct), null);
    }

    private Task<int> LinkedCountAsync(Guid id, CancellationToken ct) =>
        db.EngineeringLinks.AsNoTracking().Where(l => l.IntegrationId == id).Select(l => l.ExternalId).Distinct().CountAsync(ct);

    private EngineeringTrackerDto Map(Integration i, int linked, string? revealedSecret)
    {
        var provider = TrackerProviders.FromIntegrationType(i.Type);
        var s = resolver.Settings(i);
        var sec = resolver.Secrets(i);
        var f = s.Fields;
        return new EngineeringTrackerDto(i.Id, TrackerProviders.Name(provider), i.Name, i.IsActive, i.Status.ToString(), i.BaseUrl, s.Project, s.Owner, s.Repository,
            s.AuthType, !string.IsNullOrEmpty(sec.Token), !string.IsNullOrEmpty(sec.AppPrivateKey), s.AppId, s.InstallationId, s.DefaultItemType,
            new Dictionary<string, string>(s.TypeMap, StringComparer.OrdinalIgnoreCase),
            new TrackerFieldMapDto(f.ReproStepsField, f.DescriptionField, f.SeverityField, f.PriorityField, f.FixVersionField, f.CustomerImpactField, f.DefaultAreaPath,
                f.DefaultIterationPath, f.DefaultTags, f.SeverityLabelPrefix, f.PriorityLabelPrefix),
            s.ResolvedStates, s.ReleasedStates, s.PollIntervalMinutes, provider == EngineeringProvider.Local ? null : WebhookUrl(i),
            !string.IsNullOrEmpty(sec.WebhookSecret), revealedSecret, i.LastSyncAt, i.LastError, linked, Convert.ToBase64String(i.RowVersion));
    }

    public async Task<EngineeringTrackerDto> CreateAsync(SaveEngineeringTrackerRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var provider = TrackerProviders.Parse(r.Provider);
        if (provider == EngineeringProvider.Jira) throw new ValidationException(new Dictionary<string, string> { ["provider"] = "Jira is not supported." });
        if (provider != EngineeringProvider.Local) await limits.RequireFeatureAsync(PlanFeatures.EngineeringIntegration, ct);
        if (provider == EngineeringProvider.Local && await resolver.Trackers().AnyAsync(i => i.Type == IntegrationTypes.LocalEngineering, ct))
            throw new ConflictException("already_exists", "The built-in tracker is already set up.");
        var i = new Integration { Type = TrackerProviders.IntegrationType(provider) };
        var webhookSecret = provider == EngineeringProvider.Local ? null : TextNormalization.RandomToken(24);
        Apply(i, provider, r, TrackerSettings.Defaults(provider), TrackerSecrets.Empty with { WebhookSecret = webhookSecret }, isNew: true);
        db.Integrations.Add(i);
        await db.SaveChangesAsync(ct);
        return Map(i, 0, webhookSecret);
    }

    public async Task<EngineeringTrackerDto> UpdateAsync(Guid id, SaveEngineeringTrackerRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var i = await resolver.GetAsync(id, ct, track: true);
        var provider = TrackerProviders.FromIntegrationType(i.Type);
        if (!string.IsNullOrWhiteSpace(r.Provider) && TrackerProviders.Parse(r.Provider) != provider)
            throw new ValidationException(new Dictionary<string, string> { ["provider"] = "The provider of a tracker cannot change; add a new tracker instead." });
        if (provider != EngineeringProvider.Local && r.IsActive) await limits.RequireFeatureAsync(PlanFeatures.EngineeringIntegration, ct);
        if (r.RowVersion is { Length: > 0 } rv) SetOriginalRowVersion(i, rv);
        Apply(i, provider, r, resolver.Settings(i), resolver.Secrets(i), isNew: false);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The tracker was changed by someone else. Reload and try again."); }
        return Map(i, await LinkedCountAsync(i.Id, ct), null);
    }

    private void SetOriginalRowVersion(Integration i, string rowVersion)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(rowVersion); }
        catch (FormatException) { throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "Invalid rowVersion." }); }
        if (!bytes.SequenceEqual(i.RowVersion)) throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The tracker was changed by someone else. Reload and try again.");
        db.Entry(i).Property(x => x.RowVersion).OriginalValue = bytes;
    }

    private void Apply(Integration i, EngineeringProvider provider, SaveEngineeringTrackerRequest r, TrackerSettings previous, TrackerSecrets oldSecrets, bool isNew)
    {
        var errors = new Dictionary<string, string>();
        var defaults = TrackerSettings.Defaults(provider);
        string? baseUrl = Parse.Clean(r.BaseUrl, 400)?.TrimEnd('/');
        string authType = defaults.AuthType;
        string? project = null, owner = null, repo = null, appId = null, installationId = null;
        switch (provider)
        {
            case EngineeringProvider.AzureDevOps:
                if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http"))
                    errors["baseUrl"] = "Enter the organization or collection URL (https://dev.azure.com/org or https://tfs.example.com/tfs/DefaultCollection).";
                project = Parse.Clean(r.Project, 200);
                if (project is null) errors["project"] = "Choose the project work items are created in.";
                authType = r.AuthType?.Trim().ToLowerInvariant() switch { null or "" or "pat" => "Pat", "oauth" => "OAuth", _ => "" };
                if (authType == "") errors["authType"] = "Authentication must be Pat or OAuth.";
                break;
            case EngineeringProvider.GitHub:
                baseUrl ??= "https://api.github.com";
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var g) || g.Scheme is not ("https" or "http"))
                    errors["baseUrl"] = "Enter the GitHub API URL (https://api.github.com or https://github.example.com/api/v3).";
                owner = Parse.Clean(r.Owner, 100);
                repo = Parse.Clean(r.Repository, 100);
                if (owner is null) errors["owner"] = "Enter the repository owner (user or organization).";
                if (repo is null) errors["repository"] = "Enter the repository name.";
                authType = r.AuthType?.Trim().ToLowerInvariant() switch { null or "" or "token" => "Token", "app" => "App", _ => "" };
                if (authType == "") errors["authType"] = "Authentication must be Token or App.";
                if (authType == "App")
                {
                    appId = Parse.Clean(r.AppId, 40);
                    installationId = Parse.Clean(r.InstallationId, 40);
                    if (appId is null || !appId.All(char.IsDigit)) errors["appId"] = "Enter the numeric GitHub App id.";
                    if (installationId is null || !installationId.All(char.IsDigit)) errors["installationId"] = "Enter the numeric installation id.";
                }
                break;
            default:
                baseUrl = null;
                authType = "None";
                break;
        }
        if (provider != EngineeringProvider.Local && r.PollIntervalMinutes is < 5 or > 1440) errors["pollIntervalMinutes"] = "Poll every 5 minutes to 24 hours.";

        var typeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in r.TypeMap ?? previous.TypeMap)
        {
            if (!Enum.TryParse<EngineeringItemType>(k, true, out var t) || t is not (EngineeringItemType.Bug or EngineeringItemType.Feature or EngineeringItemType.Task))
                errors["typeMap"] = $"Unknown item type '{k}' (use Bug, Feature, Task).";
            else if (Parse.Clean(v, 100) is { } mapped) typeMap[t.ToString()] = mapped;
        }
        foreach (var (k, v) in defaults.TypeMap) typeMap.TryAdd(k, v);
        var defaultType = Parse.Clean(r.DefaultItemType, 20) ?? previous.DefaultItemType;
        if (!Enum.TryParse<EngineeringItemType>(defaultType, true, out var dType) || dType is not (EngineeringItemType.Bug or EngineeringItemType.Feature or EngineeringItemType.Task))
            errors["defaultItemType"] = "Default type must be Bug, Feature or Task.";
        List<string> States(List<string>? input, List<string> fallback, string field)
        {
            var list = (input ?? fallback).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count > 20 || list.Any(x => x.Length > 100)) errors[field] = "Use at most 20 states of up to 100 characters.";
            return list;
        }
        var resolved = States(r.ResolvedStates, previous.ResolvedStates, "resolvedStates");
        var released = States(r.ReleasedStates, previous.ReleasedStates, "releasedStates");
        var f = r.Fields;
        var fields = f is null ? previous.Fields : new TrackerFields
        {
            ReproStepsField = Parse.Clean(f.ReproStepsField, 200), DescriptionField = Parse.Clean(f.DescriptionField, 200), SeverityField = Parse.Clean(f.SeverityField, 200),
            PriorityField = Parse.Clean(f.PriorityField, 200), FixVersionField = Parse.Clean(f.FixVersionField, 200), CustomerImpactField = Parse.Clean(f.CustomerImpactField, 200),
            DefaultAreaPath = Parse.Clean(f.DefaultAreaPath, 300), DefaultIterationPath = Parse.Clean(f.DefaultIterationPath, 300),
            DefaultTags = (f.DefaultTags ?? []).Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 100).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            SeverityLabelPrefix = Parse.Clean(f.SeverityLabelPrefix, 40), PriorityLabelPrefix = Parse.Clean(f.PriorityLabelPrefix, 40)
        };

        // Write-only secrets: null keeps the stored value, "" clears it.
        var token = r.Token is null ? oldSecrets.Token : r.Token.Length == 0 ? null : r.Token.Trim();
        var key = r.AppPrivateKey is null ? oldSecrets.AppPrivateKey : r.AppPrivateKey.Length == 0 ? null : r.AppPrivateKey.Trim();
        if (provider != EngineeringProvider.Local && r.IsActive)
        {
            if (authType is "Pat" or "OAuth" or "Token" && token is null) errors["token"] = provider == EngineeringProvider.GitHub ? "Enter a GitHub token." : "Enter a personal access token.";
            if (authType == "App" && key is null) errors["appPrivateKey"] = "Paste the GitHub App private key (PEM).";
            if (authType == "App" && key is not null && !key.Contains("PRIVATE KEY", StringComparison.Ordinal)) errors["appPrivateKey"] = "The private key must be in PEM format.";
        }
        if (errors.Count > 0) throw new ValidationException(errors);

        var settingsValue = new TrackerSettings
        {
            Project = project, Owner = owner, Repository = repo, AuthType = authType, AppId = appId, InstallationId = installationId, DefaultItemType = dType.ToString(),
            TypeMap = typeMap, Fields = fields, ResolvedStates = resolved, ReleasedStates = released,
            PollIntervalMinutes = provider == EngineeringProvider.Local ? 0 : r.PollIntervalMinutes, ApiVersion = previous.ApiVersion
        };
        var newSecrets = oldSecrets with { Token = token, AppPrivateKey = key };
        i.Name = Parse.Clean(r.Name, 200) ?? (provider switch
        {
            EngineeringProvider.AzureDevOps => $"Azure DevOps – {project}",
            EngineeringProvider.GitHub => $"GitHub – {owner}/{repo}",
            _ => "Engineering board"
        });
        i.BaseUrl = baseUrl;
        i.SettingsJson = Json.Serialize(settingsValue);
        i.SecretsProtected = newSecrets == TrackerSecrets.Empty ? null : secrets.Protect(Json.Serialize(newSecrets));
        i.IsActive = r.IsActive;
        i.Status = r.IsActive ? (i.Status == IntegrationStatus.Error && !isNew ? IntegrationStatus.Error : IntegrationStatus.Connected) : IntegrationStatus.Disabled;
        audit.Write(AuditActions.IntegrationChanged, "Integration", i.Id, $"Engineering tracker {i.Name} {(isNew ? "configured" : "updated")}",
            new
            {
                provider = TrackerProviders.Name(provider), baseUrl, project, owner, repo, authType, appId, installationId, r.IsActive, settingsValue.PollIntervalMinutes,
                resolved, released, typeMap, tokenChanged = r.Token is not null, privateKeyChanged = r.AppPrivateKey is not null
            });
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var i = await resolver.GetAsync(id, ct, track: true);
        if (await db.EngineeringLinks.AnyAsync(l => l.IntegrationId == id, ct))
            throw new ConflictException("tracker_in_use", "Tickets are linked to this tracker. Disable it instead to keep their history.");
        if (await db.Set<LocalWorkItem>().IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AnyAsync(w => w.IntegrationId == id, ct))
            throw new ConflictException("tracker_in_use", "The built-in tracker has work items. Disable it instead.");
        await db.Set<EngineeringItem>().Where(x => x.IntegrationId == id).ExecuteDeleteAsync(ct);
        await db.Set<EngineeringWebhookDelivery>().Where(x => x.IntegrationId == id).ExecuteDeleteAsync(ct);
        await db.Set<LocalRelease>().Where(x => x.IntegrationId == id).ExecuteDeleteAsync(ct);
        db.Integrations.Remove(i);
        audit.Write(AuditActions.IntegrationChanged, "Integration", i.Id, $"Engineering tracker {i.Name} deleted", new { i.Type });
        await db.SaveChangesAsync(ct);
    }

    public async Task<EngineeringTrackerDto> RotateWebhookSecretAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var i = await resolver.GetAsync(id, ct, track: true);
        if (i.Type == IntegrationTypes.LocalEngineering) throw new DomainException(ErrorCodes.Validation, "The built-in tracker has no webhook.");
        var secret = TextNormalization.RandomToken(24);
        var sec = resolver.Secrets(i) with { WebhookSecret = secret };
        i.SecretsProtected = secrets.Protect(Json.Serialize(sec));
        audit.Write(AuditActions.IntegrationChanged, "Integration", i.Id, $"Webhook secret of {i.Name} rotated", new { webhookSecretChanged = true });
        await db.SaveChangesAsync(ct);
        return Map(i, await LinkedCountAsync(i.Id, ct), secret);
    }

    public async Task<TrackerTestResultDto> TestAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var i = await resolver.GetAsync(id, ct, track: true);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await resolver.Create(i).TestAsync(ct);
            i.Status = !i.IsActive ? IntegrationStatus.Disabled : result.Ok ? IntegrationStatus.Connected : IntegrationStatus.Error;
            i.LastError = result.Ok ? null : TextNormalization.Truncate(result.Message, 1000);
            await db.SaveChangesAsync(ct);
            return new TrackerTestResultDto(result.Ok, result.Message, (int)sw.ElapsedMilliseconds, result.Projects);
        }
        catch (Exception ex) when (ex is TrackerException or DomainException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Engineering tracker test failed for {Tracker}", id);
            i.Status = IntegrationStatus.Error;
            i.LastError = TextNormalization.Truncate(ex.Message, 1000);
            await db.SaveChangesAsync(ct);
            return new TrackerTestResultDto(false, ex.Message, (int)sw.ElapsedMilliseconds, []);
        }
    }

    public async Task<IReadOnlyList<TrackerProjectDto>> ProjectsAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var i = await resolver.GetAsync(id, ct);
        try { return await resolver.Create(i).ProjectsAsync(ct); }
        catch (TrackerException ex) { throw new DomainException("integration_error", ex.Message, 502); }
    }

    public async Task<TrackerSyncResultDto> SyncNowAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        await resolver.GetActiveAsync(id, ct);
        return await sync.SyncTrackerAsync(id, ct);
    }

    public async Task<List<EngineeringDeliveryDto>> DeliveriesAsync(Guid id, int limit, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        await resolver.GetAsync(id, ct);
        return await db.Set<EngineeringWebhookDelivery>().AsNoTracking().Where(d => d.IntegrationId == id).OrderByDescending(d => d.ReceivedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(d => new EngineeringDeliveryDto(d.Id, d.Provider.ToString(), d.DeliveryId, d.EventType, d.ExternalId, d.Status.ToString(), d.Detail, d.ReceivedAt))
            .ToListAsync(ct);
    }

    public async Task<List<IntegrationLogDto>> LogsAsync(Guid id, int limit, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        await resolver.GetAsync(id, ct);
        return await db.IntegrationLogs.AsNoTracking().Where(l => l.IntegrationId == id).OrderByDescending(l => l.CreatedAt).Take(Math.Clamp(limit, 1, 500))
            .Select(l => new IntegrationLogDto(l.Id, l.System, l.Direction.ToString(), l.Operation, l.Url, l.StatusCode, l.Success, l.DurationMs, l.RequestExcerpt,
                l.ResponseExcerpt, l.CorrelationId, l.CreatedAt)).ToListAsync(ct);
    }

    // ───────────────────────────── Tenant settings ─────────────────────────────

    public async Task<EngineeringSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var s = await settings.GetAsync<EngineeringSettings>(EngineeringSettings.Key, ct);
        return await MapSettingsAsync(s, ct);
    }

    private async Task<EngineeringSettingsDto> MapSettingsAsync(EngineeringSettings s, CancellationToken ct)
    {
        var templates = await db.ReplyTemplates.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name).Select(t => new IdName(t.Id, t.Name)).ToListAsync(ct);
        var statuses = (await db.TicketStatuses.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).ToListAsync(ct))
            .Where(x => Json.StringList(x.SubstatusesJson).Contains(EngineeringTicketService.EngineeringSubstatus, StringComparer.OrdinalIgnoreCase))
            .Select(x => new IdName(x.Id, x.Name)).ToList();
        return new EngineeringSettingsDto(s.AddNoteOnResolved, s.AddNoteOnReleased, s.NotifyAssignee, s.NotifyCustomerOnRelease, s.ReleaseReplyTemplateId,
            s.SetEngineeringSubstatus, s.EscalationStatusId, s.DefaultTrackerId, templates, statuses);
    }

    public async Task<EngineeringSettingsDto> SaveSettingsAsync(SaveEngineeringSettingsRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminIntegrations);
        var errors = new Dictionary<string, string>();
        if (r.ReleaseReplyTemplateId is { } tid && !await db.ReplyTemplates.AnyAsync(t => t.Id == tid && t.IsActive, ct)) errors["releaseReplyTemplateId"] = "Unknown reply template.";
        if (r.EscalationStatusId is { } sid)
        {
            var st = await db.TicketStatuses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sid && x.IsActive, ct);
            if (st is null) errors["escalationStatusId"] = "Unknown status.";
            else if (st.Category.IsResolvedOrClosed()) errors["escalationStatusId"] = "Choose an open status.";
        }
        if (r.DefaultTrackerId is { } dt && !await resolver.Trackers().AnyAsync(i => i.Id == dt, ct)) errors["defaultTrackerId"] = "Unknown tracker.";
        if (errors.Count > 0) throw new ValidationException(errors);
        var value = new EngineeringSettings
        {
            AddNoteOnResolved = r.AddNoteOnResolved, AddNoteOnReleased = r.AddNoteOnReleased, NotifyAssignee = r.NotifyAssignee,
            NotifyCustomerOnRelease = r.NotifyCustomerOnRelease, ReleaseReplyTemplateId = r.ReleaseReplyTemplateId, SetEngineeringSubstatus = r.SetEngineeringSubstatus,
            EscalationStatusId = r.EscalationStatusId, DefaultTrackerId = r.DefaultTrackerId
        };
        await settings.SetAsync(EngineeringSettings.Key, value, ct);
        return await MapSettingsAsync(value, ct);
    }
}
