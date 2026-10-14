using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Knowledge;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Search;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Api.Controllers.Knowledge;

/// <summary>
/// Knowledge base for agents: categories, articles (versions, diff/restore, workflow, translations, attachments, access
/// rules), feedback review, ticket → article, import/export, analytics and guided-troubleshooting flows.
/// </summary>
[Route("api/v1/kb")]
public sealed class KnowledgeController(
    ArticleService articles,
    ArticleAuthoringService authoring,
    ArticleTranslationService translations,
    KbCategoryService categories,
    KbFeedbackService feedback,
    KbImportExportService transfer,
    TroubleshootingService flows) : DeskControllerBase
{
    // ───── Options ─────

    [HttpGet("options")]
    public Task<KbOptionsDto> Options(CancellationToken ct) => authoring.OptionsAsync(ct);

    [HttpGet("options/customers")]
    public Task<List<IdName>> Customers([FromQuery] string? q, CancellationToken ct) => authoring.CustomerOptionsAsync(q, ct);

    // ───── Categories ─────

    [HttpGet("categories")]
    public Task<List<KbCategoryDto>> Categories(CancellationToken ct) => categories.ListAsync(ct);

    [HttpPost("categories")]
    public Task<KbCategoryDto> CreateCategory(SaveKbCategoryRequest r, CancellationToken ct) => categories.SaveAsync(null, r, ct);

    [HttpPut("categories/{id:guid}")]
    public Task<KbCategoryDto> UpdateCategory(Guid id, SaveKbCategoryRequest r, CancellationToken ct) => categories.SaveAsync(id, r, ct);

    [HttpDelete("categories/{id:guid}")]
    public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct)
    {
        await categories.DeleteAsync(id, ct);
        return NoContent();
    }

    // ───── Articles ─────

    [HttpGet("articles")]
    public Task<PageResult<ArticleListItemDto>> List([FromQuery] ArticleQuery q, CancellationToken ct) => articles.ListAsync(q, ct);

    [HttpPost("articles")]
    public Task<ArticleDetailDto> Create(SaveArticleRequest r, CancellationToken ct) => articles.CreateAsync(r, ct);

    [HttpGet("articles/{id:guid}")]
    public Task<ArticleDetailDto> Get(Guid id, CancellationToken ct) => articles.GetAsync(id, ct);

    [HttpPut("articles/{id:guid}")]
    public Task<ArticleDetailDto> Update(Guid id, SaveArticleRequest r, CancellationToken ct) => articles.UpdateAsync(id, r, ct);

    [HttpDelete("articles/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await articles.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("articles/{id:guid}/transition")]
    public Task<ArticleDetailDto> Transition(Guid id, ArticleTransitionRequest r, CancellationToken ct) => articles.TransitionAsync(id, r, ct);

    [HttpPost("articles/from-ticket/{ticketId:guid}")]
    public Task<ArticleDetailDto> FromTicket(Guid ticketId, [FromBody] ArticleFromTicketRequest? r, CancellationToken ct) =>
        authoring.CreateFromTicketAsync(ticketId, r ?? new ArticleFromTicketRequest(), ct);

    // ───── Versions ─────

    [HttpGet("articles/{id:guid}/versions")]
    public Task<List<ArticleVersionDto>> Versions(Guid id, CancellationToken ct) => articles.VersionsAsync(id, ct);

    [HttpGet("articles/{id:guid}/versions/{number:int}")]
    public Task<ArticleVersionContentDto> Version(Guid id, int number, CancellationToken ct) => articles.VersionAsync(id, number, ct);

    [HttpGet("articles/{id:guid}/diff")]
    public Task<ArticleDiffDto> Diff(Guid id, [FromQuery] int from, [FromQuery] int to, CancellationToken ct) => articles.DiffAsync(id, from, to, ct);

    [HttpPost("articles/{id:guid}/versions/{number:int}/restore")]
    public Task<ArticleDetailDto> Restore(Guid id, int number, [FromBody] RestoreVersionRequest? r, CancellationToken ct) =>
        articles.RestoreAsync(id, number, r?.ChangeNote, ct);

    // ───── Translations ─────

    [HttpGet("articles/{id:guid}/translations")]
    public Task<List<ArticleTranslationDto>> Translations(Guid id, CancellationToken ct) => translations.ListAsync(id, ct);

    [HttpPut("articles/{id:guid}/translations/{locale}")]
    public Task<ArticleTranslationDto> SaveTranslation(Guid id, string locale, SaveTranslationRequest r, CancellationToken ct) =>
        translations.SaveAsync(id, locale, r, ct);

    [HttpPost("articles/{id:guid}/translations/{locale}/transition")]
    public Task<ArticleTranslationDto> TransitionTranslation(Guid id, string locale, TranslationTransitionRequest r, CancellationToken ct) =>
        translations.TransitionAsync(id, locale, r.Action, ct);

    [HttpDelete("articles/{id:guid}/translations/{locale}")]
    public async Task<IActionResult> DeleteTranslation(Guid id, string locale, CancellationToken ct)
    {
        await translations.DeleteAsync(id, locale, ct);
        return NoContent();
    }

    // ───── Attachments & images ─────

    [HttpPost("articles/{id:guid}/attachments")]
    [RequestSizeLimit(110 * 1024 * 1024)]
    public async Task<ArticleAttachmentDto> UploadAttachment(Guid id, IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return await authoring.AddAttachmentAsync(id, stream, file.FileName, file.ContentType, ct);
    }

    [HttpDelete("articles/{id:guid}/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> DeleteAttachment(Guid id, Guid attachmentId, CancellationToken ct)
    {
        await authoring.DeleteAttachmentAsync(id, attachmentId, ct);
        return NoContent();
    }

    [HttpGet("articles/{id:guid}/attachments/{attachmentId:guid}/download")]
    public async Task<IActionResult> DownloadAttachment(Guid id, Guid attachmentId, CancellationToken ct)
    {
        var (att, stream) = await authoring.OpenAttachmentAsync(id, attachmentId, ct);
        return FileResults.Download(this, stream, att.FileName, att.ContentType);
    }

    [HttpPost("assets")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<ArticleAttachmentDto> UploadImage(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return await authoring.UploadAssetAsync(stream, file.FileName, file.ContentType, ct);
    }

    // ───── Feedback & analytics ─────

    [HttpGet("feedback")]
    public Task<PageResult<ArticleFeedbackDto>> Feedback([FromQuery] ArticleFeedbackQuery q, CancellationToken ct) => feedback.ListAsync(q, ct);

    [HttpPost("feedback/review")]
    public async Task<IActionResult> Review(ReviewFeedbackRequest r, CancellationToken ct)
    {
        await feedback.MarkReviewedAsync(r.Ids, ct);
        return NoContent();
    }

    [HttpGet("analytics")]
    public Task<KbAnalyticsDto> Analytics([FromQuery] int days = 30, CancellationToken ct = default) => feedback.AnalyticsAsync(days, ct);

    // ───── Import / export ─────

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string format = "json", [FromQuery] string? ids = null, CancellationToken ct = default)
    {
        var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var (content, contentType, fileName) = await transfer.ExportAsync(format, list, ct);
        return File(content, contentType, fileName);
    }

    [HttpPost("import")]
    [RequestSizeLimit(55 * 1024 * 1024)]
    public async Task<KbImportResultDto> Import(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return await transfer.ImportAsync(stream, file.FileName, ct);
    }

    // ───── Guided troubleshooting ─────

    [HttpGet("flows")]
    public Task<List<FlowListItemDto>> Flows(CancellationToken ct) => flows.ListAsync(ct);

    [HttpPost("flows")]
    public Task<FlowDetailDto> CreateFlow(SaveFlowRequest r, CancellationToken ct) => flows.SaveAsync(null, r, ct);

    [HttpPost("flows/validate")]
    public Task<List<FlowValidationIssueDto>> ValidateFlow(FlowDefinitionDto definition, CancellationToken ct) => flows.ValidateDefinitionAsync(definition, ct);

    [HttpGet("flows/{id:guid}")]
    public Task<FlowDetailDto> Flow(Guid id, CancellationToken ct) => flows.GetAsync(id, ct);

    [HttpPut("flows/{id:guid}")]
    public Task<FlowDetailDto> UpdateFlow(Guid id, SaveFlowRequest r, CancellationToken ct) => flows.SaveAsync(id, r, ct);

    [HttpPost("flows/{id:guid}/transition")]
    public Task<FlowDetailDto> TransitionFlow(Guid id, FlowTransitionRequest r, CancellationToken ct) => flows.TransitionAsync(id, r.Action, ct);

    [HttpDelete("flows/{id:guid}")]
    public async Task<IActionResult> DeleteFlow(Guid id, CancellationToken ct)
    {
        await flows.DeleteAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Public article images (unauthenticated, unguessable ids, images only).</summary>
[Route("api/v1/kb-assets")]
[AllowAnonymous]
[EnableRateLimiting(HostingSetup.PublicLimiter)]
public sealed class KbAssetsController(KbAssetService assets) : ControllerBase
{
    [HttpGet("{fileId:guid}")]
    public async Task<IActionResult> Get(Guid fileId, CancellationToken ct)
    {
        var (file, stream) = await assets.OpenAsync(fileId, ct);
        Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(stream, file.ContentType);
    }
}

/// <summary>Community moderation and agent answers.</summary>
[Route("api/v1/community")]
public sealed class CommunityController(CommunityService community) : DeskControllerBase
{
    [HttpGet("topics")]
    public Task<PageResult<TopicListItemDto>> Topics([FromQuery] TopicQuery q, CancellationToken ct) => community.ListAsync(q, null, ct);

    [HttpGet("topics/{id:guid}")]
    public Task<TopicDetailDto> Topic(Guid id, CancellationToken ct) => community.GetAsync(id, null, false, ct);

    [HttpPut("topics/{id:guid}")]
    public Task<TopicDetailDto> UpdateTopic(Guid id, UpdateTopicRequest r, CancellationToken ct) => community.UpdateTopicAsync(id, r, ct);

    [HttpPost("topics/{id:guid}/posts")]
    public Task<TopicDetailDto> Reply(Guid id, CreatePostRequest r, CancellationToken ct) => community.ReplyAsync(id, r, null, ct);

    [HttpPut("posts/{postId:guid}")]
    public Task<PostDto> UpdatePost(Guid postId, UpdatePostRequest r, CancellationToken ct) => community.UpdatePostAsync(postId, r, ct);

    [HttpPost("topics/{id:guid}/moderate")]
    public Task<TopicDetailDto> ModerateTopic(Guid id, ModerateRequest r, CancellationToken ct) => community.ModerateTopicAsync(id, r, ct);

    [HttpPost("posts/{postId:guid}/moderate")]
    public Task<PostDto> ModeratePost(Guid postId, ModerateRequest r, CancellationToken ct) => community.ModeratePostAsync(postId, r, ct);

    [HttpPost("topics/{id:guid}/accept/{postId:guid}")]
    public Task<TopicDetailDto> Accept(Guid id, Guid postId, CancellationToken ct) => community.AcceptAsync(id, postId, null, ct);

    [HttpPost("topics/{id:guid}/vote")]
    public Task<VoteResultDto> VoteTopic(Guid id, VoteRequest r, CancellationToken ct) => community.VoteAsync(id, null, r.Value, null, ct);

    [HttpPost("posts/{postId:guid}/vote")]
    public Task<VoteResultDto> VotePost(Guid postId, VoteRequest r, CancellationToken ct) => community.VoteAsync(null, postId, r.Value, null, ct);

    [HttpPost("topics/{id:guid}/convert")]
    public Task<ConvertTopicResultDto> Convert(Guid id, CancellationToken ct) => community.ConvertToTicketAsync(id, ct);
}

/// <summary>Global search across tickets, articles, contacts, customers and community topics (permission-aware).</summary>
[Route("api/v1/search")]
public sealed class SearchController(GlobalSearchService search, SearchBackfill backfill, RequestContext ctx) : DeskControllerBase
{
    [HttpGet]
    public Task<GlobalSearchResultDto> Search([FromQuery] string? q, [FromQuery] string? types, [FromQuery] int limit = 20, CancellationToken ct = default) =>
        search.SearchAsync(q, types, limit, ct);

    [HttpGet("status")]
    public Task<SearchIndexStatusDto> Status(CancellationToken ct) => search.StatusAsync(ct);

    /// <summary>Rebuilds the tenant's index (re-tokenises every record).</summary>
    [HttpPost("reindex")]
    public async Task<IActionResult> Reindex(CancellationToken ct)
    {
        ctx.RequireAny(Permissions.AdminKnowledge, Permissions.AdminSecurity);
        var count = await backfill.RebuildAsync(ct);
        return Ok(new { indexed = count });
    }
}

/// <summary>File responses for downloads: attachment disposition unless an inline-safe type, plus a sandbox CSP.</summary>
public static class FileResults
{
    public static IActionResult Download(ControllerBase controller, Stream stream, string fileName, string contentType, bool inline = false)
    {
        controller.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        var safeInline = inline && FileService.IsInlineSafe(contentType);
        var result = controller.File(stream, safeInline ? contentType : "application/octet-stream", safeInline ? null : fileName, enableRangeProcessing: true);
        if (safeInline) controller.Response.Headers.ContentDisposition = $"inline; filename=\"{fileName.Replace("\"", "")}\"";
        return result;
    }
}
