using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Contracts.Automation;
using SupportDesk.Domain;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Automation;

/// <summary>
/// Anonymous inbound webhook trigger: POST /api/v1/automation/webhooks/{key} with header X-Webhook-Secret. The rule is
/// found by its key across tenants (the tenant comes from the rule record, never from the caller), the secret is compared
/// by SHA-256 hash in constant time, and the payload must reference a ticket ("ticketId", "ticketNumber" or
/// "ticket": {id|number}). Payload fields are available to conditions and templates as "webhook.&lt;field&gt;".
/// An Idempotency-Key header makes retries of the same call run the rule at most once.
/// </summary>
public sealed class InboundWebhookService(DeskDbContext db, RequestContext ctx, RuleEngine engine)
{
    public async Task<InboundWebhookResultDto> HandleAsync(string webhookKey, string? secret, string? idempotencyKey, JsonElement payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(webhookKey) || webhookKey.Length > 100) throw new NotFoundException("Webhook");
        var rule = await db.AllTenants<AutomationRule>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.WebhookKey == webhookKey && r.Trigger == RuleTriggers.Webhook, ct) ?? throw new NotFoundException("Webhook");
        if (string.IsNullOrEmpty(secret) || rule.WebhookSecretHash is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(TextNormalization.Sha256Hex(secret)), Encoding.ASCII.GetBytes(rule.WebhookSecretHash)))
            throw new DomainException(Contracts.Common.ErrorCodes.Unauthorized, "Missing or invalid webhook secret.", 401);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == rule.TenantId, ct);
        if (tenant is null || tenant.Status is not (TenantStatus.Active or TenantStatus.Trial)) throw new NotFoundException("Webhook");

        ctx.SetSystem(rule.TenantId, $"Webhook: {rule.Name}", ActorType.Integration);
        if (!rule.IsActive || rule.PublishedVersionId is null)
            throw new DomainException("rule_inactive", "The rule behind this webhook is not active.", 409);
        if (payload.ValueKind != JsonValueKind.Object)
            throw new Tickets.ValidationException(new Dictionary<string, string> { ["body"] = "Send a JSON object." });

        var ticket = await FindTicketAsync(payload, ct)
                     ?? throw new Tickets.ValidationException(new Dictionary<string, string> { ["ticket"] = "Reference an existing ticket with \"ticketId\" or \"ticketNumber\"." });
        var eventId = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid() : DeterministicGuid.From("automation-webhook", rule.Id, idempotencyKey.Trim());
        var values = TemplateValues.Flatten(payload, "webhook");
        var execution = await engine.RunWebhookAsync(rule, ticket.Id, eventId, values, ct);
        var status = execution?.Status.ToString() ?? "Skipped";
        var message = execution?.Status switch
        {
            RuleExecutionStatus.NotMatched => "The rule's conditions did not match the ticket.",
            RuleExecutionStatus.SkippedLoop => execution.Error,
            RuleExecutionStatus.Failed or RuleExecutionStatus.PartiallyFailed => execution.Error,
            null => "The rule could not run for this ticket.",
            _ => null
        };
        return new InboundWebhookResultDto(rule.Id, ticket.Id, ticket.Number, status, execution?.Id, message);
    }

    private async Task<(Guid Id, long Number)?> FindTicketAsync(JsonElement payload, CancellationToken ct)
    {
        Guid? id = null;
        long? number = null;
        void Read(JsonElement obj, string idKey, string numberKey)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (p.Name.Equals(idKey, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(p.Value.ToString(), out var g)) id ??= g;
                if (p.Name.Equals(numberKey, StringComparison.OrdinalIgnoreCase))
                {
                    var raw = p.Value.ToString().TrimStart('#');
                    if (long.TryParse(raw, out var n)) number ??= n;
                }
            }
        }
        Read(payload, "ticketId", "ticketNumber");
        if (payload.TryGetProperty("ticket", out var nested) && nested.ValueKind == JsonValueKind.Object) Read(nested, "id", "number");

        var q = db.Tickets.AsNoTracking().Where(t => t.MergedIntoTicketId == null);
        if (id is { } tid && await q.Where(t => t.Id == tid).Select(t => new { t.Id, t.Number }).FirstOrDefaultAsync(ct) is { } byId) return (byId.Id, byId.Number);
        if (number is { } n && await q.Where(t => t.Number == n).Select(t => new { t.Id, t.Number }).FirstOrDefaultAsync(ct) is { } byNumber)
            return (byNumber.Id, byNumber.Number);
        return null;
    }
}
