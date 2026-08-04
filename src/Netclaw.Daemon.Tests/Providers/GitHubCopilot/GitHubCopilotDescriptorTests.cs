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

    // A realistic token exchange response: GitHub always reports the API host in
    // endpoints.api. For a standard account that host is api.githubcopilot.com,
    // which keeps ModelsUrl below correct.
    private static HttpResponseMessage TokenOk() => Json(new
    {
        token = "copilot-api-token",
        expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
        endpoints = new { api = "https://api.githubcopilot.com" },
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
    public void ParseCopilotModelCapabilities_RetainsHttpEndpointsWithoutConfusingWebSocketResponses()
    {
        var capabilities = GitHubCopilotDescriptor.ParseCopilotModelCapabilities(
            """
            { "data": [
              { "id": "responses-only", "capabilities": { "type": "chat" }, "supported_endpoints": ["/responses", "ws:/responses"] },
              { "id": "chat-only", "capabilities": { "type": "chat" }, "supported_endpoints": ["/chat/completions"] },
              { "id": "dual", "capabilities": { "type": "chat" }, "supported_endpoints": ["/responses", "/chat/completions"] },
              { "id": "not-chat", "capabilities": { "type": "embeddings" }, "supported_endpoints": ["/responses"] },
              { "id": "hidden", "capabilities": { "type": "chat" }, "model_picker_enabled": false, "supported_endpoints": ["/responses"] }
            ] }
            """);

        Assert.Equal(3, capabilities.Count);
        var responsesOnly = Assert.Single(capabilities, capability => capability.ModelId == "responses-only");
        Assert.True(responsesOnly.SupportsResponses);
        Assert.False(responsesOnly.SupportsChatCompletions);
        Assert.Contains("ws:/responses", responsesOnly.SupportedEndpoints);
        Assert.Equal(GitHubCopilotApiKind.Responses, responsesOnly.PreferredApi);
        Assert.Equal(GitHubCopilotApiKind.ChatCompletions,
            Assert.Single(capabilities, capability => capability.ModelId == "chat-only").PreferredApi);
        Assert.Equal(GitHubCopilotApiKind.Responses,
            Assert.Single(capabilities, capability => capability.ModelId == "dual").PreferredApi);
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
    public async Task Probe_AuthError_SurfacesFailureInsteadOfCuratedFallback()
    {
        // A 401 on /models means the token/tenant is wrong — a real
        // misconfiguration that must surface at `provider add`, not be masked by
        // the curated list only to fail on the first chat (issue #1550).
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.ToString() switch
            {
                TokenExchangeUrl => TokenOk(),
                ModelsUrl => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var httpClient = new HttpClient(handler);
        var descriptor = new GitHubCopilotDescriptor(httpClient,
            new CopilotTokenExchanger(httpClient));

        var result = await descriptor.ProbeAsync(OAuthEntry(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.DoesNotContain("curated fallback", result.ErrorMessage ?? string.Empty);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task Probe_NoApiHostWhileFollowingToken_SurfacesFailure()
    {
        // The exchange succeeds but reports no endpoints.api. We must not silently
        // probe the public default (issue #1550) — surface the anomaly. The
        // /models handler returns success on purpose: if the code wrongly fell
        // back to api.githubcopilot.com the probe would pass, so asserting failure
        // proves we did not guess a host.
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.ToString() == TokenExchangeUrl
                ? Json(new
                {
                    token = "copilot-api-token",
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                })
                : Json(new
                {
                    data = new[] { new { id = "gpt-4o", capabilities = new { type = "chat" } } },
                }));

        var httpClient = new HttpClient(handler);
        var descriptor = new GitHubCopilotDescriptor(httpClient,
            new CopilotTokenExchanger(httpClient));

        var result = await descriptor.ProbeAsync(OAuthEntry(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("endpoints.api", result.ErrorMessage);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task Probe_ConnectionFailureToTokenHost_SurfacesFailure()
    {
        // An unreachable tenant host (endpoints.api) must surface at setup, not be
        // masked by the curated fallback — otherwise the provider reports healthy
        // and only fails on the first chat, the exact symptom of issue #1550.
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.ToString() == TokenExchangeUrl)
                return Json(new
                {
                    token = "copilot-api-token",
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                    endpoints = new { api = "https://api.unreachable.ghe.com" },
                });

            throw new HttpRequestException("No such host is known.");
        });

        var httpClient = new HttpClient(handler);
        var descriptor = new GitHubCopilotDescriptor(httpClient,
            new CopilotTokenExchanger(httpClient));

        var result = await descriptor.ProbeAsync(OAuthEntry(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.DoesNotContain("curated fallback", result.ErrorMessage ?? string.Empty);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task Probe_ProbesModelsAtTokenApiHost()
    {
        // GHE data residency: /models must be probed at the tenant host reported
        // in endpoints.api, not the public api.githubcopilot.com (issue #1550).
        string? probedModelsUrl = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == TokenExchangeUrl)
                return Json(new
                {
                    token = "copilot-api-token",
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                    endpoints = new { api = "https://api.tenant.ghe.com" },
                });

            probedModelsUrl = url;
            return Json(new
            {
                data = new[] { new { id = "gpt-4o", capabilities = new { type = "chat" } } },
            });
        });

        var httpClient = new HttpClient(handler);
        var descriptor = new GitHubCopilotDescriptor(httpClient,
            new CopilotTokenExchanger(httpClient));

        var result = await descriptor.ProbeAsync(OAuthEntry(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("https://api.tenant.ghe.com/models", probedModelsUrl);
    }

    [Fact]
    public async Task Probe_CustomEndpointOverride_ProbesConfiguredHostNotTokenApi()
    {
        // A deliberate proxy override must win over the token's endpoints.api on
        // the probe path too, matching the chat path.
        string? probedModelsUrl = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == TokenExchangeUrl)
                return Json(new
                {
                    token = "copilot-api-token",
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                    endpoints = new { api = "https://api.tenant.ghe.com" },
                });

            probedModelsUrl = url;
            return Json(new
            {
                data = new[] { new { id = "gpt-4o", capabilities = new { type = "chat" } } },
            });
        });

        var httpClient = new HttpClient(handler);
        var descriptor = new GitHubCopilotDescriptor(httpClient,
            new CopilotTokenExchanger(httpClient));
        var entry = OAuthEntry();
        entry.Endpoint = "https://copilot-proxy.example.com";

        var result = await descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("https://copilot-proxy.example.com/models", probedModelsUrl);
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
