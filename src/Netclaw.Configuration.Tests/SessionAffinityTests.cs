using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SessionAffinityTests
{
    [Fact]
    public async Task Handler_adds_header_when_context_is_set()
    {
        var captured = new CaptureHandler();
        using var handler = new SessionAffinityHandler(captured);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost")
        };

        SessionAffinityContext.SessionId = "C99999/1708531200.000100";
        try
        {
            await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions"),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            SessionAffinityContext.SessionId = null;
        }

        Assert.NotNull(captured.LastRequest);
        Assert.True(captured.LastRequest!.Headers.Contains(SessionAffinityHandler.HeaderName));
        Assert.Equal("C99999/1708531200.000100",
            captured.LastRequest.Headers.GetValues(SessionAffinityHandler.HeaderName).Single());
    }

    [Fact]
    public async Task Handler_omits_header_when_context_is_null()
    {
        var captured = new CaptureHandler();
        using var handler = new SessionAffinityHandler(captured);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost")
        };

        SessionAffinityContext.SessionId = null;
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured.LastRequest);
        Assert.False(captured.LastRequest!.Headers.Contains(SessionAffinityHandler.HeaderName));
    }

    [Fact]
    public async Task Context_flows_through_async_boundary()
    {
        var captured = new CaptureHandler();
        using var handler = new SessionAffinityHandler(captured);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost")
        };

        SessionAffinityContext.SessionId = "test/async-flow";
        try
        {
            await Task.Yield();
            await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions"),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            SessionAffinityContext.SessionId = null;
        }

        Assert.Equal("test/async-flow",
            captured.LastRequest!.Headers.GetValues(SessionAffinityHandler.HeaderName).Single());
    }

    [Fact]
    public async Task Header_flows_through_OpenAiCompatibleChatClient()
    {
        // Integration test: proves the AsyncLocal survives through the real
        // OpenAiCompatibleChatClient → HttpClient → SessionAffinityHandler
        // chain, which is the production code path.
        var captured = new CaptureHandler(FakeOpenAiResponse);
        using var handler = new SessionAffinityHandler(captured);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost")
        };

        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        SessionAffinityContext.SessionId = "C12345/1708531200.000100";
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, "hello")
            };
            await client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            SessionAffinityContext.SessionId = null;
        }

        Assert.NotNull(captured.LastRequest);
        Assert.True(captured.LastRequest!.Headers.Contains(SessionAffinityHandler.HeaderName),
            "X-Session-Id header should be present on the HTTP request sent by OpenAiCompatibleChatClient");
        Assert.Equal("C12345/1708531200.000100",
            captured.LastRequest.Headers.GetValues(SessionAffinityHandler.HeaderName).Single());
    }

    private static readonly string FakeOpenAiResponse = """
        {
            "id": "chatcmpl-test",
            "object": "chat.completion",
            "created": 1700000000,
            "model": "test-model",
            "choices": [{
                "index": 0,
                "message": { "role": "assistant", "content": "hello back" },
                "finish_reason": "stop"
            }],
            "usage": { "prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15 }
        }
        """;

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly string? _responseBody;

        public HttpRequestMessage? LastRequest { get; private set; }

        public CaptureHandler(string? responseBody = null)
        {
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            if (_responseBody is not null)
                response.Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }
}
