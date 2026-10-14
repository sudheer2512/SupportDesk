using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Application.Communication;
using SupportDesk.Contracts.Communication;
using SupportDesk.Application.Automation;
using SupportDesk.Domain.Automation;
using SupportDesk.IntegrationTests.Infrastructure;
using SupportDesk.Persistence;
using static SupportDesk.IntegrationTests.Automation.AutomationApi;
using static SupportDesk.IntegrationTests.Security.SecurityTestKit;

namespace SupportDesk.IntegrationTests.Security;

/// <summary>
/// Signature/secret verification failures on every anonymous inbound webhook: missing or wrong signatures, bodies
/// tampered after signing, a secret belonging to another rule/tenant and unknown keys — all rejected with nothing stored.
/// (Valid deliveries are covered by the module tests.)
/// </summary>
[Collection("api")]
public sealed class InboundWebhookSecurityTests(DeskApiFactory factory)
{
    private static async Task<ChannelAccountDto> ChannelAsync(HttpClient admin, CreateChannelAccountRequest request)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/channels", request, DeskApiFactory.Json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChannelAccountDto>(DeskApiFactory.Json))!;
    }

    private static HttpRequestMessage Post(string url, byte[] body, string contentType, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    private Task<int> MessagesAsync(Guid tenantId) =>
        factory.InTenantAsync(tenantId, sp => sp.GetRequiredService<DeskDbContext>().Messages.CountAsync());

    [Fact]
    public async Task WhatsApp_webhook_rejects_missing_signatures_and_tampered_bodies()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var channel = await ChannelAsync(admin, new CreateChannelAccountRequest
        {
            Type = "WhatsApp", Provider = ChannelProviders.MetaCloud, Name = "WhatsApp", Address = "+91 80 5555 0101",
            Settings = JsonSerializer.SerializeToElement(new { phoneNumberId = "PNID-SEC", verifyToken = "vt-sec" }),
            Secrets = new() { ["appSecret"] = "wa-sec-secret", ["accessToken"] = "token" }
        });
        var anonymous = factory.CreateClient();
        string Payload(string text) => $$$"""
            {"object":"whatsapp_business_account","entry":[{"id":"WABA","changes":[{"field":"messages","value":{"messaging_product":"whatsapp",
            "metadata":{"display_phone_number":"918055550101","phone_number_id":"PNID-SEC"},
            "contacts":[{"profile":{"name":"Eve"},"wa_id":"919800000001"}],
            "messages":[{"from":"919800000001","id":"wamid.{{{Guid.NewGuid():N}}}","timestamp":"{{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}","type":"text","text":{"body":"{{{text}}}"}}]}}]}]}
            """;
        var original = Encoding.UTF8.GetBytes(Payload("Please refund order 1"));
        var tampered = Encoding.UTF8.GetBytes(Payload("Please refund order 9999"));
        var signature = "sha256=" + WebhookSignatures.HmacSha256Hex("wa-sec-secret", original);

        foreach (var (body, headers) in new (byte[], (string, string)[])[]
                 {
                     (original, []),                                                   // no signature at all
                     (original, [("X-Hub-Signature-256", "")]),                         // empty
                     (original, [("X-Hub-Signature-256", "sha256=")]),                  // prefix only
                     (original, [("X-Hub-Signature-256", signature.Replace("sha256=", "sha1="))]),
                     (original, [("X-Hub-Signature-256", signature[..^2] + "00")]),     // one byte off
                     (tampered, [("X-Hub-Signature-256", signature)])                   // body changed after signing
                 })
        {
            var r = await anonymous.SendAsync(Post(channel.WebhookPath!, body, "application/json", headers));
            Assert.True(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{string.Join(",", headers.Select(h => h.Item2))} → {(int)r.StatusCode}");
        }
        Assert.Equal(0, await MessagesAsync(t.Id));

        // Unknown webhook keys and providers reveal nothing.
        Assert.True((await anonymous.SendAsync(Post("/api/v1/channels/webhooks/metacloud/not-a-real-key", original, "application/json",
            ("X-Hub-Signature-256", signature)))).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Twilio_and_telephony_webhooks_require_valid_signatures()
    {
        var t = await factory.CreateTenantAsync();
        var admin = await t.AdminClientAsync();
        var sms = await ChannelAsync(admin, new CreateChannelAccountRequest
        {
            Type = "Sms", Provider = ChannelProviders.Twilio, Name = "SMS", Address = "+15005550006",
            Settings = JsonSerializer.SerializeToElement(new { accountSid = "AC-SEC" }), Secrets = new() { ["authToken"] = "twilio-sec-token" }
        });
        var voice = await ChannelAsync(admin, new CreateChannelAccountRequest
        {
            Type = "Voice", Provider = ChannelProviders.GenericVoice, Name = "Phone", Address = "+918040001234",
            Settings = JsonSerializer.SerializeToElement(new { }), Secrets = new() { ["webhookSecret"] = "voice-sec-secret" }
        });
        var anonymous = factory.CreateClient();

        var form = new Dictionary<string, string> { ["MessageSid"] = $"SM{Guid.NewGuid():N}", ["From"] = "+919810099999", ["To"] = "+15005550006", ["Body"] = "Hi", ["NumMedia"] = "0" };
        var formBody = Encoding.UTF8.GetBytes(string.Join("&", form.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")));
        var wrongUrl = WebhookSignatures.TwilioSignature("twilio-sec-token", "https://evil.example" + sms.WebhookPath, form);
        var wrongToken = WebhookSignatures.TwilioSignature("not-the-token", "http://localhost" + sms.WebhookPath, form);
        foreach (var headers in new (string, string)[][] { [], [("X-Twilio-Signature", "bad")], [("X-Twilio-Signature", wrongUrl)], [("X-Twilio-Signature", wrongToken)] })
        {
            var r = await anonymous.SendAsync(Post(sms.WebhookPath!, formBody, "application/x-www-form-urlencoded", headers));
            Assert.True(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"twilio {headers.FirstOrDefault().Item2} → {(int)r.StatusCode}");
        }

        var call = Encoding.UTF8.GetBytes("""{"callId":"sec-1","event":"missed","from":"+919810088888"}""");
        var other = Encoding.UTF8.GetBytes("""{"callId":"sec-1","event":"voicemail","from":"+919810088888","recordingUrl":"https://evil.example/x.mp3"}""");
        foreach (var (body, headers) in new (byte[], (string, string)[])[]
                 {
                     (call, []),
                     (call, [("X-Signature", WebhookSignatures.HmacSha256Hex("wrong", call))]),
                     (other, [("X-Signature", WebhookSignatures.HmacSha256Hex("voice-sec-secret", call))])
                 })
        {
            var r = await anonymous.SendAsync(Post(voice.WebhookPath!, body, "application/json", headers));
            Assert.True(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"voice → {(int)r.StatusCode}");
        }
        Assert.Equal(0, await MessagesAsync(t.Id));
        Assert.Equal(0, await factory.InTenantAsync(t.Id, sp => sp.GetRequiredService<DeskDbContext>().CallRecords.CountAsync()));
    }

    [Fact]
    public async Task Automation_webhooks_reject_missing_foreign_and_unknown_secrets()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var adminA = await a.AdminClientAsync();
        var adminB = await b.AdminClientAsync();
        var ticket = await CreateTicketAsync(adminA, "Webhook target");
        var definition = new
        {
            actions = new[] { Act(ActionTypes.AddNote, new { bodyHtml = "<p>Webhook ran</p>" }) }
        };
        var ruleA = await CreateRuleAsync(adminA, "Inbound A", RuleTriggers.Webhook, definition);
        var ruleB = await CreateRuleAsync(adminB, "Inbound B", RuleTriggers.Webhook, definition);
        var anonymous = factory.CreateClient();
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ticketNumber = ticket.Number }));

        foreach (var (url, headers) in new (string, (string, string)[])[]
                 {
                     (ruleA.Rule.WebhookUrl!, []),                                          // no secret
                     (ruleA.Rule.WebhookUrl!, [("X-Webhook-Secret", "")]),
                     (ruleA.Rule.WebhookUrl!, [("X-Webhook-Secret", ruleB.WebhookSecret!)]), // another tenant's valid secret
                     (ruleA.Rule.WebhookUrl!, [("X-Webhook-Secret", ruleA.WebhookSecret! + " ")]),
                     (ruleA.Rule.WebhookUrl!, [("X-Webhook-Secret", ruleA.WebhookSecret!.ToUpperInvariant() == ruleA.WebhookSecret ? "x" : ruleA.WebhookSecret!.ToUpperInvariant())])
                 })
        {
            var r = await anonymous.SendAsync(Post(url, payload, "application/json", headers));
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
        // Unknown and over-long keys are simply not found.
        foreach (var key in new[] { "does-not-exist", new string('k', 300), "../../admin" })
        {
            var r = await anonymous.SendAsync(Post($"/api/v1/automation/webhooks/{Uri.EscapeDataString(key)}", payload, "application/json",
                ("X-Webhook-Secret", ruleA.WebhookSecret!)));
            Assert.True(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{key[..Math.Min(20, key.Length)]} → {(int)r.StatusCode}");
        }

        var notes = await factory.InTenantAsync(a.Id, sp => sp.GetRequiredService<DeskDbContext>().TicketThreads.AsNoTracking()
            .CountAsync(x => x.TicketId == ticket.Id && x.BodyHtml != null && x.BodyHtml.Contains("Webhook ran")));
        Assert.Equal(0, notes);
    }
}
