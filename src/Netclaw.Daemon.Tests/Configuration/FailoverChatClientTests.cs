using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class FailoverChatClientTests
{
    [Fact]
    public async Task UsesPrimary_WhenHealthy()
    {
        var primary = new FakeChatClient((_,_,_) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "primary")])));
        var fallback = new FakeChatClient((_,_,_) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "fallback")])));

        var client = new FailoverChatClient(primary, fallback, NullLogger.Instance);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("primary", response.Messages[0].Text);
    }

    [Fact]
    public async Task FallsBack_WhenPrimaryFails()
    {
        var primary = new FakeChatClient((_,_,_) =>
            throw new HttpRequestException("primary down"));
        var fallback = new FakeChatClient((_,_,_) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "fallback")])));

        var client = new FailoverChatClient(primary, fallback, NullLogger.Instance);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("fallback", response.Messages[0].Text);
    }

    [Fact]
    public async Task PropagatesException_WhenBothFail()
    {
        var primary = new FakeChatClient((_,_,_) =>
            throw new HttpRequestException("primary down"));
        var fallback = new FakeChatClient((_,_,_) =>
            throw new HttpRequestException("fallback down"));

        var client = new FailoverChatClient(primary, fallback, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Contains("fallback down", ex.Message);
    }

    [Fact]
    public async Task DoesNotFallback_OnCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var primary = new FakeChatClient((_,_,ct) =>
            throw new OperationCanceledException(ct));
        var fallback = new FakeChatClient((_,_,_) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "fallback")])));

        var client = new FailoverChatClient(primary, fallback, NullLogger.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Streaming_UsesPrimary_WhenHealthy()
    {
        var primary = new FakeChatClient(streaming: true);
        var fallback = new FakeChatClient(streaming: true);

        var client = new FailoverChatClient(primary, fallback, NullLogger.Instance);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]))
        {
            updates.Add(u);
        }

        Assert.Single(updates);
        Assert.Equal("streamed", updates[0].Text);
    }
}
