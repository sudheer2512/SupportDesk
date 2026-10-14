using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportDesk.Api.Controllers.Knowledge;
using SupportDesk.Api.Infrastructure;
using SupportDesk.Application.Knowledge;
using SupportDesk.Application.Portal;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Contracts.Portal;
using SupportDesk.Domain;

namespace SupportDesk.Api.Controllers.Portal;

/// <summary>
/// Public portal content (anonymous or signed-in contact): home, knowledge base, search, deflection suggestions,
/// guided troubleshooting, community reading, support contacts, assistant and anonymous submissions. The brand slug is
/// resolved server-side; a contact token only works for brands of its own tenant.
/// </summary>
[ApiController]
[Route("api/v1/portal/{brand}")]
[AllowAnonymous]
[EnableRateLimiting(HostingSetup.PublicLimiter)]
[Produces("application/json")]
public sealed class PortalContentController(
    PortalContext portal,
    PortalKnowledgeService knowledge,
    PortalTicketService tickets,
    CommunityService community,
    PortalSubmissionService submissions,
    PortalAssistantService assistant) : ControllerBase
{
    [HttpGet("home")]
    public async Task<PortalHomeDto> Home(string brand, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await knowledge.HomeAsync(tickets, ct);
    }

    // ───── Knowledge base ─────

    [HttpGet("kb/categories")]
    public async Task<List<PortalCategoryDto>> Categories(string brand, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await knowledge.CategoriesAsync(ct);
    }

    [HttpGet("kb/categories/{slug}")]
    public async Task<PortalCategoryPageDto> Category(string brand, string slug, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await knowledge.CategoryAsync(slug, ct);
    }

    [HttpGet("kb/articles")]
    public async Task<List<PortalArticleSummaryDto>> Articles(string brand, [FromQuery] string? category, [FromQuery] string? tag, [FromQuery] string? sort,
        [FromQuery] int? limit, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await knowledge.ArticlesAsync(category, tag, sort, limit, ct);
    }

    [HttpGet("kb/articles/{slugOrId}")]
    public async Task<PortalArticleDto> Article(string brand, string slugOrId, [FromQuery] string? locale, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await knowledge.ArticleAsync(slugOrId, locale, ct);
    }

    [HttpPost("kb/articles/{id:guid}/feedback")]
    public async Task<IActionResult> Feedback(string brand, Guid id, SubmitArticleFeedbackRequest r, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        await knowledge.SubmitFeedbackAsync(id, r, ct);
        return NoContent();
    }

    [HttpGet("kb/articles/{id:guid}/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> ArticleAttachment(string brand, Guid id, Guid attachmentId, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        var (att, stream) = await knowledge.OpenAttachmentAsync(id, attachmentId, ct);
        return FileResults.Download(this, stream, att.FileName, att.ContentType);
    }

    [HttpGet("search")]
    public async Task<PortalSearchResultDto> Search(string brand, [FromQuery] string? q, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await knowledge.SearchAsync(q, ct);
    }

    [HttpGet("kb/suggest")]
    public async Task<List<PortalArticleSummaryDto>> Suggest(string brand, [FromQuery] string? q, [FromQuery] int limit = 5, CancellationToken ct = default)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await knowledge.SuggestAsync(q, limit, ct);
    }

    // ───── Guided troubleshooting ─────

    [HttpGet("flows")]
    public async Task<List<PortalFlowSummaryDto>> Flows(string brand, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await knowledge.FlowsAsync(ct);
    }

    [HttpGet("flows/{id:guid}")]
    public async Task<PortalFlowDto> Flow(string brand, Guid id, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await knowledge.FlowAsync(id, ct);
    }

    [HttpPost("flows/{id:guid}/usage")]
    public async Task<IActionResult> FlowUsage(string brand, Guid id, FlowUsageRequest r, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        await knowledge.RecordFlowUsageAsync(id, r, ct);
        return NoContent();
    }

    // ───── Community (reading) ─────

    [HttpGet("community/topics")]
    public async Task<PageResult<TopicListItemDto>> Topics(string brand, [FromQuery] TopicQuery q, CancellationToken ct)
    {
        var b = await portal.ResolveAsync(brand, ct: ct);
        RequireCommunity();
        return await community.ListAsync(q, b.Id, ct);
    }

    [HttpGet("community/topics/{id:guid}")]
    public async Task<TopicDetailDto> Topic(string brand, Guid id, CancellationToken ct)
    {
        var b = await portal.ResolveAsync(brand, ct: ct);
        RequireCommunity();
        return await community.GetAsync(id, b.Id, countView: true, ct);
    }

    [HttpGet("community/tags")]
    public async Task<List<string>> Tags(string brand, CancellationToken ct)
    {
        var b = await portal.ResolveAsync(brand, ct: ct);
        RequireCommunity();
        return await community.TagsAsync(b.Id, ct);
    }

    private void RequireCommunity()
    {
        if (!portal.Brand.CommunityEnabled) throw new NotFoundException("Community");
    }

    // ───── Support, assistant, anonymous submissions ─────

    [HttpGet("support")]
    public async Task<PortalSupportInfoDto> Support(string brand, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await knowledge.SupportAsync(ct);
    }

    [HttpPost("assistant")]
    [EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<PortalAssistantReplyDto> Assistant(string brand, PortalAssistantRequest r, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await assistant.AskAsync(r, ct);
    }

    /// <summary>Anonymous ticket from the portal (brands that allow it).</summary>
    [HttpPost("submit")]
    [EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<PortalSubmissionResultDto> Submit(string brand, PortalAnonymousTicketRequest r, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        return await submissions.SubmitAsync(r, HttpContext.Connection.RemoteIpAddress?.ToString(), Channel.Portal, ct);
    }

    /// <summary>Embeddable web form (JSON or classic form post) for customer websites.</summary>
    [HttpPost("webform")]
    [EnableRateLimiting(HostingSetup.AuthLimiter)]
    [Consumes("application/json", "application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<PortalSubmissionResultDto> WebForm(string brand, CancellationToken ct)
    {
        await portal.ResolveAsync(brand, ct: ct);
        PortalAnonymousTicketRequest r;
        if (Request.HasFormContentType)
        {
            var f = await Request.ReadFormAsync(ct);
            r = new PortalAnonymousTicketRequest
            {
                Name = f["name"].ToString(), Email = f["email"].ToString(), Phone = f["phone"].ToString(), Subject = f["subject"].ToString(),
                Description = f["description"].ToString(), Website = f["website"].ToString(),
                FormStartedAt = long.TryParse(f["formStartedAt"].ToString(), out var started) ? started : null
            };
        }
        else
        {
            r = await Request.ReadFromJsonAsync<PortalAnonymousTicketRequest>(Application.Common.Json.Options, ct) ?? new PortalAnonymousTicketRequest();
        }
        return await submissions.SubmitAsync(r, HttpContext.Connection.RemoteIpAddress?.ToString(), Channel.WebForm, ct);
    }
}

/// <summary>Signed-in contact endpoints: tickets (scope-enforced), uploads, community posting and account.</summary>
[ApiController]
[Route("api/v1/portal/{brand}")]
[Authorize(Policy = Policies.Contact)]
[EnableRateLimiting(HostingSetup.ApiLimiter)]
[Produces("application/json")]
public sealed class PortalTicketsController(
    PortalContext portal,
    PortalTicketService tickets,
    PortalAccountService account,
    CommunityService community) : ControllerBase
{
    private Task Me(string brand, CancellationToken ct) => portal.ResolveAsync(brand, requireContact: true, ct);

    // ───── Tickets ─────

    [HttpGet("tickets/scope")]
    public async Task<PortalTicketScopeDto> Scope(string brand, CancellationToken ct)
    {
        await Me(brand, ct);
        return tickets.Scope();
    }

    [HttpGet("tickets/stats")]
    public async Task<PortalTicketStatsDto> Stats(string brand, CancellationToken ct)
    {
        await Me(brand, ct);
        return await tickets.StatsAsync(ct);
    }

    [HttpGet("tickets")]
    public async Task<PagedResult<PortalTicketListItemDto>> List(string brand, [FromQuery] PortalTicketQuery q, CancellationToken ct)
    {
        await Me(brand, ct);
        return await tickets.ListAsync(q, ct);
    }

    [HttpGet("tickets/form")]
    public async Task<PortalTicketFormDto> Form(string brand, CancellationToken ct)
    {
        await Me(brand, ct);
        return await tickets.FormAsync(ct);
    }

    [HttpPost("tickets")]
    public async Task<PortalTicketCreatedDto> Create(string brand, PortalCreateTicketRequest r, CancellationToken ct)
    {
        await Me(brand, ct);
        return await tickets.CreateAsync(r, ct);
    }

    [HttpGet("tickets/{id:guid}")]
    public async Task<PortalTicketDetailDto> Get(string brand, Guid id, CancellationToken ct)
    {
        await Me(brand, ct);
        return await tickets.GetAsync(id, ct);
    }

    [HttpGet("tickets/{id:guid}/messages")]
    public async Task<List<PortalMessageDto>> Messages(string brand, Guid id, CancellationToken ct)
    {
        await Me(brand, ct);
        return await tickets.MessagesAsync(id, ct);
    }

    [HttpPost("tickets/{id:guid}/replies")]
    public async Task<PortalMessageDto> Reply(string brand, Guid id, PortalReplyRequest r, CancellationToken ct)
    {
        await Me(brand, ct);
        return await tickets.ReplyAsync(id, r, ct);
    }

    [HttpPost("tickets/{id:guid}/reopen")]
    public async Task<PortalTicketDetailDto> Reopen(string brand, Guid id, PortalReopenRequest r, CancellationToken ct)
    {
        await Me(brand, ct);
        return await tickets.ReopenAsync(id, r.Reason, ct);
    }

    [HttpPost("tickets/{id:guid}/confirm")]
    public async Task<PortalTicketDetailDto> Confirm(string brand, Guid id, PortalConfirmRequest r, CancellationToken ct)
    {
        await Me(brand, ct);
        return await tickets.ConfirmAsync(id, r, ct);
    }

    [HttpPost("tickets/{id:guid}/satisfaction")]
    public async Task<PortalTicketDetailDto> Rate(string brand, Guid id, PortalSatisfactionRequest r, CancellationToken ct)
    {
        await Me(brand, ct);
        return await tickets.RateAsync(id, r, ct);
    }

    [HttpGet("tickets/{id:guid}/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> Attachment(string brand, Guid id, Guid attachmentId, CancellationToken ct)
    {
        await Me(brand, ct);
        var (att, stream) = await tickets.OpenAttachmentAsync(id, attachmentId, ct);
        return FileResults.Download(this, stream, att.FileName, att.ContentType);
    }

    [HttpPost("files")]
    [RequestSizeLimit(110 * 1024 * 1024)]
    public async Task<UploadedFileDto> Upload(string brand, IFormFile file, CancellationToken ct)
    {
        await Me(brand, ct);
        await using var stream = file.OpenReadStream();
        return await tickets.UploadAsync(stream, file.FileName, file.ContentType, ct);
    }

    // ───── Community (posting) ─────

    [HttpPost("community/topics")]
    public async Task<TopicDetailDto> CreateTopic(string brand, CreateTopicRequest r, CancellationToken ct)
    {
        var b = await portal.ResolveAsync(brand, requireContact: true, ct);
        if (!b.CommunityEnabled) throw new NotFoundException("Community");
        return await community.CreateTopicAsync(r, b.Id, ct);
    }

    [HttpPost("community/topics/{id:guid}/posts")]
    public async Task<TopicDetailDto> Reply(string brand, Guid id, CreatePostRequest r, CancellationToken ct)
    {
        var b = await portal.ResolveAsync(brand, requireContact: true, ct);
        if (!b.CommunityEnabled) throw new NotFoundException("Community");
        return await community.ReplyAsync(id, r, b.Id, ct);
    }

    [HttpPost("community/topics/{id:guid}/vote")]
    public async Task<VoteResultDto> VoteTopic(string brand, Guid id, VoteRequest r, CancellationToken ct)
    {
        var b = await portal.ResolveAsync(brand, requireContact: true, ct);
        if (!b.CommunityEnabled) throw new NotFoundException("Community");
        return await community.VoteAsync(id, null, r.Value, b.Id, ct);
    }

    [HttpPost("community/posts/{postId:guid}/vote")]
    public async Task<VoteResultDto> VotePost(string brand, Guid postId, VoteRequest r, CancellationToken ct)
    {
        var b = await portal.ResolveAsync(brand, requireContact: true, ct);
        if (!b.CommunityEnabled) throw new NotFoundException("Community");
        return await community.VoteAsync(null, postId, r.Value, b.Id, ct);
    }

    [HttpPost("community/topics/{id:guid}/accept/{postId:guid}")]
    public async Task<TopicDetailDto> Accept(string brand, Guid id, Guid postId, CancellationToken ct)
    {
        var b = await portal.ResolveAsync(brand, requireContact: true, ct);
        if (!b.CommunityEnabled) throw new NotFoundException("Community");
        return await community.AcceptAsync(id, postId, b.Id, ct);
    }

    // ───── Account ─────

    [HttpGet("account/profile")]
    public async Task<PortalProfileDto> Profile(string brand, CancellationToken ct)
    {
        await Me(brand, ct);
        return await account.ProfileAsync(ct);
    }

    [HttpPut("account/profile")]
    public async Task<PortalProfileDto> UpdateProfile(string brand, UpdatePortalProfileRequest r, CancellationToken ct)
    {
        await Me(brand, ct);
        return await account.UpdateProfileAsync(r, ct);
    }

    [HttpGet("account/preferences")]
    public async Task<PortalPreferencesDto> Preferences(string brand, CancellationToken ct)
    {
        await Me(brand, ct);
        return await account.PreferencesAsync(ct);
    }

    [HttpPut("account/preferences")]
    public async Task<PortalPreferencesDto> UpdatePreferences(string brand, UpdatePortalPreferencesRequest r, CancellationToken ct)
    {
        await Me(brand, ct);
        return await account.UpdatePreferencesAsync(r, ct);
    }

    [HttpPost("account/password")]
    [EnableRateLimiting(HostingSetup.AuthLimiter)]
    public async Task<IActionResult> ChangePassword(string brand, ChangePortalPasswordRequest r, CancellationToken ct)
    {
        await Me(brand, ct);
        await account.ChangePasswordAsync(r, ct);
        return NoContent();
    }

    [HttpGet("account/overview")]
    public async Task<PortalAccountOverviewDto> Overview(string brand, CancellationToken ct)
    {
        await Me(brand, ct);
        return await account.OverviewAsync(ct);
    }
}
