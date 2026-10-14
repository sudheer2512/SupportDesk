using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Communication;
using SupportDesk.Domain;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Communication;

/// <summary>
/// Channel account administration (mailboxes, WhatsApp numbers, SMS senders, chat widgets, voice lines, social pages):
/// CRUD with encrypted secrets, plan limit "channels", audit, connection tests and webhook key rotation.
/// </summary>
public sealed class ChannelAccountService(
    DeskDbContext db,
    RequestContext ctx,
    LimitGuard limits,
    UsageMeter usage,
    IAuditWriter audit,
    HtmlSanitization html,
    ChannelContextFactory contexts,
    ChannelProviderRegistry registry)
{
    public async Task<List<ChannelAccountDto>> ListAsync(string? type, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminChannels);
        var q = db.ChannelAccounts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<ChannelType>(type, true, out var t)) throw new ValidationException(new Dictionary<string, string> { ["type"] = "Unknown channel type." });
            q = q.Where(a => a.Type == t);
        }
        return (await q.OrderBy(a => a.Type).ThenBy(a => a.Name).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<ChannelAccountDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminChannels);
        return Map(await LoadAsync(id, ct));
    }

    public IReadOnlyList<ChannelProviderDto> Providers()
    {
        ctx.Require(Permissions.AdminChannels);
        return ChannelProviders.Catalog.Select(p => new ChannelProviderDto(p.Provider, p.Types.Select(t => t.ToString()).ToList(), p.Description,
            p.SettingKeys, p.SecretKeys, p.Polling, p.Webhooks)).ToList();
    }

    public async Task<ChannelAccountDto> CreateAsync(CreateChannelAccountRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminChannels);
        var errors = new Dictionary<string, string>();
        if (!Enum.TryParse<ChannelType>(r.Type, true, out var type) || type is ChannelType.WebForm or ChannelType.Api)
            errors["type"] = "Choose Email, WhatsApp, Sms, Chat, Voice, Facebook or Instagram.";
        var info = ChannelProviders.Find(r.Provider);
        if (info is null) errors["provider"] = "Unknown provider.";
        else if (errors.Count == 0 && !info.Types.Contains(type)) errors["provider"] = $"{info.Provider} does not support {type} channels.";
        if (string.IsNullOrWhiteSpace(r.Name)) errors["name"] = "A name is required.";
        if (r.Settings is { } s && s.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined)) errors["settings"] = "Settings must be a JSON object.";
        if (errors.Count > 0) throw new ValidationException(errors);

        await limits.EnsureWithinAsync(UsageMetrics.Channels, 1, ct);
        var address = await NormalizeAddressAsync(type, info!.Provider, r.Address, null, ct);
        await ValidateReferencesAsync(r.BrandId, r.DepartmentId, ct);

        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in r.Secrets ?? []) if (!string.IsNullOrEmpty(v)) secrets[CheckSecretKey(info, k)] = v;

        var account = new ChannelAccount
        {
            Type = type, Provider = info.Provider, Name = r.Name.Trim(), Address = address, DisplayName = r.DisplayName?.Trim(), BrandId = r.BrandId,
            DepartmentId = r.DepartmentId, SettingsJson = ChannelJson.Merge("{}", r.Settings), SecretsProtected = contexts.ProtectSecrets(secrets),
            SignatureHtml = r.SignatureHtml is null ? null : html.Sanitize(r.SignatureHtml), AutoAcknowledge = r.AutoAcknowledge,
            AcceptUnknownSenders = r.AcceptUnknownSenders, IsActive = r.IsActive, Status = r.IsActive ? ChannelAccountStatus.Connected : ChannelAccountStatus.Disabled,
            WebhookKey = info.Webhooks ? NewWebhookKey() : null
        };
        db.ChannelAccounts.Add(account);
        audit.Write(AuditActions.ChannelChanged, "ChannelAccount", account.Id, $"Channel '{account.Name}' ({type}/{info.Provider}) created",
            new { account.Type, account.Provider, account.Address, secretKeys = secrets.Keys.ToList() });
        await db.SaveChangesAsync(ct);
        await usage.SetGaugeAsync(UsageMetrics.Channels, await db.ChannelAccounts.CountAsync(ct), ct);
        return Map(account);
    }

    public async Task<ChannelAccountDto> UpdateAsync(Guid id, UpdateChannelAccountRequest r, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminChannels);
        var a = await LoadAsync(id, ct);
        SetOriginalRowVersion(a, r.RowVersion);
        var info = ChannelProviders.Find(a.Provider)!;
        var changed = new List<string>();
        if (r.Name is { } name && name.Trim().Length > 0 && name.Trim() != a.Name) { a.Name = name.Trim(); changed.Add("name"); }
        if (r.Address is not null)
        {
            var address = await NormalizeAddressAsync(a.Type, a.Provider, r.Address, a.Id, ct);
            if (address != a.Address) { a.Address = address; a.PollCursor = null; changed.Add("address"); }
        }
        if (r.DisplayName is not null && r.DisplayName != a.DisplayName) { a.DisplayName = r.DisplayName.Trim().Length == 0 ? null : r.DisplayName.Trim(); changed.Add("displayName"); }
        if (r.BrandId is not null || r.DepartmentId is not null) await ValidateReferencesAsync(r.BrandId, r.DepartmentId, ct);
        if (r.BrandId is { } b && b != a.BrandId) { a.BrandId = b; changed.Add("brandId"); }
        if (r.DepartmentId is { } d && d != a.DepartmentId) { a.DepartmentId = d; changed.Add("departmentId"); }
        if (r.Settings is { ValueKind: JsonValueKind.Object })
        {
            var merged = ChannelJson.Merge(a.SettingsJson, r.Settings);
            if (merged != a.SettingsJson) { a.SettingsJson = merged; changed.Add("settings"); }
        }
        else if (r.Settings is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
            throw new ValidationException(new Dictionary<string, string> { ["settings"] = "Settings must be a JSON object." });
        var secretChanges = new List<string>();
        if (r.Secrets is { Count: > 0 })
        {
            var secrets = contexts.ReadSecrets(a);
            foreach (var (k, v) in r.Secrets)
            {
                var key = CheckSecretKey(info, k);
                if (string.IsNullOrEmpty(v)) { if (secrets.Remove(key)) secretChanges.Add(key); }
                else if (!secrets.TryGetValue(key, out var old) || old != v) { secrets[key] = v; secretChanges.Add(key); }
            }
            if (secretChanges.Count > 0) { a.SecretsProtected = contexts.ProtectSecrets(secrets); changed.Add("secrets"); }
        }
        if (r.SignatureHtml is not null) { a.SignatureHtml = r.SignatureHtml.Trim().Length == 0 ? null : html.Sanitize(r.SignatureHtml); changed.Add("signature"); }
        if (r.AutoAcknowledge is { } ack && ack != a.AutoAcknowledge) { a.AutoAcknowledge = ack; changed.Add("autoAcknowledge"); }
        if (r.AcceptUnknownSenders is { } aus && aus != a.AcceptUnknownSenders) { a.AcceptUnknownSenders = aus; changed.Add("acceptUnknownSenders"); }
        if (r.IsActive is { } active && active != a.IsActive)
        {
            a.IsActive = active;
            a.Status = active ? ChannelAccountStatus.Connected : ChannelAccountStatus.Disabled;
            if (active) a.LastError = null;
            changed.Add("isActive");
        }
        if (r.ResetCursor) { a.PollCursor = null; changed.Add("cursor"); }
        if (changed.Count == 0) return Map(a);
        audit.Write(AuditActions.ChannelChanged, "ChannelAccount", a.Id, $"Channel '{a.Name}' updated ({string.Join(", ", changed)})",
            new { changed, secretKeys = secretChanges });
        await SaveAsync(ct);
        return Map(a);
    }

    /// <summary>Deletes an unused channel; channels with message history are deactivated instead (history must stay intact).</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminChannels);
        var a = await LoadAsync(id, ct);
        var used = await db.Messages.AnyAsync(m => m.ChannelAccountId == id, ct) || await db.Conversations.AnyAsync(c => c.ChannelAccountId == id, ct)
                   || await db.CallRecords.AnyAsync(c => c.ChannelAccountId == id, ct);
        if (used)
        {
            a.IsActive = false;
            a.Status = ChannelAccountStatus.Disabled;
            audit.Write(AuditActions.ChannelChanged, "ChannelAccount", a.Id, $"Channel '{a.Name}' deactivated (has message history)");
        }
        else
        {
            db.ChannelAccounts.Remove(a);
            audit.Write(AuditActions.ChannelChanged, "ChannelAccount", a.Id, $"Channel '{a.Name}' deleted");
        }
        await db.SaveChangesAsync(ct);
        await usage.SetGaugeAsync(UsageMetrics.Channels, await db.ChannelAccounts.CountAsync(ct), ct);
        return !used;
    }

    public async Task<ChannelTestResultDto> TestAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminChannels);
        var a = await LoadAsync(id, ct);
        ConnectionTestResult result;
        try
        {
            var context = contexts.Create(a);
            result = a.Provider switch
            {
                ChannelProviders.Widget => new ConnectionTestResult(true, "The chat widget is ready. Embed: <script src=\"/widget/widget.js\" data-widget-key=\"" + a.Address + "\" async></script>"),
                _ when a.Type == ChannelType.Email => await registry.RequireEmail(a.Provider).TestAsync(context, ct),
                _ => await registry.RequireMessaging(a.Provider).TestAsync(context, ct)
            };
        }
        catch (ChannelConfigurationException ex)
        {
            result = new ConnectionTestResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or TaskCanceledException or InvalidOperationException)
        {
            result = new ConnectionTestResult(false, $"Connection failed: {ex.Message}");
        }
        a.Status = result.Success ? (a.IsActive ? ChannelAccountStatus.Connected : ChannelAccountStatus.Disabled) : ChannelAccountStatus.Error;
        a.LastError = result.Success ? null : TextNormalization.Truncate(result.Message, 1000);
        await SaveAsync(ct);
        return new ChannelTestResultDto(result.Success, result.Message, result.Details, a.Status.ToString(), db.Clock.UtcNow);
    }

    public async Task<ChannelAccountDto> RotateWebhookKeyAsync(Guid id, CancellationToken ct = default)
    {
        ctx.Require(Permissions.AdminChannels);
        var a = await LoadAsync(id, ct);
        if (ChannelProviders.Find(a.Provider) is not { Webhooks: true }) throw new DomainException(ErrorCodes.Validation, "This provider does not use webhooks.");
        a.WebhookKey = NewWebhookKey();
        audit.Write(AuditActions.ChannelChanged, "ChannelAccount", a.Id, $"Webhook key of channel '{a.Name}' rotated");
        await SaveAsync(ct);
        return Map(a);
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private async Task<ChannelAccount> LoadAsync(Guid id, CancellationToken ct) =>
        await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Channel", id);

    private async Task<string> NormalizeAddressAsync(ChannelType type, string provider, string? address, Guid? selfId, CancellationToken ct)
    {
        string normalized;
        switch (type)
        {
            case ChannelType.Email:
                normalized = TextNormalization.NormalizeEmail(address) ?? throw new ValidationException(new Dictionary<string, string> { ["address"] = "Enter the mailbox e-mail address." });
                break;
            case ChannelType.WhatsApp or ChannelType.Sms or ChannelType.Voice:
                var digits = TextNormalization.NormalizePhone(address) ?? throw new ValidationException(new Dictionary<string, string> { ["address"] = "Enter the phone number in international format (+countrycode…)." });
                normalized = "+" + digits;
                break;
            case ChannelType.Chat:
                normalized = string.IsNullOrWhiteSpace(address) ? "wk_" + TextNormalization.RandomAlphanumeric(20) : address.Trim();
                if (await db.AllTenants<ChannelAccount>().AnyAsync(a => a.Type == ChannelType.Chat && a.Address == normalized && a.Id != selfId, ct))
                    throw new ConflictException("widget_key_taken", "That widget key is already in use.");
                return normalized;
            default:
                normalized = (address ?? "").Trim();
                if (normalized.Length == 0) throw new ValidationException(new Dictionary<string, string> { ["address"] = "Enter the page or account id." });
                break;
        }
        if (await db.ChannelAccounts.AnyAsync(a => a.Type == type && a.Address == normalized && a.Id != selfId, ct))
            throw new ConflictException("channel_exists", $"A {type} channel for {normalized} already exists.");
        _ = provider;
        return normalized;
    }

    private async Task ValidateReferencesAsync(Guid? brandId, Guid? departmentId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        if (brandId is { } b && !await db.Brands.AnyAsync(x => x.Id == b, ct)) errors["brandId"] = "Unknown brand.";
        if (departmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d && x.IsActive, ct)) errors["departmentId"] = "Unknown or inactive department.";
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private static string CheckSecretKey(ChannelProviders.Info info, string key)
    {
        var match = info.SecretKeys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ValidationException(new Dictionary<string, string>
        {
            [$"secrets.{key}"] = info.SecretKeys.Length == 0 ? $"{info.Provider} has no secrets." : $"Unknown secret. Allowed: {string.Join(", ", info.SecretKeys)}."
        });
    }

    private static string NewWebhookKey() => TextNormalization.RandomAlphanumeric(32);

    private void SetOriginalRowVersion(ChannelAccount a, string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion)) throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "rowVersion is required for updates." });
        byte[] bytes;
        try { bytes = Convert.FromBase64String(rowVersion); }
        catch (FormatException) { throw new ValidationException(new Dictionary<string, string> { ["rowVersion"] = "Invalid rowVersion." }); }
        if (!bytes.SequenceEqual(a.RowVersion)) throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The channel was changed by someone else. Reload and try again.");
        db.Entry(a).Property(x => x.RowVersion).OriginalValue = bytes;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The channel was changed by someone else. Reload and try again."); }
    }

    public ChannelAccountDto Map(ChannelAccount a)
    {
        var secretKeys = new List<string>();
        if (!string.IsNullOrEmpty(a.SecretsProtected))
        {
            try { secretKeys = contexts.ReadSecrets(a).Keys.OrderBy(k => k).ToList(); }
            catch (System.Security.Cryptography.CryptographicException) { secretKeys = ["(unreadable)"]; }
        }
        return new ChannelAccountDto(a.Id, a.Type.ToString(), a.Provider, a.Name, a.Address, a.DisplayName, a.BrandId, a.DepartmentId, Json.Element(a.SettingsJson),
            secretKeys, a.IsActive, a.Status.ToString(), a.LastError, a.LastPolledAt, a.SignatureHtml, a.AutoAcknowledge, a.AcceptUnknownSenders,
            a.WebhookKey is null ? null : $"/api/v1/channels/webhooks/{a.Provider.ToLowerInvariant()}/{a.WebhookKey}", a.CreatedAt, a.UpdatedAt,
            Convert.ToBase64String(a.RowVersion));
    }
}
