using MimeKit;
using SupportDesk.Application.Communication;

namespace SupportDesk.Integrations.Email;

/// <summary>MimeKit implementation of the MIME port: parses raw RFC 5322 messages and composes outbound ones.</summary>
public sealed class MimeCodec : IMimeCodec
{
    private const int MaxAttachments = 50;
    private const int MaxHeaderValue = 1000;

    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "From", "To", "Cc", "Bcc", "Reply-To", "Subject", "Message-ID", "In-Reply-To", "References", "Date", "MIME-Version", "Content-Type",
        "Content-Transfer-Encoding", "Sender", "Return-Path"
    };

    public InboundEmail Parse(byte[] raw)
    {
        using var ms = new MemoryStream(raw, writable: false);
        var message = MimeMessage.Load(ms);
        return FromMime(message, raw);
    }

    public static InboundEmail FromMime(MimeMessage m, byte[]? raw)
    {
        var from = m.From.Mailboxes.FirstOrDefault() ?? m.Sender ?? m.ReplyTo.Mailboxes.FirstOrDefault();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in m.Headers)
        {
            if (headers.Count >= 200) break;
            if (!headers.ContainsKey(h.Field)) headers[h.Field] = h.Value.Length > MaxHeaderValue ? h.Value[..MaxHeaderValue] : h.Value;
        }
        var attachments = new List<EmailAttachment>();
        var index = 0;
        foreach (var entity in m.BodyParts)
        {
            if (attachments.Count >= MaxAttachments) break;
            index++;
            switch (entity)
            {
                case MessagePart forwarded:
                {
                    using var fs = new MemoryStream();
                    forwarded.Message?.WriteTo(fs);
                    var name = forwarded.ContentDisposition?.FileName ?? $"{Safe(forwarded.Message?.Subject, "forwarded-message")}.eml";
                    attachments.Add(new EmailAttachment(name, "message/rfc822", fs.ToArray()));
                    break;
                }
                case MimePart part:
                {
                    if (part is TextPart text && !part.IsAttachment && (text.IsPlain || text.IsHtml)) continue;
                    if (part.Content is null) continue;
                    using var content = new MemoryStream();
                    part.Content.DecodeTo(content);
                    var contentType = part.ContentType.MimeType;
                    var extension = MimeTypes.TryGetExtension(contentType, out var ext) ? ext : ".bin";
                    var fileName = part.FileName ?? $"{(part.ContentId is null ? "attachment" : "inline")}-{index}{extension}";
                    var inline = !part.IsAttachment && part.ContentId is not null;
                    attachments.Add(new EmailAttachment(fileName, contentType, content.ToArray(), part.ContentId, inline));
                    break;
                }
            }
        }
        return new InboundEmail(
            m.MessageId,
            m.InReplyTo,
            m.References.ToList(),
            from?.Address ?? "",
            string.IsNullOrWhiteSpace(from?.Name) ? null : from!.Name,
            m.To.Mailboxes.Select(a => a.Address).ToList(),
            m.Cc.Mailboxes.Select(a => a.Address).ToList(),
            m.Subject ?? "",
            m.TextBody,
            m.HtmlBody,
            headers,
            attachments,
            m.Date == DateTimeOffset.MinValue ? null : m.Date.UtcDateTime,
            raw,
            m.Body is MultipartReport || m.Body?.ContentType.IsMimeType("multipart", "report") == true);
    }

    public byte[] Compose(OutboundEmail e)
    {
        var m = ToMime(e);
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        return ms.ToArray();
    }

    public static MimeMessage ToMime(OutboundEmail e)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress(e.FromName ?? "", e.From));
        foreach (var a in e.To) m.To.Add(MailboxAddress.Parse(a));
        foreach (var a in e.Cc) m.Cc.Add(MailboxAddress.Parse(a));
        foreach (var a in e.Bcc) m.Bcc.Add(MailboxAddress.Parse(a));
        if (!string.IsNullOrWhiteSpace(e.ReplyTo)) m.ReplyTo.Add(new MailboxAddress(e.FromName ?? "", e.ReplyTo));
        m.Subject = e.Subject;
        m.MessageId = e.MessageId;
        if (!string.IsNullOrWhiteSpace(e.InReplyTo)) m.InReplyTo = e.InReplyTo;
        foreach (var r in e.References) m.References.Add(r);
        m.Date = DateTimeOffset.UtcNow;
        foreach (var (k, v) in e.Headers)
        {
            if (ReservedHeaders.Contains(k) || k.Any(ch => ch is ':' or '\r' or '\n' || char.IsWhiteSpace(ch))) continue;
            m.Headers.Add(k, v.Replace("\r", " ").Replace("\n", " "));
        }
        var body = new BodyBuilder { TextBody = e.TextBody };
        if (!string.IsNullOrWhiteSpace(e.HtmlBody)) body.HtmlBody = e.HtmlBody.Contains("<html", StringComparison.OrdinalIgnoreCase)
            ? e.HtmlBody
            : $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px\">{e.HtmlBody}</body></html>";
        foreach (var a in e.Attachments)
        {
            var type = ContentType.TryParse(a.ContentType, out var ctype) ? ctype : new ContentType("application", "octet-stream");
            if (a.Inline && a.ContentId is { Length: > 0 } cid && !string.IsNullOrWhiteSpace(body.HtmlBody))
            {
                var res = body.LinkedResources.Add(a.FileName, a.Content, type);
                res.ContentId = cid;
            }
            else
            {
                body.Attachments.Add(a.FileName, a.Content, type);
            }
        }
        m.Body = body.ToMessageBody();
        return m;
    }

    private static string Safe(string? s, string fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? fallback : clean.Length > 80 ? clean[..80] : clean;
    }
}

/// <summary>Maps the security setting ("ssl", "starttls", "none", "auto") to MailKit options.</summary>
internal static class MailSecurity
{
    public static MailKit.Security.SecureSocketOptions Parse(string? value, int port) => (value ?? "auto").ToLowerInvariant() switch
    {
        "ssl" or "tls" or "sslonconnect" => MailKit.Security.SecureSocketOptions.SslOnConnect,
        "starttls" => MailKit.Security.SecureSocketOptions.StartTls,
        "none" => MailKit.Security.SecureSocketOptions.None,
        _ => port is 993 or 465 ? MailKit.Security.SecureSocketOptions.SslOnConnect : MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable
    };
}
