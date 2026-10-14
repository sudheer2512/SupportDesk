using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Events;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Community forums shared by the portal (contacts) and the desk (agents): topics (questions / discussions), posts,
/// one vote per voter per item, accepted answers, pinning and moderation (hide / close / edit, convert a topic into a
/// ticket — community.moderate). Hidden topics/posts are only visible to moderators.
/// </summary>
public sealed class CommunityService(
    DeskDbContext db,
    RequestContext ctx,
    HtmlSanitization html,
    IEventPublisher events,
    IAuditWriter audit,
    TicketService tickets)
{
    private bool IsModerator => ctx.IsAgent && ctx.Has(Permissions.CommunityModerate);
    private string? VoterKey => ctx.IsContact ? $"contact:{ctx.ContactId:N}" : ctx.IsAgent ? $"user:{ctx.UserId:N}" : null;

    // ───────────────────────────── Queries ─────────────────────────────

    public async Task<PageResult<TopicListItemDto>> ListAsync(TopicQuery q, Guid? brandId, CancellationToken ct = default)
    {
        var query = Scope(db.CommunityTopics.AsNoTracking(), brandId);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            query = query.Where(t => t.Title.Contains(term) || t.BodyHtml.Contains(term) || t.TagsJson.Contains(term));
        }
        if (KbRules.NormalizeTags([q.Tag ?? ""]).FirstOrDefault() is { } tag) query = query.Where(t => t.TagsJson.Contains("\"" + tag + "\""));
        var status = (q.Status ?? "").Trim().ToLowerInvariant();
        query = status switch
        {
            "open" => query.Where(t => t.Status == TopicStatus.Open),
            "answered" => query.Where(t => t.Status == TopicStatus.Answered),
            "closed" => query.Where(t => t.Status == TopicStatus.Closed),
            "hidden" when IsModerator => query.Where(t => t.Status == TopicStatus.Hidden),
            "unanswered" => query.Where(t => t.AcceptedPostId == null && t.Status == TopicStatus.Open),
            _ => query
        };
        var sort = (q.Sort ?? "activity").ToLowerInvariant();
        if (sort == "unanswered") query = query.Where(t => t.PostCount == 0 && t.Status == TopicStatus.Open);
        var total = await query.CountAsync(ct);
        query = sort switch
        {
            "newest" => query.OrderByDescending(t => t.CreatedAt),
            "votes" => query.OrderByDescending(t => t.VoteScore).ThenByDescending(t => t.LastActivityAt),
            "unanswered" => query.OrderByDescending(t => t.CreatedAt),
            _ => query.OrderByDescending(t => t.IsPinned).ThenByDescending(t => t.LastActivityAt)
        };
        var page = Math.Max(1, q.Page ?? 1);
        var size = Math.Clamp(q.PageSize ?? 20, 1, 100);
        var rows = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PageResult<TopicListItemDto>(rows.Select(MapListItem).ToList(), page, size, total);
    }

    public async Task<List<string>> TagsAsync(Guid? brandId, CancellationToken ct = default) =>
        (await Scope(db.CommunityTopics.AsNoTracking(), brandId).OrderByDescending(t => t.LastActivityAt).Select(t => t.TagsJson).Take(1000).ToListAsync(ct))
        .SelectMany(Json.StringList).GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(40).ToList();

    public async Task<TopicDetailDto> GetAsync(Guid id, Guid? brandId, bool countView, CancellationToken ct = default)
    {
        var topic = await Scope(db.CommunityTopics.AsNoTracking(), brandId).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Topic", id);
        if (countView)
        {
            await db.CommunityTopics.Where(t => t.Id == id).ExecuteUpdateAsync(u => u.SetProperty(t => t.ViewCount, t => t.ViewCount + 1), ct);
            topic.ViewCount++;
        }
        return await MapDetailAsync(topic, ct);
    }

    /// <summary>Portal readers never see hidden topics; brand portals show their own topics plus brand-less ones.</summary>
    private IQueryable<CommunityTopic> Scope(IQueryable<CommunityTopic> q, Guid? brandId)
    {
        if (!IsModerator) q = q.Where(t => t.Status != TopicStatus.Hidden);
        if (brandId is { } b) q = q.Where(t => t.BrandId == null || t.BrandId == b);
        return q;
    }

    // ───────────────────────────── Authoring ─────────────────────────────

    public async Task<TopicDetailDto> CreateTopicAsync(CreateTopicRequest r, Guid? brandId, CancellationToken ct = default)
    {
        RequireParticipant();
        var errors = new Dictionary<string, string>();
        var title = (r.Title ?? "").Trim();
        if (title.Length < 5 || title.Length > 200) errors["title"] = "Use a title of 5 to 200 characters.";
        var body = html.Sanitize(r.BodyHtml);
        if (TextNormalization.HtmlToText(body).Length < 10) errors["bodyHtml"] = "Describe your question in a little more detail.";
        if (body.Length > 100_000) errors["bodyHtml"] = "The post is too long.";
        if (errors.Count > 0) throw new ValidationException(errors);
        var now = db.Clock.UtcNow;
        var topic = new CommunityTopic
        {
            BrandId = brandId, Title = title, BodyHtml = body, IsQuestion = r.IsQuestion, TagsJson = Json.Serialize(KbRules.NormalizeTags(r.Tags).Take(5)),
            LastActivityAt = now, AuthorContactId = ctx.ContactId, AuthorUserId = ctx.IsAgent ? ctx.UserId : null, AuthorName = ctx.ActorName ?? "Member"
        };
        db.CommunityTopics.Add(topic);
        events.Publish(KnowledgeEvents.TopicChanged, "Topic", topic.Id, new EntityEventPayload(topic.Id, topic.Title));
        await db.SaveChangesAsync(ct);
        return await MapDetailAsync(topic, ct);
    }

    public async Task<TopicDetailDto> ReplyAsync(Guid topicId, CreatePostRequest r, Guid? brandId, CancellationToken ct = default)
    {
        RequireParticipant();
        if (ctx.IsAgent) ctx.RequireAny(Permissions.TicketsReply, Permissions.CommunityModerate);
        var topic = await Scope(db.CommunityTopics, brandId).FirstOrDefaultAsync(t => t.Id == topicId, ct) ?? throw new NotFoundException("Topic", topicId);
        if (topic.Status is TopicStatus.Closed or TopicStatus.Hidden) throw new DomainException("topic_closed", "This topic is closed for replies.");
        var body = html.Sanitize(r.BodyHtml);
        if (TextNormalization.HtmlToText(body).Length < 2 && !body.Contains("<img", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException(new Dictionary<string, string> { ["bodyHtml"] = "Write a reply first." });
        if (body.Length > 100_000) throw new ValidationException(new Dictionary<string, string> { ["bodyHtml"] = "The reply is too long." });
        var post = new CommunityPost
        {
            TopicId = topic.Id, BodyHtml = body, AuthorContactId = ctx.ContactId, AuthorUserId = ctx.IsAgent ? ctx.UserId : null,
            AuthorName = ctx.ActorName ?? "Member", IsAgentResponse = ctx.IsAgent
        };
        db.CommunityPosts.Add(post);
        topic.PostCount++;
        topic.LastActivityAt = db.Clock.UtcNow;
        events.Publish(KnowledgeEvents.TopicChanged, "Topic", topic.Id, new EntityEventPayload(topic.Id, topic.Title));
        await db.SaveChangesAsync(ct);
        return await MapDetailAsync(topic, ct);
    }

    public async Task<VoteResultDto> VoteAsync(Guid? topicId, Guid? postId, int value, Guid? brandId, CancellationToken ct = default)
    {
        RequireParticipant();
        if (value is < -1 or > 1) throw new ValidationException(new Dictionary<string, string> { ["value"] = "Vote 1, -1 or 0." });
        var key = VoterKey!;
        CommunityTopic? topic = null;
        CommunityPost? post = null;
        if (postId is { } pid)
        {
            post = await db.CommunityPosts.FirstOrDefaultAsync(p => p.Id == pid && !p.IsHidden, ct) ?? throw new NotFoundException("Post", pid);
            var postTopicId = post.TopicId;
            if (!await Scope(db.CommunityTopics.AsNoTracking(), brandId).AnyAsync(t => t.Id == postTopicId, ct)) throw new NotFoundException("Post", pid);
            if (IsAuthor(post.AuthorContactId, post.AuthorUserId)) throw new DomainException("own_content", "You cannot vote on your own post.");
        }
        else if (topicId is { } tid)
        {
            topic = await Scope(db.CommunityTopics, brandId).FirstOrDefaultAsync(t => t.Id == tid, ct) ?? throw new NotFoundException("Topic", tid);
            if (IsAuthor(topic.AuthorContactId, topic.AuthorUserId)) throw new DomainException("own_content", "You cannot vote on your own topic.");
        }
        else throw new ValidationException(new Dictionary<string, string> { ["target"] = "Vote on a topic or a post." });

        Guid? voteTopicId = post is null ? topic!.Id : null;
        Guid? votePostId = post?.Id;
        var existing = await db.CommunityVotes.FirstOrDefaultAsync(v => v.TopicId == voteTopicId && v.PostId == votePostId && v.VoterKey == key, ct);
        if (value == 0) { if (existing is not null) db.CommunityVotes.Remove(existing); }
        else if (existing is null)
            db.CommunityVotes.Add(new CommunityVote { TopicId = voteTopicId, PostId = votePostId, VoterKey = key, Value = value, CreatedAt = db.Clock.UtcNow });
        else existing.Value = value;
        await db.SaveChangesAsync(ct);

        var score = post is not null
            ? await db.CommunityVotes.Where(v => v.PostId == post.Id).SumAsync(v => v.Value, ct)
            : await db.CommunityVotes.Where(v => v.TopicId == topic!.Id && v.PostId == null).SumAsync(v => v.Value, ct);
        if (post is not null) post.VoteScore = score; else topic!.VoteScore = score;
        await db.SaveChangesAsync(ct);
        return new VoteResultDto(score, value);
    }

    public async Task<TopicDetailDto> AcceptAsync(Guid topicId, Guid postId, Guid? brandId, CancellationToken ct = default)
    {
        RequireParticipant();
        var topic = await Scope(db.CommunityTopics, brandId).FirstOrDefaultAsync(t => t.Id == topicId, ct) ?? throw new NotFoundException("Topic", topicId);
        if (!IsModerator && !IsAuthor(topic.AuthorContactId, topic.AuthorUserId)) throw new ForbiddenException("Only the author or a moderator can accept an answer.");
        var post = await db.CommunityPosts.FirstOrDefaultAsync(p => p.Id == postId && p.TopicId == topicId && !p.IsHidden, ct) ?? throw new NotFoundException("Post", postId);
        await db.CommunityPosts.Where(p => p.TopicId == topicId && p.IsAcceptedAnswer && p.Id != postId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.IsAcceptedAnswer, false), ct);
        post.IsAcceptedAnswer = true;
        topic.AcceptedPostId = post.Id;
        if (topic.Status == TopicStatus.Open) topic.Status = TopicStatus.Answered;
        topic.LastActivityAt = db.Clock.UtcNow;
        events.Publish(KnowledgeEvents.TopicChanged, "Topic", topic.Id, new EntityEventPayload(topic.Id, topic.Title));
        await db.SaveChangesAsync(ct);
        return await MapDetailAsync(topic, ct);
    }

    // ───────────────────────────── Moderation ─────────────────────────────

    public async Task<TopicDetailDto> UpdateTopicAsync(Guid id, UpdateTopicRequest r, CancellationToken ct = default)
    {
        var topic = await db.CommunityTopics.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Topic", id);
        if (!IsModerator && !IsAuthor(topic.AuthorContactId, topic.AuthorUserId)) throw new ForbiddenException();
        var title = (r.Title ?? "").Trim();
        if (title.Length < 5 || title.Length > 200) throw new ValidationException(new Dictionary<string, string> { ["title"] = "Use a title of 5 to 200 characters." });
        topic.Title = title;
        topic.BodyHtml = html.Sanitize(r.BodyHtml);
        if (r.Tags is not null) topic.TagsJson = Json.Serialize(KbRules.NormalizeTags(r.Tags).Take(5));
        events.Publish(KnowledgeEvents.TopicChanged, "Topic", topic.Id, new EntityEventPayload(topic.Id, topic.Title));
        await db.SaveChangesAsync(ct);
        return await MapDetailAsync(topic, ct);
    }

    public async Task<PostDto> UpdatePostAsync(Guid postId, UpdatePostRequest r, CancellationToken ct = default)
    {
        var post = await db.CommunityPosts.FirstOrDefaultAsync(p => p.Id == postId, ct) ?? throw new NotFoundException("Post", postId);
        if (!IsModerator && !IsAuthor(post.AuthorContactId, post.AuthorUserId)) throw new ForbiddenException();
        var body = html.Sanitize(r.BodyHtml);
        if (TextNormalization.HtmlToText(body).Length == 0) throw new ValidationException(new Dictionary<string, string> { ["bodyHtml"] = "The post cannot be empty." });
        post.BodyHtml = body;
        events.Publish(KnowledgeEvents.TopicChanged, "Topic", post.TopicId, new EntityEventPayload(post.TopicId));
        await db.SaveChangesAsync(ct);
        return MapPost(post, 0);
    }

    public async Task<TopicDetailDto> ModerateTopicAsync(Guid id, ModerateRequest r, CancellationToken ct = default)
    {
        RequireModerator();
        var topic = await db.CommunityTopics.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Topic", id);
        var reopened = topic.AcceptedPostId is null ? TopicStatus.Open : TopicStatus.Answered;
        switch ((r.Action ?? "").Trim().ToLowerInvariant())
        {
            case "hide": topic.Status = TopicStatus.Hidden; break;
            case "unhide" or "reopen": topic.Status = reopened; break;
            case "close": topic.Status = TopicStatus.Closed; break;
            case "pin": topic.IsPinned = true; break;
            case "unpin": topic.IsPinned = false; break;
            default: throw new ValidationException(new Dictionary<string, string> { ["action"] = "Use hide, unhide, close, reopen, pin or unpin." });
        }
        audit.Write("community.moderated", "Topic", topic.Id, $"Topic '{topic.Title}': {r.Action}", new { r.Action, r.Reason });
        events.Publish(KnowledgeEvents.TopicChanged, "Topic", topic.Id, new EntityEventPayload(topic.Id, topic.Title));
        await db.SaveChangesAsync(ct);
        return await MapDetailAsync(topic, ct);
    }

    public async Task<PostDto> ModeratePostAsync(Guid postId, ModerateRequest r, CancellationToken ct = default)
    {
        RequireModerator();
        var post = await db.CommunityPosts.FirstOrDefaultAsync(p => p.Id == postId, ct) ?? throw new NotFoundException("Post", postId);
        var action = (r.Action ?? "").Trim().ToLowerInvariant();
        if (action is not ("hide" or "unhide")) throw new ValidationException(new Dictionary<string, string> { ["action"] = "Use hide or unhide." });
        var topic = await db.CommunityTopics.FirstAsync(t => t.Id == post.TopicId, ct);
        var hide = action == "hide";
        if (post.IsHidden != hide)
        {
            post.IsHidden = hide;
            topic.PostCount = Math.Max(0, topic.PostCount + (hide ? -1 : 1));
            if (hide && topic.AcceptedPostId == post.Id)
            {
                topic.AcceptedPostId = null;
                post.IsAcceptedAnswer = false;
                if (topic.Status == TopicStatus.Answered) topic.Status = TopicStatus.Open;
            }
        }
        audit.Write("community.moderated", "Post", post.Id, $"Post on '{topic.Title}': {action}", new { action, r.Reason });
        events.Publish(KnowledgeEvents.TopicChanged, "Topic", topic.Id, new EntityEventPayload(topic.Id, topic.Title));
        await db.SaveChangesAsync(ct);
        return MapPost(post, 0);
    }

    public async Task<ConvertTopicResultDto> ConvertToTicketAsync(Guid id, CancellationToken ct = default)
    {
        RequireModerator();
        ctx.Require(Permissions.TicketsCreate);
        var topic = await db.CommunityTopics.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Topic", id);
        if (topic.ConvertedTicketId is { } existing)
        {
            var number = await db.Tickets.Where(t => t.Id == existing).Select(t => t.Number).FirstOrDefaultAsync(ct);
            return new ConvertTopicResultDto(existing, number, "#" + number);
        }
        var ticket = await tickets.CreateAsync(new CreateTicketRequest
        {
            Subject = topic.Title,
            DescriptionHtml = topic.BodyHtml + "<p><em>Converted from a community topic.</em></p>",
            Channel = nameof(Channel.Portal),
            ContactId = topic.AuthorContactId,
            BrandId = topic.BrandId,
            Tags = Json.StringList(topic.TagsJson),
            Origin = new ExternalReferenceInput("community", "topic", topic.Id, null, $"/kb/community/{topic.Id}", null)
        }, TicketCreationSource.Agent, ct);
        topic.ConvertedTicketId = ticket.Id;
        db.CommunityPosts.Add(new CommunityPost
        {
            TopicId = topic.Id, AuthorUserId = ctx.UserId, AuthorName = ctx.ActorName ?? "Support", IsAgentResponse = true,
            BodyHtml = $"<p>We have opened support ticket <strong>#{ticket.Number}</strong> to follow this up directly with you.</p>"
        });
        topic.PostCount++;
        topic.LastActivityAt = db.Clock.UtcNow;
        events.Publish(KnowledgeEvents.TopicChanged, "Topic", topic.Id, new EntityEventPayload(topic.Id, topic.Title));
        await db.SaveChangesAsync(ct);
        return new ConvertTopicResultDto(ticket.Id, ticket.Number, $"#{ticket.Number}");
    }

    // ───────────────────────────── Mapping ─────────────────────────────

    private void RequireParticipant()
    {
        if (!ctx.IsContact && !ctx.IsAgent) throw new ForbiddenException("Sign in to take part in the community.");
    }

    private void RequireModerator()
    {
        ctx.RequireAgent();
        ctx.Require(Permissions.CommunityModerate);
    }

    private bool IsAuthor(Guid? contactId, Guid? userId) =>
        (ctx.IsContact && contactId is not null && contactId == ctx.ContactId) || (ctx.IsAgent && userId is not null && userId == ctx.UserId);

    private static TopicListItemDto MapListItem(CommunityTopic t) =>
        new(t.Id, t.Title, TextNormalization.Truncate(TextNormalization.HtmlToText(t.BodyHtml), 220), t.AuthorName, t.AuthorUserId is not null, t.Status.ToString(),
            t.IsQuestion, t.IsPinned, t.AcceptedPostId is not null, t.VoteScore, t.PostCount, t.ViewCount, Json.StringList(t.TagsJson), t.CreatedAt, t.LastActivityAt,
            t.ConvertedTicketId);

    private PostDto MapPost(CommunityPost p, int myVote) =>
        new(p.Id, p.BodyHtml, p.AuthorName, p.IsAgentResponse, IsAuthor(p.AuthorContactId, p.AuthorUserId), p.IsAcceptedAnswer, p.IsHidden, p.VoteScore, myVote,
            p.CreatedAt, p.UpdatedAt);

    public async Task<TopicDetailDto> MapDetailAsync(CommunityTopic t, CancellationToken ct)
    {
        var posts = await db.CommunityPosts.AsNoTracking().Where(p => p.TopicId == t.Id && (IsModerator || !p.IsHidden))
            .OrderByDescending(p => p.IsAcceptedAnswer).ThenBy(p => p.CreatedAt).ToListAsync(ct);
        var key = VoterKey;
        var postIds = posts.Select(p => p.Id).ToList();
        var votes = key is null ? [] : await db.CommunityVotes.AsNoTracking()
            .Where(v => v.VoterKey == key && ((v.TopicId == t.Id && v.PostId == null) || (v.PostId != null && postIds.Contains(v.PostId.Value))))
            .ToListAsync(ct);
        var myTopicVote = votes.FirstOrDefault(v => v.PostId == null)?.Value ?? 0;
        var mine = IsAuthor(t.AuthorContactId, t.AuthorUserId);
        var open = t.Status is TopicStatus.Open or TopicStatus.Answered;
        var participant = ctx.IsContact || ctx.IsAgent;
        var actions = new TopicActionsDto(
            Reply: participant && open && (!ctx.IsAgent || ctx.Has(Permissions.TicketsReply) || IsModerator),
            Vote: participant,
            Accept: t.IsQuestion && (IsModerator || mine),
            Moderate: IsModerator,
            Edit: IsModerator || mine,
            Convert: IsModerator && ctx.Has(Permissions.TicketsCreate) && t.ConvertedTicketId is null);
        string? ticketNumber = null;
        if (t.ConvertedTicketId is { } tid && ctx.IsAgent)
            ticketNumber = await db.Tickets.AsNoTracking().Where(x => x.Id == tid).Select(x => "#" + x.Number).FirstOrDefaultAsync(ct);
        return new TopicDetailDto(t.Id, t.Title, t.BodyHtml, t.AuthorName, t.AuthorUserId is not null, mine, t.Status.ToString(), t.IsQuestion, t.IsPinned,
            t.AcceptedPostId, t.VoteScore, myTopicVote, t.PostCount, t.ViewCount, Json.StringList(t.TagsJson), t.CreatedAt, t.LastActivityAt,
            posts.Select(p => MapPost(p, votes.FirstOrDefault(v => v.PostId == p.Id)?.Value ?? 0)).ToList(), actions,
            ctx.IsAgent ? t.ConvertedTicketId : null, ticketNumber);
    }
}
