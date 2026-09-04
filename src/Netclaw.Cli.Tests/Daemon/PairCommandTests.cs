// -----------------------------------------------------------------------
// <copyright file="PairCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Daemon;

/// <summary>
/// Verifies recovery guidance and the credential persistence boundary for device pairing.
/// </summary>
public sealed class PairCommandTests : IDisposable
{
    private const string Endpoint = "https://daemon.example";
    private readonly DisposableTempDir _directory = new();
    private readonly NetclawPaths _paths;

    public PairCommandTests()
    {
        _paths = new NetclawPaths(_directory.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task DuplicateName_RecommendsSameCodeWithoutSavingClientState()
    {
        using var response = ErrorResponse(HttpStatusCode.Conflict, "Device name already exists.");

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("different device name", result.Stderr);
        Assert.Contains("same unexpired pairing code", result.Stderr);
        Assert.DoesNotContain("netclaw daemon pair", result.Stderr);
        AssertNoClientState();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid, expired, or already used")]
    [InlineData(HttpStatusCode.NotFound, "No active pairing code")]
    public async Task UnusableCode_RecommendsNewCodeWithoutSavingClientState(
        HttpStatusCode statusCode,
        string expectedReason)
    {
        using var response = ErrorResponse(statusCode, "Pairing code rejected.");

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedReason, result.Stderr);
        Assert.Contains("netclaw daemon pair", result.Stderr);
        AssertNoClientState();
    }

    [Fact]
    public async Task RateLimit_HonorsRetryAfterWithoutSavingClientState()
    {
        using var response = ErrorResponse(HttpStatusCode.TooManyRequests, "Too many attempts.");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Wait at least 30 seconds", result.Stderr);
        Assert.DoesNotContain("netclaw daemon pair", result.Stderr);
        AssertNoClientState();
    }

    [Fact]
    public async Task SuccessfulExchange_SavesTokenAndEndpoint()
    {
        using var response = FakeHttpMessageHandler.JsonResponse(new { token = "device-token" });

        var result = await RunAsync(response);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Endpoint, ClientConfigFile.ReadEndpoint(_paths));

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(secrets.TryGetValue("DeviceToken", out var storedToken));
        var protectedToken = storedToken is JsonElement element ? element.GetString() : storedToken?.ToString();
        Assert.Equal("device-token", ConfigFileHelper.DecryptIfEncrypted(_paths, protectedToken));
    }

    [Fact]
    public async Task NonLoopbackHttpEndpoint_FailsBeforeCodeInput()
    {
        var requestCount = 0;
        using var handler = new FakeHttpMessageHandler(_ =>
        {
            requestCount++;
            return FakeHttpMessageHandler.JsonResponse(new { token = "device-token" });
        });
        using var httpClient = new HttpClient(handler);

        var result = await RunAsync(httpClient, "http://daemon.example", "ABCD-EFGH\ntablet\n");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must use HTTPS", result.Stderr);
        Assert.DoesNotContain("Pairing code", result.Stdout);
        Assert.Equal(0, requestCount);
        AssertNoClientState();
    }

    [Fact]
    public async Task LoopbackHttpEndpoint_RemainsAvailable()
    {
        using var response = FakeHttpMessageHandler.JsonResponse(new { token = "device-token" });
        using var handler = new FakeHttpMessageHandler(_ => response);
        using var httpClient = new HttpClient(handler);

        var result = await RunAsync(httpClient, "http://127.0.0.1:5199", "ABCD-EFGH\ntablet\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("http://127.0.0.1:5199", ClientConfigFile.ReadEndpoint(_paths));
    }

    [Fact]
    public void PairingHttpHandler_DoesNotFollowRedirects()
    {
        using var handler = PairCommand.CreateHttpHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task RedirectResponse_FailsWithoutSavingClientState()
    {
        var requestCount = 0;
        using var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            Headers = { Location = new Uri("https://redirect.example") },
        };
        using var handler = new FakeHttpMessageHandler(_ =>
        {
            requestCount++;
            return response;
        });
        using var httpClient = new HttpClient(handler);

        var result = await RunAsync(httpClient, Endpoint, "ABCD-EFGH\ntablet\n");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, requestCount);
        AssertNoClientState();
    }

    [Fact]
    public async Task InvalidSuccessJson_FailsWithoutSavingClientState()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{")
        };

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("invalid JSON", result.Stderr);
        AssertNoClientState();
    }

    [Fact]
    public async Task Timeout_FailsWithoutSavingClientState()
    {
        using var handler = new TimeoutMessageHandler();
        using var httpClient = new HttpClient(handler);

        var result = await RunAsync(httpClient, Endpoint, "ABCD-EFGH\ntablet\n");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("did not respond before the timeout", result.Stderr);
        AssertNoClientState();
    }

    [Fact]
    public async Task OversizedErrorBody_IsNotCopiedToCliOutput()
    {
        var marker = new string('X', 5_000);
        using var response = ErrorResponse(HttpStatusCode.BadGateway, marker);

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain(marker, result.Stderr);
        AssertNoClientState();
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(HttpResponseMessage response)
    {
        using var handler = new FakeHttpMessageHandler(_ => response);
        using var httpClient = new HttpClient(handler);
        return await RunAsync(httpClient, Endpoint, "ABCD-EFGH\ntablet\n");
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        HttpClient httpClient,
        string endpoint,
        string inputText)
    {
        using var input = new StringReader(inputText);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await PairCommand.RunAsync(
            ["pair", endpoint],
            _paths,
            httpClient,
            input,
            stdout,
            stderr,
            CancellationToken.None);

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode, string error)
        => FakeHttpMessageHandler.JsonResponse(new { error }, statusCode);

    private void AssertNoClientState()
    {
        Assert.False(File.Exists(_paths.SecretsPath));
        Assert.False(File.Exists(_paths.ClientConfigPath));
    }

    private sealed class TimeoutMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new TaskCanceledException("Test timeout."));
    }
}
