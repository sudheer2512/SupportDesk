using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SupportDesk.Application.Communication;

/// <summary>
/// Separates the new content of an HTML e-mail reply from the quoted history. Recognises Gmail (div.gmail_quote),
/// Outlook (divRplyFwdMsg / appendonsend / "From: … Sent:" separators), Apple Mail and Thunderbird (blockquote type=cite,
/// moz-cite-prefix), Yahoo (yahoo_quoted) and generic "On … wrote:" + blockquote patterns. The cut HTML is re-sanitised by
/// the caller (which also repairs unclosed tags).
/// </summary>
public static partial class QuoteStripper
{
    public static (string Reply, string? Quoted) SplitHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return ("", null);
        var cut = -1;
        foreach (var regex in new[] { GmailQuote(), OutlookReply(), AppendOnSend(), StopSpelling(), CiteBlockquote(), MozCite(), YahooQuoted(), OutlookFromSent(), OriginalMessage() })
        {
            var m = regex.Match(html);
            if (m.Success && (cut < 0 || m.Index < cut)) cut = m.Index;
        }
        if (cut < 0)
        {
            // Generic: "On … wrote:" immediately followed by a blockquote.
            var w = OnWroteBlockquote().Match(html);
            if (w.Success) cut = w.Index;
        }
        if (cut <= 0) return (html, null);
        var reply = html[..cut];
        reply = TrailingOnWrote().Replace(reply, "");
        var replyText = Domain.Rules.TextNormalization.HtmlToText(reply);
        if (string.IsNullOrWhiteSpace(replyText) && !reply.Contains("<img", StringComparison.OrdinalIgnoreCase)) return (html, null);
        return (reply, html[cut..]);
    }

    [GeneratedRegex(@"<div[^>]*class\s*=\s*[""'][^""']*gmail_quote", RegexOptions.IgnoreCase)] private static partial Regex GmailQuote();
    [GeneratedRegex(@"<div[^>]*id\s*=\s*[""']?divRplyFwdMsg", RegexOptions.IgnoreCase)] private static partial Regex OutlookReply();
    [GeneratedRegex(@"<div[^>]*id\s*=\s*[""']?appendonsend", RegexOptions.IgnoreCase)] private static partial Regex AppendOnSend();
    [GeneratedRegex(@"<hr[^>]*id\s*=\s*[""']?stopSpelling", RegexOptions.IgnoreCase)] private static partial Regex StopSpelling();
    [GeneratedRegex(@"<blockquote[^>]*type\s*=\s*[""']?cite", RegexOptions.IgnoreCase)] private static partial Regex CiteBlockquote();
    [GeneratedRegex(@"<div[^>]*class\s*=\s*[""'][^""']*moz-cite-prefix", RegexOptions.IgnoreCase)] private static partial Regex MozCite();
    [GeneratedRegex(@"<div[^>]*class\s*=\s*[""'][^""']*yahoo_quoted", RegexOptions.IgnoreCase)] private static partial Regex YahooQuoted();
    [GeneratedRegex(@"<(?:p|div)[^>]*>\s*(?:<[^>]+>\s*)*From:\s*(?:<[^>]+>\s*)*[^<]{1,300}(?:<[^>]+>\s*)*(?:<br\s*/?>|</p>|</div>)\s*(?:<[^>]+>\s*)*Sent:", RegexOptions.IgnoreCase)]
    private static partial Regex OutlookFromSent();
    [GeneratedRegex(@"-{3,}\s*Original Message\s*-{3,}", RegexOptions.IgnoreCase)] private static partial Regex OriginalMessage();
    [GeneratedRegex(@"(?:<(?:div|p)[^>]*>\s*)?On\s[^<]{4,200}\swrote:\s*(?:<br\s*/?>\s*)?(?:</(?:div|p)>\s*)?<blockquote", RegexOptions.IgnoreCase)]
    private static partial Regex OnWroteBlockquote();
    [GeneratedRegex(@"(?:<(?:div|p)[^>]*>\s*)?On\s[^<]{4,200}\swrote:\s*(?:<br\s*/?>\s*)?(?:</(?:div|p)>\s*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingOnWrote();
}

/// <summary>Webhook signature verification helpers (constant-time comparisons).</summary>
public static class WebhookSignatures
{
    /// <summary>Hex HMAC-SHA256 of the raw body (Meta X-Hub-Signature-256 is "sha256=" + this).</summary>
    public static string HmacSha256Hex(string secret, byte[] body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    /// <summary>Verifies "sha256=&lt;hex&gt;" (or a bare hex digest) against the raw body.</summary>
    public static bool VerifyHmacSha256(string? secret, byte[] body, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(signatureHeader)) return false;
        var provided = signatureHeader.Trim();
        if (provided.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) provided = provided[7..];
        var expected = HmacSha256Hex(secret, body);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(provided.ToLowerInvariant()));
    }

    /// <summary>Twilio request signature: Base64(HMAC-SHA1(authToken, url + concat(sorted form key+value))).</summary>
    public static string TwilioSignature(string authToken, string url, IReadOnlyDictionary<string, string> form)
    {
        var sb = new StringBuilder(url);
        foreach (var key in form.Keys.OrderBy(k => k, StringComparer.Ordinal)) sb.Append(key).Append(form[key]);
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(authToken), Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static bool VerifyTwilio(string? authToken, string url, IReadOnlyDictionary<string, string> form, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(authToken) || string.IsNullOrWhiteSpace(signatureHeader)) return false;
        var expected = TwilioSignature(authToken, url, form);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signatureHeader.Trim()));
    }

    /// <summary>Parses an application/x-www-form-urlencoded body (last value wins for repeated keys).</summary>
    public static Dictionary<string, string> ParseForm(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var k = Uri.UnescapeDataString((i < 0 ? pair : pair[..i]).Replace('+', ' '));
            var v = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
            result[k] = v;
        }
        return result;
    }
}
