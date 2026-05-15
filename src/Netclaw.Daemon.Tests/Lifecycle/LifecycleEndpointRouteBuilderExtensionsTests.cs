// -----------------------------------------------------------------------
// <copyright file="LifecycleEndpointRouteBuilderExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Lifecycle;
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Lifecycle;

/// <summary>
/// Real integration tests for the lifecycle endpoints registered by
/// <see cref="LifecycleEndpointRouteBuilderExtensions.MapLifecycleEndpoints"/>.
///
/// The test host calls the actual extension method — no handler reimplementation.
/// </summary>
public sealed class LifecycleEndpointRouteBuilderExtensionsTests : IAsyncDisposable
{
    private readonly TrackingNotificationSink _sink = new();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ─── App factory ───────────────────────────────────────────────────────────

    private async Task<WebApplication> CreateAppAsync(bool spoofLoopback)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IOperationalNotificationSink>(_sink);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<DaemonLifecycleNotifier>();

        var app = builder.Build();

        if (spoofLoopback)
        {
            app.Use(async (ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                await next(ctx);
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapLifecycleEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    // ─── POST /api/lifecycle/shutdown ─────────────────────────────────────────

    [Fact]
    public async Task Shutdown_returns_401_for_unauthenticated_request()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/lifecycle/shutdown?reason=test", null, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_sink.Emitted);
    }

    [Fact]
    public async Task Shutdown_returns_400_when_reason_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/lifecycle/shutdown", null, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Contains("reason", body.GetProperty("error").GetString());
        Assert.Empty(_sink.Emitted);
    }

    [Fact]
    public async Task Shutdown_returns_200_with_reason_echo_and_invokes_notifier()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        const string shutdownReason = "cli-stop";
        var response = await client.PostAsync($"/api/lifecycle/shutdown?reason={shutdownReason}", null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(shutdownReason, body.GetProperty("reason").GetString());
        Assert.True(body.TryGetProperty("pid", out _));

        // Verify NotifyShutdown was invoked via the notification sink
        Assert.Single(_sink.Emitted);
        var alert = _sink.Emitted[0];
        Assert.Equal("daemon.stopping", alert.Type);
        Assert.NotNull(alert.Context);
        Assert.Equal(shutdownReason, alert.Context["reason"]);
    }

    // ─── Notification sink ────────────────────────────────────────────────────

    /// <summary>
    /// Records all emitted operational alerts so tests can assert that
    /// <see cref="DaemonLifecycleNotifier.NotifyShutdown"/> was invoked.
    /// </summary>
    private sealed class TrackingNotificationSink : IOperationalNotificationSink
    {
        private readonly List<OperationalAlert> _emitted = [];
        public IReadOnlyList<OperationalAlert> Emitted => _emitted;

        public void Emit(OperationalAlert alert) => _emitted.Add(alert);
    }
}
