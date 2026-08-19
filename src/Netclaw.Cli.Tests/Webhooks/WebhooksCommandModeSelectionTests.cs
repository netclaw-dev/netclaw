// -----------------------------------------------------------------------
// <copyright file="WebhooksCommandModeSelectionTests.cs" company="Petabridge, LLC">
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
/// What <c>netclaw webhooks</c> does on each write path. Each test names the
/// probe answer and asserts the observable effect: which HTTP call the command
/// made, whether a route file changed, and the exit code.
/// <para>
/// The direct-file notice belongs to <c>WebhookRouteWriteGatewayTests</c>: the
/// command writes it to <c>Console.Error</c>, which is process-wide state that
/// concurrent test classes share, so counting it here would be unreliable.
/// </para>
/// </summary>
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
        var calls = new List<RecordedCall>();
        var api = CreateDaemonApi(request => Record(calls, request, _ => RouteListResponse()));
        var stdout = new StringWriter();

        var result = await RunSetAsync(stdout, api);

        Assert.Equal(0, result);
        var upsert = Assert.Single(calls, call => call.Method == "PUT");
        Assert.Equal($"/api/webhooks/{RouteName}", upsert.Path);
        Assert.False(File.Exists(RouteFilePath));
        Assert.Contains($"[OK] Created webhook route '{RouteName}'.", stdout.ToString(), StringComparison.Ordinal);

        // The patch is the cross-boundary contract with the daemon's request body:
        // camel-case names, the CLI's documented 'public' default for a new route,
        // and a null for every flag the operator did not pass.
        using var body = JsonDocument.Parse(upsert.Body);
        Assert.Equal("Triage the delivery", body.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("test-secret-value", body.RootElement.GetProperty("secret").GetString());
        Assert.Equal("public", body.RootElement.GetProperty("audience").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("enabled").ValueKind);
    }

    [Fact]
    public async Task Delete_with_a_reachable_daemon_calls_the_resource_instead_of_the_file()
    {
        WriteRouteFile();
        var calls = new List<RecordedCall>();
        var api = CreateDaemonApi(request => Record(calls, request, r => r.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : RouteListResponse()));
        var stdout = new StringWriter();

        var result = await WebhooksCommand.RunAsync(
            ["webhooks", "delete", RouteName, "--force"], _paths, stdout, api);

        Assert.Equal(0, result);
        Assert.Contains(calls, call => call.Method == "DELETE" && call.Path == $"/api/webhooks/{RouteName}");
        Assert.Equal($"[OK] Deleted webhook route '{RouteName}'.", stdout.ToString().TrimEnd());

        // The daemon owns the deletion, so the command must not remove the file itself.
        Assert.True(File.Exists(RouteFilePath));
    }

    [Fact]
    public async Task Set_with_an_unreachable_daemon_writes_the_file_itself()
    {
        var api = CreateDaemonApi(_ => throw new HttpRequestException("connection refused"));
        var stdout = new StringWriter();

        var result = await RunSetAsync(stdout, api);

        Assert.Equal(0, result);
        Assert.True(File.Exists(RouteFilePath));
        Assert.Contains($"[OK] Created webhook route '{RouteName}'.", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_against_an_old_daemon_without_the_resource_writes_the_file_itself()
    {
        // An old daemon answers, so this is a different probe outcome from an
        // unreachable daemon: the resource is absent, not the process.
        var calls = new List<RecordedCall>();
        var api = CreateDaemonApi(request => Record(
            calls, request, _ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var stdout = new StringWriter();

        var result = await RunSetAsync(stdout, api);

        Assert.Equal(0, result);
        Assert.True(File.Exists(RouteFilePath));
        Assert.DoesNotContain(calls, call => call.Method == "PUT");
    }

    [Fact]
    public async Task Set_rejected_with_a_validation_error_fails_without_writing_a_file()
    {
        var api = CreateDaemonApi(request => request.Method == HttpMethod.Put
            ? JsonResponse(HttpStatusCode.BadRequest, new { error = "Route audience exceeds creator authority." })
            : RouteListResponse());
        var stdout = new StringWriter();

        var result = await RunSetAsync(stdout, api);

        Assert.Equal(1, result);
        Assert.False(File.Exists(RouteFilePath));
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task Set_rejected_by_authentication_fails_without_writing_a_file()
    {
        var calls = new List<RecordedCall>();
        var api = CreateDaemonApi(request => Record(
            calls, request, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var stdout = new StringWriter();

        var result = await RunSetAsync(stdout, api);

        Assert.Equal(1, result);
        Assert.False(File.Exists(RouteFilePath));
        Assert.DoesNotContain(calls, call => call.Method == "PUT");
    }

    [Fact]
    public async Task Delete_rejected_by_authorization_fails_without_removing_the_file()
    {
        WriteRouteFile();
        var api = CreateDaemonApi(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : RouteListResponse());
        var stdout = new StringWriter();

        var result = await WebhooksCommand.RunAsync(
            ["webhooks", "delete", RouteName, "--force"], _paths, stdout, api);

        Assert.Equal(1, result);
        Assert.True(File.Exists(RouteFilePath));
    }

    private async Task<int> RunSetAsync(TextWriter stdout, DaemonApi api)
    {
        // --secret-env keeps the shell-history warning out of the command output.
        Environment.SetEnvironmentVariable("NETCLAW_TEST_WEBHOOK_SECRET", "test-secret-value");
        try
        {
            return await WebhooksCommand.RunAsync(SetArguments(), _paths, stdout, api);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETCLAW_TEST_WEBHOOK_SECRET", null);
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

    private DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        ClientConfigFile.WriteEndpoint(_paths, "http://127.0.0.1:5199");
        return new DaemonApi(new FakeHttpClientFactory(handler), new ConfigurationBuilder().Build(), _paths);
    }

    private static HttpResponseMessage Record(
        List<RecordedCall> calls,
        HttpRequestMessage request,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        // ReadAsStream is the synchronous content reader, so the fake handler
        // records the body without a blocking wait on a task.
        var body = string.Empty;
        if (request.Content is { } content)
        {
            using var reader = new StreamReader(content.ReadAsStream(), Encoding.UTF8);
            body = reader.ReadToEnd();
        }

        calls.Add(new RecordedCall(request.Method.Method, request.RequestUri!.AbsolutePath, body));
        return respond(request);
    }

    private static HttpResponseMessage RouteListResponse()
        => JsonResponse(HttpStatusCode.OK, Array.Empty<object>());

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private sealed record RecordedCall(string Method, string Path, string Body);
}
