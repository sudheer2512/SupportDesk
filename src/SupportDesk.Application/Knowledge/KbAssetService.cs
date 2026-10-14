using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Platform;
using SupportDesk.Domain;
using SupportDesk.Domain.Platform;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Serves images embedded in article bodies (<c>/api/v1/kb-assets/{fileId}</c>) without authentication so portals and
/// e-mails can render them. Only files uploaded through the article image endpoint (purpose kb-asset), of an inline-safe
/// image type and with a clean scan are served; the unguessable file id is the capability. The tenant is taken from the
/// file record, never from the request.
/// </summary>
public sealed class KbAssetService(DeskDbContext db, RequestContext ctx, IFileStorage storage)
{
    public async Task<(StoredFile File, Stream Content)> OpenAsync(Guid fileId, CancellationToken ct = default)
    {
        var file = await db.AllTenants<StoredFile>().AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId && f.Purpose == ArticleAuthoringService.AssetPurpose, ct);
        if (file is null || file.ScanStatus is ScanStatus.Infected or ScanStatus.Blocked || !file.ContentType.StartsWith("image/", StringComparison.Ordinal)
            || !FileService.IsInlineSafe(file.ContentType))
            throw new NotFoundException("Image", fileId);
        if (ctx.HasTenant && ctx.TenantId != file.TenantId) throw new NotFoundException("Image", fileId);
        return (file, await storage.OpenReadAsync(file.StorageKey, ct));
    }
}
