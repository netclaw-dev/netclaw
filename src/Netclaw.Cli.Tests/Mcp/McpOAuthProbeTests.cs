// -----------------------------------------------------------------------
// <copyright file="McpOAuthProbeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Netclaw.Cli.Mcp;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Mcp;

public sealed class McpOAuthProbeTests
{
    public static TheoryData<bool, bool, bool, bool> DetectionCases => new()
    {
        { true, true, true, true },     // protected-resource + registration endpoint
        { true, false, true, false },   // protected-resource, no registration
        { false, false, false, false }, // no metadata at all
    };

    [Theory]
    [MemberData(nameof(DetectionCases))]
    public async Task Detect_VariesByMetadata(
        bool withProtectedResource,
        bool withRegistration,
        bool expectedOAuth,
        bool expectedDynamic)
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (withProtectedResource && url.EndsWith("/.well-known/oauth-protected-resource/mcp", StringComparison.Ordinal))
            {
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    resource = "https://mcp.example/mcp",
                    authorization_servers = new[] { "https://auth.example" }
                });
            }
            if (withRegistration && url.EndsWith("/.well-known/oauth-authorization-server", StringComparison.Ordinal))
            {
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    issuer = "https://auth.example",
                    registration_endpoint = "https://auth.example/register"
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);

        var result = await McpOAuthProbe.DetectAsync("https://mcp.example/mcp", client, TestContext.Current.CancellationToken);

        if (expectedOAuth)
        {
            Assert.NotNull(result);
            Assert.True(result.OAuthRequired);
            Assert.Equal(expectedDynamic, result.DynamicRegistrationAvailable);
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Theory]
    [InlineData(true)]   // path-suffixed well-known document
    [InlineData(false)]  // origin-level fallback
    public async Task Detect_FindsMetadataAtBothWellKnownLocations(bool pathSuffixed)
    {
        var suffix = pathSuffixed ? "/mcp" : string.Empty;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith($"/.well-known/oauth-protected-resource{suffix}", StringComparison.Ordinal))
            {
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    resource = "https://mcp.example",
                    authorization_servers = new[] { "https://auth.example" }
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);

        var result = await McpOAuthProbe.DetectAsync("https://mcp.example/mcp", client, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result.OAuthRequired);
    }

    [Fact]
    public async Task Detect_UnreachableEndpoint_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("no such host"));
        using var client = new HttpClient(handler);

        var result = await McpOAuthProbe.DetectAsync("https://mcp.example/mcp", client, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }
}
