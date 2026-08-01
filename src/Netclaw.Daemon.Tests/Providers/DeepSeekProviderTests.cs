// -----------------------------------------------------------------------
// <copyright file="DeepSeekProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers.DeepSeek;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class DeepSeekProviderTests
{
    [Fact]
    public async Task ProbeRequiresApiKey()
    {
        var descriptor = new DeepSeekDescriptor(new HttpClient());

        var result = await descriptor.ProbeAsync(
            new ProviderEntry(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("API key is required", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeSendsBearerTokenAndAddsKnownMetadata()
    {
        HttpRequestMessage? captured = null;
        using var handler = new RecordingHandler(request =>
        {
            captured = request;
            return JsonResponse("""
                {"data":[{"id":"deepseek-v4-pro"},{"id":"future-model"}]}
                """);
        });
        using var http = new HttpClient(handler);
        var descriptor = new DeepSeekDescriptor(http);

        var result = await descriptor.ProbeAsync(new ProviderEntry
        {
            Endpoint = "https://api.deepseek.com/v1",
            ApiKey = new SensitiveString("test-deepseek-key")
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("https://api.deepseek.com/v1/models", captured!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("test-deepseek-key", captured.Headers.Authorization.Parameter);

        var known = Assert.Single(result.Models, model => model.ModelId.Value == "deepseek-v4-pro");
        Assert.Equal(1_000_000, known.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, known.InputModalities);
        Assert.Equal(ModelModality.Text, known.OutputModalities);

        var unknown = Assert.Single(result.Models, model => model.ModelId.Value == "future-model");
        Assert.Null(unknown.ContextWindowTokens);
        Assert.Null(unknown.InputModalities);
        Assert.Null(unknown.OutputModalities);
    }

    [Theory]
    [InlineData(ReasoningEffort.None, "disabled", null)]
    [InlineData(ReasoningEffort.Low, "enabled", "high")]
    [InlineData(ReasoningEffort.Medium, "enabled", "high")]
    [InlineData(ReasoningEffort.High, "enabled", "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "enabled", "max")]
    public async Task DeepSeekProfileMapsReasoningEffort(
        ReasoningEffort effort,
        string thinkingType,
        string? expectedEffort)
    {
        string? body = null;
        using var handler = new RecordingHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return ChatResponse();
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.deepseek.com") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            "https://api.deepseek.com/v1", "test-deepseek-key");
        var client = new OpenAiCompatibleChatClient(
            http, endpoint, "deepseek-v4-pro", OpenAiCompatibleWireProfile.DeepSeek);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } },
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal(thinkingType, root.GetProperty("thinking").GetProperty("type").GetString());
        if (expectedEffort is null)
            Assert.False(root.TryGetProperty("reasoning_effort", out _));
        else
            Assert.Equal(expectedEffort, root.GetProperty("reasoning_effort").GetString());
        Assert.Equal("test-deepseek-key", handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public void DeepSeekProfileReplaysReasoningWithToolCall()
    {
        var message = new ChatMessage(ChatRole.Assistant,
        [
            new TextReasoningContent("tool analysis"),
            new FunctionCallContent("call-1", "get_status", new Dictionary<string, object?>())
        ]);

        var serialized = OpenAiCompatibleChatClient.ToMessage(
            message, OpenAiCompatibleWireProfile.DeepSeek);

        Assert.Equal("tool analysis", serialized["reasoning_content"]!.GetValue<string>());
        Assert.Single(serialized["tool_calls"]!.AsArray());
    }

    [Fact]
    public void DeepSeekPluginRejectsOAuthToken()
    {
        using var http = new HttpClient();
        var plugin = new DeepSeekProviderPlugin(
            new DeepSeekDescriptor(http),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var entry = new ProviderEntry
        {
            OAuthAccessToken = new SensitiveString("oauth-token")
        };
        var model = new ModelReference
        {
            Provider = "deepseek",
            ModelId = "deepseek-v4-pro"
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => plugin.CreateChatClient(entry, model));

        Assert.Contains("requires an API key", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("content_filter")]
    [InlineData("insufficient_system_resource")]
    public async Task DeepSeekProfilePreservesTerminalFinishReason(string finishReason)
    {
        using var handler = new RecordingHandler(_ => JsonResponse($$$"""
            {"id":"response-1","model":"deepseek-v4-pro","choices":[{"finish_reason":"{{{finishReason}}}","message":{"role":"assistant","content":null}}]}
            """));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.deepseek.com") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            "https://api.deepseek.com/v1", "test-deepseek-key");
        var client = new OpenAiCompatibleChatClient(
            http, endpoint, "deepseek-v4-pro", OpenAiCompatibleWireProfile.DeepSeek);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(finishReason, response.FinishReason?.Value);
    }

    [Fact]
    public async Task WireProfilesKeepProviderFieldsIsolated()
    {
        var bodies = new List<string>();
        using var handler = new RecordingHandler(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("https://example.test/v1");
        var generic = new OpenAiCompatibleChatClient(
            http, endpoint, "model", OpenAiCompatibleWireProfile.Generic);
        var deepSeek = new OpenAiCompatibleChatClient(
            http, endpoint, "model", OpenAiCompatibleWireProfile.DeepSeek);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        await DrainAsync(generic.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options,
            TestContext.Current.CancellationToken));
        await DrainAsync(deepSeek.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options,
            TestContext.Current.CancellationToken));

        using var genericBody = JsonDocument.Parse(bodies[0]);
        Assert.True(genericBody.RootElement.GetProperty("return_progress").GetBoolean());
        Assert.False(genericBody.RootElement.TryGetProperty("thinking", out _));

        using var deepSeekBody = JsonDocument.Parse(bodies[1]);
        Assert.False(deepSeekBody.RootElement.TryGetProperty("return_progress", out _));
        Assert.Equal("enabled", deepSeekBody.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    private static async Task DrainAsync(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        await foreach (var _ in updates)
        {
        }
    }

    private static HttpResponseMessage ChatResponse() => JsonResponse("""
        {"id":"response-1","model":"deepseek-v4-pro","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}]}
        """);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }
}
