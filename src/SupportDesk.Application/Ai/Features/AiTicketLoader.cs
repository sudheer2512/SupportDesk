using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Tickets;
using SupportDesk.Domain;
using SupportDesk.Domain.Rules;
using SupportDesk.Domain.Tickets;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Ai.Features;

/// <summary>Loads a ticket (respecting the caller's data scope) into a <see cref="TicketSnapshot"/> and renders it for prompts.</summary>
public sealed class AiTicketLoader(DeskDbContext db, TicketService tickets, TicketLookups lookups)
{
    public async Task<(Ticket Ticket, TicketSnapshot Snapshot)> LoadAsync(Guid ticketId, bool internalView, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().Include(x => x.Tags).FirstOrDefaultAsync(x => x.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        await tickets.EnsureVisibleAsync(t, ct);
        return (t, await SnapshotAsync(t, internalView, ct));
    }

    public async Task<TicketSnapshot> SnapshotAsync(Ticket t, bool internalView, CancellationToken ct)
    {
        await lookups.LoadAsync(ct);
        var contact = t.ContactId is null ? null : await db.Contacts.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(c => c.Id == t.ContactId).Select(c => new { c.FullName, c.FirstName }).FirstOrDefaultAsync(ct);
        var customer = t.CustomerId is null ? null : await db.Customers.IgnoreQueryFilters([DeskDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(c => c.Id == t.CustomerId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var threads = await db.TicketThreads.AsNoTracking().Where(x => x.TicketId == t.Id && (internalView || x.Visibility == Visibility.Public))
            .OrderByDescending(x => x.Seq).Take(40).ToListAsync(ct);
        var messages = threads.OrderBy(x => x.Seq).Select(x => new TicketMessage(x.Seq, x.Kind.ToString(), x.AuthorType.ToString(), x.AuthorName,
            x.Visibility == Visibility.Public, TextNormalization.Truncate(x.BodyText ?? TextNormalization.HtmlToText(x.BodyHtml), 2000), x.CreatedAt)).ToList();
        var tasks = await db.TicketTasks.AsNoTracking().Where(x => x.TicketId == t.Id && (x.Status == TaskItemStatus.Open || x.Status == TaskItemStatus.InProgress))
            .OrderBy(x => x.SortOrder).Select(x => x.Title).Take(10).ToListAsync(ct);
        var hasAttachments = await db.TicketAttachments.AsNoTracking().AnyAsync(a => a.TicketId == t.Id, ct);
        var rootCause = t.RootCause is null ? null : await db.LookupValues.AsNoTracking().Where(l => l.Type == "root_cause" && l.Value == t.RootCause)
            .Select(l => l.Label).FirstOrDefaultAsync(ct) ?? t.RootCause;
        var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Json.Object(t.CustomFieldsJson))
            custom[k] = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        var firstName = contact?.FirstName ?? contact?.FullName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return new TicketSnapshot(
            t.Id, t.Number, lookups.Display(t.Number), t.Subject, TextNormalization.Truncate(t.DescriptionText ?? TextNormalization.HtmlToText(t.DescriptionHtml), 3000),
            lookups.Status(t.StatusId), t.StatusCategory.ToString(), t.Priority.ToString(), t.Channel.ToString(), lookups.Department(t.DepartmentId),
            lookups.User(t.AssigneeId), customer, contact?.FullName, firstName, lookups.Product(t.ProductId), lookups.Version(t.VersionId),
            lookups.Category(t.CategoryId), lookups.Category(t.SubcategoryId), t.SlaState.ToString(), t.DueAt, t.FirstResponseDueAt, t.CreatedAt,
            db.Clock.UtcNow, t.AwaitingCustomerConfirmation, TextNormalization.HtmlToText(t.ResolutionHtml), rootCause, messages, tasks, custom,
            t.VersionId is not null, hasAttachments, t.Language);
    }

    /// <summary>Trusted metadata (names and states the help desk itself set).</summary>
    public static string RenderMetadata(TicketSnapshot s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Ticket: {s.DisplayNumber}");
        sb.AppendLine($"Status: {s.Status} ({s.StatusCategory}){(s.AwaitingCustomerConfirmation ? ", awaiting customer confirmation" : "")}");
        sb.AppendLine($"Priority: {s.Priority} · Channel: {s.Channel} · SLA: {s.SlaState}{(s.DueAt is { } due ? $", resolution due {due:yyyy-MM-dd HH:mm} UTC" : "")}");
        sb.AppendLine($"Department: {s.Department ?? "—"} · Assignee: {s.Assignee ?? "unassigned"}");
        sb.AppendLine($"Customer: {s.Customer ?? "—"} · Contact: {s.Contact ?? "—"}");
        sb.AppendLine($"Product: {s.Product ?? "—"}{(s.Version is null ? "" : " " + s.Version)} · Category: {s.Category ?? "—"}{(s.Subcategory is null ? "" : " › " + s.Subcategory)}");
        sb.AppendLine($"Created: {s.CreatedAt:yyyy-MM-dd HH:mm} UTC · Now: {s.Now:yyyy-MM-dd HH:mm} UTC");
        if (s.OpenTasks.Count > 0) sb.AppendLine("Open tasks: " + string.Join("; ", s.OpenTasks));
        if (!string.IsNullOrWhiteSpace(s.RootCause)) sb.AppendLine($"Root cause: {s.RootCause}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Conversation text (untrusted: contains customer content).</summary>
    public static string RenderConversation(TicketSnapshot s, bool includeInternal, int maxChars = 12_000)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Subject: {s.Subject}");
        if (s.Description.Length > 0) sb.AppendLine($"Description: {s.Description}");
        foreach (var m in s.Messages.Where(m => includeInternal || m.IsPublic))
        {
            sb.AppendLine();
            sb.AppendLine($"--- #{m.Seq} {m.Kind} by {m.AuthorName ?? m.AuthorType} ({m.AuthorType}{(m.IsPublic ? "" : ", internal")}) at {m.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC");
            sb.AppendLine(m.Text);
        }
        if (!string.IsNullOrWhiteSpace(s.ResolutionText)) sb.AppendLine().AppendLine($"Resolution: {s.ResolutionText}");
        var text = sb.ToString();
        // Keep the beginning (problem statement) and the most recent part when trimming.
        return text.Length <= maxChars ? text : text[..(maxChars / 3)] + "\n…\n" + text[^(maxChars * 2 / 3)..];
    }

    /// <summary>What the customer wrote (subject, description, customer messages) — classification/retrieval input.</summary>
    public static string CustomerText(TicketSnapshot s, int maxChars = 4000)
    {
        var parts = new List<string> { s.Subject, s.Description };
        parts.AddRange(s.Messages.Where(m => m.AuthorType == nameof(AuthorType.Customer) && m.Text != s.Description).Select(m => m.Text));
        return TextNormalization.Truncate(string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p))), maxChars);
    }

    /// <summary>The latest thing the customer said (or the description).</summary>
    public static string LatestCustomerText(TicketSnapshot s) =>
        s.Messages.LastOrDefault(m => m.AuthorType == nameof(AuthorType.Customer))?.Text is { Length: > 0 } last ? last : s.Description;
}

/// <summary>Lenient JSON extraction and typed accessors for model output.</summary>
public static class AiJson
{
    public static JsonElement ParseObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new AiOutputException("Empty response.");
        var s = text.Trim();
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start < 0 || end <= start) throw new AiOutputException("No JSON object in the response.");
        using var doc = JsonDocument.Parse(s[start..(end + 1)]);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new AiOutputException("The response is not a JSON object.");
        return doc.RootElement.Clone();
    }

    public static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    public static string? Str(JsonElement e, string name) => Prop(e, name) is { } v
        ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(), _ => null }
        : null;

    public static double? Num(JsonElement e, string name)
    {
        if (Prop(e, name) is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static bool? Bool(JsonElement e, string name) => Prop(e, name) is { } v
        ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null, _ => null }
        : null;

    public static List<string> StrList(JsonElement e, string name, int max = 20, int maxLength = 500) => Prop(e, name) is { ValueKind: JsonValueKind.Array } a
        ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ValueKind == JsonValueKind.Number ? x.ToString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => TextNormalization.Truncate(x!.Trim(), maxLength)).Take(max).ToList()
        : [];

    public static List<int> IntList(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.Array } a
        ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var i) ? i
                : x.ValueKind == JsonValueKind.String && int.TryParse(x.GetString()?.Trim('[', ']', ' '), out var j) ? j : -1)
            .Where(i => i >= 0).Distinct().ToList()
        : [];

    public static double Clamp01(double? v, double fallback = 0.5) => v is { } d && !double.IsNaN(d) ? Math.Clamp(d, 0, 1) : fallback;
}
