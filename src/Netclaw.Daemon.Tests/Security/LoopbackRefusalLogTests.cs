// -----------------------------------------------------------------------
// <copyright file="LoopbackRefusalLogTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Daemon.Tests.Security;

/// <summary>
/// The refusal warning marks a denied request. A foreign Host or Origin on a route that
/// allows anonymous access is not a denial, so a web page must not be able to fill the log
/// through such a route.
/// </summary>
public sealed class LoopbackRefusalLogTests
{
    private static async Task<(WebApplication App, CapturingLoggerProvider Logs)> CreateAppAsync()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(ctx);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/anonymous", () => "healthy");
        app.MapGet("/protected", () => "operator").RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, logs);
    }

    private static IEnumerable<string> RefusalWarnings(CapturingLoggerProvider logs)
        => logs.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("Refused loopback request", StringComparison.Ordinal))
            .Select(e => e.Message);

    [Fact]
    public async Task Anonymous_route_with_foreign_host_writes_no_refusal_warning()
    {
        var (app, logs) = await CreateAppAsync();
        await using var _ = app;
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/anonymous");
        request.Headers.Host = "rebind.example:5199";
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(RefusalWarnings(logs));
    }

    [Fact]
    public async Task Protected_route_with_foreign_origin_writes_one_warning_naming_host_and_origin()
    {
        var (app, logs) = await CreateAppAsync();
        await using var _ = app;
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
        request.Headers.Host = "localhost:5199";
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var warning = Assert.Single(RefusalWarnings(logs));
        Assert.Contains("Host=localhost:5199", warning);
        Assert.Contains("Origin=http://evil.example", warning);
    }

    [Fact]
    public async Task Protected_route_with_loopback_host_writes_no_refusal_warning()
    {
        var (app, logs) = await CreateAppAsync();
        await using var _ = app;
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
        request.Headers.Host = "localhost:5199";
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(RefusalWarnings(logs));
    }
}
