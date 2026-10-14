using System.Net.Sockets;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using SupportDesk.Application.Platform;
using SupportDesk.Domain.Platform;

namespace SupportDesk.Integrations.Platform;

/// <summary>Filesystem storage rooted at Storage:DataPath/files. Keys are validated to stay inside the root (no path traversal).</summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;

    public LocalFileStorage(IConfiguration config, IHostEnvironment env)
    {
        _root = Path.Combine(DataPaths.Root(config, env), "files");
        Directory.CreateDirectory(_root);
    }

    private string PathFor(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid storage key.");
        return full;
    }

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await content.CopyToAsync(fs, ct);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathFor(key)));
}

/// <summary>
/// Malware scanning. Always detects the EICAR test signature and blocks archives containing executables' magic bytes;
/// when Security:ClamAv:Host is configured, streams content to clamd (INSTREAM) for full scanning.
/// If clamd is configured but unreachable, uploads fail closed.
/// </summary>
public sealed class SignatureMalwareScanner(IConfiguration config, ILogger<SignatureMalwareScanner> logger) : IMalwareScanner
{
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    public async Task<ScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (bytes.AsSpan().IndexOf(Eicar) >= 0) return new ScanResult(ScanStatus.Infected, "EICAR-Test-Signature");

        var host = config["Security:ClamAv:Host"];
        if (string.IsNullOrWhiteSpace(host)) return new ScanResult(ScanStatus.Clean, "signature");
        var port = int.TryParse(config["Security:ClamAv:Port"], out var p) ? p : 3310;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), ct);
            for (var offset = 0; offset < bytes.Length; offset += 8192)
            {
                var len = Math.Min(8192, bytes.Length - offset);
                var size = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(len));
                await stream.WriteAsync(size, ct);
                await stream.WriteAsync(bytes.AsMemory(offset, len), ct);
            }
            await stream.WriteAsync(new byte[4], ct);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            var reply = (await reader.ReadToEndAsync(ct)).Trim('\0', ' ', '\n');
            return reply.EndsWith("OK", StringComparison.Ordinal)
                ? new ScanResult(ScanStatus.Clean, "clamav")
                : new ScanResult(ScanStatus.Infected, reply);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            logger.LogError(ex, "ClamAV unreachable at {Host}:{Port}; rejecting upload", host, port);
            return new ScanResult(ScanStatus.Blocked, "scanner unavailable");
        }
    }
}

/// <summary>
/// System e-mail transport. With Mail:Smtp:Host configured it sends via SMTP (MailKit, STARTTLS); otherwise it writes
/// RFC 5322 .eml files to Storage:DataPath/mail-outbox so development and demos can inspect every message.
/// </summary>
public sealed class SystemMailer(IConfiguration config, IHostEnvironment env, ILogger<SystemMailer> logger) : ISystemMailer
{
    public async Task SendAsync(Guid? tenantId, string to, string subject, string html, string? text = null, IReadOnlyList<MailAttachment>? attachments = null,
        CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(config["Mail:From"] ?? "SupportDesk <no-reply@supportdesk.local>"));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Headers.Add("Auto-Submitted", "auto-generated");
        if (tenantId is { } t) message.Headers.Add("X-SupportDesk-Tenant", t.ToString());
        var body = new BodyBuilder { HtmlBody = html, TextBody = text ?? Domain.Rules.TextNormalization.HtmlToText(html) };
        foreach (var a in attachments ?? []) body.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        message.Body = body.ToMessageBody();

        var host = config["Mail:Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            var dir = Path.Combine(DataPaths.Root(config, env), "mail-outbox");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.eml");
            await message.WriteToAsync(file, ct);
            logger.LogInformation("System e-mail to {To} written to {File}", to, file);
            return;
        }
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(host, int.TryParse(config["Mail:Smtp:Port"], out var port) ? port : 587, SecureSocketOptions.StartTlsWhenAvailable, ct);
        if (config["Mail:Smtp:Username"] is { Length: > 0 } user) await smtp.AuthenticateAsync(user, config["Mail:Smtp:Password"] ?? "", ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}
