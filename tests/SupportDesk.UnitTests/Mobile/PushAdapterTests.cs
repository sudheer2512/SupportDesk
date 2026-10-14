using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SupportDesk.Domain.Communication;
using SupportDesk.Integrations.Messaging;

namespace SupportDesk.UnitTests.Mobile;

/// <summary>APNs provider token + request shape, and per-platform routing of push sends (no network).</summary>
public sealed class PushAdapterTests
{
    private const string Token = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    [Fact]
    public void Apns_provider_token_is_an_es256_jwt_signed_with_the_p8_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = new ApnsOptions("KEY123ABCD", "TEAM456XYZ", "com.supportdesk.app", key.ExportPkcs8PrivateKeyPem(), Sandbox: true);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

        var jwt = ApnsPushSender.CreateProviderToken(options, now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        using var header = JsonDocument.Parse(FromB64(parts[0]));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("KEY123ABCD", header.RootElement.GetProperty("kid").GetString());
        using var claims = JsonDocument.Parse(FromB64(parts[1]));
        Assert.Equal("TEAM456XYZ", claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal(1_790_000_000, claims.RootElement.GetProperty("iat").GetInt64());

        var signature = FromB64(parts[2]);
        Assert.Equal(64, signature.Length); // raw r‖s, not DER
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task Apns_send_posts_an_http2_alert_and_maps_dead_tokens()
    {
        ApnsPushSender.ResetCache();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var config = ApnsConfig(key);
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, null, ("apns-id", "8F1E2C3D-0000-4000-8000-000000000001")));
        var sender = new ApnsPushSender(new HttpClient(handler), config);
        Assert.True(sender.IsConfigured);

        var ok = await sender.SendAsync(Token, "New reply on #42", "Please update the driver.", new Dictionary<string, string> { ["ticketId"] = "t-1", ["link"] = "/customer/tickets/t-1" }, default);
        Assert.True(ok.Success);
        Assert.Equal("8F1E2C3D-0000-4000-8000-000000000001", ok.ProviderMessageId);

        var req = Assert.Single(handler.Requests);
        Assert.Equal($"https://api.sandbox.push.apple.com/3/device/{Token}", req.Uri);
        Assert.Equal(HttpVersion.Version20, req.Version);
        Assert.Equal("com.supportdesk.app", req.Headers["apns-topic"]);
        Assert.Equal("alert", req.Headers["apns-push-type"]);
        Assert.StartsWith("bearer ey", req.Headers["Authorization"]);
        using var body = JsonDocument.Parse(req.Body);
        Assert.Equal("New reply on #42", body.RootElement.GetProperty("aps").GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("/customer/tickets/t-1", body.RootElement.GetProperty("link").GetString());

        handler.Next = _ => Respond(HttpStatusCode.Gone, "{\"reason\":\"Unregistered\"}");
        var gone = await sender.SendAsync(Token, "t", "b", new Dictionary<string, string>(), default);
        Assert.False(gone.Success);
        Assert.True(gone.DeadToken);

        handler.Next = _ => Respond(HttpStatusCode.BadRequest, "{\"reason\":\"PayloadTooLarge\"}");
        var tooLarge = await sender.SendAsync(Token, "t", "b", new Dictionary<string, string>(), default);
        Assert.True(tooLarge.Permanent);
        Assert.False(tooLarge.DeadToken); // a payload problem must not switch the device off

        handler.Next = _ => Respond(HttpStatusCode.ServiceUnavailable, null);
        var busy = await sender.SendAsync(Token, "t", "b", new Dictionary<string, string>(), default);
        Assert.False(busy.Permanent);

        var notApns = await sender.SendAsync("fcm:APA91b-not-hex", "t", "b", new Dictionary<string, string>(), default);
        Assert.True(notApns.DeadToken);
        Assert.Equal(4, handler.Requests.Count); // the malformed token never reached Apple
    }

    [Fact]
    public void Router_sends_ios_to_apns_android_to_fcm_and_logs_without_providers()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var log = new LogOnlyPushSender(NullLogger<LogOnlyPushSender>.Instance);

        var none = new ConfigurationBuilder().Build();
        var nothing = Router(none, log);
        Assert.False(nothing.IsConfigured);
        Assert.Same(log, nothing.Pick(DevicePlatform.iOS));
        Assert.Same(log, nothing.Pick(DevicePlatform.Android));

        var apnsOnly = Router(ApnsConfig(key), log);
        Assert.IsType<ApnsPushSender>(apnsOnly.Pick(DevicePlatform.iOS));
        Assert.Same(log, apnsOnly.Pick(DevicePlatform.Android));

        var both = new ConfigurationBuilder().AddConfiguration(ApnsConfig(key)).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Push:Fcm:ServiceAccountJson"] = "{\"project_id\":\"sd-test\",\"client_email\":\"push@sd-test.iam.gserviceaccount.com\",\"private_key\":\"x\"}"
        }).Build();
        var router = Router(both, log);
        Assert.True(router.IsConfigured);
        Assert.IsType<ApnsPushSender>(router.Pick(DevicePlatform.iOS));
        Assert.IsType<FcmPushSender>(router.Pick(DevicePlatform.Android));
        Assert.IsType<FcmPushSender>(router.Pick(DevicePlatform.Web));
    }

    private static PushRouter Router(IConfiguration config, LogOnlyPushSender log) =>
        new(new FcmPushSender(new HttpClient(), config), new ApnsPushSender(new HttpClient(), config), log, NullLogger<PushRouter>.Instance);

    private static IConfiguration ApnsConfig(ECDsa key) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Push:Apns:KeyId"] = "KEY123ABCD",
        ["Push:Apns:TeamId"] = "TEAM456XYZ",
        ["Push:Apns:BundleId"] = "com.supportdesk.app",
        ["Push:Apns:PrivateKey"] = key.ExportPkcs8PrivateKeyPem(),
        ["Push:Apns:Environment"] = "sandbox"
    }).Build();

    private static byte[] FromB64(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string? json, params (string Name, string Value)[] headers)
    {
        var res = new HttpResponseMessage(status) { Content = new StringContent(json ?? "") };
        foreach (var (name, value) in headers) res.Headers.TryAddWithoutValidation(name, value);
        return res;
    }

    private sealed record Captured(string Uri, Version Version, IReadOnlyDictionary<string, string> Headers, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Next { get; set; } = respond;
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            Requests.Add(new Captured(request.RequestUri!.ToString(), request.Version, headers, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return Next(request);
        }
    }
}
