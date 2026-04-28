// -----------------------------------------------------------------------
// <copyright file="LoopbackAuthenticationHandlerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

/// <summary>
/// Unit tests for <see cref="LoopbackAuthenticationHandler"/>.
/// Verifies that loopback IPs receive Operator/LocalProcess claims
/// and that non-loopback IPs receive NoResult.
/// </summary>
public sealed class LoopbackAuthenticationHandlerTests
{
    private static (IServiceProvider Sp, IAuthenticationService Auth) BuildAuthService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddAuthentication(LoopbackAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LoopbackAuthenticationHandler>(
                LoopbackAuthenticationHandler.SchemeName, _ => { });
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IAuthenticationService>());
    }

    private static DefaultHttpContext BuildContext(IPAddress remoteIp, IServiceProvider sp)
    {
        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Connection.RemoteIpAddress = remoteIp;
        return ctx;
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Loopback_ip_returns_success_with_operator_claims(string ip)
    {
        var (sp, authService) = BuildAuthService();
        var ctx = BuildContext(IPAddress.Parse(ip), sp);

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);

        var principal = result.Principal!;
        Assert.Equal(
            nameof(PrincipalClassification.Operator),
            principal.FindFirst(NetclawClaimTypes.PrincipalClassification)?.Value);
        Assert.Equal(
            nameof(TransportAuthenticity.LocalProcess),
            principal.FindFirst(NetclawClaimTypes.TransportAuthenticity)?.Value);
        Assert.Equal(
            "local",
            principal.FindFirst(NetclawClaimTypes.DeviceId)?.Value);
    }

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("203.0.113.1")]
    public async Task Non_loopback_ip_returns_no_result(string ip)
    {
        var (sp, authService) = BuildAuthService();
        var ctx = BuildContext(IPAddress.Parse(ip), sp);

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);   // NoResult, not Fail
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task Null_remote_ip_returns_no_result()
    {
        var (sp, authService) = BuildAuthService();
        var ctx = BuildContext(IPAddress.None, sp);
        ctx.Connection.RemoteIpAddress = null;

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);
        Assert.Null(result.Principal);
    }
}
