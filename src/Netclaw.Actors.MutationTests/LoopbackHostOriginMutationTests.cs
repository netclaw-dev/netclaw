// -----------------------------------------------------------------------
// <copyright file="LoopbackHostOriginMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class LoopbackHostOriginMutationTests
{
    [Theory]
    [InlineData("localhost:5199")]
    [InlineData("LOCALHOST:5199")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:5199")]
    [InlineData("[::1]:5199")]
    [InlineData("127.0.0.2:5199")]
    [InlineData("127.255.255.254")]
    [InlineData("localhost:1")]
    [InlineData("localhost:65535")]
    public void Loopback_host_name_without_an_origin_is_admitted(string host)
    {
        Assert.Null(LoopbackHostOriginPolicy.FindRefusal(new HostString(host), StringValues.Empty));
    }

    [Theory]
    [InlineData("rebind.example:5199")]
    [InlineData("localhost.evil.example")]
    [InlineData("127.0.0.1.evil.example:5199")]
    [InlineData("localhost:abc")]
    [InlineData("localhost:")]
    [InlineData("localhost:1234567")]
    [InlineData("localhost:80a")]
    [InlineData("localhost:5199:5199")]
    [InlineData("127.1:5199")]
    [InlineData("0x7f.1:5199")]
    [InlineData("2130706433:5199")]
    [InlineData("0.0.0.0:5199")]
    [InlineData("[::ffff:127.0.0.1]:5199")]
    [InlineData("[127.0.0.1]:5199")]
    [InlineData("[localhost]:5199")]
    [InlineData("::1")]
    [InlineData("[::1")]
    [InlineData("::1]")]
    [InlineData("[::1x")]
    [InlineData("x::1]")]
    [InlineData("[127.0.0.1")]
    [InlineData("127.0.0.1]")]
    [InlineData("localhost]")]
    [InlineData(" localhost")]
    [InlineData("")]
    public void Foreign_host_name_is_refused_even_with_a_matching_origin(string host)
    {
        var origin = new StringValues("http://" + host);

        var reason = LoopbackHostOriginPolicy.FindRefusal(new HostString(host), origin);

        Assert.Contains("Host", reason);
    }

    [Theory]
    [InlineData("localhost:5199", "http://localhost:5199")]
    [InlineData("LOCALHOST:5199", "http://localhost:5199")]
    public void Origin_equal_to_http_scheme_plus_host_is_admitted(string host, string origin)
    {
        Assert.Null(LoopbackHostOriginPolicy.FindRefusal(new HostString(host), new StringValues(origin)));
    }

    [Theory]
    [InlineData("127.0.0.1:5199", "http://evil.example")]
    [InlineData("localhost:5199", "null")]
    [InlineData("localhost:5199", "http://localhost:3000")]
    [InlineData("localhost:5199", "https://localhost:5199")]
    [InlineData("localhost:5199", "")]
    public void Origin_that_differs_from_http_scheme_plus_host_is_refused(string host, string origin)
    {
        var reason = LoopbackHostOriginPolicy.FindRefusal(new HostString(host), new StringValues(origin));

        Assert.Contains("Origin", reason);
    }

    [Fact]
    public void Two_origin_headers_are_refused()
    {
        var origins = new StringValues(["http://localhost:5199", "http://localhost:5199"]);

        var reason = LoopbackHostOriginPolicy.FindRefusal(new HostString("localhost:5199"), origins);

        Assert.Contains("Origin", reason);
    }

    private static (IAuthenticationService Auth, DefaultHttpContext Context) BuildRequest(string host, string? origin)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DaemonConfig());
        services
            .AddAuthentication(LoopbackAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LoopbackAuthenticationHandler>(
                LoopbackAuthenticationHandler.SchemeName, _ => { });
        var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers.Host = host;
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        return (provider.GetRequiredService<IAuthenticationService>(), context);
    }

    [Theory]
    [InlineData("rebind.example:5199", null)]
    [InlineData("localhost:5199", "http://evil.example")]
    public async Task Handler_fails_authentication_when_the_policy_refuses(string host, string? origin)
    {
        var (auth, context) = BuildRequest(host, origin);

        var result = await auth.AuthenticateAsync(context, LoopbackAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Theory]
    [InlineData("localhost:5199", null)]
    [InlineData("127.0.0.2:5199", "http://127.0.0.2:5199")]
    public async Task Handler_grants_operator_identity_when_the_policy_admits(string host, string? origin)
    {
        var (auth, context) = BuildRequest(host, origin);

        var result = await auth.AuthenticateAsync(context, LoopbackAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded);
    }
}
