using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SupportDesk.IntegrationTests.Infrastructure;

namespace SupportDesk.IntegrationTests.Core;

/// <summary>
/// X-Forwarded-For decides the client IP used by the agent IP allow-list and per-IP rate limits, so only trusted proxies
/// may set it: loopback by default, configured proxies/networks otherwise, everyone only with an explicit TrustAll.
/// </summary>
[Collection("api")]
public sealed class ForwardedHeadersTests(DeskApiFactory factory)
{
    private static readonly IPAddress Internet = IPAddress.Parse("203.0.113.5");

    private ForwardedHeadersOptions OptionsWith(params (string Key, string Value)[] settings)
    {
        using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ForwardedHeaders:TrustAll", "false");
            foreach (var (key, value) in settings) b.UseSetting(key, value);
        });
        return host.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }

    private static bool Trusted(ForwardedHeadersOptions o, IPAddress ip) =>
        (o.KnownProxies.Count == 0 && o.KnownIPNetworks.Count == 0) || o.KnownProxies.Contains(ip) || o.KnownIPNetworks.Any(n => n.Contains(ip));

    [Fact]
    public void Only_loopback_proxies_are_trusted_by_default()
    {
        var o = OptionsWith();
        Assert.True(Trusted(o, IPAddress.Loopback));
        Assert.False(Trusted(o, Internet));
    }

    [Fact]
    public void Configured_networks_replace_the_default()
    {
        var o = OptionsWith(("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/8"));
        Assert.True(Trusted(o, IPAddress.Parse("10.20.30.40")));
        Assert.False(Trusted(o, Internet));
        Assert.False(Trusted(o, IPAddress.Loopback));
    }

    [Fact]
    public void Trust_all_is_an_explicit_opt_in()
    {
        var o = OptionsWith(("ForwardedHeaders:TrustAll", "true"));
        Assert.True(Trusted(o, Internet));
    }
}
