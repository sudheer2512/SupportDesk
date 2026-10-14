using System.Text.RegularExpressions;
using SupportDesk.Domain;
using SupportDesk.Domain.Knowledge;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Internal outbox event names of the knowledge module (not part of the public webhook catalogue). They keep the
/// search index current for drafts and community activity; the public article.published / article.updated events are
/// raised only when readers see new content.
/// </summary>
public static class KnowledgeEvents
{
    public const string ArticleChanged = "kb.article_changed";
    public const string TopicChanged = "community.topic_changed";
}

/// <summary>Notification types raised by the knowledge module (delivered in-app; preferences may add e-mail).</summary>
public static class KnowledgeNotificationTypes
{
    public const string ReviewRequested = "kb.review_requested";
    public const string ArticleReviewed = "kb.article_reviewed";
    public const string ArticlePublished = "kb.article_published";
    public const string ReviewDue = "kb.review_due";
    public const string ArticleExpired = "kb.article_expired";
    public const string CommunityReply = "community.reply";
}

/// <summary>Validation helpers shared by the knowledge services.</summary>
public static partial class KbRules
{
    public static readonly string[] ArticleTypes = ["FAQ", "HowTo", "Troubleshooting", "Reference", "ReleaseNote", "ProductDoc"];

    public static readonly string[] CommonLocales = ["en", "hi", "ta", "te", "kn", "ml", "mr", "bn", "gu", "pa", "ar", "fr", "de", "es", "pt", "zh", "ja"];

    public static string? NormalizeArticleType(string? type) =>
        ArticleTypes.FirstOrDefault(t => t.Equals(type?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool TryVisibility(string? value, out KbVisibility visibility) =>
        Enum.TryParse(value?.Trim(), true, out visibility) && Enum.IsDefined(visibility);

    public static string? NormalizeLocale(string? locale)
    {
        var l = locale?.Trim();
        if (string.IsNullOrEmpty(l) || !LocaleRegex().IsMatch(l)) return null;
        var parts = l.Replace('_', '-').Split('-');
        return parts.Length == 1 ? parts[0].ToLowerInvariant() : $"{parts[0].ToLowerInvariant()}-{parts[1].ToUpperInvariant()}";
    }

    public static List<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? []).Select(t => TagRegex().Replace(t.Trim().ToLowerInvariant(), "-").Trim('-'))
        .Where(t => t.Length is > 0 and <= 40).Distinct().Take(20).ToList();

    [GeneratedRegex(@"^[A-Za-z]{2,3}([-_][A-Za-z0-9]{2,4})?$")] private static partial Regex LocaleRegex();
    [GeneratedRegex(@"[^\p{L}\p{N}\.\+#]+")] private static partial Regex TagRegex();
}
