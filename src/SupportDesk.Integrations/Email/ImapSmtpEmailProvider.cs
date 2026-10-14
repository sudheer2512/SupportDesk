using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using SupportDesk.Application.Communication;

namespace SupportDesk.Integrations.Email;

/// <summary>
/// IMAP (inbound, UID cursor "uidvalidity:lastuid") + SMTP (outbound) via MailKit. SSL-on-connect, STARTTLS or plain
/// (setting imapSecurity/smtpSecurity), password or OAuth2 XOAUTH2 (authMode = "oauth2" with an accessToken secret).
/// The first poll imports unread messages only; afterwards everything with a higher UID.
/// </summary>
public class ImapSmtpEmailProvider : IEmailProvider
{
    public virtual string Provider => ChannelProviders.Imap;
    public virtual bool SupportsPolling => true;

    public virtual async Task<EmailFetchResult> FetchAsync(ChannelAccountContext account, string? cursor, int max, CancellationToken ct)
    {
        using var client = new ImapClient { Timeout = 60_000 };
        await ConnectImapAsync(client, account, ct);
        var folder = await OpenFolderAsync(client, account, FolderAccess.ReadOnly, ct);
        var validity = folder.UidValidity;
        uint? lastUid = null;
        if (cursor is { } c && c.Split(':') is [var v, var u] && uint.TryParse(v, out var cv) && uint.TryParse(u, out var cu) && cv == validity) lastUid = cu;

        IList<UniqueId> uids = lastUid is { } last
            ? (await folder.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(validity, last + 1), UniqueId.MaxValue)), ct)).Where(x => x.Id > last).ToList()
            : await folder.SearchAsync(SearchQuery.NotSeen, ct);
        var ordered = uids.OrderBy(x => x.Id).ToList();
        var batch = ordered.Take(max).ToList();
        var items = new List<FetchedEmail>();
        foreach (var uid in batch)
        {
            var message = await folder.GetMessageAsync(uid, ct);
            using var ms = new MemoryStream();
            await message.WriteToAsync(ms, ct);
            items.Add(new FetchedEmail(uid.Id.ToString(), MimeCodec.FromMime(message, ms.ToArray())));
        }
        uint highest;
        var hasMore = ordered.Count > batch.Count;
        if (lastUid is null)
        {
            // First poll: unread mail only, then continue from the current end of the mailbox (read history is never imported).
            highest = folder.UidNext is { } next ? next.Id - 1 : batch.Count > 0 ? batch[^1].Id : 0;
            hasMore = false;
        }
        else highest = batch.Count > 0 ? batch[^1].Id : lastUid.Value;
        await client.DisconnectAsync(true, ct);
        return new EmailFetchResult(items, $"{validity}:{highest}", hasMore);
    }

    public virtual async Task AcknowledgeAsync(ChannelAccountContext account, IReadOnlyList<string> providerIds, CancellationToken ct)
    {
        if (!account.Bool("markSeen", true) || providerIds.Count == 0) return;
        using var client = new ImapClient { Timeout = 60_000 };
        await ConnectImapAsync(client, account, ct);
        var folder = await OpenFolderAsync(client, account, FolderAccess.ReadWrite, ct);
        var uids = providerIds.Select(id => uint.TryParse(id, out var n) ? new UniqueId(folder.UidValidity, n) : UniqueId.Invalid).Where(u => u.IsValid).ToList();
        if (uids.Count > 0) await folder.AddFlagsAsync(uids, MessageFlags.Seen, true, ct);
        await client.DisconnectAsync(true, ct);
    }

    public async Task<SendResult> SendAsync(ChannelAccountContext account, OutboundEmail email, CancellationToken ct)
    {
        MimeMessage message;
        try
        {
            message = MimeCodec.ToMime(email);
        }
        catch (ParseException ex)
        {
            return SendResult.Fail($"Invalid recipient address: {ex.Message}", permanent: true);
        }
        try
        {
            using var smtp = new SmtpClient { Timeout = 60_000 };
            var host = account.RequireSetting("smtpHost");
            var port = account.Int("smtpPort", 587);
            await smtp.ConnectAsync(host, port, MailSecurity.Parse(account.Setting("smtpSecurity"), port), ct);
            await AuthenticateAsync(smtp, account, ct);
            await smtp.SendAsync(message, ct);
            await smtp.DisconnectAsync(true, ct);
            return SendResult.Ok(email.MessageId);
        }
        catch (AuthenticationException ex)
        {
            return SendResult.Fail($"SMTP authentication failed: {ex.Message}", permanent: true);
        }
        catch (SmtpCommandException ex) when (ex.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.SenderNotAccepted)
        {
            return SendResult.Fail($"SMTP rejected the message ({(int)ex.StatusCode}): {ex.Message}", permanent: (int)ex.StatusCode >= 500);
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or IOException or SocketException or ServiceNotConnectedException)
        {
            throw new MailTransportException($"SMTP delivery failed: {ex.Message}", ex);
        }
    }

    public virtual async Task<ConnectionTestResult> TestAsync(ChannelAccountContext account, CancellationToken ct)
    {
        var details = new Dictionary<string, string>();
        try
        {
            if (SupportsPolling)
            {
                using var imap = new ImapClient { Timeout = 30_000 };
                await ConnectImapAsync(imap, account, ct);
                var folder = await OpenFolderAsync(imap, account, FolderAccess.ReadOnly, ct);
                details["imap"] = $"{folder.FullName}: {folder.Count} message(s), {folder.Unread} unread";
                await imap.DisconnectAsync(true, ct);
            }
            if (account.Setting("smtpHost") is { Length: > 0 } host)
            {
                using var smtp = new SmtpClient { Timeout = 30_000 };
                var port = account.Int("smtpPort", 587);
                await smtp.ConnectAsync(host, port, MailSecurity.Parse(account.Setting("smtpSecurity"), port), ct);
                await AuthenticateAsync(smtp, account, ct);
                details["smtp"] = $"{host}:{port} authenticated";
                await smtp.DisconnectAsync(true, ct);
            }
            return new ConnectionTestResult(true, "Connected successfully.", details);
        }
        catch (AuthenticationException ex)
        {
            return new ConnectionTestResult(false, $"Authentication failed: {ex.Message}", details);
        }
        catch (Exception ex) when (ex is ImapCommandException or ImapProtocolException or SmtpCommandException or SmtpProtocolException or IOException or SocketException
                                       or SslHandshakeException)
        {
            return new ConnectionTestResult(false, $"Connection failed: {ex.Message}", details);
        }
    }

    private static async Task ConnectImapAsync(ImapClient client, ChannelAccountContext account, CancellationToken ct)
    {
        var host = account.RequireSetting("imapHost");
        var port = account.Int("imapPort", 993);
        await client.ConnectAsync(host, port, MailSecurity.Parse(account.Setting("imapSecurity"), port), ct);
        await AuthenticateAsync(client, account, ct);
    }

    private static async Task<IMailFolder> OpenFolderAsync(ImapClient client, ChannelAccountContext account, FolderAccess access, CancellationToken ct)
    {
        var name = account.SettingOr("folder", "INBOX");
        var folder = name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(name, ct);
        await folder.OpenAsync(access, ct);
        return folder;
    }

    private static async Task AuthenticateAsync(MailService client, ChannelAccountContext account, CancellationToken ct)
    {
        var user = account.Setting("username") ?? account.Account.Address;
        if (string.Equals(account.Setting("authMode"), "oauth2", StringComparison.OrdinalIgnoreCase))
        {
            await client.AuthenticateAsync(new SaslMechanismOAuth2(user, account.RequireSecret("accessToken")), ct);
            return;
        }
        if (account.Secret("password") is { } password) await client.AuthenticateAsync(user, password, ct);
    }
}

/// <summary>Outbound-only SMTP mailbox (no polling).</summary>
public sealed class SmtpEmailProvider : ImapSmtpEmailProvider
{
    public override string Provider => ChannelProviders.Smtp;
    public override bool SupportsPolling => false;

    public override Task<EmailFetchResult> FetchAsync(ChannelAccountContext account, string? cursor, int max, CancellationToken ct) =>
        Task.FromResult(new EmailFetchResult([], cursor));

    public override Task AcknowledgeAsync(ChannelAccountContext account, IReadOnlyList<string> providerIds, CancellationToken ct) => Task.CompletedTask;
}
