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
    private static (IServiceProvider Sp, IAuthenticationService Auth) BuildAuthService(
        DaemonConfig? config = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config ?? new DaemonConfig());
        services
            .AddAuthentication(LoopbackAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LoopbackAuthenticationHandler>(
                LoopbackAuthenticationHandler.SchemeName, _ => { });
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IAuthenticationService>());
    }

    private const string AllowedHost = "localhost:5199";

    private static DefaultHttpContext BuildContext(
        IPAddress remoteIp,
        IServiceProvider sp,
        string? host = AllowedHost,
        string? origin = null)
    {
        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Connection.RemoteIpAddress = remoteIp;
        if (host is not null)
            ctx.Request.Headers.Host = host;
        if (origin is not null)
            ctx.Request.Headers.Origin = origin;
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
    [InlineData("localhost:5199")]
    [InlineData("LOCALHOST:5199")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:5199")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]:5199")]
    [InlineData("[::1]")]
    [InlineData("127.0.0.2:5199")]
    [InlineData("127.255.255.254")]
    public async Task Allowed_host_names_receive_operator_claims(string host)
    {
        var (sp, authService) = BuildAuthService();
        var ctx = BuildContext(IPAddress.Loopback, sp, host);

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("rebind.example:5199")]
    [InlineData("127.0.0.1.evil.example:5199")]
    [InlineData("localhost.evil.example")]
    [InlineData("localhost.:5199")]
    [InlineData("192.168.1.10:5199")]
    [InlineData("[::2]:5199")]
    [InlineData("localhost:5199,rebind.example")]
    [InlineData("localhost:abc")]
    [InlineData("localhost:")]
    [InlineData("localhost:1234567")]
    [InlineData("127.1:5199")]
    [InlineData("0x7f.1:5199")]
    [InlineData("2130706433:5199")]
    [InlineData("0.0.0.0:5199")]
    [InlineData("[::ffff:127.0.0.1]:5199")]
    [InlineData("[localhost]:5199")]
    [InlineData("::1")]
    [InlineData(" localhost")]
    [InlineData("[127.0.0.1]:5199")]
    [InlineData("127.0.0.1 ")]
    [InlineData("")]
    public async Task Foreign_or_missing_host_is_refused(string host)
    {
        var (sp, authService) = BuildAuthService();
        var ctx = BuildContext(IPAddress.Loopback, sp, host);

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Theory]
    [InlineData("127.0.0.2")]
    [InlineData("127.10.20.30")]
    [InlineData("::1")]
    [InlineData("0:0:0:0:0:0:0:1")]
    public async Task Cli_request_to_a_config_valid_local_bind_is_admitted(string bindHost)
    {
        var config = new DaemonConfig { ExposureMode = ExposureMode.Local, Host = bindHost, Port = 5199 };
        Assert.Null(DaemonExposureValidator.GetLoopbackViolationIssue(config));
        var cliHost = new Uri(DaemonControlPlaneEndpointResolver.ResolveFallbackEndpoint(config)).Authority;
        var (sp, authService) = BuildAuthService(config);
        var ctx = BuildContext(IPAddress.Loopback, sp, cliHost);

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded, $"Host {cliHost} was refused: {result.Failure?.Message}");
    }

    [Fact]
    public async Task Request_without_a_host_header_is_refused()
    {
        var (sp, authService) = BuildAuthService();
        var ctx = BuildContext(IPAddress.Loopback, sp, host: null);

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Theory]
    [InlineData("localhost:5199", "http://localhost:5199")]
    [InlineData("LOCALHOST:5199", "http://localhost:5199")]
    [InlineData("127.0.0.1:5199", "http://127.0.0.1:5199")]
    [InlineData("[::1]:5199", "http://[::1]:5199")]
    [InlineData("localhost", "http://localhost")]
    public async Task Origin_equal_to_http_scheme_plus_host_is_accepted(string host, string origin)
    {
        var (sp, authService) = BuildAuthService();
        var ctx = BuildContext(IPAddress.Loopback, sp, host, origin);

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("127.0.0.1:5199", "http://evil.example")]
    [InlineData("localhost:5199", "null")]
    [InlineData("localhost:5199", "http://localhost:3000")]
    [InlineData("localhost:5199", "https://localhost:5199")]
    [InlineData("localhost:5199", "http://localhost:5199/path")]
    [InlineData("localhost:5199", "")]
    public async Task Origin_that_differs_from_http_scheme_plus_host_is_refused(string host, string origin)
    {
        var (sp, authService) = BuildAuthService();
        var ctx = BuildContext(IPAddress.Loopback, sp, host, origin);

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
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
    public async Task Non_loopback_ip_with_foreign_host_and_origin_still_returns_no_result()
    {
        var (sp, authService) = BuildAuthService();
        var ctx = BuildContext(IPAddress.Parse("203.0.113.1"), sp, "rebind.example", "http://evil.example");

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task Reverse_proxy_mode_with_foreign_host_still_returns_no_result()
    {
        var (sp, authService) = BuildAuthService(new DaemonConfig { ExposureMode = ExposureMode.ReverseProxy });
        var ctx = BuildContext(IPAddress.Loopback, sp, "app.example.com", "https://app.example.com");

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);
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

    [Fact]
    public async Task Reverse_proxy_mode_returns_no_result_for_loopback_ip()
    {
        var (sp, authService) = BuildAuthService(new DaemonConfig { ExposureMode = ExposureMode.ReverseProxy });
        var ctx = BuildContext(IPAddress.Loopback, sp);

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);
        Assert.Null(result.Principal);
    }

    // Tunnel agents (tailscaled, cloudflared) forward public traffic over the
    // loopback socket, so every external request arrives with RemoteIpAddress
    // == 127.0.0.1 / ::1. A handler that auto-promotes loopback to Operator
    // under those modes hands an unauthenticated internet caller full local
    // operator trust (audit finding #24). Loopback auto-auth must be reserved
    // for ExposureMode.Local; under tunnel modes remote callers must go
    // through the device bearer scheme. Both address families are exercised
    // because the bug is symmetric across IPv4 and IPv6.
    public static TheoryData<ExposureMode, string> TunnelModesWithLoopbackIps()
    {
        var data = new TheoryData<ExposureMode, string>();
        foreach (var mode in new[]
                 {
                     ExposureMode.CloudflareTunnel,
                     ExposureMode.TailscaleFunnel,
                     ExposureMode.TailscaleServe,
                 })
        {
            data.Add(mode, "127.0.0.1");
            data.Add(mode, "::1");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TunnelModesWithLoopbackIps))]
    public async Task Tunnel_modes_return_no_result_for_loopback_ip(
        ExposureMode tunnelMode,
        string loopbackIp)
    {
        var (sp, authService) = BuildAuthService(new DaemonConfig { ExposureMode = tunnelMode });
        var ctx = BuildContext(IPAddress.Parse(loopbackIp), sp);

        var result = await authService.AuthenticateAsync(ctx, LoopbackAuthenticationHandler.SchemeName);

        Assert.False(
            result.Succeeded,
            $"ExposureMode={tunnelMode} with RemoteIpAddress={loopbackIp} must not auto-authenticate the caller as Operator. " +
            "Loopback auto-auth is reserved for ExposureMode.Local; under tunnel modes the " +
            "loopback peer is the tunnel agent forwarding remote traffic.");
        Assert.Null(result.Failure);
        Assert.Null(result.Principal);
    }
}
