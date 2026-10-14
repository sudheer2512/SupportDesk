using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Platform;
using SupportDesk.Domain.Communication;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Communication;

/// <summary>
/// Polls every active mailbox (FileDrop, IMAP, Microsoft 365, Gmail) of every tenant on its own interval
/// (setting pollIntervalSeconds, default 30 s). The provider cursor is stored on the account only after every fetched
/// message was processed, items are acknowledged after they were stored, and errors set the account status/LastError
/// (credentials are never logged). One failing mailbox never blocks the others.
/// </summary>
public sealed class MailboxPollingTask(TenantScopeRunner tenants, ILogger<MailboxPollingTask> logger) : IRecurringTask
{
    public string Name => "mailbox-poller";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public Task RunAsync(CancellationToken ct) => tenants.ForEachActiveTenantAsync(Name, async (sp, tenant, token) =>
    {
        var db = sp.GetRequiredService<DeskDbContext>();
        var ids = await db.ChannelAccounts.AsNoTracking().Where(a => a.Type == ChannelType.Email && a.IsActive).Select(a => a.Id).ToListAsync(token);
        foreach (var id in ids)
        {
            try
            {
                await tenants.RunForTenantAsync(tenant.Id, Name, s => s.GetRequiredService<MailboxPoller>().PollAsync(id, force: false, token), token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Mailbox {Account} of tenant {Tenant} could not be polled", id, tenant.Slug);
            }
        }
    }, ct);
}

public sealed record PollResult(int Fetched, int Processed, int Duplicates, int Failed, string? Error);

public sealed class MailboxPoller(
    DeskDbContext db,
    ChannelProviderRegistry registry,
    ChannelContextFactory contexts,
    InboundEmailProcessor processor,
    ILogger<MailboxPoller> logger)
{
    private const int BatchSize = 25;

    public async Task<PollResult> PollAsync(Guid accountId, bool force, CancellationToken ct)
    {
        var account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null || !account.IsActive || account.Type != ChannelType.Email) return new PollResult(0, 0, 0, 0, "Mailbox is not active.");
        var provider = registry.Email(account.Provider);
        if (provider is null || !provider.SupportsPolling) return new PollResult(0, 0, 0, 0, null);
        var context = contexts.Create(account);
        var now = db.Clock.UtcNow;
        var interval = Math.Clamp(context.Int("pollIntervalSeconds", 30), 0, 3600);
        if (!force && account.LastPolledAt is { } last && last.AddSeconds(interval) > now.AddSeconds(2)) return new PollResult(0, 0, 0, 0, null);

        int fetched = 0, processed = 0, duplicates = 0, failed = 0;
        string? error = null;
        try
        {
            var cursor = account.PollCursor;
            for (var round = 0; round < 8; round++)
            {
                var batch = await provider.FetchAsync(context, cursor, BatchSize, ct);
                fetched += batch.Items.Count;
                var done = new List<string>();
                var allOk = true;
                foreach (var item in batch.Items)
                {
                    var outcome = await TryProcessAsync(account.Id, item, ct);
                    if (outcome is null) { failed++; allOk = false; continue; }
                    if (outcome.Outcome is InboundOutcomes.Duplicate) duplicates++;
                    else if (outcome.Outcome is not InboundOutcomes.Processing) processed++;
                    if (outcome.Outcome is not InboundOutcomes.Processing) done.Add(item.ProviderId);
                }
                if (done.Count > 0) await provider.AcknowledgeAsync(context, done, ct);
                if (!allOk) break; // keep the cursor: failed items are fetched again (duplicates are skipped)
                cursor = batch.NextCursor ?? cursor;
                await SaveCursorAsync(accountId, cursor, ct);
                if (!batch.HasMore || batch.Items.Count == 0) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex is ChannelConfigurationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning("Polling mailbox {Account} ({Provider}) failed: {Error}", accountId, account.Provider, error);
        }
        db.ChangeTracker.Clear();
        var row = await db.ChannelAccounts.FirstAsync(a => a.Id == accountId, ct);
        row.LastPolledAt = db.Clock.UtcNow;
        if (error is not null)
        {
            row.Status = ChannelAccountStatus.Error;
            row.LastError = TextNormalization.Truncate(error, 1000);
        }
        else if (failed == 0)
        {
            row.Status = ChannelAccountStatus.Connected;
            row.LastError = null;
        }
        await db.SaveChangesAsync(ct);
        return new PollResult(fetched, processed, duplicates, failed, error);
    }

    private async Task<InboundOutcome?> TryProcessAsync(Guid accountId, FetchedEmail item, CancellationToken ct)
    {
        try
        {
            db.ChangeTracker.Clear();
            var account = await db.ChannelAccounts.AsNoTracking().FirstAsync(a => a.Id == accountId, ct);
            return await processor.ProcessAsync(account, item.Email, item.ProviderId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Message {ProviderId} of mailbox {Account} failed; it will be retried", item.ProviderId, accountId);
            return null;
        }
    }

    private async Task SaveCursorAsync(Guid accountId, string? cursor, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await db.ChannelAccounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(u => u.SetProperty(a => a.PollCursor, cursor), ct);
    }
}
