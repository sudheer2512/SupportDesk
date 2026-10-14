using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Ai.Retrieval;
using SupportDesk.Application.Ai.Safety;
using SupportDesk.Application.Common;
using SupportDesk.Application.Notifications;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Ai;

/// <summary>A model call routed through the gateway.</summary>
public sealed class AiCall
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    /// <summary>Feature toggle and logging key (<see cref="AiFeatures"/>).</summary>
    public required string Feature { get; init; }
    /// <summary>Prompt template key (defaults to <see cref="Feature"/>).</summary>
    public string? PromptKey { get; init; }
    public string? EntityType { get; init; }
    public Guid? EntityId { get; init; }
    /// <summary>Agent-initiated (requires ai.use and honours the feature's role restriction).</summary>
    public bool Interactive { get; init; }
    public Guid? AgentId { get; init; }
    /// <summary>Also requires the ai_agents plan feature.</summary>
    public bool RequiresAgentsPlan { get; init; }
    /// <summary>Trusted placeholder values (metadata, option lists).</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = None;
    /// <summary>Untrusted placeholder values (customer text, documents): wrapped in data blocks and scanned for injection.</summary>
    public IReadOnlyDictionary<string, string> Untrusted { get; init; } = None;
    public object? LocalTask { get; init; }
    public bool JsonMode { get; init; } = true;
    public int MaxTokens { get; init; } = 800;
    public double Temperature { get; init; } = 0.2;
    /// <summary>Put redacted e-mail addresses/phone numbers back into the output.</summary>
    public bool RestorePii { get; init; } = true;
    /// <summary>Skip the per-feature toggle (provider connectivity test).</summary>
    public bool IgnoreFeatureToggle { get; init; }
}

/// <summary>Parsed, validated model output plus what to log.</summary>
public sealed record AiOutput<T>(T Value, double? Confidence = null, object? Citations = null, object? Logged = null);

public sealed record AiResult<T>(Guid ExecutionId, T Value, double? Confidence, string Provider, string Model, bool IsLocal,
    IReadOnlyList<string> InjectionFlags);

public sealed record EmbeddingBatch(IReadOnlyList<float[]> Vectors, string ModelKey, int Dimensions);

/// <summary>The model output did not match the expected schema or allowed values.</summary>
public sealed class AiOutputException(string message) : Exception(message);

/// <summary>
/// Single entry point for every AI call. Enforces the tenant configuration (enabled, per-feature toggles and roles), the
/// plan feature, ai.use for interactive calls, the monthly budget (block or warn), the token allowance, PII redaction
/// for external providers, prompt-injection protection (data blocks + detection), output validation, cost/latency
/// measurement, and writes an <see cref="AiExecution"/> audit row and the monthly <see cref="AiUsage"/> roll-up.
/// The model never triggers actions: callers turn validated output into suggestions or proposals.
/// </summary>
public sealed class AiGateway(
    DeskDbContext db,
    RequestContext ctx,
    AiConfigurationService config,
    AiPromptService prompts,
    IAiProviderFactory providers,
    LimitGuard limits,
    IServiceScopeFactory scopes,
    ILogger<AiGateway> logger)
{
    public const string BuiltinModel = "statistics-v1";
    private HashSet<string>? _roleNames;

    public Task<AiSettings> SettingsAsync(CancellationToken ct) => config.GetAsync(ct);

    /// <summary>Non-throwing availability check (configuration, toggle, plan, permission) used by background handlers and panels.</summary>
    public async Task<bool> IsAvailableAsync(string feature, bool interactive, CancellationToken ct, bool requiresAgentsPlan = false)
    {
        var settings = await config.GetAsync(ct);
        if (!settings.IsEnabled || !settings.Feature(feature).Enabled) return false;
        if (!await limits.HasFeatureAsync(PlanFeatures.Ai, ct)) return false;
        if (requiresAgentsPlan && !await limits.HasFeatureAsync(PlanFeatures.AiAgents, ct)) return false;
        if (interactive && (!ctx.Has(Permissions.AiUse) || !await RoleAllowedAsync(settings.Feature(feature), ct))) return false;
        return true;
    }

    /// <summary>Throwing variant of <see cref="IsAvailableAsync"/> (records refused interactive attempts).</summary>
    public async Task EnsureAvailableAsync(string feature, bool interactive, CancellationToken ct, bool requiresAgentsPlan = false)
    {
        var settings = await config.GetAsync(ct);
        await GuardAsync(new AiCall { Feature = feature, Interactive = interactive, RequiresAgentsPlan = requiresAgentsPlan }, settings,
            AiProviderNames.Builtin, BuiltinModel, chargeable: false, ct);
    }

    // ───────────────────────────── Chat completions ─────────────────────────────

    public async Task<AiResult<T>> CompleteAsync<T>(AiCall call, Func<string, AiOutput<T>> parse, CancellationToken ct)
    {
        var settings = await config.GetAsync(ct);
        var model = providers.CreateChat(settings.Chat);
        await GuardAsync(call, settings, model.Provider, model.Model, chargeable: !model.IsLocal, ct);

        var template = await prompts.GetEffectiveAsync(call.PromptKey ?? call.Feature, ct);
        var values = new Dictionary<string, string>(call.Values, StringComparer.Ordinal);
        foreach (var (k, v) in call.Untrusted) values[k] = DataBlocks.Wrap(k, v);
        var system = template.SystemPrompt.Trim() + "\n\n" + DataBlocks.SecurityPreamble +
                     (call.JsonMode ? "\nRespond with a single JSON object only, without markdown fences or commentary." : "");
        var user = AiPromptService.Render(template.UserTemplate, values);
        var redaction = new PiiRedaction();
        if (!model.IsLocal && settings.Entity.RedactPii)
        {
            system = PiiRedactor.Redact(system, redaction);
            user = PiiRedactor.Redact(user, redaction);
        }
        var flags = PromptInjectionDetector.Detect(call.Untrusted.Values);
        var inputHash = TextNormalization.Sha256Hex(system + "\n␞\n" + user);
        var execution = NewExecution(call, model.Provider, model.Model, inputHash, template.Version, flags);

        if (!model.IsLocal)
        {
            var estimate = (system.Length + user.Length) / 4 + call.MaxTokens;
            try { await limits.EnsureWithinAsync(UsageMetrics.AiTokens, estimate, ct); }
            catch (LimitExceededException ex)
            {
                execution.Status = AiExecutionStatus.Blocked;
                execution.Error = ex.Message;
                await RecordAsync(execution, 0, 0, 0m, ct);
                throw;
            }
        }

        var request = new AiChatRequest
        {
            Feature = call.Feature, System = system, Messages = [new AiChatMessage("user", user)], JsonMode = call.JsonMode,
            MaxTokens = call.MaxTokens, Temperature = call.Temperature, LocalTask = call.LocalTask
        };
        var sw = Stopwatch.StartNew();
        AiChatResponse response;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(settings.Chat.Timeout + TimeSpan.FromSeconds(5));
            response = await model.CompleteAsync(request, cts.Token);
        }
        catch (Exception ex) when (IsProviderFailure(ex) && !ct.IsCancellationRequested)
        {
            sw.Stop();
            execution.Status = AiExecutionStatus.Failed;
            execution.LatencyMs = (int)sw.ElapsedMilliseconds;
            execution.Error = Trim($"{ex.GetType().Name}: {ex.Message}", 2000);
            await RecordAsync(execution, 0, 0, 0m, ct);
            logger.LogWarning(ex, "AI provider {Provider} failed for {Feature}", model.Provider, call.Feature);
            throw new DomainException("ai_provider_error", $"The AI provider ({model.Provider}) could not complete the request. {ProviderMessage(ex)}", 502);
        }
        sw.Stop();
        execution.LatencyMs = (int)sw.ElapsedMilliseconds;

        var text = !model.IsLocal && redaction.Count > 0 && call.RestorePii ? PiiRedactor.Restore(response.Text, redaction) : response.Text;
        var inTokens = response.InputTokens > 0 ? response.InputTokens : Estimate(system) + Estimate(user);
        var outTokens = response.OutputTokens > 0 ? response.OutputTokens : Estimate(response.Text);
        var cost = model.IsLocal ? 0m : Cost(settings.Entity, inTokens, outTokens);

        AiOutput<T> output;
        try
        {
            output = parse(text);
        }
        catch (Exception ex) when (ex is AiOutputException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException
                                       or ArgumentException)
        {
            execution.Status = AiExecutionStatus.Failed;
            execution.Error = Trim($"Invalid output: {ex.Message}", 2000);
            execution.OutputJson = Json.Serialize(new { raw = Trim(text, 4000) });
            await RecordAsync(execution, inTokens, outTokens, cost, ct);
            throw new DomainException("ai_invalid_output", "The AI response did not match the expected format. Try again or switch the model.", 502);
        }

        execution.Status = AiExecutionStatus.Succeeded;
        execution.OutputJson = Json.Serialize(output.Logged ?? output.Value);
        execution.CitationsJson = output.Citations is null ? null : Json.Serialize(output.Citations);
        execution.Confidence = output.Confidence is { } c ? Math.Round(Math.Clamp(c, 0, 1), 4) : null;
        await RecordAsync(execution, inTokens, outTokens, cost, ct);
        return new AiResult<T>(execution.Id, output.Value, execution.Confidence, model.Provider, model.Model, model.IsLocal, flags);
    }

    /// <summary>
    /// Statistical features computed in-process (resolution prediction, duplicate scoring, anomaly detection): same toggles,
    /// plan and permission checks and an execution row, but no model call and no cost.
    /// </summary>
    public async Task<AiResult<T>> RunBuiltinAsync<T>(AiCall call, Func<Task<AiOutput<T>>> compute, CancellationToken ct)
    {
        var settings = await config.GetAsync(ct);
        await GuardAsync(call, settings, AiProviderNames.Builtin, BuiltinModel, chargeable: false, ct);
        var execution = NewExecution(call, AiProviderNames.Builtin, BuiltinModel, TextNormalization.Sha256Hex($"{call.Feature}|{call.EntityId}|{db.Clock.UtcNow:O}"), 0, []);
        var sw = Stopwatch.StartNew();
        var output = await compute();
        execution.LatencyMs = (int)sw.ElapsedMilliseconds;
        execution.Status = AiExecutionStatus.Succeeded;
        execution.OutputJson = Json.Serialize(output.Logged ?? output.Value);
        execution.CitationsJson = output.Citations is null ? null : Json.Serialize(output.Citations);
        execution.Confidence = output.Confidence is { } c ? Math.Round(Math.Clamp(c, 0, 1), 4) : null;
        await RecordAsync(execution, 0, 0, 0m, ct);
        return new AiResult<T>(execution.Id, output.Value, execution.Confidence, AiProviderNames.Builtin, BuiltinModel, true, []);
    }

    // ───────────────────────────── Embeddings ─────────────────────────────

    /// <summary>
    /// Embeds texts with the tenant's embedding model. Local embeddings are computed in-process (free, not metered);
    /// external embeddings are budget-checked, PII-redacted, metered and audited like chat calls.
    /// </summary>
    public async Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, string feature, CancellationToken ct)
    {
        var settings = await config.GetAsync(ct);
        if (!settings.IsEnabled) throw new DomainException("ai_disabled", "AI is turned off for this organisation.", 409);
        var model = providers.CreateEmbedding(settings.Embedding);
        if (texts.Count == 0) return new EmbeddingBatch([], settings.EmbeddingModelKey, 0);
        if (model.IsLocal)
        {
            var local = await model.EmbedAsync(texts, ct);
            var vectors = local.Vectors.Select(v => VectorMath.Normalize(v.ToArray())).ToList();
            return new EmbeddingBatch(vectors, settings.EmbeddingModelKey, vectors.FirstOrDefault()?.Length ?? 0);
        }

        var call = new AiCall { Feature = feature, IgnoreFeatureToggle = true };
        await GuardAsync(call, settings, model.Provider, model.Model, chargeable: true, ct);
        var redaction = new PiiRedaction();
        var input = settings.Entity.RedactPii ? texts.Select(t => PiiRedactor.Redact(t, redaction)).ToList() : texts.ToList();
        var execution = NewExecution(call, model.Provider, model.Model, TextNormalization.Sha256Hex(string.Join("\n␞\n", input)), 0, []);
        execution.EntityType = "Embedding";
        var estimate = input.Sum(t => t.Length) / 4 + 1;
        try { await limits.EnsureWithinAsync(UsageMetrics.AiTokens, estimate, ct); }
        catch (LimitExceededException ex)
        {
            execution.Status = AiExecutionStatus.Blocked;
            execution.Error = ex.Message;
            await RecordAsync(execution, 0, 0, 0m, ct);
            throw;
        }
        var sw = Stopwatch.StartNew();
        AiEmbeddingResponse response;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(settings.Embedding.Timeout + TimeSpan.FromSeconds(5));
            response = await model.EmbedAsync(input, cts.Token);
            if (response.Vectors.Count != input.Count) throw new AiProviderException($"Expected {input.Count} embeddings, got {response.Vectors.Count}.");
        }
        catch (Exception ex) when (IsProviderFailure(ex) && !ct.IsCancellationRequested)
        {
            execution.Status = AiExecutionStatus.Failed;
            execution.LatencyMs = (int)sw.ElapsedMilliseconds;
            execution.Error = Trim($"{ex.GetType().Name}: {ex.Message}", 2000);
            await RecordAsync(execution, 0, 0, 0m, ct);
            throw new DomainException("ai_provider_error", $"The embedding provider ({model.Provider}) could not complete the request. {ProviderMessage(ex)}", 502);
        }
        execution.LatencyMs = (int)sw.ElapsedMilliseconds;
        execution.Status = AiExecutionStatus.Succeeded;
        execution.OutputJson = Json.Serialize(new { count = response.Vectors.Count, dimensions = response.Vectors.FirstOrDefault()?.Length ?? 0 });
        var tokens = response.InputTokens > 0 ? response.InputTokens : estimate;
        await RecordAsync(execution, tokens, 0, Cost(settings.Entity, tokens, 0), ct);
        var normalized = response.Vectors.Select(v => VectorMath.Normalize(v.ToArray())).ToList();
        return new EmbeddingBatch(normalized, settings.EmbeddingModelKey, normalized.FirstOrDefault()?.Length ?? 0);
    }

    // ───────────────────────────── Outcomes ─────────────────────────────

    /// <summary>Records the human (or automatic) outcome of a suggestion.</summary>
    public async Task MarkOutcomeAsync(Guid executionId, AiOutcome outcome, CancellationToken ct)
    {
        var now = db.Clock.UtcNow;
        var reviewer = ctx.UserId;
        await db.AiExecutions.Where(e => e.Id == executionId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.Outcome, outcome).SetProperty(e => e.ReviewedById, reviewer).SetProperty(e => e.ReviewedAt, now), ct);
    }

    // ───────────────────────────── Guards ─────────────────────────────

    private async Task GuardAsync(AiCall call, AiSettings settings, string provider, string model, bool chargeable, CancellationToken ct)
    {
        if (!settings.IsEnabled)
            await RefuseAsync(call, provider, model, AiExecutionStatus.Disabled, "ai_disabled", "AI is turned off for this organisation.", 409, ct);
        var feature = settings.Feature(call.Feature);
        if (!call.IgnoreFeatureToggle && !feature.Enabled)
            await RefuseAsync(call, provider, model, AiExecutionStatus.Disabled, "ai_feature_disabled", $"The AI feature '{call.Feature}' is turned off.", 409, ct);
        if (!await limits.HasFeatureAsync(PlanFeatures.Ai, ct))
            await RefuseAsync(call, provider, model, AiExecutionStatus.Blocked, "feature_not_in_plan", "AI features are not included in your plan.", 402, ct);
        if (call.RequiresAgentsPlan && !await limits.HasFeatureAsync(PlanFeatures.AiAgents, ct))
            await RefuseAsync(call, provider, model, AiExecutionStatus.Blocked, "feature_not_in_plan", "AI agents are not included in your plan.", 402, ct);
        if (call.Interactive)
        {
            if (!ctx.Has(Permissions.AiUse))
                await RefuseAsync(call, provider, model, AiExecutionStatus.Blocked, "forbidden", $"Missing permission '{Permissions.AiUse}'.", 403, ct);
            if (!await RoleAllowedAsync(feature, ct))
                await RefuseAsync(call, provider, model, AiExecutionStatus.Blocked, "ai_role_restricted", $"Your role cannot use the AI feature '{call.Feature}'.", 403, ct);
        }
        if (!chargeable) return;
        var budget = settings.Entity.MonthlyBudget;
        if (budget <= 0) return;
        var period = UsageMeter.PeriodOf(db.Clock.UtcNow);
        var spent = await db.AiUsage.AsNoTracking().Where(u => u.PeriodStart == period).SumAsync(u => (decimal?)u.Cost, ct) ?? 0m;
        if (spent < budget) return;
        if (settings.Entity.BudgetAction == BudgetAction.Block)
            await RefuseAsync(call, provider, model, AiExecutionStatus.BudgetExceeded, "ai_budget_exceeded",
                $"The monthly AI budget ({budget:0.##} {settings.Entity.Currency}) is used up ({spent:0.##} spent). An administrator can raise it in AI settings.", 402, ct);
        await WarnBudgetAsync(budget, spent, settings.Entity.Currency, period, ct);
    }

    private async Task RefuseAsync(AiCall call, string provider, string model, AiExecutionStatus status, string code, string message, int httpStatus, CancellationToken ct)
    {
        // Background refusals are expected (feature switched off) and not worth an audit row; interactive and budget refusals are.
        if (call.Interactive || status == AiExecutionStatus.BudgetExceeded)
        {
            var e = NewExecution(call, provider, model, "", 0, []);
            e.Status = status;
            e.Error = message;
            await RecordAsync(e, 0, 0, 0m, ct);
        }
        throw new DomainException(code, message, httpStatus);
    }

    private async Task<bool> RoleAllowedAsync(AiFeatureSettings feature, CancellationToken ct)
    {
        if (feature.Roles.Count == 0 || ctx.IsSystem || ctx.UserId is not { } uid) return true;
        _roleNames ??= (await db.UserRoles.AsNoTracking().Where(ur => ur.UserId == uid)
            .Join(db.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (ur, r) => r.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return feature.Roles.Any(_roleNames.Contains);
    }

    private async Task WarnBudgetAsync(decimal budget, decimal spent, string currency, DateTime period, CancellationToken ct)
    {
        try
        {
            var tenantId = ctx.TenantId;
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<RequestContext>().SetSystem(tenantId, "AI budget monitor", ActorType.Ai);
            var sdb = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            var admins = await sdb.UserRoles.AsNoTracking()
                .Where(ur => sdb.RolePermissions.Any(p => p.RoleId == ur.RoleId && p.PermissionCode == Permissions.AdminAi))
                .Select(ur => ur.UserId).Distinct().ToListAsync(ct);
            await scope.ServiceProvider.GetRequiredService<NotificationService>().NotifyAsync(new NotifyRequest(admins, NotificationTypes.System,
                "AI budget exceeded", $"AI spend this month is {spent:0.##} {currency}, above the budget of {budget:0.##} {currency}. Calls continue because the budget action is 'Warn'.",
                "/admin/ai", NotificationSeverity.Warning, false, "AiConfiguration", null, $"ai-budget-warn:{period:yyyyMM}"), ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Could not send the AI budget warning");
        }
    }

    // ───────────────────────────── Recording ─────────────────────────────

    private AiExecution NewExecution(AiCall call, string provider, string model, string inputHash, int promptVersion, IReadOnlyList<string> flags) => new()
    {
        TenantId = ctx.TenantId,
        Feature = call.Feature,
        Provider = provider,
        Model = model,
        EntityType = call.EntityType,
        EntityId = call.EntityId,
        UserId = ctx.IsAgent || ctx.ActorType == ActorType.ApiClient ? ctx.UserId : null,
        AgentId = call.AgentId,
        InputHash = inputHash,
        PromptVersion = promptVersion,
        InjectionFlagsJson = flags.Count > 0 ? Json.Serialize(flags) : null,
        Outcome = AiOutcome.Pending,
        CreatedAt = db.Clock.UtcNow
    };

    /// <summary>
    /// Persists the execution and the monthly usage roll-up in a separate unit of work, so recording never flushes (or is
    /// rolled back with) the caller's pending changes.
    /// </summary>
    private async Task RecordAsync(AiExecution execution, int inputTokens, int outputTokens, decimal cost, CancellationToken ct)
    {
        execution.InputTokens = inputTokens;
        execution.OutputTokens = outputTokens;
        execution.Cost = cost;
        var tenantId = ctx.TenantId;
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<RequestContext>().SetSystem(tenantId, ctx.ActorName ?? "AI", ActorType.Ai);
        var sdb = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
        sdb.AiExecutions.Add(execution);
        await sdb.SaveChangesAsync(ct);
        if (execution.Status is not (AiExecutionStatus.Succeeded or AiExecutionStatus.Failed)) return;
        var now = sdb.Clock.UtcNow;
        await sdb.Database.ExecuteSqlRawAsync("""
            MERGE dbo.AiUsage WITH (HOLDLOCK) AS t
            USING (SELECT @tenant AS TenantId, @period AS PeriodStart, @feature AS Feature, @model AS Model) AS s
            ON t.TenantId = s.TenantId AND t.PeriodStart = s.PeriodStart AND t.Feature = s.Feature AND t.Model = s.Model
            WHEN MATCHED THEN UPDATE SET Requests = t.Requests + 1, InputTokens = t.InputTokens + @in, OutputTokens = t.OutputTokens + @out,
                Cost = t.Cost + @cost, UpdatedAt = @now
            WHEN NOT MATCHED THEN INSERT (Id, TenantId, PeriodStart, Feature, Model, Requests, InputTokens, OutputTokens, Cost, UpdatedAt)
                VALUES (@id, @tenant, @period, @feature, @model, 1, @in, @out, @cost, @now);
            """,
            [
                new SqlParameter("@tenant", tenantId), new SqlParameter("@period", UsageMeter.PeriodOf(now)),
                new SqlParameter("@feature", Trim(execution.Feature, 100)), new SqlParameter("@model", Trim($"{execution.Provider}:{execution.Model}", 200)),
                new SqlParameter("@in", (long)inputTokens), new SqlParameter("@out", (long)outputTokens),
                new SqlParameter("@cost", System.Data.SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = cost },
                new SqlParameter("@now", now), new SqlParameter("@id", SequentialGuid.New())
            ], ct);
        if (!AiProviderNames.IsLocal(execution.Provider) && execution.Provider != AiProviderNames.Builtin && inputTokens + outputTokens > 0)
            await scope.ServiceProvider.GetRequiredService<UsageMeter>().IncrementAsync(UsageMetrics.AiTokens, inputTokens + outputTokens, ct);
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    public static decimal Cost(AiConfiguration c, int inputTokens, int outputTokens) =>
        Math.Round(inputTokens / 1000m * c.InputCostPer1K + outputTokens / 1000m * c.OutputCostPer1K, 4, MidpointRounding.AwayFromZero);

    public static int Estimate(string? text) => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);

    private static bool IsProviderFailure(Exception ex) =>
        ex is AiProviderException or HttpRequestException or TaskCanceledException or TimeoutException or JsonException or OperationCanceledException
            or NotSupportedException;

    private static string ProviderMessage(Exception ex) => ex switch
    {
        AiProviderException p => Trim(p.Message, 300),
        TaskCanceledException or OperationCanceledException or TimeoutException => "The request timed out.",
        HttpRequestException h => Trim(h.Message, 300),
        NotSupportedException n => Trim(n.Message, 300),
        _ => "The response could not be read."
    };

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
