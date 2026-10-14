using System.Globalization;
using System.Net;
using System.Text;
using ClosedXML.Excel;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using SupportDesk.Contracts.Analytics;
using SupportDesk.Domain.Analytics;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Application.Analytics;

/// <summary>Display formatting shared by PDF, e-mail and CSV text cells.</summary>
public static class ReportFormatting
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Value(double? v, string unitOrType)
    {
        if (v is not { } x) return "—";
        return unitOrType switch
        {
            "percent" => x.ToString("0.0", C) + " %",
            "hours" => Hours(x),
            "minutes" => x.ToString("0.#", C) + " min",
            "score" => x.ToString("0.00", C),
            _ => Math.Abs(x % 1) < 1e-9 ? x.ToString("#,0", C) : x.ToString("#,0.##", C)
        };
    }

    /// <summary>3.25 → "3h 15m"; 0.5 → "30m"; 49.5 → "49h 30m".</summary>
    public static string Hours(double hours)
    {
        var total = (long)Math.Round(hours * 60, MidpointRounding.AwayFromZero);
        var h = total / 60;
        var m = total % 60;
        return h == 0 ? $"{m}m" : m == 0 ? $"{h}h" : $"{h}h {m:00}m";
    }

    public static string Cell(object? value, string type, TimeZoneInfo tz) => value switch
    {
        null => "",
        double d => Value(d, type),
        int or long or decimal => Value(Convert.ToDouble(value, C), type),
        DateTime dt => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc), tz).ToString(type == "date" ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", C),
        bool b => b ? "Yes" : "No",
        _ => Convert.ToString(value, C) ?? ""
    };

    public static string PeriodText(ReportResultDto r) => $"{r.Period.Label} ({r.Period.FromDay} – {r.Period.ToDay})";
}

/// <summary>
/// Renders a <see cref="ReportResultDto"/> as CSV, Excel (ClosedXML) or PDF (PDFsharp/MigraDoc): a summary (period,
/// filters, KPIs with comparison) followed by every report table. Charts are represented by their tables.
/// </summary>
public static class ReportRenderer
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static (byte[] Content, string ContentType, string Extension) Render(ReportResultDto report, ReportFormat format) => format switch
    {
        ReportFormat.Csv => (Csv(report), "text/csv", ".csv"),
        ReportFormat.Xlsx => (Xlsx(report), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
        _ => (Pdf(report), "application/pdf", ".pdf")
    };

    public static int RowCount(ReportResultDto r) => r.Tables.Sum(t => t.Rows.Count);

    public static string FileName(ReportResultDto r, string extension)
    {
        var slug = new string(r.Name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return $"{slug.Trim('-')}-{r.Period.FromDay}-to-{r.Period.ToDay}{extension}";
    }

    private static TimeZoneInfo Tz(ReportResultDto r) => WorkingCalendar.ResolveTimeZone(r.TimeZone);

    // ───────────────────────────── CSV ─────────────────────────────

    public static byte[] Csv(ReportResultDto r)
    {
        var tz = Tz(r);
        var sb = new StringBuilder();
        void Line(params string?[] cells) => sb.AppendLine(string.Join(",", cells.Select(Escape)));
        Line("Report", r.Name);
        Line("Period", ReportFormatting.PeriodText(r));
        if (r.ComparePeriod is { } cp) Line("Compared with", $"{cp.Label} ({cp.FromDay} – {cp.ToDay})");
        Line("Filters", r.AppliedFilters.Count == 0 ? "None" : string.Join("; ", r.AppliedFilters));
        Line("Time zone", r.TimeZone);
        Line("Generated (UTC)", r.GeneratedAt.ToString("yyyy-MM-dd HH:mm", C));
        sb.AppendLine();
        Line("Metric", "Value", "Unit", "Compare value", "Change %");
        foreach (var k in r.Kpis) Line(k.Label, Num(k.Value), k.Unit, Num(k.CompareValue), Num(k.DeltaPercent));
        foreach (var t in r.Tables)
        {
            sb.AppendLine();
            Line(t.Title);
            Line(t.Columns.Select(c => c.Label).ToArray());
            foreach (var row in t.Rows)
                Line(t.Columns.Select(c => row.TryGetValue(c.Key, out var v) ? RawCell(v, c.Type, tz) : "").ToArray());
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Num(double? v) => v?.ToString("0.####", C) ?? "";

    private static string RawCell(object? v, string type, TimeZoneInfo tz) => v switch
    {
        null => "",
        double d => d.ToString("0.####", C),
        int or long or decimal => Convert.ToString(v, C) ?? "",
        DateTime => ReportFormatting.Cell(v, type, tz),
        _ => Convert.ToString(v, C) ?? ""
    };

    /// <summary>Quotes cells and neutralises spreadsheet formula injection (=, +, -, @ at the start of text).</summary>
    public static string Escape(string? value)
    {
        var s = value ?? "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !double.TryParse(s, NumberStyles.Float, C, out _)) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 || s.StartsWith(' ') || s.EndsWith(' ') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // ───────────────────────────── Excel ─────────────────────────────

    public static byte[] Xlsx(ReportResultDto r)
    {
        var tz = Tz(r);
        using var wb = new XLWorkbook();
        var summary = wb.Worksheets.Add("Summary");
        summary.Cell(1, 1).Value = r.Name;
        summary.Cell(1, 1).Style.Font.Bold = true;
        summary.Cell(1, 1).Style.Font.FontSize = 14;
        summary.Cell(2, 1).Value = "Period";
        summary.Cell(2, 2).Value = ReportFormatting.PeriodText(r);
        var row = 3;
        if (r.ComparePeriod is { } cp)
        {
            summary.Cell(row, 1).Value = "Compared with";
            summary.Cell(row++, 2).Value = $"{cp.Label} ({cp.FromDay} – {cp.ToDay})";
        }
        summary.Cell(row, 1).Value = "Filters";
        summary.Cell(row++, 2).Value = r.AppliedFilters.Count == 0 ? "None" : string.Join("; ", r.AppliedFilters);
        summary.Cell(row, 1).Value = "Time zone";
        summary.Cell(row++, 2).Value = r.TimeZone;
        summary.Cell(row, 1).Value = "Generated (UTC)";
        summary.Cell(row++, 2).Value = r.GeneratedAt;
        row++;
        string[] header = ["Metric", "Value", "Unit", "Compare value", "Change %"];
        for (var i = 0; i < header.Length; i++) summary.Cell(row, i + 1).Value = header[i];
        summary.Range(row, 1, row, header.Length).Style.Font.Bold = true;
        foreach (var k in r.Kpis)
        {
            row++;
            summary.Cell(row, 1).Value = k.Label;
            SetNumber(summary.Cell(row, 2), k.Value, k.Unit);
            summary.Cell(row, 3).Value = k.Unit;
            SetNumber(summary.Cell(row, 4), k.CompareValue, k.Unit);
            SetNumber(summary.Cell(row, 5), k.DeltaPercent, "number");
        }
        summary.Columns().AdjustToContents(1, row, 10, 60);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Summary" };
        foreach (var t in r.Tables)
        {
            var name = SheetName(t.Title, names);
            var ws = wb.Worksheets.Add(name);
            for (var c = 0; c < t.Columns.Count; c++) ws.Cell(1, c + 1).Value = t.Columns[c].Label;
            ws.Range(1, 1, 1, Math.Max(1, t.Columns.Count)).Style.Font.Bold = true;
            var rr = 1;
            foreach (var data in t.Rows)
            {
                rr++;
                for (var c = 0; c < t.Columns.Count; c++)
                {
                    var col = t.Columns[c];
                    var cell = ws.Cell(rr, c + 1);
                    data.TryGetValue(col.Key, out var v);
                    switch (v)
                    {
                        case null: break;
                        case double d: SetNumber(cell, d, col.Type); break;
                        case int or long or decimal: SetNumber(cell, Convert.ToDouble(v, C), col.Type); break;
                        case DateTime dt:
                            cell.Value = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc), tz);
                            cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                            break;
                        default: cell.Value = Convert.ToString(v, C); break;
                    }
                }
                if (data.ContainsKey("_total")) ws.Range(rr, 1, rr, t.Columns.Count).Style.Font.Bold = true;
            }
            if (t.Columns.Count > 0)
            {
                ws.SheetView.FreezeRows(1);
                ws.Range(1, 1, Math.Max(1, rr), t.Columns.Count).SetAutoFilter();
                ws.Columns().AdjustToContents(1, Math.Min(rr, 200), 8, 60);
            }
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void SetNumber(IXLCell cell, double? v, string unit)
    {
        if (v is not { } x) return;
        cell.Value = x;
        cell.Style.NumberFormat.Format = unit switch { "percent" => "0.0", "hours" => "0.00", "score" => "0.00", "minutes" => "0.0", _ => "#,##0.##" };
    }

    private static string SheetName(string title, HashSet<string> used)
    {
        var clean = new string(title.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()).Trim();
        if (clean.Length == 0) clean = "Table";
        if (clean.Length > 28) clean = clean[..28].Trim();
        var name = clean;
        for (var i = 2; !used.Add(name); i++) name = $"{clean[..Math.Min(clean.Length, 26)]} {i}";
        return name;
    }

    // ───────────────────────────── PDF ─────────────────────────────

    private const int MaxPdfRowsPerTable = 500;

    public static byte[] Pdf(ReportResultDto r)
    {
        PdfFonts.Ensure();
        var tz = Tz(r);
        var doc = new Document();
        doc.Info.Title = r.Name;
        doc.Info.Author = "SupportDesk";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = PdfFonts.Family;
        normal.Font.Size = 9;
        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7;
        footer.AddText($"{r.Name} · generated {r.GeneratedAt:yyyy-MM-dd HH:mm} UTC · page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();

        var title = section.AddParagraph(r.Name);
        title.Format.Font.Size = 16;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = Unit.FromPoint(4);
        var meta = section.AddParagraph($"Period: {ReportFormatting.PeriodText(r)}" + (r.ComparePeriod is { } cp ? $" · compared with {cp.Label} ({cp.FromDay} – {cp.ToDay})" : ""));
        meta.Format.Font.Size = 9;
        var filters = section.AddParagraph($"Filters: {(r.AppliedFilters.Count == 0 ? "none" : string.Join("; ", r.AppliedFilters))} · time zone {r.TimeZone}");
        filters.Format.Font.Size = 8;
        filters.Format.SpaceAfter = Unit.FromPoint(10);

        if (r.Kpis.Count > 0)
        {
            Heading(section, "Key figures");
            var kt = NewTable(section, [7, 4, 4, 3]);
            HeaderRow(kt, ["Metric", "Value", "Compare", "Change"]);
            foreach (var k in r.Kpis)
            {
                var row = kt.AddRow();
                row.Cells[0].AddParagraph(k.Label);
                row.Cells[1].AddParagraph(ReportFormatting.Value(k.Value, k.Unit));
                row.Cells[2].AddParagraph(ReportFormatting.Value(k.CompareValue, k.Unit));
                row.Cells[3].AddParagraph(k.DeltaPercent is { } d ? (d > 0 ? "+" : "") + d.ToString("0.0", C) + " %" : "—");
            }
        }

        foreach (var t in r.Tables.Where(t => t.Columns.Count > 0))
        {
            Heading(section, t.Title);
            if (t.Rows.Count == 0)
            {
                section.AddParagraph("No data for this period.").Format.Font.Italic = true;
                continue;
            }
            var cols = t.Columns.Take(12).ToList();
            var weights = cols.Select(c => c.Type is "text" ? (c.Key is "subject" or "label" ? 3.0 : 2.0) : c.Type == "datetime" ? 2.0 : 1.4).ToArray();
            var table = NewTable(section, weights);
            HeaderRow(table, cols.Select(c => c.Label).ToArray());
            foreach (var data in t.Rows.Take(MaxPdfRowsPerTable))
            {
                var row = table.AddRow();
                if (data.ContainsKey("_total")) row.Format.Font.Bold = true;
                for (var c = 0; c < cols.Count; c++)
                {
                    data.TryGetValue(cols[c].Key, out var v);
                    var text = ReportFormatting.Cell(v, cols[c].Type, tz);
                    row.Cells[c].AddParagraph(text.Length > 120 ? text[..117] + "…" : text);
                    if (cols[c].Type is not ("text" or "ticket")) row.Cells[c].Format.Alignment = ParagraphAlignment.Right;
                }
            }
            if (t.Rows.Count > MaxPdfRowsPerTable)
                section.AddParagraph($"{t.Rows.Count - MaxPdfRowsPerTable} more rows are available in the CSV or Excel export.").Format.Font.Italic = true;
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static void Heading(Section s, string text)
    {
        var p = s.AddParagraph(text);
        p.Format.Font.Size = 11;
        p.Format.Font.Bold = true;
        p.Format.SpaceBefore = Unit.FromPoint(10);
        p.Format.SpaceAfter = Unit.FromPoint(4);
        p.Format.KeepWithNext = true;
    }

    private static Table NewTable(Section s, double[] weights)
    {
        var table = s.AddTable();
        table.Borders.Width = 0.25;
        table.Borders.Color = Colors.LightGray;
        table.Format.Font.Size = 7.5;
        var available = 26.7 - 3.0; // A4 landscape width minus margins (cm)
        var total = weights.Sum();
        foreach (var w in weights) table.AddColumn(Unit.FromCentimeter(available * w / total));
        return table;
    }

    private static void HeaderRow(Table table, string[] labels)
    {
        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Format.Font.Bold = true;
        header.Shading.Color = Colors.WhiteSmoke;
        for (var i = 0; i < labels.Length && i < table.Columns.Count; i++) header.Cells[i].AddParagraph(labels[i]);
    }

    // ───────────────────────────── E-mail ─────────────────────────────

    public static string EmailHtml(ReportResultDto r, string scheduleName)
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2937\">");
        sb.Append($"<h2 style=\"margin:0 0 4px\">{H(r.Name)}</h2>");
        sb.Append($"<p style=\"margin:0 0 12px;color:#4b5563\">{H(ReportFormatting.PeriodText(r))} · scheduled report “{H(scheduleName)}”</p>");
        if (r.Kpis.Count > 0)
        {
            sb.Append("<table style=\"border-collapse:collapse\">");
            foreach (var k in r.Kpis)
                sb.Append($"<tr><td style=\"padding:4px 16px 4px 0;color:#4b5563\">{H(k.Label)}</td><td style=\"padding:4px 0;font-weight:600\">{H(ReportFormatting.Value(k.Value, k.Unit))}</td>" +
                          (k.DeltaPercent is { } d ? $"<td style=\"padding:4px 0 4px 12px;color:#6b7280\">{(d > 0 ? "+" : "")}{d.ToString("0.0", C)} %</td>" : "<td></td>") + "</tr>");
            sb.Append("</table>");
        }
        if (r.AppliedFilters.Count > 0) sb.Append($"<p style=\"color:#6b7280;font-size:12px\">Filters: {H(string.Join("; ", r.AppliedFilters))}</p>");
        sb.Append("<p>The full report is attached.</p></div>");
        return sb.ToString();
    }
}

/// <summary>
/// Font resolver for server-side PDFs: every family maps to the Segoe WP faces embedded in PDFsharp, so rendering does
/// not depend on fonts installed on the host (Windows, Linux containers alike).
/// </summary>
public sealed class PdfFonts : IFontResolver
{
    public const string Family = "Segoe WP";
    private static readonly object Gate = new();
    private static bool _ready;

    public static void Ensure()
    {
        if (_ready) return;
        lock (Gate)
        {
            if (_ready) return;
            if (GlobalFontSettings.FontResolver is null)
            {
                try { GlobalFontSettings.FontResolver = new PdfFonts(); }
                catch (InvalidOperationException) { /* already configured by the host */ }
            }
            _ready = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? "SegoeWP-Bold" : "SegoeWP", false, isItalic);

    public byte[]? GetFont(string faceName) => faceName == "SegoeWP-Bold"
        ? PdfSharp.WPFonts.FontDataHelper.SegoeWPBold
        : PdfSharp.WPFonts.FontDataHelper.SegoeWP;
}
