using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportDesk.Application.Common;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Engineering;
using SupportDesk.Domain;
using SupportDesk.Domain.Integration;
using SupportDesk.Domain.Platform;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Integrations.Engineering;

public sealed record EngineeringWebhookRequest(IReadOnlyDictionary<string, string> Headers, byte[] Body);

public sealed record EngineeringWebhookResult(int StatusCode, string Status, string? Detail);

/// <summary>
/// Inbound tracker webhooks (anonymous): POST /api/v1/engineering/hooks/{github|azure-devops}/{trackerId}.
/// The tenant comes from the tracker record, never the caller. GitHub deliveries are verified with
/// X-Hub-Signature-256 (HMAC-SHA256 of the raw body with the tracker's webhook secret); Azure DevOps service hooks with
/// the secret in the X-SupportDesk-Secret header or as the Basic-auth password. Deliveries are de-duplicated by
/// delivery id (X-GitHub-Delivery / the service hook event id), so redeliveries are harmless.
/// </summary>
public sealed class EngineeringWebhookService(
    DeskDbContext db,
    RequestContext ctx,
    EngineeringTrackerResolver resolver,
    EngineeringSyncService sync,
    ILogger<EngineeringWebhookService> logger)
{
    public const string SecretHeader = "X-SupportDesk-Secret";
    public const int MaxBody = 1024 * 1024;

    public async Task<EngineeringWebhookResult> HandleAsync(string provider, Guid trackerId, EngineeringWebhookRequest request, CancellationToken ct)
    {
        var type = provider.ToLowerInvariant() switch
        {
            "github" => IntegrationTypes.GitHub,
            "azure-devops" or "azuredevops" or "ado" or "tfs" => IntegrationTypes.AzureDevOps,
            _ => null
        };
        if (type is null) return new EngineeringWebhookResult(404, "not_found", "Unknown provider.");
        var tracker = await db.AllTenants<Integration>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == trackerId && i.Type == type, ct);
        if (tracker is null) return new EngineeringWebhookResult(404, "not_found", "Unknown tracker.");
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tracker.TenantId, ct);
        if (tenant is null || tenant.Status is not (TenantStatus.Active or TenantStatus.Trial)) return new EngineeringWebhookResult(404, "not_found", "Unknown tracker.");

        ctx.SetSystem(tracker.TenantId, $"{tracker.Name} webhook", ActorType.Integration);
        var secret = resolver.Secrets(tracker).WebhookSecret;
        var ok = type == IntegrationTypes.GitHub ? VerifyGitHub(secret, request) : VerifyAzureDevOps(secret, request);
        if (!ok)
        {
            logger.LogWarning("Rejected {Provider} webhook for tracker {Tracker}: invalid signature/secret", provider, trackerId);
            return new EngineeringWebhookResult(401, ErrorCodes.Unauthorized, "Missing or invalid webhook signature.");
        }

        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(request.Body);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new EngineeringWebhookResult(400, ErrorCodes.Validation, "The body must be JSON.");
        }
        var isGitHub = type == IntegrationTypes.GitHub;
        var eventType = isGitHub ? Header(request, "X-GitHub-Event") ?? "unknown" : payload.Str("eventType") ?? "unknown";
        var deliveryId = (isGitHub ? Header(request, "X-GitHub-Delivery") : payload.Str("id") ?? Header(request, "X-Request-Id"))
                         ?? TextNormalization.Sha256Hex(request.Body);
        deliveryId = TextNormalization.Truncate(deliveryId, 200);

        if (await db.Set<EngineeringWebhookDelivery>().AnyAsync(d => d.IntegrationId == tracker.Id && d.DeliveryId == deliveryId, ct))
            return new EngineeringWebhookResult(200, "duplicate", "Already processed.");
        var delivery = new EngineeringWebhookDelivery
        {
            IntegrationId = tracker.Id, Provider = TrackerProviders.FromIntegrationType(type), DeliveryId = deliveryId, EventType = TextNormalization.Truncate(eventType, 120),
            ReceivedAt = db.Clock.UtcNow, Status = EngineeringDeliveryStatus.Processed
        };
        db.Set<EngineeringWebhookDelivery>().Add(delivery);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent redelivery won the unique (integration, delivery id) race.
            return new EngineeringWebhookResult(200, "duplicate", "Already processed.");
        }

        if (!tracker.IsActive)
        {
            delivery.Status = EngineeringDeliveryStatus.Ignored;
            delivery.Detail = "Tracker is disabled.";
            await db.SaveChangesAsync(ct);
            return new EngineeringWebhookResult(202, "ignored", delivery.Detail);
        }

        var settings = resolver.Settings(tracker);
        try
        {
            var (status, detail, externalId) = isGitHub
                ? await GitHubAsync(tracker, settings, eventType, payload, ct)
                : await AzureDevOpsAsync(tracker, settings, eventType, payload, ct);
            delivery.Status = status;
            delivery.Detail = detail is null ? null : TextNormalization.Truncate(detail, 1000);
            delivery.ExternalId = externalId;
            await sync.SaveWithRetryAsync(ct);
            return new EngineeringWebhookResult(200, status == EngineeringDeliveryStatus.Processed ? "processed" : "ignored", detail);
        }
        catch (Exception ex) when (ex is TrackerException or DomainException or InvalidOperationException or FormatException)
        {
            logger.LogWarning(ex, "Engineering webhook {Delivery} for tracker {Tracker} failed", deliveryId, tracker.Id);
            db.ChangeTracker.Clear();
            // Failed deliveries are removed so the tracker's retry is processed again.
            await db.Set<EngineeringWebhookDelivery>().Where(d => d.Id == delivery.Id).ExecuteDeleteAsync(ct);
            return new EngineeringWebhookResult(500, "failed", ex.Message);
        }
    }

    private static string? Header(EngineeringWebhookRequest r, string name) =>
        r.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v ? v : null;

    /// <summary>X-Hub-Signature-256: "sha256=" + hex HMAC-SHA256(secret, raw body), compared in constant time.</summary>
    public static bool VerifyGitHub(string? secret, EngineeringWebhookRequest r)
    {
        if (string.IsNullOrEmpty(secret) || Header(r, "X-Hub-Signature-256") is not { } sig) return false;
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), r.Body)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig.Trim().ToLowerInvariant()));
    }

    /// <summary>The secret in X-SupportDesk-Secret, or as the password of Basic authentication (service hook "Basic authentication").</summary>
    public static bool VerifyAzureDevOps(string? secret, EngineeringWebhookRequest r)
    {
        if (string.IsNullOrEmpty(secret)) return false;
        string? presented = Header(r, SecretHeader);
        if (presented is null && Header(r, "Authorization") is { } auth && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
                var colon = decoded.IndexOf(':');
                presented = colon >= 0 ? decoded[(colon + 1)..] : decoded;
            }
            catch (FormatException) { return false; }
        }
        if (presented is null) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private Task<EngineeringItem?> ItemAsync(Guid trackerId, string externalId, CancellationToken ct) =>
        db.Set<EngineeringItem>().FirstOrDefaultAsync(i => i.IntegrationId == trackerId && i.ExternalId == externalId, ct);

    private async Task<(EngineeringDeliveryStatus, string?, string?)> GitHubAsync(Integration tracker, TrackerSettings s, string eventType, JsonElement p,
        CancellationToken ct)
    {
        var action = p.Str("action");
        var repo = p.Prop("repository")?.Str("full_name") ?? $"{s.Owner}/{s.Repository}";
        switch (eventType)
        {
            case "ping":
                return (EngineeringDeliveryStatus.Ignored, "Ping received — the webhook is connected.", null);
            case "issues":
            {
                if (p.Prop("issue") is not { } issue) return (EngineeringDeliveryStatus.Ignored, "No issue in payload.", null);
                var number = issue.Str("number")!;
                var item = await ItemAsync(tracker.Id, number, ct);
                if (item is null) return (EngineeringDeliveryStatus.Ignored, $"Issue #{number} is not linked to any ticket.", number);
                if (action == "deleted") return (Result(await sync.OrphanAsync(item, "deleted", "webhook", ct)), $"Issue #{number} deleted.", number);
                if (action == "transferred") return (Result(await sync.OrphanAsync(item, "moved", "webhook", ct)), $"Issue #{number} transferred.", number);
                var changed = await sync.ApplyAsync(item, TrackerLookup.Found(GitHubMapping.FromIssue(issue, s, repo)), s, "webhook", ct);
                return (Result(changed), $"Issue #{number} {action}.", number);
            }
            case "release":
            {
                if (action is not ("published" or "released") || p.Prop("release") is not { } rel || rel.Str("draft") == "true")
                    return (EngineeringDeliveryStatus.Ignored, $"Release {action} ignored.", null);
                var at = rel.Date("published_at") ?? db.Clock.UtcNow;
                var count = 0;
                foreach (var version in new[] { rel.Str("tag_name"), rel.Str("name") }.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct())
                    count += await sync.ApplyReleaseAsync(tracker, version!, at, deployed: false, "webhook", ct);
                return (count > 0 ? EngineeringDeliveryStatus.Processed : EngineeringDeliveryStatus.Ignored,
                    $"Release {rel.Str("tag_name")}: {count} linked item(s) released.", rel.Str("tag_name"));
            }
            case "pull_request":
            {
                if (action != "closed" || p.Prop("pull_request") is not { } pr || pr.Str("merged_at") is null)
                    return (EngineeringDeliveryStatus.Ignored, $"Pull request {action} ignored.", null);
                var refs = GitHubMapping.ReferencedIssues($"{pr.Str("title")}\n{pr.Str("body")}");
                var touched = 0;
                foreach (var number in refs)
                {
                    if (await ItemAsync(tracker.Id, number, ct) is not { } item) continue;
                    sync.AddArtifacts(item, [new EngineeringArtifactDto("PullRequest", pr.Str("number") ?? "", pr.Str("title"), pr.Str("html_url"), "merged")]);
                    touched++;
                }
                return (touched > 0 ? EngineeringDeliveryStatus.Processed : EngineeringDeliveryStatus.Ignored, $"Merged PR #{pr.Str("number")} references {touched} linked item(s).",
                    pr.Str("number"));
            }
            default:
                return (EngineeringDeliveryStatus.Ignored, $"Event '{eventType}' is not used.", null);
        }
    }

    private async Task<(EngineeringDeliveryStatus, string?, string?)> AzureDevOpsAsync(Integration tracker, TrackerSettings s, string eventType, JsonElement p,
        CancellationToken ct)
    {
        var resource = p.Prop("resource") ?? default;
        switch (eventType)
        {
            case "workitem.updated":
            case "workitem.created":
            case "workitem.restored":
            case "workitem.commented":
            {
                var id = eventType == "workitem.updated" ? resource.Str("workItemId") ?? resource.Prop("revision")?.Str("id") : resource.Str("id");
                if (id is null) return (EngineeringDeliveryStatus.Ignored, "No work item id in payload.", null);
                var item = await ItemAsync(tracker.Id, id, ct);
                if (item is null) return (EngineeringDeliveryStatus.Ignored, $"Work item {id} is not linked to any ticket.", id);
                var fields = (eventType == "workitem.updated" ? resource.Prop("revision")?.Prop("fields") : resource.Prop("fields")) ?? default;
                var relations = eventType == "workitem.updated" ? resource.Prop("revision")?.Prop("relations") : resource.Prop("relations");
                if (fields.ValueKind != JsonValueKind.Object) return (EngineeringDeliveryStatus.Ignored, "No fields in payload (use resource version 1.0 with all fields).", id);
                var project = fields.Str("System.TeamProject");
                if (s.Project is { } configured && project is not null && !string.Equals(project, configured, StringComparison.OrdinalIgnoreCase))
                    return (Result(await sync.OrphanAsync(item, "moved", "webhook", ct)), $"Work item {id} moved to project {project}.", id);
                var url = AzureDevOpsMapping.WebUrl(tracker.BaseUrl, project ?? s.Project, id);
                var changed = await sync.ApplyAsync(item, TrackerLookup.Found(AzureDevOpsMapping.FromFields(id, fields, relations, s, url)), s, "webhook", ct);
                return (Result(changed), $"Work item {id} {eventType[9..]}.", id);
            }
            case "workitem.deleted":
            {
                var id = resource.Str("id");
                if (id is null || await ItemAsync(tracker.Id, id, ct) is not { } item) return (EngineeringDeliveryStatus.Ignored, "Work item is not linked.", id);
                return (Result(await sync.OrphanAsync(item, "deleted", "webhook", ct)), $"Work item {id} deleted.", id);
            }
            case "ms.vss-release.deployment-completed-event":
            {
                var env = resource.Prop("environment");
                var status = env?.Str("status") ?? resource.Prop("deployment")?.Str("deploymentStatus");
                if (status is not null && !status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) && !status.Equals("partiallySucceeded", StringComparison.OrdinalIgnoreCase))
                    return (EngineeringDeliveryStatus.Ignored, $"Deployment {status}.", null);
                var release = resource.Prop("deployment")?.Prop("release")?.Str("name") ?? resource.Prop("release")?.Str("name") ?? env?.Prop("release")?.Str("name");
                if (release is null) return (EngineeringDeliveryStatus.Ignored, "No release name in payload.", null);
                var count = await sync.ApplyReleaseAsync(tracker, release, db.Clock.UtcNow, deployed: true, "webhook", ct);
                return (count > 0 ? EngineeringDeliveryStatus.Processed : EngineeringDeliveryStatus.Ignored, $"Release {release} deployed: {count} linked item(s).", release);
            }
            default:
                return (EngineeringDeliveryStatus.Ignored, $"Event '{eventType}' is not used.", null);
        }
    }

    private static EngineeringDeliveryStatus Result(bool changed) => changed ? EngineeringDeliveryStatus.Processed : EngineeringDeliveryStatus.Ignored;
}
