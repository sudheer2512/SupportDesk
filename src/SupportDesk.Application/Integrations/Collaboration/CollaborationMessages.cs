using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using SupportDesk.Application.Automation;
using SupportDesk.Domain.Integration;

namespace SupportDesk.Application.Integrations.Collaboration;

/// <summary>Events that can be posted to Teams/Slack channels.</summary>
public static class CollaborationEvents
{
    public const string UrgentTicket = "ticket.urgent";
    public const string SlaBreached = "sla.breached";
    public const string Escalated = "ticket.escalated";
    public const string Mention = "ticket.mention";
    public const string EngineeringEscalated = "engineering.escalated";
    public const string EngineeringResolved = "engineering.resolved";
    public const string EngineeringReleased = "engineering.released";

    public static readonly (string Key, string Label, string Description)[] All =
    [
        (UrgentTicket, "New urgent ticket", "A ticket is created with at least the channel's minimum priority."),
        (SlaBreached, "SLA breach", "A first-response, next-response or resolution target is breached."),
        (Escalated, "Escalation", "A ticket is escalated (manually, by automation or by SLA escalation levels)."),
        (Mention, "Mention", "An agent is @mentioned in an internal note."),
        (EngineeringEscalated, "Escalated to engineering", "A ticket is escalated to (or linked with) an engineering work item."),
        (EngineeringResolved, "Engineering fix done", "A linked work item is resolved in the tracker."),
        (EngineeringReleased, "Fix released", "The fix for a linked work item is released or deployed.")
    ];

    public static bool IsKnown(string key) => All.Any(e => e.Key == key);
}

public sealed record CollaborationFact(string Name, string Value);

/// <summary>A provider-neutral message: rendered as an Adaptive Card (Teams) or Block Kit (Slack) with a deep link.</summary>
public sealed record CollaborationMessage(string Title, string Text, IReadOnlyList<CollaborationFact> Facts, string? Link, string Tone = "info");

/// <summary>Builds the JSON bodies posted to Teams and Slack incoming webhooks.</summary>
public static class CollaborationPayloads
{
    public const string OpenLabel = "Open in SupportDesk";

    public static string Build(CollaborationProvider provider, CollaborationMessage m) => provider == CollaborationProvider.Slack ? Slack(m) : Teams(m);

    /// <summary>Teams incoming webhook (classic connector or Workflows "post to channel"): one Adaptive Card 1.4 attachment.</summary>
    public static string Teams(CollaborationMessage m)
    {
        var body = new JsonArray(
            new JsonObject
            {
                ["type"] = "TextBlock", ["text"] = Clip(m.Title, 300), ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true,
                ["color"] = m.Tone switch { "critical" => "Attention", "warning" => "Warning", "success" => "Good", _ => "Accent" }
            });
        if (!string.IsNullOrWhiteSpace(m.Text)) body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = Clip(m.Text, 2000), ["wrap"] = true });
        if (m.Facts.Count > 0)
        {
            var facts = new JsonArray();
            foreach (var f in m.Facts.Take(10)) facts.Add(new JsonObject { ["title"] = Clip(f.Name, 60), ["value"] = Clip(f.Value, 300) });
            body.Add(new JsonObject { ["type"] = "FactSet", ["facts"] = facts });
        }
        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["type"] = "AdaptiveCard", ["version"] = "1.4", ["body"] = body,
            ["msteams"] = new JsonObject { ["width"] = "Full" },
            ["actions"] = m.Link is null ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = OpenLabel, ["url"] = m.Link })
        };
        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["contentUrl"] = null, ["content"] = card })
        }.ToJsonString();
    }

    /// <summary>Slack incoming webhook: Block Kit header, text, fields and a link button (plus a plain-text fallback).</summary>
    public static string Slack(CollaborationMessage m)
    {
        var blocks = new JsonArray(new JsonObject
        {
            ["type"] = "header", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = Clip(m.Title, 150), ["emoji"] = true }
        });
        if (!string.IsNullOrWhiteSpace(m.Text))
            blocks.Add(new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = Clip(Escape(m.Text), 2900) } });
        if (m.Facts.Count > 0)
        {
            var fields = new JsonArray();
            foreach (var f in m.Facts.Take(10)) fields.Add(new JsonObject { ["type"] = "mrkdwn", ["text"] = Clip($"*{Escape(f.Name)}*\n{Escape(f.Value)}", 2000) });
            blocks.Add(new JsonObject { ["type"] = "section", ["fields"] = fields });
        }
        if (m.Link is not null)
            blocks.Add(new JsonObject
            {
                ["type"] = "actions",
                ["elements"] = new JsonArray(new JsonObject
                {
                    ["type"] = "button", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = OpenLabel }, ["url"] = m.Link,
                    ["style"] = m.Tone == "critical" ? "danger" : "primary"
                })
            });
        return new JsonObject { ["text"] = Clip($"{m.Title} — {m.Text}", 3000), ["blocks"] = blocks }.ToJsonString();
    }

    /// <summary>Slack mrkdwn control characters.</summary>
    public static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>
/// SSRF guard for Teams/Slack webhook URLs, consistent with automation webhooks (Automation:Webhooks:AllowPrivateNetworks):
/// https only (http only when private networks are allowed, e.g. on-premises relays), no credentials in the URL, and no
/// loopback/private/link-local targets unless allowed (link-local/metadata addresses are always blocked). The HTTP client
/// re-checks every address at connect time, so DNS rebinding cannot bypass the guard.
/// </summary>
public static class CollaborationUrlPolicy
{
    /// <summary>Synchronous checks (format, scheme, IP literals, localhost). Returns an error message or null.</summary>
    public static string? Validate(string? url, bool allowPrivate)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "Enter the full incoming-webhook URL.";
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowPrivate && uri.Scheme == Uri.UriSchemeHttp)) return "The webhook URL must use https.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "Credentials in the URL are not allowed.";
        var host = uri.IdnHost.Trim('[', ']');
        if (IPAddress.TryParse(host, out var literal)) return OutboundHttpSender.IsAllowed(literal, allowPrivate) ? null : Blocked(host);
        if (!allowPrivate && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))
            return Blocked(host);
        return null;
    }

    /// <summary>Full check including DNS resolution of host names (every resolved address must be allowed).</summary>
    public static async Task<string?> CheckAsync(string url, bool allowPrivate, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, CancellationToken ct = default)
    {
        if (Validate(url, allowPrivate) is { } error) return error;
        var uri = new Uri(url.Trim());
        var host = uri.IdnHost.Trim('[', ']');
        if (IPAddress.TryParse(host, out _)) return null;
        IPAddress[] addresses;
        try { addresses = await (resolve ?? ((h, t) => Dns.GetHostAddressesAsync(h, t)))(host, ct); }
        catch (SocketException) { return $"The host '{host}' could not be resolved."; }
        if (addresses.Length == 0) return $"The host '{host}' could not be resolved.";
        return addresses.All(a => OutboundHttpSender.IsAllowed(a, allowPrivate)) ? null : Blocked(host);
    }

    public static string Blocked(string host) => $"Calls to '{host}' are not allowed (private or reserved network address).";

    /// <summary>Non-secret hint of a webhook URL: host plus the last characters of the path.</summary>
    public static string Hint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "…";
        var path = u.AbsolutePath.TrimEnd('/');
        return path.Length <= 4 ? u.Host : $"{u.Host}/…{path[^4..]}";
    }
}
