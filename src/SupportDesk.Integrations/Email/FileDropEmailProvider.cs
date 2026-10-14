using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Communication;
using SupportDesk.Application.Platform;

namespace SupportDesk.Integrations.Email;

/// <summary>
/// Development/demo mailbox backed by folders: inbound .eml files are read from {DataPath}/maildrop/{address}/inbox and moved
/// to processed/ once stored (unparseable files go to failed/); outbound mail is written as .eml to …/sent.
/// </summary>
public sealed class FileDropEmailProvider(IConfiguration config, IHostEnvironment env, IMimeCodec mime, ILogger<FileDropEmailProvider> logger) : IEmailProvider
{
    public string Provider => ChannelProviders.FileDrop;
    public bool SupportsPolling => true;

    public string MailboxRoot(string address)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var folder = new string(address.Trim().ToLowerInvariant().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (folder.Length == 0 || folder is "." or "..") throw new ChannelConfigurationException("The mailbox address is not valid for a file-drop folder.");
        return Path.Combine(DataPaths.Root(config, env), "maildrop", folder);
    }

    private (string Inbox, string Processed, string Failed, string Sent) Folders(ChannelAccountContext account)
    {
        var root = MailboxRoot(account.Account.Address);
        var f = (Path.Combine(root, "inbox"), Path.Combine(root, "processed"), Path.Combine(root, "failed"), Path.Combine(root, "sent"));
        Directory.CreateDirectory(f.Item1);
        Directory.CreateDirectory(f.Item2);
        Directory.CreateDirectory(f.Item3);
        Directory.CreateDirectory(f.Item4);
        return f;
    }

    public async Task<EmailFetchResult> FetchAsync(ChannelAccountContext account, string? cursor, int max, CancellationToken ct)
    {
        var (inbox, _, failed, _) = Folders(account);
        var files = Directory.EnumerateFiles(inbox, "*.eml").OrderBy(f => File.GetLastWriteTimeUtc(f)).ThenBy(f => f, StringComparer.Ordinal).ToList();
        var items = new List<FetchedEmail>();
        foreach (var path in files.Take(max))
        {
            byte[] raw;
            try
            {
                raw = await File.ReadAllBytesAsync(path, ct);
            }
            catch (IOException)
            {
                continue; // still being written — next poll
            }
            try
            {
                items.Add(new FetchedEmail(Path.GetFileName(path), mime.Parse(raw)));
            }
            catch (Exception ex) when (ex is FormatException or MimeKit.ParseException or ArgumentException)
            {
                logger.LogWarning("File-drop message {File} is not valid MIME; moved to failed/: {Error}", Path.GetFileName(path), ex.Message);
                Move(path, Path.Combine(failed, Path.GetFileName(path)));
            }
        }
        return new EmailFetchResult(items, null, files.Count > max);
    }

    public Task AcknowledgeAsync(ChannelAccountContext account, IReadOnlyList<string> providerIds, CancellationToken ct)
    {
        var (inbox, processed, _, _) = Folders(account);
        foreach (var id in providerIds)
        {
            var name = Path.GetFileName(id);
            var source = Path.Combine(inbox, name);
            if (File.Exists(source)) Move(source, Path.Combine(processed, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{name}"));
        }
        return Task.CompletedTask;
    }

    public async Task<SendResult> SendAsync(ChannelAccountContext account, OutboundEmail email, CancellationToken ct)
    {
        byte[] raw;
        try
        {
            raw = mime.Compose(email);
        }
        catch (MimeKit.ParseException ex)
        {
            return SendResult.Fail($"Invalid recipient address: {ex.Message}", permanent: true);
        }
        var (_, _, _, sent) = Folders(account);
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Domain.Rules.TextNormalization.Sha256Hex(email.MessageId)[..12]}.eml";
        await File.WriteAllBytesAsync(Path.Combine(sent, name), raw, ct);
        return SendResult.Ok(email.MessageId);
    }

    public Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct)
    {
        var (inbox, processed, _, sent) = Folders(account);
        var pending = Directory.EnumerateFiles(inbox, "*.eml").Count();
        return Task.FromResult(new ConnectionTestResult(true, $"File-drop mailbox ready ({pending} message(s) waiting).",
            new Dictionary<string, string> { ["inbox"] = inbox, ["processed"] = processed, ["sent"] = sent }));
    }

    private static void Move(string from, string to)
    {
        try
        {
            if (File.Exists(to)) to = Path.Combine(Path.GetDirectoryName(to)!, $"{Guid.NewGuid():N}-{Path.GetFileName(to)}");
            File.Move(from, to);
        }
        catch (IOException)
        {
            // Another poller moved it already.
        }
    }
}
