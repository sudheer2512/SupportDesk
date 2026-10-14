using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Ai;
using SupportDesk.Application.Ai.Agents;
using SupportDesk.Application.Ai.Analytics;
using SupportDesk.Application.Ai.Anomalies;
using SupportDesk.Application.Ai.Features;
using SupportDesk.Contracts.Ai;
using SupportDesk.Domain;
using SupportDesk.Domain.Ai;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Api.Controllers.Ai;

/// <summary>Agent-facing AI assistance: classification, summaries, replies, search, insights, analytics, proposals, anomalies.</summary>
[Route("api/v1/ai")]
public sealed class AiController(
    AiClassificationService classification,
    AiSummaryService summaries,
    AiReplyService replies,
    AiSentimentService sentiment,
    AiSearchService search,
    AiInsightService insights,
    NlAnalyticsService analytics,
    AiProposalService proposals,
    AiAgentRunner agents,
    AnomalyDetectionService anomalies,
    AiGateway gateway,
    RequestContext ctx) : DeskControllerBase
{
    // ───────────── Ticket assistance ─────────────

    [HttpPost("tickets/{id:guid}/classify")]
    public Task<ClassificationSuggestionDto> Classify(Guid id, CancellationToken ct) => classification.SuggestAsync(id, ct);

    [HttpPost("tickets/{id:guid}/classify/apply")]
    public Task<ClassificationApplyResultDto> ApplyClassification(Guid id, ApplyClassificationRequest request, CancellationToken ct) =>
        classification.ApplyAsync(id, request, ct);

    [HttpPost("tickets/{id:guid}/summarize")]
    public Task<TicketSummaryDto> Summarize(Guid id, CancellationToken ct) => summaries.SummarizeAsync(id, interactive: true, ct);

    [HttpPost("tickets/{id:guid}/suggest-reply")]
    public Task<SuggestedReplyDto> SuggestReply(Guid id, [FromBody] SuggestReplyRequest? request, CancellationToken ct) =>
        replies.SuggestAsync(id, request ?? new SuggestReplyRequest(), ct);

    [HttpGet("tickets/{id:guid}/sentiment")]
    public Task<SentimentDto> Sentiment(Guid id, CancellationToken ct) => sentiment.GetAsync(id, ct);

    [HttpPost("tickets/{id:guid}/sentiment")]
    public async Task<SentimentDto> AnalyzeSentiment(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.TicketsView);
        await sentiment.AnalyzeTicketAsync(id, interactive: true, ct);
        return await sentiment.GetAsync(id, ct);
    }

    [HttpGet("tickets/{id:guid}/similar")]
    public Task<List<SimilarTicketDto>> Similar(Guid id, CancellationToken ct) => search.SimilarAsync(id, ct);

    [HttpGet("tickets/{id:guid}/duplicates")]
    public Task<DuplicateCheckDto> Duplicates(Guid id, CancellationToken ct) => search.DuplicatesAsync(id, interactive: true, ct);

    [HttpPost("tickets/{id:guid}/duplicates/merge")]
    public async Task<object> MergeDuplicate(Guid id, MergeDuplicateRequest request, CancellationToken ct) =>
        new { survivingTicketId = await search.MergeDuplicateAsync(id, request, ct) };

    [HttpPost("tickets/{id:guid}/root-cause")]
    public Task<RootCauseDto> RootCause(Guid id, CancellationToken ct) => insights.RootCauseAsync(id, ct);

    [HttpGet("tickets/{id:guid}/resolution-prediction")]
    public Task<ResolutionPredictionDto> Prediction(Guid id, CancellationToken ct) => insights.PredictAsync(id, interactive: true, ct);

    [HttpPost("tickets/{id:guid}/knowledge-draft")]
    public Task<KnowledgeDraftDto> KnowledgeDraft(Guid id, CancellationToken ct) => insights.DraftArticleAsync(id, ct);

    [HttpGet("tickets/{id:guid}/proposals")]
    public Task<List<AiProposalDto>> TicketProposals(Guid id, [FromQuery] string? status, CancellationToken ct) => proposals.ListAsync(id, status, 100, ct);

    /// <summary>Runs the enabled AI agents on the ticket now (proposals only — manual runs never send automatic replies).</summary>
    [HttpPost("tickets/{id:guid}/agents/run")]
    public Task<AgentRunResultDto> RunAgents(Guid id, CancellationToken ct) => agents.RunManualAsync(id, ct);

    // ───────────── Search, translation, analytics ─────────────

    [HttpPost("search")]
    public Task<SemanticSearchResultDto> Search(SemanticSearchRequest request, CancellationToken ct) => search.SearchAsync(request, ct);

    [HttpPost("translate")]
    public Task<TranslationDto> Translate(TranslateRequest request, CancellationToken ct) => insights.TranslateAsync(request, ct);

    [HttpPost("analytics/ask")]
    public Task<AnalyticsAnswerDto> Ask(AskAnalyticsRequest request, CancellationToken ct) => analytics.AskAsync(request, ct);

    /// <summary>Records whether a suggestion was used (feeds the acceptance statistics in the AI dashboard).</summary>
    [HttpPost("executions/{id:guid}/feedback")]
    public async Task<IActionResult> Feedback(Guid id, ExecutionFeedbackRequest request, CancellationToken ct)
    {
        ctx.Require(Permissions.AiUse);
        await gateway.MarkOutcomeAsync(id, request.Accepted ? AiOutcome.Accepted : AiOutcome.Rejected, ct);
        return NoContent();
    }

    // ───────────── Proposals ─────────────

    [HttpGet("proposals")]
    public Task<List<AiProposalDto>> Proposals([FromQuery] Guid? ticketId, [FromQuery] string? status, [FromQuery] int limit = 100, CancellationToken ct = default) =>
        proposals.ListAsync(ticketId, status, limit, ct);

    [HttpPost("proposals/{id:guid}/approve")]
    public Task<AiProposalDto> Approve(Guid id, [FromBody] DecideProposalRequest? request, CancellationToken ct) =>
        proposals.ApproveAsync(id, request ?? new DecideProposalRequest(), ct);

    [HttpPost("proposals/{id:guid}/reject")]
    public Task<AiProposalDto> Reject(Guid id, [FromBody] DecideProposalRequest? request, CancellationToken ct) =>
        proposals.RejectAsync(id, request ?? new DecideProposalRequest(), ct);

    [HttpPost("proposals/{id:guid}/revert")]
    public Task<AiProposalDto> Revert(Guid id, [FromBody] DecideProposalRequest? request, CancellationToken ct) =>
        proposals.RevertAsync(id, request ?? new DecideProposalRequest(), ct);

    // ───────────── Anomalies ─────────────

    [HttpGet("anomalies")]
    public Task<List<AnomalyAlertDto>> Anomalies([FromQuery] string? status, [FromQuery] int days = 30, CancellationToken ct = default) =>
        anomalies.ListAsync(status, days, ct);

    [HttpPost("anomalies/{id:guid}/acknowledge")]
    public Task<AnomalyAlertDto> Acknowledge(Guid id, CancellationToken ct) => anomalies.SetStatusAsync(id, AnomalyStatus.Acknowledged, ct);

    [HttpPost("anomalies/{id:guid}/dismiss")]
    public Task<AnomalyAlertDto> Dismiss(Guid id, CancellationToken ct) => anomalies.SetStatusAsync(id, AnomalyStatus.Dismissed, ct);
}
