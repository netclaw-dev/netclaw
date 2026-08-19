// -----------------------------------------------------------------------
// <copyright file="WebhookRouteWriteGatewayTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Webhooks;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Webhooks;

/// <summary>
/// The write-path rule itself (design D4). Every CLI surface that mutates a
/// webhook route resolves its mode here, so these tests own the mode decision and
/// the direct-file notice. The gateway takes its notice writer, so the assertions
/// need no process-wide console redirection.
/// </summary>
public sealed class WebhookRouteWriteGatewayTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly StringWriter _notices = new();

    public WebhookRouteWriteGatewayTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _notices.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task A_reachable_daemon_selects_the_daemon_and_prints_no_notice()
    {
        var gateway = CreateGateway(_ => JsonResponse(HttpStatusCode.OK, Array.Empty<object>()));

        var resolution = await gateway.ResolveModeAsync(TestContext.Current.CancellationToken);

        Assert.False(resolution.Failed);
        Assert.Equal(WebhookRouteWriteMode.Daemon, resolution.Mode);
        Assert.Equal(string.Empty, _notices.ToString());
    }

    [Fact]
    public async Task An_unreachable_daemon_selects_direct_file_and_discloses_the_mode()
    {
        var gateway = CreateGateway(_ => throw new HttpRequestException("connection refused"));

        var resolution = await gateway.ResolveModeAsync(TestContext.Current.CancellationToken);

        Assert.False(resolution.Failed);
        Assert.Equal(WebhookRouteWriteMode.DirectFile, resolution.Mode);
        Assert.Equal(1, CountNotices());
    }

    [Fact]
    public async Task An_old_daemon_without_the_resource_selects_direct_file_and_discloses_the_mode()
    {
        // A 404 is a different probe answer from an unreachable daemon: the
        // process runs, the resource does not exist yet.
        var gateway = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var resolution = await gateway.ResolveModeAsync(TestContext.Current.CancellationToken);

        Assert.False(resolution.Failed);
        Assert.Equal(WebhookRouteWriteMode.DirectFile, resolution.Mode);
        Assert.Equal(1, CountNotices());
    }

    [Fact]
    public async Task A_missing_daemon_client_selects_direct_file_and_discloses_the_mode()
    {
        var gateway = new WebhookRouteWriteGateway(daemonApi: null, _notices);

        var resolution = await gateway.ResolveModeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WebhookRouteWriteMode.DirectFile, resolution.Mode);
        Assert.Equal(1, CountNotices());
    }

    [Fact]
    public async Task The_mode_resolves_once_so_one_invocation_prints_one_notice()
    {
        var probes = 0;
        var gateway = CreateGateway(_ =>
        {
            probes++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var ct = TestContext.Current.CancellationToken;

        await gateway.ResolveModeAsync(ct);
        await gateway.ResolveModeAsync(ct);
        await gateway.ResolveModeAsync(ct);

        Assert.Equal(1, probes);
        Assert.Equal(1, CountNotices());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_daemon_that_refuses_the_probe_fails_without_selecting_direct_file(HttpStatusCode status)
    {
        var gateway = CreateGateway(_ => new HttpResponseMessage(status));

        var resolution = await gateway.ResolveModeAsync(TestContext.Current.CancellationToken);

        Assert.True(resolution.Failed);
        Assert.Contains(((int)status).ToString(), resolution.Error!, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _notices.ToString());
    }

    [Fact]
    public async Task A_rejected_upsert_reports_the_daemon_message_and_never_succeeds()
    {
        var gateway = CreateGateway(request => request.Method == HttpMethod.Put
            ? JsonResponse(HttpStatusCode.BadRequest, new { error = "Prompt is required." })
            : JsonResponse(HttpStatusCode.OK, Array.Empty<object>()));
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(WebhookRouteWriteMode.Daemon, (await gateway.ResolveModeAsync(ct)).Mode);
        var saved = await gateway.UpsertAsync("guarded-route", new WebhookRoutePatch { Prompt = "x" }, ct);

        Assert.False(saved.Success);
        Assert.Equal("Prompt is required.", saved.Error);
    }

    [Fact]
    public async Task A_delete_of_a_missing_route_reports_not_found_rather_than_an_error()
    {
        var gateway = CreateGateway(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : JsonResponse(HttpStatusCode.OK, Array.Empty<object>()));
        var ct = TestContext.Current.CancellationToken;

        await gateway.ResolveModeAsync(ct);
        var removed = await gateway.DeleteAsync("missing-route", ct);

        Assert.False(removed.Success);
        Assert.True(removed.NotFound);
        Assert.Null(removed.Error);
    }

    private int CountNotices()
        => _notices.ToString()
            .Split(WebhookRouteWriteGateway.DirectFileNotice, StringSplitOptions.None)
            .Length - 1;

    private WebhookRouteWriteGateway CreateGateway(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        ClientConfigFile.WriteEndpoint(_paths, "http://127.0.0.1:5199");
        var api = new DaemonApi(new FakeHttpClientFactory(handler), new ConfigurationBuilder().Build(), _paths);
        return new WebhookRouteWriteGateway(api, _notices);
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
}
