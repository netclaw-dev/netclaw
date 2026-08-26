// -----------------------------------------------------------------------
// <copyright file="WebhookRouteDaemonClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Netclaw.Cli.Webhooks;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Webhooks;

/// <summary>
/// The write rule itself (design D4). Every CLI surface that mutates a webhook
/// route calls the daemon through this client, so these tests own the rule: the
/// daemon writes, or the command fails. No answer selects a local write.
/// </summary>
public sealed class WebhookRouteDaemonClientTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public WebhookRouteDaemonClientTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_reachable_daemon_is_available()
    {
        var client = CreateClient(_ => FakeWebhookDaemon.RouteList());

        var available = await client.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.True(available.Success);
        Assert.Null(available.Error);
    }

    [Fact]
    public async Task An_unreachable_daemon_fails_and_names_the_remedy()
    {
        var client = CreateClient(_ => throw new HttpRequestException("connection refused"));

        var available = await client.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.False(available.Success);
        Assert.Equal(
            "The daemon is not reachable. Start the daemon to manage webhook routes.",
            available.Error);
    }

    [Fact]
    public async Task An_old_daemon_without_the_resource_fails_and_asks_for_an_upgrade()
    {
        // A 404 is a different probe answer from an unreachable daemon: the
        // process runs, the resource does not exist yet. The operator needs a
        // different remedy, so the message differs.
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var available = await client.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.False(available.Success);
        Assert.Equal(
            "This daemon does not serve the webhook route API. Upgrade the daemon.",
            available.Error);
    }

    [Fact]
    public async Task A_missing_daemon_client_fails_like_an_unreachable_daemon()
    {
        var client = new WebhookRouteDaemonClient(daemonApi: null);

        var available = await client.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.False(available.Success);
        Assert.Equal(
            "The daemon is not reachable. Start the daemon to manage webhook routes.",
            available.Error);
    }

    [Fact]
    public async Task Availability_resolves_once_so_one_invocation_probes_one_time()
    {
        var probes = 0;
        var client = CreateClient(_ =>
        {
            probes++;
            return FakeWebhookDaemon.RouteList();
        });
        var ct = TestContext.Current.CancellationToken;

        await client.EnsureAvailableAsync(ct);
        await client.EnsureAvailableAsync(ct);
        await client.EnsureAvailableAsync(ct);

        Assert.Equal(1, probes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_daemon_that_refuses_the_probe_fails_with_its_own_answer(HttpStatusCode status)
    {
        var client = CreateClient(_ => new HttpResponseMessage(status));

        var available = await client.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.False(available.Success);
        Assert.Contains(((int)status).ToString(), available.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_upsert_reports_the_daemon_message_and_never_succeeds()
    {
        var client = CreateClient(request => request.Method == HttpMethod.Put
            ? FakeWebhookDaemon.Json(HttpStatusCode.BadRequest, new { error = "Prompt is required." })
            : FakeWebhookDaemon.RouteList());
        var ct = TestContext.Current.CancellationToken;

        Assert.True((await client.EnsureAvailableAsync(ct)).Success);
        var saved = await client.UpsertAsync("guarded-route", new WebhookRoutePatch { Prompt = "x" }, ct);

        Assert.False(saved.Success);
        Assert.Equal("Prompt is required.", saved.Error);
    }

    [Fact]
    public async Task A_daemon_that_dies_mid_write_fails_closed_and_names_the_uncertainty()
    {
        // The probe succeeded, then the transport broke. The daemon may have
        // applied the change, so the command must not retry silently or write a
        // file of its own.
        var probed = false;
        var client = CreateClient(_ =>
        {
            if (probed)
                throw new HttpRequestException("connection reset");

            probed = true;
            return FakeWebhookDaemon.RouteList();
        });
        var ct = TestContext.Current.CancellationToken;

        Assert.True((await client.EnsureAvailableAsync(ct)).Success);
        var saved = await client.UpsertAsync("guarded-route", new WebhookRoutePatch { Prompt = "x" }, ct);

        Assert.False(saved.Success);
        Assert.Contains("may or may not have applied", saved.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_delete_of_a_missing_route_reports_not_found_rather_than_an_error()
    {
        var client = CreateClient(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : FakeWebhookDaemon.RouteList());
        var ct = TestContext.Current.CancellationToken;

        await client.EnsureAvailableAsync(ct);
        var removed = await client.DeleteAsync("missing-route", ct);

        Assert.False(removed.Success);
        Assert.True(removed.NotFound);
        Assert.Null(removed.Error);
    }

    private WebhookRouteDaemonClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new FakeWebhookDaemon(_paths, respond).Api);
}
