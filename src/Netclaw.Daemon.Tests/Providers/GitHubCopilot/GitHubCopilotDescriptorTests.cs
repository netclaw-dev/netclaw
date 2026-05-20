// -----------------------------------------------------------------------
// <copyright file="GitHubCopilotDescriptorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.GitHubCopilot;

public sealed class GitHubCopilotDescriptorTests
{
    private const string TokenExchangeUrl = "https://api.github.com/copilot_internal/v2/token";
    private const string ModelsUrl = "https://api.githubcopilot.com/models";

    private static ProviderEntry OAuthEntry(string token = "oauth-1") =>
        new()
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString(token),
        };

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage TokenOk() => Json(new
    {
        token = "copilot-api-token",
        expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
    });

    [Fact]
    public async Task Probe_FiltersByCapabilityAndPickerEligibility()
    {
        var modelsPayload = new
        {
            data = new object[]
            {
                new { id = "gpt-4o", capabilities = new { type = "chat" }, model_picker_enabled = true },
                new { id = "embed-3", capabilities = new { type = "embeddings" }, model_picker_enabled = true },
                new { id = "hidden", capabilities = new { type = "chat" }, model_picker_enabled = false },
                new { id = "no-caps", model_picker_enabled = true },
            },
        };

        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.ToString() switch
            {
                TokenExchangeUrl => TokenOk(),
                ModelsUrl => Json(modelsPayload),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var httpClient = new HttpClient(handler);
        var descriptor = new GitHubCopilotDescriptor(httpClient,
            new CopilotTokenExchanger(httpClient));

        var result = await descriptor.ProbeAsync(OAuthEntry(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var ids = result.Models.Select(m => m.ModelId.Value).ToList();
        Assert.Contains("gpt-4o", ids);
        Assert.Contains("no-caps", ids); // capabilities absent → not filtered out
        Assert.DoesNotContain("embed-3", ids);
        Assert.DoesNotContain("hidden", ids);
    }

    [Fact]
    public async Task Probe_FallsBackToCuratedListWhenModelsEndpointFails()
    {
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.ToString() switch
            {
                TokenExchangeUrl => TokenOk(),
                ModelsUrl => new HttpResponseMessage(HttpStatusCode.BadGateway),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var httpClient = new HttpClient(handler);
        var descriptor = new GitHubCopilotDescriptor(httpClient,
            new CopilotTokenExchanger(httpClient));

        var result = await descriptor.ProbeAsync(OAuthEntry(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("curated fallback", result.ErrorMessage);
        Assert.Equal(GitHubCopilotDescriptor.CuratedModels.Length, result.Models.Count);
    }

    [Fact]
    public async Task Probe_FallsBackToCuratedListWhenModelsReturnsEmpty()
    {
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.ToString() switch
            {
                TokenExchangeUrl => TokenOk(),
                ModelsUrl => Json(new { data = Array.Empty<object>() }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var httpClient = new HttpClient(handler);
        var descriptor = new GitHubCopilotDescriptor(httpClient,
            new CopilotTokenExchanger(httpClient));

        var result = await descriptor.ProbeAsync(OAuthEntry(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(GitHubCopilotDescriptor.CuratedModels.Length, result.Models.Count);
    }

    [Fact]
    public async Task Probe_AuthExpired_ReturnsFailWithReauthGuidance()
    {
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.ToString() == TokenExchangeUrl
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var httpClient = new HttpClient(handler);
        var descriptor = new GitHubCopilotDescriptor(httpClient,
            new CopilotTokenExchanger(httpClient));

        var result = await descriptor.ProbeAsync(OAuthEntry(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("expired", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provider remove", result.ErrorMessage);
        Assert.Contains("provider add", result.ErrorMessage);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task Probe_MissingOAuthToken_FailsLoudly()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var httpClient = new HttpClient(handler);
        var descriptor = new GitHubCopilotDescriptor(httpClient,
            new CopilotTokenExchanger(httpClient));

        var entry = new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = null,
        };

        var result = await descriptor.ProbeAsync(entry,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("GitHub OAuth token", result.ErrorMessage);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task Probe_SendsCopilotRequestHeaders()
    {
        HttpRequestMessage? modelsRequest = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.ToString() == TokenExchangeUrl)
                return TokenOk();
            modelsRequest = request;
            return Json(new
            {
                data = new[] { new { id = "gpt-4o", capabilities = new { type = "chat" } } },
            });
        });

        var httpClient = new HttpClient(handler);
        var descriptor = new GitHubCopilotDescriptor(httpClient,
            new CopilotTokenExchanger(httpClient));

        await descriptor.ProbeAsync(OAuthEntry(), TestContext.Current.CancellationToken);

        Assert.NotNull(modelsRequest);
        Assert.Equal("Bearer copilot-api-token",
            modelsRequest!.Headers.Authorization!.ToString());
        Assert.Equal("vscode-chat",
            modelsRequest.Headers.GetValues("copilot-integration-id").Single());
        Assert.True(modelsRequest.Headers.Contains("editor-version"));
        Assert.Equal("conversation-agent",
            modelsRequest.Headers.GetValues("openai-intent").Single());
    }
}
