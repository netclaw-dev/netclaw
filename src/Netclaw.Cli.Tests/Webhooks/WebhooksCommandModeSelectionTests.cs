// -----------------------------------------------------------------------
// <copyright file="WebhooksCommandModeSelectionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Netclaw.Cli.Webhooks;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Webhooks;

/// <summary>
/// What <c>netclaw webhooks</c> does with each daemon answer. Route mutations are
/// daemon-only: each test names the answer and asserts the observable effect —
/// which HTTP call the command made, the exit code, and that no route file
/// changed on any path.
/// <para>
/// The failure tests read <c>Console.Error</c>, so the class joins the console
/// redirection collection.
/// </para>
/// </summary>
[Collection(ConsoleRedirectionCollection.Name)]
public sealed class WebhooksCommandModeSelectionTests : IDisposable
{
    private const string RouteName = "mode-route";

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public WebhooksCommandModeSelectionTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    private string RouteFilePath => Path.Combine(_paths.WebhooksDirectory, $"{RouteName}.json");

    private static string[] SetArguments() =>
    [
        "webhooks", "set", RouteName,
        "--prompt", "Triage the delivery",
        "--secret-env", "NETCLAW_TEST_WEBHOOK_SECRET"
    ];

    [Fact]
    public async Task Set_with_a_reachable_daemon_sends_the_patch_and_writes_no_file()
    {
        var daemon = FakeWebhookDaemon.Healthy(_paths);
        var stdout = new StringWriter();

        var result = await RunSetAsync(stdout, daemon);

        Assert.Equal(0, result);
        Assert.False(File.Exists(RouteFilePath));
        Assert.Contains($"[OK] Created webhook route '{RouteName}'.", stdout.ToString(), StringComparison.Ordinal);

        // The patch is the cross-boundary contract with the daemon's request body:
        // camel-case names, the CLI's documented 'public' default for a new route,
        // and a null for every flag the operator did not pass.
        using var body = daemon.SingleUpsertBody(RouteName);
        Assert.Equal("Triage the delivery", body.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("test-secret-value", body.RootElement.GetProperty("secret").GetString());
        Assert.Equal("public", body.RootElement.GetProperty("audience").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("enabled").ValueKind);
    }

    [Fact]
    public async Task Delete_with_a_reachable_daemon_calls_the_resource_instead_of_the_file()
    {
        WriteRouteFile();
        var daemon = FakeWebhookDaemon.Healthy(_paths);
        var stdout = new StringWriter();

        var result = await WebhooksCommand.RunAsync(
            ["webhooks", "delete", RouteName, "--force"], _paths, stdout, daemon.Api);

        Assert.Equal(0, result);
        Assert.Contains(daemon.Calls, call => call.Method == "DELETE" && call.Path == $"/api/webhooks/{RouteName}");
        Assert.Equal($"[OK] Deleted webhook route '{RouteName}'.", stdout.ToString().TrimEnd());

        // The daemon owns the deletion, so the command must not remove the file itself.
        Assert.True(File.Exists(RouteFilePath));
    }

    [Fact]
    public async Task Set_with_an_unreachable_daemon_fails_and_writes_no_file()
    {
        var daemon = FakeWebhookDaemon.Unreachable(_paths);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var result = await RunSetAsync(stdout, daemon, stderr);

        Assert.Equal(1, result);
        Assert.False(File.Exists(RouteFilePath));
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains(
            "[FAIL] The daemon is not reachable. Start the daemon to manage webhook routes.",
            stderr.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_with_an_unreachable_daemon_fails_and_leaves_the_file()
    {
        WriteRouteFile();
        var daemon = FakeWebhookDaemon.Unreachable(_paths);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var result = await RunWithStderrAsync(
            stderr,
            () => WebhooksCommand.RunAsync(
                ["webhooks", "delete", RouteName, "--force"], _paths, stdout, daemon.Api));

        Assert.Equal(1, result);
        Assert.True(File.Exists(RouteFilePath));
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains(
            "[FAIL] The daemon is not reachable. Start the daemon to manage webhook routes.",
            stderr.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_against_an_old_daemon_without_the_resource_fails_and_asks_for_an_upgrade()
    {
        // An old daemon answers, so this is a different outcome from an
        // unreachable daemon: the resource is absent, not the process. The
        // remedy differs, so the message does too.
        var daemon = FakeWebhookDaemon.WithoutRouteResource(_paths);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var result = await RunSetAsync(stdout, daemon, stderr);

        Assert.Equal(1, result);
        Assert.False(File.Exists(RouteFilePath));
        Assert.DoesNotContain(daemon.Calls, call => call.Method == "PUT");
        Assert.Contains(
            "[FAIL] This daemon does not serve the webhook route API. Upgrade the daemon.",
            stderr.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_rejected_with_a_validation_error_fails_without_writing_a_file()
    {
        var daemon = new FakeWebhookDaemon(_paths, request => request.Method == HttpMethod.Put
            ? FakeWebhookDaemon.Json(HttpStatusCode.BadRequest, new { error = "Route audience exceeds creator authority." })
            : FakeWebhookDaemon.RouteList());
        var stdout = new StringWriter();

        var result = await RunSetAsync(stdout, daemon);

        Assert.Equal(1, result);
        Assert.False(File.Exists(RouteFilePath));
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task Set_rejected_by_authentication_fails_without_writing_a_file()
    {
        var daemon = new FakeWebhookDaemon(_paths, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var stdout = new StringWriter();

        var result = await RunSetAsync(stdout, daemon);

        Assert.Equal(1, result);
        Assert.False(File.Exists(RouteFilePath));
        Assert.DoesNotContain(daemon.Calls, call => call.Method == "PUT");
    }

    [Fact]
    public async Task Delete_rejected_by_authorization_fails_without_removing_the_file()
    {
        WriteRouteFile();
        var daemon = new FakeWebhookDaemon(_paths, request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : FakeWebhookDaemon.RouteList());
        var stdout = new StringWriter();

        var result = await WebhooksCommand.RunAsync(
            ["webhooks", "delete", RouteName, "--force"], _paths, stdout, daemon.Api);

        Assert.Equal(1, result);
        Assert.True(File.Exists(RouteFilePath));
    }

    private Task<int> RunSetAsync(TextWriter stdout, FakeWebhookDaemon daemon, TextWriter stderr)
        => RunWithStderrAsync(stderr, () => RunSetAsync(stdout, daemon));

    private async Task<int> RunSetAsync(TextWriter stdout, FakeWebhookDaemon daemon)
    {
        // --secret-env keeps the shell-history warning out of the command output.
        Environment.SetEnvironmentVariable("NETCLAW_TEST_WEBHOOK_SECRET", "test-secret-value");
        try
        {
            return await WebhooksCommand.RunAsync(SetArguments(), _paths, stdout, daemon.Api);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETCLAW_TEST_WEBHOOK_SECRET", null);
        }
    }

    /// <summary>
    /// Captures the command's stderr. The command writes failures to
    /// <see cref="Console.Error"/>, which is process-wide, so only the tests that
    /// assert on a message pay the cost of redirecting it.
    /// </summary>
    private static async Task<int> RunWithStderrAsync(TextWriter stderr, Func<Task<int>> run)
    {
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            return await run();
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private void WriteRouteFile()
    {
        var store = new WebhookRouteStore(_paths);
        store.Save(RouteName, new WebhookRouteConfig
        {
            Prompt = "Existing prompt",
            Verification = new WebhookVerificationConfig { Secret = new SensitiveString("existing-secret") }
        });
    }
}
