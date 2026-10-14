using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Ai.Agents;
using SupportDesk.Application.Ai.Retrieval;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Ai;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Ai.Admin;

/// <summary>AI administration (admin.ai): provider/model/budget configuration, prompt templates, usage and cost, execution log, provider test, agents.</summary>
public sealed class AiAdminService(
    DeskDbContext db,
    RequestContext ctx,
    AiConfigurationService config,
    AiGateway gateway,
    AiOptions options,
    ISecretProtector secrets,
    LimitGuard limits,
    IAuditWriter audit,
    IJobQueue jobs,
    ChunkVectorCache vectorCache,
    KnowledgeIndexer indexer,
    TicketLookups lookups)
{
    // ───────────────────────────── Configuration ─────────────────────────────

    public async Task<AiConfigurationDto> GetConfigAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminAi);
        var e = await config.GetOrCreateEntityAsync(ct);
        var period = UsageMeter.PeriodOf(db.Clock.UtcNow);
        var usage = await db.AiUsage.AsNoTracking().Where(u => u.PeriodStart == period)
            .GroupBy(_ => 1).Select(g => new { Cost = g.Sum(u => u.Cost), // Only external-provider tokens count against the plan allowance (local and built-in calls are free and unmetered).
                Tokens = g.Sum(u => u.Model.StartsWith("Local:") || u.Model.StartsWith("Builtin:") ? 0 : u.InputTokens + u.OutputTokens) }).FirstOrDefaultAsync(ct);
        var plan = await limits.GetLimitsAsync(ct);
        var features = AiConfigurationService.ParseFeatures(e.FeaturesJson);
        return new AiConfigurationDto(e.IsEnabled, AiProviderNames.Normalize(e.Provider) is { Length: > 0 } p ? p : AiProviderNames.Local, e.Model, e.Endpoint,
            !string.IsNullOrEmpty(e.ApiKeyProtected), e.EmbeddingProvider, e.EmbeddingModel, e.MonthlyBudget, e.Currency, e.BudgetAction.ToString(), e.InputCostPer1K,
            e.OutputCostPer1K, e.RedactPii, e.ExecutionRetentionDays,
            AiFeatures.All.Select(f => features.TryGetValue(f, out var s) ? s : AiFeatureSettings.Default)
                .Zip(AiFeatures.All, (s, f) => new AiFeatureSettingDto(f, s.Enabled, s.Roles, s.AutoApply, s.MinConfidence)).ToList(),
            AiConfigurationService.ParseScenarios(e.AutoReplyScenariosJson).Select(MapScenario).ToList(),
            usage?.Cost ?? 0, usage?.Tokens ?? 0, plan.AiTokensMonthly, plan.Features.Contains(PlanFeatures.Ai, StringComparer.OrdinalIgnoreCase),
            plan.Features.Contains(PlanFeatures.AiAgents, StringComparer.OrdinalIgnoreCase), AiProviderNames.All, Convert.ToBase64String(e.RowVersion));
    }

    private static AutoReplyScenarioDto MapScenario(AutoReplyScenario s) =>
        new(s.Key, s.Name, s.Description, s.Keywords, s.CategoryIds, s.ArticleIds, s.Channels, s.MinConfidence, s.Enabled);

    public async Task<AiConfigurationDto> UpdateConfigAsync(UpdateAiConfigurationRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminAi);
        var e = await config.GetOrCreateEntityAsync(ct);
        if (r.RowVersion is { Length: > 0 } rv)
        {
            var bytes = Convert.FromBase64String(rv);
            if (!bytes.SequenceEqual(e.RowVersion)) throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The AI settings were changed by someone else. Reload and try again.");
            db.Entry(e).Property(x => x.RowVersion).OriginalValue = bytes;
        }
        var errors = new Dictionary<string, string>();
        var changes = new List<string>();
        var oldEmbeddingKey = EmbeddingKey(e);

        if (r.IsEnabled is { } enabled && enabled != e.IsEnabled) { e.IsEnabled = enabled; changes.Add(enabled ? "enabled" : "disabled"); }
        var provider = r.Provider is null ? AiProviderNames.Normalize(e.Provider) : AiProviderNames.Normalize(r.Provider);
        if (provider.Length == 0) errors["provider"] = $"Choose one of: {string.Join(", ", AiProviderNames.All)}.";
        else if (provider != e.Provider) { e.Provider = provider; changes.Add($"provider → {provider}"); }
        if (r.Model is not null) { e.Model = r.Model.Trim(); changes.Add("model"); }
        if (r.Endpoint is not null) { e.Endpoint = string.IsNullOrWhiteSpace(r.Endpoint) ? null : r.Endpoint.Trim().TrimEnd('/'); changes.Add("endpoint"); }
        if (r.ClearApiKey) { e.ApiKeyProtected = null; changes.Add("API key removed"); }
        if (!string.IsNullOrWhiteSpace(r.ApiKey)) { e.ApiKeyProtected = secrets.Protect(r.ApiKey.Trim()); changes.Add("API key replaced"); }
        if (r.EmbeddingProvider is not null)
        {
            var ep = AiProviderNames.Normalize(r.EmbeddingProvider);
            if (!AiProviderNames.EmbeddingCapable.Contains(ep)) errors["embeddingProvider"] = $"Embeddings are supported by: {string.Join(", ", AiProviderNames.EmbeddingCapable)}.";
            else e.EmbeddingProvider = ep;
        }
        if (r.EmbeddingModel is not null) e.EmbeddingModel = r.EmbeddingModel.Trim();
        if (r.MonthlyBudget is { } budget) { if (budget < 0) errors["monthlyBudget"] = "The budget cannot be negative."; else e.MonthlyBudget = budget; }
        if (r.Currency is { } cur) { if (cur.Trim().Length != 3) errors["currency"] = "Use a 3-letter currency code."; else e.Currency = cur.Trim().ToUpperInvariant(); }
        if (r.BudgetAction is { } ba)
        {
            if (Enum.TryParse<BudgetAction>(ba, true, out var action)) e.BudgetAction = action;
            else errors["budgetAction"] = "Use Warn or Block.";
        }
        if (r.InputCostPer1K is { } ic) { if (ic < 0) errors["inputCostPer1K"] = "Cannot be negative."; else e.InputCostPer1K = ic; }
        if (r.OutputCostPer1K is { } oc) { if (oc < 0) errors["outputCostPer1K"] = "Cannot be negative."; else e.OutputCostPer1K = oc; }
        if (r.RedactPii is { } redact) { e.RedactPii = redact; changes.Add(redact ? "PII redaction on" : "PII redaction off"); }
        if (r.ExecutionRetentionDays is { } days) { if (days is < 7 or > 3650) errors["executionRetentionDays"] = "Between 7 and 3650 days."; else e.ExecutionRetentionDays = days; }

        // Provider consistency.
        if (AiProviderNames.IsLocal(e.Provider)) e.Model = AiProviderNames.LocalChatModel;
        else
        {
            if (string.IsNullOrWhiteSpace(e.Model) || e.Model == AiProviderNames.LocalChatModel) errors["model"] = "Enter the model (or Azure deployment) name.";
            if (e.Provider is AiProviderNames.OpenAICompatible or AiProviderNames.AzureOpenAI && string.IsNullOrWhiteSpace(e.Endpoint))
                errors["endpoint"] = "This provider needs an endpoint URL.";
            if (e.Provider is AiProviderNames.OpenAI or AiProviderNames.AzureOpenAI or AiProviderNames.Anthropic && string.IsNullOrEmpty(e.ApiKeyProtected))
                errors["apiKey"] = "This provider needs an API key.";
        }
        if (e.Endpoint is not null && ValidateEndpoint(e.Endpoint, options.AllowPrivateEndpoints) is { } endpointError) errors["endpoint"] = endpointError;
        if (AiProviderNames.IsLocal(e.EmbeddingProvider)) e.EmbeddingModel = AiProviderNames.LocalEmbeddingModel;
        else
        {
            if (!AiProviderNames.IsLocal(e.Provider) && !e.EmbeddingProvider.Equals(e.Provider, StringComparison.OrdinalIgnoreCase))
                errors["embeddingProvider"] = "A remote embedding provider must be the same as the chat provider (they share the endpoint and API key), or Local.";
            if (string.IsNullOrWhiteSpace(e.EmbeddingModel) || e.EmbeddingModel == AiProviderNames.LocalEmbeddingModel) errors["embeddingModel"] = "Enter the embedding model name.";
            if (e.EmbeddingProvider is AiProviderNames.OpenAICompatible or AiProviderNames.AzureOpenAI && string.IsNullOrWhiteSpace(e.Endpoint))
                errors["endpoint"] = "This embedding provider needs an endpoint URL.";
        }

        if (r.Features is { } featureList)
        {
            var features = AiConfigurationService.ParseFeatures(e.FeaturesJson);
            var roleNames = (await db.Roles.AsNoTracking().Select(x => x.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in featureList)
            {
                if (!AiFeatures.All.Contains(f.Feature)) { errors[$"features.{f.Feature}"] = "Unknown AI feature."; continue; }
                if (f.MinConfidence is < 0 or > 1) { errors[$"features.{f.Feature}.minConfidence"] = "Between 0 and 1."; continue; }
                var unknown = (f.Roles ?? []).Where(x => !roleNames.Contains(x)).ToList();
                if (unknown.Count > 0) { errors[$"features.{f.Feature}.roles"] = $"Unknown role(s): {string.Join(", ", unknown)}."; continue; }
                features[f.Feature] = new AiFeatureSettings(f.Enabled, f.Roles ?? [], f.AutoApply, f.MinConfidence);
            }
            e.FeaturesJson = AiConfigurationService.SerializeFeatures(features);
            changes.Add("feature settings");
        }
        if (r.AutoReplyScenarios is { } scenarios)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<AutoReplyScenario>();
            foreach (var s in scenarios)
            {
                var key = TextNormalization.Slugify(s.Key ?? "", 60).Replace('-', '_');
                if (key.Length == 0 || !keys.Add(key)) { errors["autoReplyScenarios"] = "Each scenario needs a unique key."; continue; }
                if (string.IsNullOrWhiteSpace(s.Name)) { errors[$"autoReplyScenarios.{key}.name"] = "A name is required."; continue; }
                var badChannels = (s.Channels ?? []).Where(c => !Enum.TryParse<Channel>(c, true, out _)).ToList();
                if (badChannels.Count > 0) { errors[$"autoReplyScenarios.{key}.channels"] = $"Unknown channel(s): {string.Join(", ", badChannels)}."; continue; }
                if ((s.Keywords ?? []).Count == 0 && (s.CategoryIds ?? []).Count == 0)
                {
                    errors[$"autoReplyScenarios.{key}"] = "Give keywords or categories so the scenario can be recognised.";
                    continue;
                }
                list.Add(new AutoReplyScenario(key, s.Name.Trim(), s.Description, s.Keywords ?? [], s.CategoryIds ?? [], s.ArticleIds ?? [],
                    (s.Channels ?? []).Select(c => Enum.Parse<Channel>(c, true).ToString()).ToList(), Math.Clamp(s.MinConfidence <= 0 ? 0.8 : s.MinConfidence, 0.5, 1), s.Enabled));
            }
            e.AutoReplyScenariosJson = AiConfigurationService.SerializeScenarios(list);
            changes.Add("auto-reply scenarios");
        }
        if (errors.Count > 0) throw new ValidationException(errors);

        audit.Write(AuditActions.AiConfigChanged, "AiConfiguration", e.Id, $"AI settings changed: {string.Join(", ", changes.Distinct())}",
            new { e.IsEnabled, e.Provider, e.Model, e.Endpoint, e.EmbeddingProvider, e.EmbeddingModel, e.MonthlyBudget, e.BudgetAction, e.RedactPii });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The AI settings were changed by someone else. Reload and try again."); }
        config.Invalidate();
        if (EmbeddingKey(e) != oldEmbeddingKey)
        {
            // New embedding model: chunks are re-embedded in the background (searches use only chunks of the current model).
            vectorCache.Invalidate(ctx.TenantId);
            await jobs.EnqueueIfAbsentAsync(AiJobTypes.Reembed, new ReembedPayload(EmbeddingKey(e)), $"ai-reembed:{ctx.TenantId:N}:{EmbeddingKey(e)}:{db.Clock.UtcNow:yyyyMMddHHmm}", ct: ct);
        }
        return await GetConfigAsync(ct);
    }

    private static string EmbeddingKey(AiConfiguration e) =>
        AiProviderNames.IsLocal(e.EmbeddingProvider) ? AiProviderNames.LocalEmbeddingModel : $"{e.EmbeddingProvider}:{e.EmbeddingModel}";

    /// <summary>SSRF guard for tenant-configured endpoints: https only and no loopback/private/link-local hosts unless the host allows it.</summary>
    public static string? ValidateEndpoint(string endpoint, bool allowPrivate)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return "Enter an absolute URL, e.g. https://api.openai.com/v1.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "Credentials must not be part of the URL; use the API key field.";
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowPrivate && uri.Scheme == Uri.UriSchemeHttp)) return "The endpoint must use https.";
        if (allowPrivate) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host is "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal"))
            return "Private or local endpoints are not allowed on this server.";
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsPrivate(ip)) return "Private or local endpoints are not allowed on this server.";
        return null;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6Any);
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    // ───────────────────────────── Prompt templates ─────────────────────────────

    public async Task<List<PromptTemplateDto>> ListPromptsAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminAi);
        var rows = await db.AiPromptTemplates.AsNoTracking().OrderBy(t => t.Feature).ThenByDescending(t => t.Version).ToListAsync(ct);
        return AiPromptCatalog.Defaults.Values.OrderBy(d => d.Key).Select(d => Map(d, rows.Where(r => r.Feature == d.Key).ToList())).ToList();
    }

    private static PromptTemplateDto Map(PromptDefinition d, List<AiPromptTemplate> versions)
    {
        var active = versions.Where(v => v.IsActive).MaxBy(v => v.Version);
        return new PromptTemplateDto(d.Key, active is null ? "default" : "custom", active?.Version ?? 0, active?.SystemPrompt ?? d.SystemPrompt,
            active?.UserTemplate ?? d.UserTemplate, d.Placeholders, d.SystemPrompt, d.UserTemplate,
            versions.Select(v => new PromptTemplateVersionDto(v.Id, v.Version, v.IsActive, v.SystemPrompt, v.UserTemplate, v.CreatedAt)).ToList());
    }

    public async Task<PromptTemplateDto> SavePromptAsync(string feature, SavePromptTemplateRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminAi);
        var d = AiPromptCatalog.Defaults.TryGetValue(feature, out var def) ? def : throw new NotFoundException("Prompt template", feature);
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(r.SystemPrompt) || r.SystemPrompt.Length > 8000) errors["systemPrompt"] = "Required, at most 8000 characters.";
        if (string.IsNullOrWhiteSpace(r.UserTemplate) || r.UserTemplate.Length > 8000) errors["userTemplate"] = "Required, at most 8000 characters.";
        var present = AiPromptService.PlaceholdersOf(r.UserTemplate ?? "");
        var missing = d.Placeholders.Except(present).ToList();
        if (missing.Count > 0) errors["userTemplate"] = $"Keep these placeholders: {string.Join(", ", missing.Select(m => "{{" + m + "}}"))}.";
        var unknown = present.Except(d.Placeholders).ToList();
        if (unknown.Count > 0) errors["userTemplate"] = $"Unknown placeholder(s): {string.Join(", ", unknown.Select(m => "{{" + m + "}}"))}.";
        if (errors.Count > 0) throw new ValidationException(errors);
        var versions = await db.AiPromptTemplates.Where(t => t.Feature == d.Key).ToListAsync(ct);
        foreach (var v in versions) v.IsActive = false;
        var next = new AiPromptTemplate { Feature = d.Key, Version = versions.Count == 0 ? 1 : versions.Max(v => v.Version) + 1, SystemPrompt = r.SystemPrompt!.Trim(), UserTemplate = r.UserTemplate!.Trim(), IsActive = true };
        db.AiPromptTemplates.Add(next);
        audit.Write(AuditActions.AiConfigChanged, "AiPromptTemplate", next.Id, $"Prompt template '{d.Key}' saved as version {next.Version}");
        await db.SaveChangesAsync(ct);
        versions.Add(next);
        return Map(d, versions.OrderByDescending(v => v.Version).ToList());
    }

    public async Task<PromptTemplateDto> ActivatePromptAsync(string feature, int version, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminAi);
        var d = AiPromptCatalog.Defaults.TryGetValue(feature, out var def) ? def : throw new NotFoundException("Prompt template", feature);
        var versions = await db.AiPromptTemplates.Where(t => t.Feature == d.Key).ToListAsync(ct);
        if (version != 0 && versions.All(v => v.Version != version)) throw new NotFoundException("Prompt version", version);
        foreach (var v in versions) v.IsActive = v.Version == version;
        audit.Write(AuditActions.AiConfigChanged, "AiPromptTemplate", null, version == 0 ? $"Prompt template '{d.Key}' reset to the built-in default" : $"Prompt template '{d.Key}' version {version} activated");
        await db.SaveChangesAsync(ct);
        return Map(d, versions.OrderByDescending(v => v.Version).ToList());
    }

    // ───────────────────────────── Usage & executions ─────────────────────────────

    public async Task<AiUsageSummaryDto> UsageAsync(int months, CancellationToken ct)
    {
        ctx.RequireAny(Permissions.AdminAi, Permissions.AdminBilling);
        var e = await config.GetOrCreateEntityAsync(ct);
        var period = UsageMeter.PeriodOf(db.Clock.UtcNow);
        var start = period.AddMonths(-Math.Clamp(months, 1, 24) + 1);
        var rows = await db.AiUsage.AsNoTracking().Where(u => u.PeriodStart >= start).OrderByDescending(u => u.PeriodStart).ThenBy(u => u.Feature).ToListAsync(ct);
        var current = rows.Where(r => r.PeriodStart == period).ToList();
        var since = db.Clock.UtcNow.AddDays(-30);
        var outcomes = await db.AiExecutions.AsNoTracking().Where(x => x.CreatedAt >= since).GroupBy(x => x.Feature).Select(g => new AiOutcomeStatsDto(g.Key, g.Count(),
            g.Count(x => x.Outcome == AiOutcome.Accepted), g.Count(x => x.Outcome == AiOutcome.Applied), g.Count(x => x.Outcome == AiOutcome.Rejected),
            g.Count(x => x.Outcome == AiOutcome.Reverted), g.Count(x => x.Status == AiExecutionStatus.Failed))).ToListAsync(ct);
        var plan = await limits.GetLimitsAsync(ct);
        static AiUsageGroupDto Group(string key, IEnumerable<AiUsage> g) =>
            new(key, g.Sum(x => x.Requests), g.Sum(x => x.InputTokens + x.OutputTokens), g.Sum(x => x.Cost));
        return new AiUsageSummaryDto(period, e.MonthlyBudget, e.Currency, current.Sum(x => x.Cost), current.Where(IsMetered).Sum(x => x.InputTokens + x.OutputTokens), plan.AiTokensMonthly,
            current.GroupBy(x => x.Feature).Select(g => Group(g.Key, g)).OrderByDescending(g => g.Requests).ToList(),
            current.GroupBy(x => x.Model).Select(g => Group(g.Key, g)).OrderByDescending(g => g.Requests).ToList(),
            rows.GroupBy(x => x.PeriodStart).OrderBy(g => g.Key).Select(g => Group(g.Key.ToString("yyyy-MM"), g)).ToList(),
            outcomes.OrderByDescending(o => o.Total).ToList(),
            rows.Select(x => new AiUsageRowDto(x.PeriodStart, x.Feature, x.Model, x.Requests, x.InputTokens, x.OutputTokens, x.Cost)).ToList());
    }

    private static bool IsMetered(AiUsage u) => !u.Model.StartsWith("Local:", StringComparison.Ordinal) && !u.Model.StartsWith("Builtin:", StringComparison.Ordinal);

    public async Task<PageResult<AiExecutionDto>> ExecutionsAsync(string? feature, string? status, string? outcome, Guid? entityId, DateTime? from, DateTime? to,
        int page, int pageSize, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminAi);
        var q = db.AiExecutions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(feature)) q = q.Where(x => x.Feature == feature);
        if (Enum.TryParse<AiExecutionStatus>(status, true, out var st)) q = q.Where(x => x.Status == st);
        if (Enum.TryParse<AiOutcome>(outcome, true, out var oc)) q = q.Where(x => x.Outcome == oc);
        if (entityId is { } id) q = q.Where(x => x.EntityId == id);
        if (from is { } f) q = q.Where(x => x.CreatedAt >= f);
        if (to is { } t) q = q.Where(x => x.CreatedAt < t);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        await lookups.LoadAsync(ct);
        return new PageResult<AiExecutionDto>(rows.Select(MapExecution).ToList(), page, pageSize, total);
    }

    public async Task<AiExecutionDto> ExecutionAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminAi);
        var e = await db.AiExecutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("AI execution", id);
        await lookups.LoadAsync(ct);
        return MapExecution(e);
    }

    private AiExecutionDto MapExecution(AiExecution e) => new(e.Id, e.Feature, e.Provider, e.Model, e.EntityType, e.EntityId, e.UserId, lookups.User(e.UserId), e.AgentId,
        e.PromptVersion, e.InputTokens, e.OutputTokens, e.Cost, e.LatencyMs, e.Status.ToString(), e.Confidence, Json.StringList(e.InjectionFlagsJson), e.Outcome.ToString(),
        e.Error, e.CreatedAt, SafeElement(e.OutputJson), SafeElement(e.CitationsJson), e.InputHash);

    private static JsonElement? SafeElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return Json.Element(json); }
        catch (JsonException) { return Json.ToElement(json); }
    }

    // ───────────────────────────── Provider test ─────────────────────────────

    public async Task<TestProviderResultDto> TestProviderAsync(TestProviderRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminAi);
        var settings = await gateway.SettingsAsync(ct);
        var prompt = string.IsNullOrWhiteSpace(r.Prompt) ? "Reply with the single word OK." : TextNormalization.Truncate(r.Prompt, 500);
        string? output = null, error = null;
        var latency = 0;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await gateway.CompleteAsync(new AiCall
            {
                Feature = "provider_test", PromptKey = PromptKeys.ProviderTest, Interactive = true, IgnoreFeatureToggle = true, JsonMode = false,
                Values = new Dictionary<string, string> { ["prompt"] = prompt }, LocalTask = new ProviderTestTask(prompt), MaxTokens = 60
            }, raw => new AiOutput<string>(TextNormalization.Truncate(raw.Trim(), 500)), ct);
            latency = (int)sw.ElapsedMilliseconds;
            output = result.Value;
        }
        catch (DomainException ex) { error = ex.Message; }
        int? dims = null;
        string? embeddingError = null;
        try { dims = (await gateway.EmbedAsync(["connectivity test"], "provider_test", ct)).Dimensions; }
        catch (DomainException ex) { embeddingError = ex.Message; }
        return new TestProviderResultDto(error is null, settings.Chat.Provider, settings.Chat.Model, latency, output, error, settings.Embedding.Provider,
            settings.Embedding.Model, dims, embeddingError);
    }

    // ───────────────────────────── Agents ─────────────────────────────

    public async Task<List<AiAgentDto>> ListAgentsAsync(CancellationToken ct)
    {
        ctx.RequireAny(Permissions.AdminAi, Permissions.AiApprove);
        return (await db.AiAgents.AsNoTracking().OrderBy(a => a.Kind).ThenBy(a => a.Name).ToListAsync(ct)).Select(MapAgent).ToList();
    }

    public async Task<AiAgentDto> SaveAgentAsync(Guid? id, SaveAiAgentRequest r, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminAi);
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 100) errors["name"] = "A name of at most 100 characters is required.";
        if (!Enum.TryParse<AiAgentKind>(r.Kind, true, out var kind)) errors["kind"] = $"Use one of: {string.Join(", ", Enum.GetNames<AiAgentKind>())}.";
        if (!Enum.TryParse<AiAgentMode>(r.Mode, true, out var mode)) errors["mode"] = "Use SuggestOnly or AutoLowRisk.";
        var configJson = r.Config is { ValueKind: JsonValueKind.Object } c ? c.GetRawText() : "{}";
        var parsed = AgentConfig.Parse(configJson);
        string[] knownActions = ["set_category", "set_department", "set_priority", "add_tag", "request_info", "suggest_article", "send_reply", "auto_reply"];
        var unknownActions = parsed.AllowedActions.Where(a => !knownActions.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknownActions.Count > 0) errors["config.allowedActions"] = $"Unknown action(s): {string.Join(", ", unknownActions)}.";
        var badChannels = parsed.Channels.Where(x => !Enum.TryParse<Channel>(x, true, out _)).ToList();
        if (badChannels.Count > 0) errors["config.channels"] = $"Unknown channel(s): {string.Join(", ", badChannels)}.";
        var badInfo = parsed.RequiredInfo.Where(x => !InfoItems.All.Contains(x)).ToList();
        if (badInfo.Count > 0) errors["config.requiredInfo"] = $"Unknown item(s): {string.Join(", ", badInfo)}.";
        if (parsed.Departments.Count > 0 && await db.Departments.CountAsync(d => parsed.Departments.Contains(d.Id), ct) != parsed.Departments.Count)
            errors["config.departments"] = "Unknown department.";
        if (kind == AiAgentKind.AutoResponder && mode == AiAgentMode.AutoLowRisk && parsed.Scenarios.Count == 0 &&
            AiConfigurationService.ParseScenarios((await config.GetOrCreateEntityAsync(ct)).AutoReplyScenariosJson).Count == 0)
            errors["config.scenarios"] = "Approve at least one auto-reply scenario in AI settings before enabling automatic replies.";
        if (errors.Count > 0) throw new ValidationException(errors);
        if (r.IsEnabled) await limits.RequireFeatureAsync(PlanFeatures.AiAgents, ct);

        AiAgent agent;
        if (id is { } existing)
        {
            agent = await db.AiAgents.FirstOrDefaultAsync(a => a.Id == existing, ct) ?? throw new NotFoundException("AI agent", existing);
        }
        else
        {
            agent = new AiAgent();
            db.AiAgents.Add(agent);
        }
        agent.Name = r.Name.Trim();
        agent.Kind = kind;
        agent.Mode = mode;
        agent.IsEnabled = r.IsEnabled;
        agent.ConfigJson = configJson;
        agent.Instructions = string.IsNullOrWhiteSpace(r.Instructions) ? null : TextNormalization.Truncate(r.Instructions.Trim(), 2000);
        audit.Write(AuditActions.AiConfigChanged, "AiAgent", agent.Id, $"AI agent '{agent.Name}' ({kind}, {mode}) {(id is null ? "created" : "updated")}, {(r.IsEnabled ? "enabled" : "disabled")}");
        await db.SaveChangesAsync(ct);
        return MapAgent(agent);
    }

    public async Task DeleteAgentAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.AdminAi);
        var agent = await db.AiAgents.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("AI agent", id);
        db.AiAgents.Remove(agent);
        audit.Write(AuditActions.AiConfigChanged, "AiAgent", id, $"AI agent '{agent.Name}' deleted");
        await db.SaveChangesAsync(ct);
    }

    public static AiAgentDto MapAgent(AiAgent a) => new(a.Id, a.Name, a.Kind.ToString(), a.IsEnabled, a.Mode.ToString(), Json.Element(a.ConfigJson), a.Instructions,
        a.RunCount, a.CreatedAt, a.UpdatedAt);

    // ───────────────────────────── Index maintenance ─────────────────────────────

    public async Task<ReindexResultDto> ReindexAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.AdminAi);
        var r = await indexer.BackfillAsync(200, 500, 2000, ct);
        return new ReindexResultDto(r.Articles, r.Tickets, r.Reembedded, r.Removed);
    }
}
