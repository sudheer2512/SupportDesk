using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Minimal, dependency-free Markdown ⇄ HTML conversion for knowledge-base import/export: headings, paragraphs,
/// emphasis, inline code, fenced code blocks, block quotes, ordered/unordered lists, links, images, horizontal rules
/// and pipe tables. Output HTML is always passed through the sanitiser by the caller.
/// </summary>
public static partial class MarkdownConverter
{
    // ───────────────────────────── Markdown → HTML ─────────────────────────────

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var html = new StringBuilder();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { i++; continue; }

            if (line.TrimStart().StartsWith("```"))
            {
                var lang = line.Trim()[3..].Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) code.AppendLine(lines[i++]);
                i++;
                var cls = lang.Length > 0 ? $" class=\"language-{WebUtility.HtmlEncode(lang)}\"" : "";
                html.Append($"<pre><code{cls}>{WebUtility.HtmlEncode(code.ToString().TrimEnd('\n'))}</code></pre>");
                continue;
            }
            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                html.Append($"<h{level}>{Inline(heading.Groups[2].Value.Trim())}</h{level}>");
                i++;
                continue;
            }
            if (HrRegex().IsMatch(line)) { html.Append("<hr>"); i++; continue; }
            if (line.TrimStart().StartsWith('>'))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>')) quote.Add(lines[i++].TrimStart()[1..].TrimStart());
                html.Append($"<blockquote>{ToHtml(string.Join('\n', quote))}</blockquote>");
                continue;
            }
            if (line.TrimStart().StartsWith('|') && i + 1 < lines.Length && TableSeparatorRegex().IsMatch(lines[i + 1]))
            {
                var header = Cells(line);
                i += 2;
                html.Append("<table><thead><tr>");
                foreach (var h in header) html.Append($"<th>{Inline(h)}</th>");
                html.Append("</tr></thead><tbody>");
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    html.Append("<tr>");
                    foreach (var c in Cells(lines[i++])) html.Append($"<td>{Inline(c)}</td>");
                    html.Append("</tr>");
                }
                html.Append("</tbody></table>");
                continue;
            }
            if (BulletRegex().IsMatch(line) || OrderedRegex().IsMatch(line))
            {
                var ordered = OrderedRegex().IsMatch(line);
                html.Append(ordered ? "<ol>" : "<ul>");
                while (i < lines.Length && (ordered ? OrderedRegex().IsMatch(lines[i]) : BulletRegex().IsMatch(lines[i])))
                {
                    var item = ordered ? OrderedRegex().Replace(lines[i], "", 1) : BulletRegex().Replace(lines[i], "", 1);
                    html.Append($"<li>{Inline(item.Trim())}</li>");
                    i++;
                }
                html.Append(ordered ? "</ol>" : "</ul>");
                continue;
            }
            var para = new List<string>();
            while (i < lines.Length && lines[i].Trim().Length > 0 && !IsBlockStart(lines, i)) para.Add(lines[i++].Trim());
            if (para.Count == 0) { para.Add(lines[i++].Trim()); }
            html.Append($"<p>{string.Join("<br>", para.Select(Inline))}</p>");
        }
        return html.ToString();
    }

    private static bool IsBlockStart(string[] lines, int i)
    {
        var l = lines[i];
        return l.TrimStart().StartsWith("```") || HeadingRegex().IsMatch(l) || HrRegex().IsMatch(l) || l.TrimStart().StartsWith('>') ||
               BulletRegex().IsMatch(l) || OrderedRegex().IsMatch(l) ||
               (l.TrimStart().StartsWith('|') && i + 1 < lines.Length && TableSeparatorRegex().IsMatch(lines[i + 1]));
    }

    private static IEnumerable<string> Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim());

    /// <summary>Inline formatting on an HTML-encoded string: code spans first (protected), then images, links, bold, italic.</summary>
    private static string Inline(string text)
    {
        var codes = new List<string>();
        var s = CodeSpanRegex().Replace(text, m =>
        {
            codes.Add($"<code>{WebUtility.HtmlEncode(m.Groups[1].Value)}</code>");
            return $"@@CODE{codes.Count - 1}@@";
        });
        s = WebUtility.HtmlEncode(s);
        s = ImageRegex().Replace(s, m => $"<img src=\"{SafeUrl(m.Groups[2].Value)}\" alt=\"{m.Groups[1].Value}\">");
        s = LinkRegex().Replace(s, m => $"<a href=\"{SafeUrl(m.Groups[2].Value)}\">{m.Groups[1].Value}</a>");
        s = BoldRegex().Replace(s, "<strong>$2</strong>");
        s = ItalicRegex().Replace(s, "<em>$2</em>");
        return CodePlaceholderRegex().Replace(s, m => codes[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private static string SafeUrl(string encoded)
    {
        var url = WebUtility.HtmlDecode(encoded).Trim();
        if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)) return "#";
        return WebUtility.HtmlEncode(url);
    }

    // ───────────────────────────── HTML → Markdown ─────────────────────────────

    public static string FromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var doc = new HtmlParser().ParseDocument($"<body>{html}</body>");
        var sb = new StringBuilder();
        foreach (var node in doc.Body!.ChildNodes) Block(node, sb, 0);
        return MultiBlankRegex().Replace(sb.ToString(), "\n\n").Trim() + "\n";
    }

    private static void Block(INode node, StringBuilder sb, int depth)
    {
        if (node is IText text)
        {
            var t = text.Text.Trim();
            if (t.Length > 0) sb.Append(Escape(t)).Append("\n\n");
            return;
        }
        if (node is not IElement el) return;
        switch (el.TagName.ToLowerInvariant())
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                sb.Append(new string('#', el.TagName[1] - '0')).Append(' ').Append(InlineMd(el).Trim()).Append("\n\n");
                break;
            case "p" or "div" or "section" or "article" or "figure" or "figcaption" or "details" or "summary":
                if (el.Children.Any(c => c.TagName is "P" or "UL" or "OL" or "TABLE" or "PRE" or "BLOCKQUOTE" or "DIV" or "H1" or "H2" or "H3" or "H4"))
                    foreach (var c in el.ChildNodes) Block(c, sb, depth);
                else
                {
                    var inline = InlineMd(el).Trim();
                    if (inline.Length > 0) sb.Append(inline).Append("\n\n");
                }
                break;
            case "ul" or "ol":
            {
                var n = 1;
                foreach (var li in el.Children.Where(c => c.TagName == "LI"))
                {
                    var marker = el.TagName == "OL" ? $"{n++}. " : "- ";
                    sb.Append(new string(' ', depth * 2)).Append(marker).Append(InlineMd(li, skipLists: true).Trim()).Append('\n');
                    foreach (var nested in li.Children.Where(c => c.TagName is "UL" or "OL")) Block(nested, sb, depth + 1);
                }
                if (depth == 0) sb.Append('\n');
                break;
            }
            case "pre":
            {
                var code = el.QuerySelector("code");
                var lang = code?.GetAttribute("class") is { } cls && cls.StartsWith("language-") ? cls["language-".Length..] : "";
                sb.Append("```").Append(lang).Append('\n').Append((code ?? el).TextContent.TrimEnd()).Append("\n```\n\n");
                break;
            }
            case "blockquote":
            {
                var inner = new StringBuilder();
                foreach (var c in el.ChildNodes) Block(c, inner, depth);
                foreach (var l in inner.ToString().Trim().Split('\n')) sb.Append("> ").Append(l).Append('\n');
                sb.Append('\n');
                break;
            }
            case "hr":
                sb.Append("---\n\n");
                break;
            case "table":
            {
                var rows = el.QuerySelectorAll("tr").ToList();
                if (rows.Count == 0) break;
                var first = true;
                foreach (var row in rows)
                {
                    var cells = row.Children.Where(c => c.TagName is "TD" or "TH").Select(c => InlineMd(c).Trim().Replace("|", "\\|")).ToList();
                    sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                    if (first)
                    {
                        sb.Append("| ").Append(string.Join(" | ", cells.Select(_ => "---"))).Append(" |\n");
                        first = false;
                    }
                }
                sb.Append('\n');
                break;
            }
            case "iframe":
                if (el.GetAttribute("src") is { Length: > 0 } src) sb.Append($"[Video]({src})\n\n");
                break;
            case "img" or "a" or "strong" or "b" or "em" or "i" or "code" or "span" or "br":
            {
                var inline = InlineMd(el).Trim();
                if (inline.Length > 0) sb.Append(inline).Append("\n\n");
                break;
            }
            default:
                foreach (var c in el.ChildNodes) Block(c, sb, depth);
                break;
        }
    }

    private static string InlineMd(IElement el, bool skipLists = false)
    {
        var sb = new StringBuilder();
        foreach (var node in el.ChildNodes)
        {
            if (node is IText t) { sb.Append(Escape(WhitespaceRegex().Replace(t.Text, " "))); continue; }
            if (node is not IElement c) continue;
            switch (c.TagName.ToLowerInvariant())
            {
                case "strong" or "b": sb.Append("**").Append(InlineMd(c).Trim()).Append("**"); break;
                case "em" or "i": sb.Append('*').Append(InlineMd(c).Trim()).Append('*'); break;
                case "code": sb.Append('`').Append(c.TextContent).Append('`'); break;
                case "a": sb.Append('[').Append(InlineMd(c).Trim()).Append("](").Append(c.GetAttribute("href") ?? "").Append(')'); break;
                case "img": sb.Append("![").Append(c.GetAttribute("alt") ?? "").Append("](").Append(c.GetAttribute("src") ?? "").Append(')'); break;
                case "br": sb.Append("  \n"); break;
                case "ul" or "ol" when skipLists: break;
                default: sb.Append(InlineMd(c, skipLists)); break;
            }
        }
        return sb.ToString();
    }

    private static string Escape(string text) => MdSpecialRegex().Replace(text, "\\$1");

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")] private static partial Regex HrRegex();
    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")] private static partial Regex TableSeparatorRegex();
    [GeneratedRegex(@"^\s*[-*+]\s+")] private static partial Regex BulletRegex();
    [GeneratedRegex(@"^\s*\d+[.)]\s+")] private static partial Regex OrderedRegex();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex CodeSpanRegex();
    [GeneratedRegex(@"@@CODE(\d+)@@")] private static partial Regex CodePlaceholderRegex();
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s]+)\)")] private static partial Regex ImageRegex();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")] private static partial Regex LinkRegex();
    [GeneratedRegex(@"(\*\*|__)(.+?)\1")] private static partial Regex BoldRegex();
    [GeneratedRegex(@"(?<![\w*])(\*|_)(?!\s)(.+?)(?<!\s)\1(?![\w*])")] private static partial Regex ItalicRegex();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex MultiBlankRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();
    [GeneratedRegex(@"([\\`*_\[\]])")] private static partial Regex MdSpecialRegex();
}
