// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleHttpTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.SelfHosted;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenAiCompatibleHttpTests
{
    [Theory]
    [InlineData(AuthMethod.ApiKey, "Bearer test-key")]
    [InlineData(AuthMethod.None, null)]
    public async Task DescriptorProbe_UsesSelectedAuthentication(
        AuthMethod authMethod,
        string? expectedAuthorization)
    {
        string? authorization = null;
        using var handler = new FakeHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return FakeHttpMessageHandler.JsonResponse(new
            {
                data = new[] { new { id = "test-model" } }
            });
        });
        using var httpClient = new HttpClient(handler);
        var descriptor = new OpenAiCompatibleDescriptor(httpClient);
        var entry = new ProviderEntry
        {
            Type = "openai-compatible",
            Endpoint = "https://gateway.example.test/v1",
            AuthMethod = authMethod,
            ApiKey = new SensitiveString("test-key")
        };

        var result = await descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(expectedAuthorization, authorization);
    }

    [Fact]
    public async Task RegistryCompatibilityProbe_SelectsApiKeyAuthForSuppliedKey()
    {
        string? authorization = null;
        using var handler = new FakeHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return FakeHttpMessageHandler.JsonResponse(new
            {
                data = new[] { new { id = "test-model" } }
            });
        });
        using var httpClient = new HttpClient(handler);
        var registry = new ProviderDescriptorRegistry(
            [new OpenAiCompatibleDescriptor(httpClient)]);

        var result = await registry.ProbeAsync(
            "openai-compatible",
            "https://gateway.example.test/v1",
            "test-key",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("Bearer test-key", authorization);
    }

    [Theory]
    [InlineData("test-key", "Bearer test-key")]
    [InlineData(null, null)]
    public async Task ModelsClient_UsesConfiguredBearerHeader(
        string? apiKey,
        string? expectedAuthorization)
    {
        string? authorization = null;
        using var handler = new FakeHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return FakeHttpMessageHandler.JsonResponse(new
            {
                data = new[] { new { id = "test-model" } }
            });
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://gateway.example.test")
        };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            "https://gateway.example.test/v1",
            apiKey);
        var client = new OpenAiCompatibleModelsClient(httpClient, endpoint);

        var models = await client.ListModelIdsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["test-model"], models);
        Assert.Equal(expectedAuthorization, authorization);
    }

    [Theory]
    [InlineData("test-key", "Bearer test-key")]
    [InlineData(null, null)]
    public async Task CapabilityResolver_UsesConfiguredBearerHeaderForBothRequests(
        string? apiKey,
        string? expectedAuthorization)
    {
        var authorizations = new List<string?>();
        using var handler = new FakeHttpMessageHandler(request =>
        {
            authorizations.Add(request.Headers.Authorization?.ToString());
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/models" => FakeHttpMessageHandler.JsonResponse(new
                {
                    data = new[] { new { id = "test-model" } }
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var httpClient = new HttpClient(handler);
        var resolver = new OpenAiCompatibleCapabilityResolver(
            httpClient,
            NullLogger<OpenAiCompatibleCapabilityResolver>.Instance,
            "https://gateway.example.test/v1",
            apiKey);

        var result = await resolver.ResolveAsync("test-model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal([expectedAuthorization, expectedAuthorization], authorizations);
    }

    [Theory]
    [InlineData("test-key", "Bearer test-key")]
    [InlineData(null, null)]
    public async Task ChatClient_UsesConfiguredBearerHeader(
        string? apiKey,
        string? expectedAuthorization)
    {
        string? authorization = null;
        using var handler = new FakeHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"response-1","model":"test-model","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}]}
                    """, Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://gateway.example.test")
        };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            "https://gateway.example.test/v1",
            apiKey);
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expectedAuthorization, authorization);
    }
}
