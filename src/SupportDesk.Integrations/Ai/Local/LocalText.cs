using System.Globalization;
using System.Text.RegularExpressions;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Integrations.Ai.Local;

public sealed record SentimentResult(string Sentiment, double Score, bool Frustrated, IReadOnlyList<string> Cues);

/// <summary>
/// Lexicon-based sentiment with negation ("not working", "isn't fixed"), intensifiers ("very", "extremely") and
/// frustration cues (repeated follow-ups, escalation threats, "!!!", shouting). Score in [-1, 1].
/// </summary>
public static partial class LexiconSentimentAnalyzer
{
    private static readonly Dictionary<string, double> Lexicon = new(StringComparer.Ordinal)
    {
        // positive
        ["thanks"] = 0.6, ["thank"] = 0.6, ["great"] = 2, ["excellent"] = 2.5, ["perfect"] = 2, ["awesome"] = 2, ["appreciate"] = 1.5, ["appreciated"] = 1.5,
        ["helpful"] = 1.5, ["resolved"] = 1.2, ["works"] = 1, ["working"] = 0.8, ["fixed"] = 1.3, ["happy"] = 2, ["glad"] = 1.5, ["good"] = 1, ["quick"] = 1,
        ["quickly"] = 1, ["fast"] = 0.8, ["love"] = 2, ["wonderful"] = 2, ["satisfied"] = 1.5, ["smooth"] = 1, ["solved"] = 1.3, ["amazing"] = 2.2, ["pleased"] = 1.5,
        // negative
        ["error"] = -0.3, ["issue"] = -0.3, ["problem"] = -0.5, ["fail"] = -0.8, ["fails"] = -0.8, ["failed"] = -0.8, ["failing"] = -0.8, ["failure"] = -0.8,
        ["broken"] = -1, ["bad"] = -1.5, ["poor"] = -1.5, ["terrible"] = -2.5, ["awful"] = -2.5, ["horrible"] = -2.5, ["worst"] = -3, ["slow"] = -1, ["wrong"] = -1,
        ["unable"] = -0.7, ["cannot"] = -0.6, ["can't"] = -0.6, ["stuck"] = -1, ["blocked"] = -1, ["blocking"] = -1, ["unacceptable"] = -3, ["disappointed"] = -2.5,
        ["disappointing"] = -2.5, ["frustrated"] = -3, ["frustrating"] = -3, ["angry"] = -3, ["annoyed"] = -2.5, ["upset"] = -2.5, ["useless"] = -2.5,
        ["ridiculous"] = -3, ["waste"] = -2, ["delay"] = -1, ["delayed"] = -1, ["urgent"] = -0.8, ["urgently"] = -0.8, ["nobody"] = -1.5, ["ignored"] = -2,
        ["complaint"] = -2, ["complain"] = -1.5, ["escalate"] = -1.5, ["cancel"] = -1.5, ["lawyer"] = -3, ["legal"] = -1.2, ["crash"] = -1, ["crashes"] = -1,
        ["freezes"] = -1, ["freezing"] = -1, ["down"] = -0.8, ["losing"] = -1.2, ["lost"] = -1, ["pathetic"] = -3, ["incompetent"] = -3, ["hopeless"] = -2.5
    };

    private static readonly Dictionary<string, double> Intensifiers = new(StringComparer.Ordinal)
    {
        ["very"] = 1.5, ["extremely"] = 2, ["really"] = 1.4, ["so"] = 1.3, ["totally"] = 1.5, ["completely"] = 1.5, ["absolutely"] = 1.6, ["highly"] = 1.4,
        ["super"] = 1.4, ["utterly"] = 1.8
    };

    private static readonly HashSet<string> Negators = new(StringComparer.Ordinal) { "not", "no", "never", "without", "hardly", "nothing", "nor", "neither" };

    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal)
    {
        "GST", "ERP", "POS", "PDF", "API", "SQL", "AMC", "TDS", "UPI", "CFO", "GRN", "HSN", "PF", "UAT", "VAT", "IT", "HR", "OK", "CSV", "XML", "URL", "SMS", "OTP", "PO", "IST", "UTC"
    };

    public static SentimentResult Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new SentimentResult("Neutral", 0, false, []);
        var cues = new List<string>();
        foreach (var (name, regex) in FrustrationCues)
            if (regex.IsMatch(text)) cues.Add(name);
        // Shouting = ordinary words in capitals ("STILL NOT FIXED"), not product/tax acronyms (GST, CGST, ERP…).
        var capsWords = ShoutRegex().Matches(text).Select(m => m.Value).Where(w => !Acronyms.Contains(w)).ToList();
        var words = WordRegex().Matches(text).Count;
        if (capsWords.Count(ShoutWords.Contains) >= 1 || (capsWords.Count >= 3 && capsWords.Count >= 0.3 * Math.Max(1, words))) cues.Add("shouting");

        var tokens = TokenRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();
        double raw = 0;
        var negateWindow = 0;
        var intensity = 1.0;
        foreach (var token in tokens)
        {
            if (Negators.Contains(token) || (token.EndsWith("n't") && token is not "can't"))
            {
                negateWindow = 3;
                continue;
            }
            if (Intensifiers.TryGetValue(token, out var boost))
            {
                intensity = boost;
                continue;
            }
            if (Lexicon.TryGetValue(token, out var weight))
            {
                var w = weight * intensity;
                if (negateWindow > 0) w = w > 0 ? -0.8 * w : -0.4 * w; // "not working" → negative; "not bad" → mildly positive
                raw += w;
                intensity = 1.0;
            }
            if (negateWindow > 0) negateWindow--;
        }
        raw -= 0.8 * cues.Count;
        var exclamations = text.Count(c => c == '!');
        if (exclamations >= 2 && raw < 0) raw -= 0.5;
        var score = Math.Round(Math.Tanh(raw / Math.Sqrt(tokens.Count + 4) * 1.6), 3);
        var frustrated = cues.Count >= 2 || (cues.Count >= 1 && score <= -0.25);
        var label = frustrated ? "Frustrated" : score <= -0.2 ? "Negative" : score >= 0.2 ? "Positive" : "Neutral";
        return new SentimentResult(label, score, frustrated, cues);
    }

    private static readonly (string Name, Regex Pattern)[] FrustrationCues =
    [
        ("still_not_fixed", StillRegex()),
        ("repeated_follow_up", RepeatRegex()),
        ("no_response", NoResponseRegex()),
        ("escalation_threat", EscalationRegex()),
        ("strong_language", StrongRegex()),
        ("multiple_exclamations", ExclaimRegex())
    ];

    [GeneratedRegex(@"\bstill\b[^.!?\n]{0,30}\b(not|no|isn't|doesn't|hasn't|haven't|can't|cannot|unable|broken|failing|waiting|the same)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StillRegex();
    [GeneratedRegex(@"\b(again|(second|third|fourth|fifth|\d+(st|nd|rd|th)) time|how many times|once again|every time|keep(s)? (happening|failing|asking))\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatRegex();
    [GeneratedRegex(@"\b(no one|nobody|no response|no reply|not heard back|haven't heard|no update|waiting (for|since) (days|weeks|\d+ (days|weeks)))\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoResponseRegex();
    [GeneratedRegex(@"\b(escalat\w*|cancel (our|my|the) (contract|subscription|amc)|switch(ing)? to (another|a different|a competitor)|legal action|lawyer|complaint|your manager)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EscalationRegex();
    [GeneratedRegex(@"\b(unacceptable|ridiculous|pathetic|useless|waste of (my |our )?time|fed up|sick of|worst|incompetent|disgusting|furious)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StrongRegex();
    [GeneratedRegex(@"[!?]{3,}|!!")] private static partial Regex ExclaimRegex();
    private static readonly HashSet<string> ShoutWords = new(StringComparer.Ordinal)
    {
        "NOT", "STILL", "NOW", "WHY", "NEVER", "AGAIN", "NOTHING", "NOBODY", "ASAP", "URGENT", "HELP", "STOP", "FIX", "WORKING", "IMMEDIATELY", "PLEASE", "ANYONE",
        "SERIOUSLY", "UNACCEPTABLE", "WORST", "TODAY"
    };

    [GeneratedRegex(@"\b[A-Z]{3,}\b")] private static partial Regex ShoutRegex();
    [GeneratedRegex(@"\b[A-Za-z]{3,}\b")] private static partial Regex WordRegex();
    [GeneratedRegex(@"[a-z]+(?:'[a-z]+)?")] private static partial Regex TokenRegex();
}

/// <summary>Sentence utilities shared by the local summariser, composer and knowledge drafter.</summary>
public static partial class Sentences
{
    public static List<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var normalized = text.Replace("\r\n", "\n");
        return SplitRegex().Split(normalized).Select(s => WhitespaceRegex().Replace(s, " ").Trim().TrimStart('-', '•', '*', ' '))
            .Where(s => s.Length >= 3).ToList();
    }

    public static bool IsBoilerplate(string sentence) => BoilerplateRegex().IsMatch(sentence) && TextNormalization.Tokenize(sentence).Count() <= 8;

    public static string Ago(DateTime then, DateTime now)
    {
        var d = now - then;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 48) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} days ago";
    }

    public static string In(DateTime then, DateTime now)
    {
        var d = then - now;
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)d.TotalMinutes)} min";
        if (d.TotalHours < 48) return $"{(int)d.TotalHours} h";
        return $"{(int)d.TotalDays} days";
    }

    public static string Clean(string s)
    {
        var t = s.Trim();
        if (t.Length > 0 && !".!?:".Contains(t[^1])) t += ".";
        return t.Length > 0 ? char.ToUpper(t[0], CultureInfo.InvariantCulture) + t[1..] : t;
    }

    // Sentence ends only before a capital, digit or quote, so "rounding vs. invoice-level" stays one sentence.
    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9""“(])|\n+")] private static partial Regex SplitRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();
    [GeneratedRegex(@"^(hi|hello|dear|hey|good (morning|afternoon|evening)|regards|best regards|thanks|thank you|many thanks|cheers|sincerely|please let (us|me) know|we('re| are) looking into|we will update|will update you|sorry for the inconvenience)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BoilerplateRegex();
}
