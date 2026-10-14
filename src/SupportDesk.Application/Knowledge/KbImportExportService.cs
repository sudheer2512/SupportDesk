using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Common;
using SupportDesk.Application.Platform;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Knowledge;
using SupportDesk.Domain;
using SupportDesk.Domain.Knowledge;
using SupportDesk.Domain.Rules;
using SupportDesk.Persistence;
using SupportDesk.Persistence.Context;

namespace SupportDesk.Application.Knowledge;

/// <summary>Portable article representation used by import and export (JSON, and the manifest of zip exports).</summary>
public sealed record KbExportArticle(
    string Title,
    string Slug,
    string? Summary,
    string BodyHtml,
    string? Keywords,
    string ArticleType,
    string Visibility,
    string? Category,
    IReadOnlyList<string>? Tags,
    string? Locale,
    string? Status,
    IReadOnlyList<KbExportTranslation>? Translations);

public sealed record KbExportTranslation(string Locale, string Title, string? Summary, string BodyHtml, string Status);

public sealed record KbExportDocument(string Format, int Version, DateTime ExportedAt, IReadOnlyList<KbExportCategory> Categories, IReadOnlyList<KbExportArticle> Articles);

public sealed record KbExportCategory(string Name, string Slug, string? Parent, string? Description, string Visibility);

/// <summary>
/// Knowledge base import/export. Export: JSON document, or a zip with one Markdown and one HTML file per article plus
/// manifest.json. Import accepts the JSON document (or a plain array of articles), a zip (manifest or .md/.html files
/// with optional front matter) or a single .md/.html file. Every imported article is validated, sanitised and created
/// as a Draft (nothing is published by an import); categories are created by name when missing.
/// </summary>
public sealed class KbImportExportService(DeskDbContext db, RequestContext ctx, ArticleService articles, IAuditWriter audit)
{
    public const int MaxArticles = 500;
    public const long MaxBytes = 50L * 1024 * 1024;

    // ───────────────────────────── Export ─────────────────────────────

    public async Task<(byte[] Content, string ContentType, string FileName)> ExportAsync(string format, IReadOnlyList<Guid>? ids, CancellationToken ct = default)
    {
        ctx.RequireAgent();
        ctx.RequireAny(Permissions.KbAuthor, Permissions.KbPublish, Permissions.AdminKnowledge);
        var query = db.Articles.AsNoTracking();
        if (ids is { Count: > 0 }) query = query.Where(a => ids.Contains(a.Id));
        var rows = await query.OrderBy(a => a.Title).Take(5000).ToListAsync(ct);
        var categories = await db.KbCategories.AsNoTracking().ToListAsync(ct);
        var catById = categories.ToDictionary(c => c.Id);
        var versionIds = rows.Select(a => a.PublishedVersionId ?? a.CurrentVersionId).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var versions = await db.ArticleVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var rowIds = rows.Select(r => r.Id).ToList();
        var translations = (await db.ArticleTranslations.AsNoTracking().Where(t => rowIds.Contains(t.ArticleId)).ToListAsync(ct)).ToLookup(t => t.ArticleId);
        var export = rows.Select(a =>
        {
            var v = (a.PublishedVersionId ?? a.CurrentVersionId) is { } vid && versions.TryGetValue(vid, out var ver) ? ver : null;
            return new KbExportArticle(v?.Title ?? a.Title, a.Slug, v?.Summary ?? a.Summary, v?.BodyHtml ?? "", v?.Keywords, a.ArticleType, a.Visibility.ToString(),
                a.CategoryId is { } c && catById.TryGetValue(c, out var cat) ? cat.Name : null, Json.StringList(a.TagsJson), a.PrimaryLocale, a.Status.ToString(),
                translations[a.Id].Select(t => new KbExportTranslation(t.Locale, t.Title, t.Summary, t.BodyHtml, t.Status.ToString())).ToList());
        }).ToList();
        var doc = new KbExportDocument("supportdesk-kb", 1, db.Clock.UtcNow,
            categories.Select(c => new KbExportCategory(c.Name, c.Slug, c.ParentId is { } p && catById.TryGetValue(p, out var parent) ? parent.Name : null,
                c.Description, c.Visibility.ToString())).ToList(), export);
        audit.Write(AuditActions.DataExported, "Knowledge", null, $"Exported {export.Count} knowledge articles ({format})", new { format, count = export.Count });
        await db.SaveChangesAsync(ct);
        var stamp = db.Clock.UtcNow.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(format, "zip", StringComparison.OrdinalIgnoreCase))
            return (JsonSerializer.SerializeToUtf8Bytes(doc, new JsonSerializerOptions(Json.Options) { WriteIndented = true }), "application/json", $"knowledge-base-{stamp}.json");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(zip, "manifest.json", JsonSerializer.Serialize(doc, new JsonSerializerOptions(Json.Options) { WriteIndented = true }), ct);
            foreach (var a in export)
            {
                var front = new StringBuilder("---\n")
                    .Append("title: ").Append(Yaml(a.Title)).Append('\n')
                    .Append("type: ").Append(a.ArticleType).Append('\n')
                    .Append("visibility: ").Append(a.Visibility).Append('\n');
                if (a.Category is not null) front.Append("category: ").Append(Yaml(a.Category)).Append('\n');
                if (a.Tags is { Count: > 0 }) front.Append("tags: [").Append(string.Join(", ", a.Tags)).Append("]\n");
                if (a.Summary is not null) front.Append("summary: ").Append(Yaml(a.Summary)).Append('\n');
                front.Append("locale: ").Append(a.Locale).Append("\n---\n\n");
                await WriteEntryAsync(zip, $"markdown/{a.Slug}.md", front + MarkdownConverter.FromHtml(a.BodyHtml), ct);
                await WriteEntryAsync(zip, $"html/{a.Slug}.html",
                    $"<!doctype html>\n<html lang=\"{a.Locale}\"><head><meta charset=\"utf-8\"><title>{System.Net.WebUtility.HtmlEncode(a.Title)}</title></head>\n<body>\n<h1>{System.Net.WebUtility.HtmlEncode(a.Title)}</h1>\n{a.BodyHtml}\n</body></html>\n", ct);
            }
        }
        return (ms.ToArray(), "application/zip", $"knowledge-base-{stamp}.zip");
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, ct);
    }

    private static string Yaml(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";

    // ───────────────────────────── Import ─────────────────────────────

    public async Task<KbImportResultDto> ImportAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        ctx.Require(Permissions.KbAuthor);
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(content, buffer, ct);
        var bytes = buffer.ToArray();
        var issues = new List<KbImportIssueDto>();
        var items = new List<(string Item, KbExportArticle Article)>();
        var categories = new List<KbExportCategory>();
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".json":
                    ParseJson(Encoding.UTF8.GetString(bytes), fileName, items, categories);
                    break;
                case ".zip":
                    ParseZip(bytes, items, categories, issues);
                    break;
                case ".md" or ".markdown" or ".html" or ".htm":
                    items.Add((fileName, ParseDocumentFile(fileName, Encoding.UTF8.GetString(bytes))));
                    break;
                default:
                    throw new DomainException("unsupported_format", "Import a .json export, a .zip, or a single .md / .html file.");
            }
        }
        catch (JsonException ex)
        {
            throw new DomainException("invalid_import", $"The file is not valid JSON: {ex.Message}");
        }
        catch (InvalidDataException)
        {
            throw new DomainException("invalid_import", "The zip file is damaged.");
        }
        if (items.Count > MaxArticles) throw new DomainException("invalid_import", $"At most {MaxArticles} articles per import.");

        var categoriesCreated = 0;
        var categoryIds = (await db.KbCategories.AsNoTracking().ToListAsync(ct)).GroupBy(c => c.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Id);
        async Task<Guid?> CategoryAsync(string? name, string? parent = null, string? description = null, string? visibility = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var key = name.Trim().ToLowerInvariant();
            if (categoryIds.TryGetValue(key, out var id)) return id;
            var parentId = parent is null ? null : await CategoryAsync(parent);
            var category = new KbCategory
            {
                Name = TextNormalization.Truncate(name.Trim(), 120), Slug = await UniqueCategorySlugAsync(name, ct), ParentId = parentId, Description = description,
                Visibility = KbRules.TryVisibility(visibility, out var v) ? v : KbVisibility.Public
            };
            db.KbCategories.Add(category);
            await db.SaveChangesAsync(ct);
            categoryIds[key] = category.Id;
            categoriesCreated++;
            return category.Id;
        }
        foreach (var c in categories) await CategoryAsync(c.Name, c.Parent, c.Description, c.Visibility);

        var created = new List<Guid>();
        var skipped = 0;
        foreach (var (item, a) in items)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(a.Title)) throw new ValidationException(new Dictionary<string, string> { ["title"] = "A title is required." });
                var visibility = KbRules.TryVisibility(a.Visibility, out var vis) && vis != KbVisibility.Restricted ? vis : KbVisibility.Internal;
                if (KbRules.TryVisibility(a.Visibility, out var asked) && asked == KbVisibility.Restricted)
                    issues.Add(new KbImportIssueDto(item, "warning", "Restricted visibility needs access rules — imported as Internal."));
                var article = await articles.CreateCoreAsync(new SaveArticleRequest
                {
                    Title = a.Title, BodyHtml = a.BodyHtml, Summary = a.Summary, Keywords = a.Keywords, Slug = a.Slug,
                    ArticleType = KbRules.NormalizeArticleType(a.ArticleType) ?? "HowTo", Visibility = visibility.ToString(), Tags = a.Tags,
                    CategoryId = await CategoryAsync(a.Category), PrimaryLocale = KbRules.NormalizeLocale(a.Locale) ?? "en", ChangeNote = $"Imported from {fileName}"
                }, null, ct);
                foreach (var t in a.Translations ?? [])
                {
                    var locale = KbRules.NormalizeLocale(t.Locale);
                    if (locale is null || locale == article.PrimaryLocale || string.IsNullOrWhiteSpace(t.Title)) continue;
                    db.ArticleTranslations.Add(new ArticleTranslation
                    {
                        ArticleId = article.Id, Locale = locale, Title = TextNormalization.Truncate(t.Title, ArticleService.MaxTitle), Summary = t.Summary,
                        BodyHtml = articles.SanitizeForImport(t.BodyHtml), Status = TranslationStatus.Draft, SourceVersionNumber = 1
                    });
                }
                await db.SaveChangesAsync(ct);
                created.Add(article.Id);
            }
            catch (ValidationException ex)
            {
                skipped++;
                issues.Add(new KbImportIssueDto(item, "error", string.Join(" ", ex.Errors.Select(e => $"{e.Key}: {e.Value}"))));
                db.ChangeTracker.Clear();
            }
            catch (DomainException ex)
            {
                skipped++;
                issues.Add(new KbImportIssueDto(item, "error", ex.Message));
                db.ChangeTracker.Clear();
            }
        }
        audit.Write("kb.import", "Knowledge", null, $"Imported {created.Count} knowledge articles from {fileName}", new { created = created.Count, skipped });
        await db.SaveChangesAsync(ct);
        return new KbImportResultDto(created.Count, skipped, categoriesCreated, issues, created);
    }

    private static async Task CopyLimitedAsync(Stream source, Stream target, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxBytes) throw new DomainException("file_too_large", "Import files may be at most 50 MB.", 413);
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static void ParseJson(string json, string source, List<(string, KbExportArticle)> items, List<KbExportCategory> categories)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            var list = doc.RootElement.Deserialize<List<KbExportArticle>>(Json.Options) ?? [];
            items.AddRange(list.Select((a, i) => ($"{source}[{i}] {a.Title}", a)));
            return;
        }
        var parsed = doc.RootElement.Deserialize<KbExportDocument>(Json.Options) ?? throw new JsonException("Empty document.");
        categories.AddRange(parsed.Categories ?? []);
        items.AddRange((parsed.Articles ?? []).Select((a, i) => ($"{source}[{i}] {a.Title}", a)));
    }

    private static void ParseZip(byte[] bytes, List<(string, KbExportArticle)> items, List<KbExportCategory> categories, List<KbImportIssueDto> issues)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 2000) throw new DomainException("invalid_import", "The zip contains too many files.");
        var manifest = zip.GetEntry("manifest.json");
        if (manifest is not null)
        {
            using var reader = new StreamReader(manifest.Open(), Encoding.UTF8);
            ParseJson(reader.ReadToEnd(), "manifest.json", items, categories);
            return;
        }
        foreach (var entry in zip.Entries.Where(e => e.Length > 0 && e.Length < 5 * 1024 * 1024))
        {
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext is not (".md" or ".markdown" or ".html" or ".htm"))
            {
                if (!entry.FullName.EndsWith('/')) issues.Add(new KbImportIssueDto(entry.FullName, "warning", "Skipped: not a Markdown or HTML file."));
                continue;
            }
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            items.Add((entry.FullName, ParseDocumentFile(entry.Name, reader.ReadToEnd())));
        }
    }

    /// <summary>Markdown (optional YAML-like front matter) or HTML (title from &lt;title&gt; / first &lt;h1&gt;).</summary>
    private static KbExportArticle ParseDocumentFile(string fileName, string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = text.Replace("\r\n", "\n");
        if (body.StartsWith("---\n"))
        {
            var end = body.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in body[4..end].Split('\n'))
                {
                    var i = line.IndexOf(':');
                    if (i > 0) meta[line[..i].Trim()] = line[(i + 1)..].Trim().Trim('"');
                }
                body = body[(end + 4)..].TrimStart('\n');
            }
        }
        var isHtml = Path.GetExtension(fileName).ToLowerInvariant() is ".html" or ".htm";
        string title;
        string html;
        if (isHtml)
        {
            var doc = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(body);
            var h1 = doc.QuerySelector("h1");
            title = meta.GetValueOrDefault("title") ?? doc.Title ?? h1?.TextContent ?? Path.GetFileNameWithoutExtension(fileName);
            if (h1 is not null && h1.TextContent.Trim() == title.Trim()) h1.Remove();
            html = doc.Body?.InnerHtml ?? body;
        }
        else
        {
            var lines = body.Split('\n').ToList();
            var headingIndex = lines.FindIndex(l => l.StartsWith("# "));
            title = meta.GetValueOrDefault("title") ?? (headingIndex >= 0 ? lines[headingIndex][2..].Trim() : Path.GetFileNameWithoutExtension(fileName).Replace('-', ' '));
            if (headingIndex >= 0 && lines[headingIndex][2..].Trim() == title.Trim()) lines.RemoveAt(headingIndex);
            html = MarkdownConverter.ToHtml(string.Join('\n', lines));
        }
        var tags = meta.GetValueOrDefault("tags")?.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return new KbExportArticle(title.Trim(), Path.GetFileNameWithoutExtension(fileName), meta.GetValueOrDefault("summary"), html, meta.GetValueOrDefault("keywords"),
            meta.GetValueOrDefault("type") ?? "HowTo", meta.GetValueOrDefault("visibility") ?? "Internal", meta.GetValueOrDefault("category"), tags,
            meta.GetValueOrDefault("locale"), null, null);
    }

    private async Task<string> UniqueCategorySlugAsync(string name, CancellationToken ct)
    {
        var slug = TextNormalization.Slugify(name, 80);
        if (slug.Length == 0) slug = "category";
        var candidate = slug;
        for (var i = 2; await db.KbCategories.AnyAsync(c => c.Slug == candidate, ct); i++) candidate = $"{slug}-{i}";
        return candidate;
    }
}
