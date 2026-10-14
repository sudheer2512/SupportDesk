using System.Text.RegularExpressions;
using SupportDesk.Application.Ai;
using SupportDesk.Application.Ai.Analytics;

namespace SupportDesk.Integrations.Ai.Local;

/// <summary>
/// Rule/regex parser for analytics questions → <see cref="AnalyticsIntent"/> (metric, dimension, filters, period, sort,
/// limit). Entity names (customers, products, categories, departments, agents, teams) are matched against the tenant's
/// vocabulary and become id filters; nothing from the question is ever used as query text.
/// </summary>
public static partial class LocalNlAnalyticsParser
{
    private static readonly string[] LegalSuffixes = ["pvt", "ltd", "private", "limited", "inc", "llc", "llp", "corp", "co", "trust", "plc", "gmbh"];

    public static AnalyticsIntent Parse(string question, IReadOnlyList<AnalyticsVocabularyEntry> vocabulary, DateTime now)
    {
        var q = " " + Regex.Replace(question.ToLowerInvariant(), @"[^\p{L}\p{N}%&'\- ]+", " ") + " ";
        var intent = new AnalyticsIntent { Metric = Metric(q), Period = Period(q), Sort = Sort(q), Limit = Limit(q) };
        intent.Dimension = Dimension(q);
        if (intent.Metric == "open_tickets" && intent.Dimension == "status") intent.Dimension = "none";

        // Enum filters ("high priority", "via WhatsApp") unless the same field is the breakdown.
        if (intent.Dimension != "priority" && PriorityFilterRegex().Match(q) is { Success: true } p)
        {
            var value = p.Groups["p"].Value;
            intent.Filters.Add(new AnalyticsFilter("priority", char.ToUpperInvariant(value[0]) + value[1..]));
        }
        if (intent.Dimension != "channel" && ChannelRegex().Match(q) is { Success: true } c)
            intent.Filters.Add(new AnalyticsFilter("channel", ChannelName(c.Groups["ch"].Value)));

        // Entity filters from the vocabulary (longest names first, one per field).
        var used = new HashSet<string>();
        foreach (var v in vocabulary.OrderByDescending(v => v.Label.Length))
        {
            if (v.Field == intent.Dimension || used.Contains(v.Field)) continue;
            if (!Mentions(q, v, vocabulary)) continue;
            intent.Filters.Add(new AnalyticsFilter(v.Field, v.Value));
            used.Add(v.Field);
        }
        return intent;
    }

    private static bool Mentions(string q, AnalyticsVocabularyEntry v, IReadOnlyList<AnalyticsVocabularyEntry> vocabulary)
    {
        var label = Regex.Replace(v.Label.ToLowerInvariant(), @"[^\p{L}\p{N}%&'\- ]+", " ").Trim();
        if (label.Length < 3) return false;
        if (q.Contains(" " + label + " ")) return true;
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var core = words.Where(w => !LegalSuffixes.Contains(w)).ToList();
        if (core.Count >= 2 && core.Count < words.Count && q.Contains(" " + string.Join(' ', core) + " ")) return true;
        // People: a unique first name ("tickets resolved by Arjun").
        if (v.Field == "assignee" && words.Count >= 2 && words[0].Length >= 4 && q.Contains(" " + words[0] + " ") &&
            vocabulary.Count(x => x.Field == "assignee" && x.Label.StartsWith(words[0], StringComparison.OrdinalIgnoreCase)) == 1)
            return true;
        return false;
    }

    private static string Metric(string q)
    {
        if (SlaRegex().IsMatch(q)) return "sla_breaches";
        if (FirstResponseRegex().IsMatch(q)) return "avg_first_response_hours";
        if (ResolutionTimeRegex().IsMatch(q)) return "avg_resolution_hours";
        if (CsatRegex().IsMatch(q)) return "csat_avg";
        if (q.Contains("reopen")) return "reopened_tickets";
        if (q.Contains("escalat")) return "escalated_tickets";
        if (FrustratedRegex().IsMatch(q)) return "frustrated_tickets";
        if (OpenRegex().IsMatch(q)) return "open_tickets";
        if (ResolvedRegex().IsMatch(q)) return "resolved_tickets";
        return "ticket_count";
    }

    private static string Dimension(string q)
    {
        if (DayRegex().IsMatch(q)) return "day";
        if (WeekRegex().IsMatch(q)) return "week";
        if (MonthRegex().IsMatch(q)) return "month";
        if (Regex.IsMatch(q, @"\b(customers?|clients?|accounts?|organi[sz]ations?|companies)\b")) return "customer";
        if (Regex.IsMatch(q, @"\b(products?|modules?)\b")) return "product";
        if (Regex.IsMatch(q, @"\b(categor(y|ies)|issue types?|topics?)\b")) return "category";
        if (Regex.IsMatch(q, @"\b(by|per|each|which|across) (priority|priorities)\b|\bpriorit(y|ies) (breakdown|split|mix)\b")) return "priority";
        if (Regex.IsMatch(q, @"\b(by|per|each|which|across) channels?\b|\bchannels\b")) return "channel";
        if (Regex.IsMatch(q, @"\bdepartments?\b")) return "department";
        if (Regex.IsMatch(q, @"\bteams?\b")) return "team";
        if (Regex.IsMatch(q, @"\b(agents?|assignees?|engineers?|staff|who)\b")) return "assignee";
        if (Regex.IsMatch(q, @"\b(by|per|each) status(es)?\b|\bstatus (breakdown|split)\b")) return "status";
        if (Regex.IsMatch(q, @"\b(by|per) sentiment\b|\bsentiment (breakdown|split)\b")) return "sentiment";
        return "none";
    }

    private static string Period(string q)
    {
        if (Regex.IsMatch(q, @"\btoday\b")) return "today";
        if (Regex.IsMatch(q, @"\byesterday\b")) return "yesterday";
        if (Regex.IsMatch(q, @"\bthis week\b")) return "this_week";
        if (Regex.IsMatch(q, @"\blast week\b|\bprevious week\b")) return "last_week";
        if (Regex.IsMatch(q, @"\bthis month\b|\bcurrent month\b|\bmonth to date\b|\bmtd\b")) return "this_month";
        if (Regex.IsMatch(q, @"\blast month\b|\bprevious month\b")) return "last_month";
        if (Regex.IsMatch(q, @"\bthis quarter\b|\bquarter\b|\bqtd\b")) return "this_quarter";
        if (Regex.IsMatch(q, @"\bthis year\b|\byear to date\b|\bytd\b")) return "this_year";
        if (Regex.IsMatch(q, @"\b(last|past) (12 months|year)\b")) return "last_365_days";
        if (Regex.IsMatch(q, @"\b(all time|ever|overall)\b")) return "all_time";
        if (Regex.Match(q, @"\b(last|past) (\d{1,3}) days?\b") is { Success: true } m && int.TryParse(m.Groups[2].Value, out var n))
            return n <= 7 ? "last_7_days" : n <= 30 ? "last_30_days" : n <= 90 ? "last_90_days" : "last_365_days";
        if (Regex.IsMatch(q, @"\b(last|past) (7 days|week)\b")) return "last_7_days";
        if (Regex.IsMatch(q, @"\b(last|past) (3 months|90 days)\b")) return "last_90_days";
        if (Regex.IsMatch(q, @"\b(last|past) (month|30 days)\b")) return "last_30_days";
        return "last_30_days";
    }

    private static string Sort(string q)
    {
        if (Regex.IsMatch(q, @"\b(least|fewest|lowest|minimum|bottom|smallest)\b")) return "asc";
        if (CsatRegex().IsMatch(q) && Regex.IsMatch(q, @"\b(worst|unhappiest)\b")) return "asc";
        return "desc";
    }

    private static int Limit(string q)
    {
        if (Regex.Match(q, @"\b(top|bottom|first)\s+(\d{1,2})\b") is { Success: true } m && int.TryParse(m.Groups[2].Value, out var n)) return n;
        if (Regex.Match(q, @"\b(\d{1,2})\s+(customers|clients|products|agents|categories|teams|departments)\b") is { Success: true } m2 &&
            int.TryParse(m2.Groups[1].Value, out var n2)) return n2;
        return 10;
    }

    private static string ChannelName(string c) => c switch
    {
        "email" or "e-mail" or "mail" => "Email", "whatsapp" => "WhatsApp", "portal" => "Portal", "phone" or "call" or "calls" => "Phone", "chat" => "Chat",
        "sms" => "Sms", "facebook" => "Facebook", "instagram" => "Instagram", "web form" or "webform" => "WebForm", "api" => "Api", "walk-in" or "walkin" => "WalkIn",
        _ => "Email"
    };

    [GeneratedRegex(@"\bsla\b|\bbreach|\bviolat|\bmissed (the )?(sla|deadline|target)|\boverdue\b")] private static partial Regex SlaRegex();
    [GeneratedRegex(@"\bfirst (response|reply)|\bresponse time|\btime to (first )?respond")] private static partial Regex FirstResponseRegex();
    [GeneratedRegex(@"\b(resolution|resolve|resolving|closure|close) time|\btime to (resolve|close)|\bhow long\b|\bmttr\b|\baverage resolution")] private static partial Regex ResolutionTimeRegex();
    [GeneratedRegex(@"\bcsat\b|\bsatisf|\brating|\bnps\b|\bhappiest\b|\bunhappiest\b")] private static partial Regex CsatRegex();
    [GeneratedRegex(@"\bfrustrat|\bangry\b|\bunhappy customers?\b|\bnegative sentiment\b|\bupset\b")] private static partial Regex FrustratedRegex();
    [GeneratedRegex(@"\bopen\b|\bbacklog\b|\bunresolved\b|\bpending\b|\boutstanding\b")] private static partial Regex OpenRegex();
    [GeneratedRegex(@"\bresolved\b|\bclosed\b|\bsolved\b|\bfixed\b")] private static partial Regex ResolvedRegex();
    [GeneratedRegex(@"\b(per day|daily|by day|each day|day by day|trend|over time|per date)\b")] private static partial Regex DayRegex();
    [GeneratedRegex(@"\b(per week|weekly|by week|each week|week by week)\b")] private static partial Regex WeekRegex();
    [GeneratedRegex(@"\b(per month|monthly|by month|each month|month by month)\b")] private static partial Regex MonthRegex();
    [GeneratedRegex(@"\b(?<p>low|medium|high|critical)[ -]priority\b|\bpriority (?<p>low|medium|high|critical)\b")]
    private static partial Regex PriorityFilterRegex();
    [GeneratedRegex(@"\b(?:via|through|from|over|on|by) (?<ch>e-?mail|mail|whatsapp|portal|phone|calls?|chat|sms|facebook|instagram|web ?form|api|walk-?in)\b|\b(?<ch>e-?mail|whatsapp|portal|phone|chat|sms) tickets\b")]
    private static partial Regex ChannelRegex();
}
