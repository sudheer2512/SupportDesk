using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.IntegrationTests.Infrastructure;

namespace SupportDesk.IntegrationTests.Core;

/// <summary>
/// Modules are built in parallel and merged: two controllers claiming the same verb + route only fail at request time
/// (AmbiguousMatchException → 500), so the whole endpoint table is checked here.
/// </summary>
[Collection("api")]
public sealed class RouteTableTests(DeskApiFactory factory)
{
    [Fact]
    public void No_two_endpoints_share_a_verb_and_route()
    {
        _ = factory.Server; // builds the host
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        var duplicates = endpoints
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (Key: $"{method} {Normalize(e.RoutePattern)}", Order: e.Order, Name: e.DisplayName ?? "?")))
            .GroupBy(x => (x.Key, x.Order))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Key}: {string.Join(" | ", g.Select(x => x.Name))}")
            .ToList();

        Assert.True(duplicates.Count == 0, "Ambiguous routes:\n" + string.Join("\n", duplicates));
    }

    /// <summary>"api/v1/tickets/{id:guid}" and "api/v1/tickets/{ticketId:guid}" are the same route.</summary>
    private static string Normalize(RoutePattern pattern) =>
        "/" + string.Join("/", pattern.PathSegments.Select(s => string.Concat(s.Parts.Select(p => p switch
        {
            RoutePatternLiteralPart l => l.Content.ToLowerInvariant(),
            RoutePatternParameterPart prm => "{" + (prm.IsCatchAll ? "*" : "") + string.Join(",", prm.ParameterPolicies.Select(pp => pp.Content)) + "}",
            RoutePatternSeparatorPart sep => sep.Content,
            _ => "?"
        }))));
}
