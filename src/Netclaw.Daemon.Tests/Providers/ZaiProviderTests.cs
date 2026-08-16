// -----------------------------------------------------------------------
// <copyright file="ZaiProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Netclaw.Providers.Zai;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class ZaiProviderTests
{
    [Fact]
    public async Task ProbeRequiresApiKey()
    {
        var descriptor = new ZaiDescriptor(new HttpClient());

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
            // Shape captured from the live coding-plan /models response.
            return JsonResponse("""
                {"data":[{"id":"glm-5.3"},{"id":"glm-5.2"},{"id":"glm-4.6"},{"id":"glm-5-turbo"}]}
                """);
        });
        using var http = new HttpClient(handler);
        var descriptor = new ZaiDescriptor(http);

        var result = await descriptor.ProbeAsync(new ProviderEntry
        {
            Endpoint = "https://api.z.ai/api/coding/paas/v4",
            ApiKey = new SensitiveString("test-zai-key")
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("https://api.z.ai/api/coding/paas/v4/models", captured!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("test-zai-key", captured.Headers.Authorization.Parameter);

        var flagship = Assert.Single(result.Models, model => model.ModelId.Value == "glm-5.3");
        Assert.Equal(1_000_000, flagship.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, flagship.InputModalities);
        Assert.Equal(ModelModality.Text, flagship.OutputModalities);

        var previous = Assert.Single(result.Models, model => model.ModelId.Value == "glm-5.2");
        Assert.Equal(200_000, previous.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, previous.InputModalities);
        Assert.Equal(ModelModality.Text, previous.OutputModalities);

        // Undocumented ids keep unresolved metadata — no invented fallback.
        var undocumented = Assert.Single(result.Models, model => model.ModelId.Value == "glm-4.6");
        Assert.Null(undocumented.ContextWindowTokens);
        Assert.Null(undocumented.InputModalities);
        Assert.Null(undocumented.OutputModalities);

        Assert.Null(
            Assert.Single(result.Models, model => model.ModelId.Value == "glm-5-turbo").ContextWindowTokens);
    }

    [Theory]
    [InlineData(ReasoningEffort.None, "disabled")]
    [InlineData(ReasoningEffort.Low, "enabled")]
    [InlineData(ReasoningEffort.Medium, "enabled")]
    [InlineData(ReasoningEffort.High, "enabled")]
    [InlineData(ReasoningEffort.ExtraHigh, "enabled")]
    public async Task ZaiProfileMapsReasoningEffort(ReasoningEffort effort, string thinkingType)
    {
        string? body = null;
        using var handler = new RecordingHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return ChatResponse();
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.z.ai") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            "https://api.z.ai/api/coding/paas/v4", "test-zai-key");
        var client = new OpenAiCompatibleChatClient(
            http, endpoint, "glm-5.3", OpenAiCompatibleWireProfile.Zai);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } },
            TestContext.Current.CancellationToken);

        // The coding-plan base already pins v4 — chat must not append another v1.
        Assert.Equal(
            "https://api.z.ai/api/coding/paas/v4/chat/completions",
            handler.Requests[0].RequestUri!.AbsoluteUri);
        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal(thinkingType, root.GetProperty("thinking").GetProperty("type").GetString());
        // Z.ai exposes a binary thinking toggle only; it has no reasoning_effort field.
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("test-zai-key", handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public void ZaiProfileReplaysReasoningWithToolCall()
    {
        var message = new ChatMessage(ChatRole.Assistant,
        [
            new TextReasoningContent("tool analysis"),
            new FunctionCallContent("call-1", "get_status", new Dictionary<string, object?>())
        ]);

        var serialized = OpenAiCompatibleChatClient.ToMessage(
            message, OpenAiCompatibleWireProfile.Zai);

        Assert.Equal("tool analysis", serialized["reasoning_content"]!.GetValue<string>());
        Assert.Single(serialized["tool_calls"]!.AsArray());
    }

    [Fact]
    public void ZaiPluginRejectsOAuthToken()
    {
        using var http = new HttpClient();
        var plugin = new ZaiProviderPlugin(
            new ZaiDescriptor(http),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var entry = new ProviderEntry
        {
            OAuthAccessToken = new SensitiveString("oauth-token")
        };
        var model = new ModelReference
        {
            Provider = "zai",
            ModelId = "glm-5.3"
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => plugin.CreateChatClient(entry, model));

        Assert.Contains("requires an API key", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("content_filter")]
    [InlineData("insufficient_system_resource")]
    public async Task ZaiProfilePreservesTerminalFinishReason(string finishReason)
    {
        using var handler = new RecordingHandler(_ => JsonResponse($$$"""
            {"id":"response-1","model":"glm-5.3","choices":[{"finish_reason":"{{{finishReason}}}","message":{"role":"assistant","content":null}}]}
            """));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.z.ai") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            "https://api.z.ai/api/coding/paas/v4", "test-zai-key");
        var client = new OpenAiCompatibleChatClient(
            http, endpoint, "glm-5.3", OpenAiCompatibleWireProfile.Zai);

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
        var zai = new OpenAiCompatibleChatClient(
            http, endpoint, "model", OpenAiCompatibleWireProfile.Zai);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        await DrainAsync(generic.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options,
            TestContext.Current.CancellationToken));
        await DrainAsync(zai.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options,
            TestContext.Current.CancellationToken));

        using var genericBody = JsonDocument.Parse(bodies[0]);
        Assert.True(genericBody.RootElement.GetProperty("return_progress").GetBoolean());
        Assert.False(genericBody.RootElement.TryGetProperty("thinking", out _));

        using var zaiBody = JsonDocument.Parse(bodies[1]);
        Assert.False(zaiBody.RootElement.TryGetProperty("return_progress", out _));
        Assert.Equal("enabled", zaiBody.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(zaiBody.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    private static async Task DrainAsync(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        await foreach (var _ in updates)
        {
        }
    }

    private static HttpResponseMessage ChatResponse() => JsonResponse("""
        {"id":"response-1","model":"glm-5.3","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}]}
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
