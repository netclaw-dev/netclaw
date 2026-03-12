using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Netclaw.OpenAICompatible;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class OpenAiCompatibleChatClientTests
{
    [Fact]
    public async Task UsesOfficialApiV1Paths_ForBareEndpoint()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"1\",\"model\":\"test\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}", Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal("/api/v1/chat/completions", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal("hi", response.Text);
    }

    [Fact]
    public async Task StreamsReasoningAndTextDeltas_FromOfficialSpectrum()
    {
        const string sse = """
data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":null}}]}

data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"reasoning_content":"Thinking"}}]}

data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hello"}}]}

data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]

""";

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000/api/v1");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            updates.Add(update);

        Assert.Equal(3, updates.Count);
        Assert.Contains(updates, u => u.Contents.OfType<TextReasoningContent>().Any(c => c.Text == "Thinking"));
        Assert.Contains(updates, u => u.Contents.OfType<TextContent>().Any(c => c.Text == "Hello"));
        Assert.Contains(updates, u => u.FinishReason == ChatFinishReason.Stop);
    }

    [Fact]
    public async Task SerializesTools_InOpenAiFunctionFormat()
    {
        string? body = null;
        using var handler = new RecordingHandler(req =>
        {
            body = req.Content is null ? null : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"1\",\"model\":\"test\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}", Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var tool = AIFunctionFactory.CreateDeclaration(
            "search_tools",
            "Search tools",
            System.Text.Json.JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}").RootElement);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Tools = [tool] });

        Assert.NotNull(body);
        Assert.Contains("\"tools\":[{\"type\":\"function\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"search_tools\"", body, StringComparison.Ordinal);
        Assert.Contains("\"required\":[\"query\"]", body, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }
}
