using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SupportDesk.Domain.Rules;

public static partial class TextNormalization
{
    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var e = email.Trim().Trim('<', '>').ToLowerInvariant();
        return e.Contains('@') ? e : null;
    }

    public static string? EmailDomain(string? email)
    {
        var e = NormalizeEmail(email);
        return e is null ? null : e[(e.IndexOf('@') + 1)..];
    }

    /// <summary>Digits-only phone form. Numbers without a country code get <paramref name="defaultCountryCode"/> prefixed.</summary>
    public static string? NormalizePhone(string? phone, string defaultCountryCode = "91")
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var trimmed = phone.Trim();
        var hasPlus = trimmed.StartsWith('+');
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (trimmed.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)) hasPlus = trimmed.Contains('+');
        if (digits.Length < 6) return null;
        if (digits.StartsWith("00")) return digits[2..];
        if (hasPlus) return digits;
        if (digits.StartsWith('0')) digits = digits.TrimStart('0');
        return digits.Length <= 10 ? defaultCountryCode + digits : digits;
    }

    public static string Slugify(string text, int maxLength = 80)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }
        var slug = MultiDashRegex().Replace(sb.ToString(), "-").Trim('-');
        return slug.Length > maxLength ? slug[..maxLength].TrimEnd('-') : slug;
    }

    public static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";

    /// <summary>Converts (sanitised) HTML to readable plain text.</summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = BlockTagRegex().Replace(html, "\n");
        s = BrRegex().Replace(s, "\n");
        s = ScriptStyleRegex().Replace(s, "");
        s = TagRegex().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = MultiBlankLineRegex().Replace(s, "\n\n");
        return s.Trim();
    }

    public static string TextToHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var encoded = WebUtility.HtmlEncode(text.Replace("\r\n", "\n"));
        return "<p>" + encoded.Replace("\n\n", "</p><p>").Replace("\n", "<br>") + "</p>";
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a","an","and","are","as","at","be","but","by","for","from","has","have","i","if","in","into","is","it","its","me","my",
        "no","not","of","on","or","our","so","that","the","their","then","there","these","this","to","was","we","were","will",
        "with","you","your","can","could","would","should","please","hi","hello","thanks","thank","regards","dear","am","do","does","did"
    };

    /// <summary>Lower-cased search tokens with stop words removed and a light English stemmer.</summary>
    public static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (Match m in TokenRegex().Matches(text.ToLowerInvariant()))
        {
            var t = m.Value;
            if (t.Length < 2 || t.Length > 64 || StopWords.Contains(t)) continue;
            yield return Stem(t);
        }
    }

    public static string Stem(string t)
    {
        if (t.Length > 5 && t.EndsWith("ing")) return t[..^3];
        if (t.Length > 4 && t.EndsWith("ies")) return t[..^3] + "y";
        if (t.Length > 4 && t.EndsWith("ed") && !t.EndsWith("eed")) return t[..^2];
        if (t.Length > 3 && t.EndsWith('s') && !t.EndsWith("ss") && !t.EndsWith("us")) return t[..^1];
        return t;
    }

    public static string Sha256Hex(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    public static string Sha256Hex(byte[] input) => Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();

    /// <summary>Random URL-safe token (for reply tokens, survey links, visitor tokens).</summary>
    public static string RandomToken(int bytes = 16)
    {
        var data = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Lower-case alphanumeric token (safe inside e-mail local parts).</summary>
    public static string RandomAlphanumeric(int length = 10)
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    /// <summary>Normalised Levenshtein similarity in [0,1] (contact duplicate detection).</summary>
    public static double Similarity(string? a, string? b)
    {
        a = (a ?? "").Trim().ToLowerInvariant();
        b = (b ?? "").Trim().ToLowerInvariant();
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1.0 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }

    [GeneratedRegex(@"-{2,}")] private static partial Regex MultiDashRegex();
    [GeneratedRegex(@"</(p|div|li|tr|h[1-6]|blockquote|pre|table)>", RegexOptions.IgnoreCase)] private static partial Regex BlockTagRegex();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex BrRegex();
    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex ScriptStyleRegex();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"\n\s*\n\s*\n+")] private static partial Regex MultiBlankLineRegex();
    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_\.\-]*[\p{L}\p{N}]|[\p{L}\p{N}]")] private static partial Regex TokenRegex();
}
