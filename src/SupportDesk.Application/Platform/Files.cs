using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Platform;

/// <summary>
/// Validated file intake: size limit (plan), blocked executable/script types (by extension and magic bytes),
/// content-type sniffing for images/PDF, SHA-256, malware scan (infected files are rejected and never stored),
/// tenant-prefixed storage keys and storage usage tracking.
/// </summary>
public sealed class FileService(DeskDbContext db, IFileStorage storage, IMalwareScanner scanner, LimitGuard limits, UsageMeter usage, RequestContext ctx)
{
    public const int DefaultMaxMb = 25;

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".bat", ".cmd", ".scr", ".pif", ".msi", ".msp", ".jar", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh",
        ".ps1", ".psm1", ".hta", ".cpl", ".lnk", ".reg", ".sys", ".scf", ".inf", ".gadget", ".application", ".appx", ".msix", ".sh",
        ".apk", ".iso", ".img", ".vhd", ".vhdx", ".docm", ".xlsm", ".pptm", ".dotm", ".xlam", ".html", ".htm", ".xhtml", ".svg", ".mht"
    };

    /// <summary>Types we are willing to render inline (images, PDF, plain text, audio, video). Everything else downloads as attachment.</summary>
    public static bool IsInlineSafe(string contentType) =>
        contentType is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "application/pdf" or "text/plain" ||
        contentType.StartsWith("audio/", StringComparison.Ordinal) || contentType.StartsWith("video/", StringComparison.Ordinal);

    public async Task<StoredFile> UploadAsync(Stream content, string fileName, string? contentType, string purpose = "attachment", CancellationToken ct = default)
    {
        if (!ctx.HasTenant) throw new ForbiddenException("Uploads require a tenant.");
        await limits.EnsureActiveAsync(ct);
        var plan = await limits.GetLimitsAsync(ct);
        var maxBytes = (long)(plan.MaxAttachmentMb ?? DefaultMaxMb) * 1024 * 1024;

        var safeName = SanitizeFileName(fileName);
        var ext = Path.GetExtension(safeName);
        if (BlockedExtensions.Contains(ext))
            throw new DomainException("file_type_blocked", $"Files of type '{ext}' are not accepted for security reasons.");

        // Buffer to a temp file so we can sniff, hash and scan without trusting the declared type or size.
        var tempPath = Path.GetTempFileName();
        try
        {
            long size;
            await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                size = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > maxBytes) throw new DomainException("file_too_large", $"Files may be at most {maxBytes / 1024 / 1024} MB.", 413);
                    await temp.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            if (size == 0) throw new DomainException("file_empty", "The file is empty.");
            await limits.EnsureWithinAsync(UsageMetrics.StorageBytes, size, ct);

            string sha;
            string detectedType;
            await using (var read = File.OpenRead(tempPath))
            {
                var header = new byte[16];
                var n = await read.ReadAsync(header, ct);
                if (IsExecutable(header.AsSpan(0, n)))
                    throw new DomainException("file_type_blocked", "Executable content is not accepted.");
                detectedType = Sniff(header.AsSpan(0, n), ext, contentType);
                read.Position = 0;
                sha = Convert.ToHexString(await SHA256.HashDataAsync(read, ct)).ToLowerInvariant();
                read.Position = 0;
                var scan = await scanner.ScanAsync(read, safeName, ct);
                if (scan.Status is ScanStatus.Infected or ScanStatus.Blocked)
                    throw new DomainException("file_infected", $"The file was rejected by the malware scanner ({scan.Detail}).");
            }

            var file = new StoredFile
            {
                FileName = safeName,
                ContentType = detectedType,
                SizeBytes = size,
                Sha256 = sha,
                Purpose = purpose,
                ScanStatus = ScanStatus.Clean,
                UploadedByUserId = ctx.UserId,
                UploadedByContactId = ctx.ContactId
            };
            file.StorageKey = $"{ctx.TenantId:N}/{db.Clock.UtcNow:yyyy/MM}/{file.Id:N}{ext.ToLowerInvariant()}";
            await using (var read = File.OpenRead(tempPath))
                await storage.SaveAsync(file.StorageKey, read, detectedType, ct);
            db.StoredFiles.Add(file);
            await db.SaveChangesAsync(ct);
            await usage.IncrementAsync(UsageMetrics.StorageBytes, size, ct);
            return file;
        }
        catch (IOException ex) when (IsHostAntivirusBlock(ex))
        {
            // An on-access scanner on the server (e.g. Microsoft Defender) quarantined the buffered upload before our own
            // scan could read it back. That is a detection too: reject it cleanly (it used to surface as a 500).
            throw new DomainException("file_infected", "The file was rejected by the malware scanner (host antivirus).");
        }
        finally
        {
            try { File.Delete(tempPath); } catch (IOException) { }
        }
    }

    public async Task<StoredFile> UploadBytesAsync(byte[] bytes, string fileName, string? contentType, string purpose, CancellationToken ct = default)
    {
        await using var ms = new MemoryStream(bytes);
        return await UploadAsync(ms, fileName, contentType, purpose, ct);
    }

    /// <summary>Opens a stored file of the current tenant (tenant filter applies). Callers authorise access to the owning record.</summary>
    public async Task<(StoredFile File, Stream Content)> OpenAsync(Guid fileId, CancellationToken ct = default)
    {
        var file = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct) ?? throw new NotFoundException("File", fileId);
        if (file.ScanStatus is ScanStatus.Infected or ScanStatus.Blocked) throw new ForbiddenException("This file is quarantined.");
        return (file, await storage.OpenReadAsync(file.StorageKey, ct));
    }

    public async Task<byte[]> ReadAllAsync(Guid fileId, CancellationToken ct = default)
    {
        var (_, stream) = await OpenAsync(fileId, ct);
        await using (stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
    }

    public static string SanitizeFileName(string? name)
    {
        var n = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "file" : name.Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars().Concat(['"', '<', '>', '|', ':', '*', '?']).ToHashSet();
        n = new string(n.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (n.Length == 0) n = "file";
        return n.Length > 180 ? n[..150] + Path.GetExtension(n) : n;
    }

    /// <summary>Win32 ERROR_VIRUS_INFECTED (225) / ERROR_VIRUS_DELETED (226) raised by the OS when an antivirus blocks the file.</summary>
    private static bool IsHostAntivirusBlock(IOException ex) => ex.HResult is unchecked((int)0x800700E1) or unchecked((int)0x800700E2);

    private static bool IsExecutable(ReadOnlySpan<byte> h) =>
        (h.Length >= 2 && h[0] == 'M' && h[1] == 'Z') ||                                  // PE / DOS
        (h.Length >= 4 && h[0] == 0x7F && h[1] == 'E' && h[2] == 'L' && h[3] == 'F') ||   // ELF
        (h.Length >= 4 && h[0] == 0xCF && h[1] == 0xFA && h[2] == 0xED && h[3] == 0xFE) || // Mach-O
        (h.Length >= 2 && h[0] == '#' && h[1] == '!');                                    // script shebang

    private static string Sniff(ReadOnlySpan<byte> h, string ext, string? declared)
    {
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G') return "image/png";
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "image/jpeg";
        if (h.Length >= 4 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8') return "image/gif";
        if (h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return "image/webp";
        if (h.Length >= 4 && h[0] == '%' && h[1] == 'P' && h[2] == 'D' && h[3] == 'F') return "application/pdf";
        if (h.Length >= 4 && h[0] == 'P' && h[1] == 'K' && h[2] == 3 && h[3] == 4)
            return ext.ToLowerInvariant() switch
            {
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                _ => "application/zip"
            };
        var byExt = ext.ToLowerInvariant() switch
        {
            ".txt" or ".log" => "text/plain",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".eml" => "message/rfc822",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".doc" => "application/msword",
            ".xls" => "application/vnd.ms-excel",
            ".rtf" => "application/rtf",
            _ => null
        };
        // Never trust a declared image/pdf/html type that the bytes don't confirm.
        if (byExt is not null) return byExt;
        if (!string.IsNullOrWhiteSpace(declared) && !declared.StartsWith("image/") && !declared.Contains("html") && declared != "application/pdf")
            return declared;
        return "application/octet-stream";
    }
}
