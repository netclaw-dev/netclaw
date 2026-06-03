// -----------------------------------------------------------------------
// <copyright file="RoutingChatClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RoutingChatClientTests
{
    private static RoutingChatClient Client(IOperationalNotificationSink sink, params IChatClient[] candidates) =>
        new(new StubRouter(candidates), new ChatRoutingContext { Role = ModelRole.Main },
            sink, NullLogger.Instance, TimeProvider.System);

    [Fact]
    public async Task UsesPrimary_WhenHealthy()
    {
        var sink = new CapturingSink();
        var primary = new FakeChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "primary")])));
        var fallback = new FakeChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "fallback")])));

        var response = await Client(sink, primary, fallback)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("primary", response.Messages[0].Text);
        Assert.Empty(sink.Alerts);
    }

    [Fact]
    public async Task FailsOverToNextCandidate_WhenPrimaryFails()
    {
        var sink = new CapturingSink();
        var primary = new FakeChatClient((_, _, _) => throw new HttpRequestException("primary down"));
        var fallback = new FakeChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "fallback")])));

        var response = await Client(sink, primary, fallback)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fallback", response.Messages[0].Text);
        Assert.Contains(sink.Alerts, a => a.Category == AlertType.ProviderFailover);
        Assert.DoesNotContain(sink.Alerts, a => a.Category == AlertType.ProviderUnreachable);
    }

    [Fact]
    public async Task EmitsUnreachable_WhenAllCandidatesFail()
    {
        var sink = new CapturingSink();
        var primary = new FakeChatClient((_, _, _) => throw new HttpRequestException("primary down"));
        var fallback = new FakeChatClient((_, _, _) => throw new HttpRequestException("fallback down"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(sink, primary, fallback)
                .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("fallback down", ex.Message);
        Assert.Contains(sink.Alerts, a => a.Category == AlertType.ProviderFailover);
        Assert.Contains(sink.Alerts, a => a.Category == AlertType.ProviderUnreachable);
    }

    [Fact]
    public async Task SingleCandidateFailure_EmitsUnreachable_NotFailover()
    {
        var sink = new CapturingSink();
        var only = new FakeChatClient((_, _, _) => throw new HttpRequestException("down"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(sink, only)
                .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(sink.Alerts, a => a.Category == AlertType.ProviderUnreachable);
        Assert.DoesNotContain(sink.Alerts, a => a.Category == AlertType.ProviderFailover);
    }

    [Fact]
    public async Task DoesNotFailover_OnCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var sink = new CapturingSink();
        var fallbackCalls = 0;
        var primary = new FakeChatClient((_, _, ct) => throw new OperationCanceledException(ct));
        var fallback = new FakeChatClient((_, _, _) =>
        {
            fallbackCalls++;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "fallback")]));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(sink, primary, fallback)
                .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token));

        Assert.Equal(0, fallbackCalls);
        Assert.Empty(sink.Alerts);
    }

    [Fact]
    public async Task Streaming_UsesPrimary_WhenHealthy()
    {
        var sink = new CapturingSink();
        var primary = new FakeChatClient(streamHandler: (_, _, ct) => SingleTextUpdateAsync("primary", ct));
        var fallback = new FakeChatClient(streamHandler: (_, _, ct) => SingleTextUpdateAsync("fallback", ct));

        var texts = await CollectText(Client(sink, primary, fallback));

        Assert.Equal(["primary"], texts);
        Assert.Empty(sink.Alerts);
    }

    [Fact]
    public async Task Streaming_FailsOver_WhenPrimaryFailsBeforeFirstChunk()
    {
        var sink = new CapturingSink();
        var fallbackCalls = 0;
        var primary = new FakeChatClient(streamHandler: (_, _, ct) => ThrowBeforeFirstChunkAsync(true, ct));
        var fallback = new FakeChatClient(streamHandler: (_, _, ct) =>
        {
            fallbackCalls++;
            return SingleTextUpdateAsync("fallback", ct);
        });

        var texts = await CollectText(Client(sink, primary, fallback));

        Assert.Equal(["fallback"], texts);
        Assert.Equal(1, fallbackCalls);
        Assert.Contains(sink.Alerts, a => a.Category == AlertType.ProviderFailover);
    }

    [Fact]
    public async Task Streaming_DoesNotFailover_AfterPrimaryAlreadyYielded()
    {
        var sink = new CapturingSink();
        var fallbackCalls = 0;
        var primary = new FakeChatClient(streamHandler: (_, _, ct) => YieldThenThrowAsync(ct));
        var fallback = new FakeChatClient(streamHandler: (_, _, ct) =>
        {
            fallbackCalls++;
            return SingleTextUpdateAsync("fallback", ct);
        });
        var texts = new List<string>();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var u in Client(sink, primary, fallback).GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
            {
                foreach (var c in u.Contents)
                    if (c is TextContent t) texts.Add(t.Text);
            }
        });

        Assert.Contains("after first chunk", ex.Message);
        Assert.Equal(["primary"], texts);
        Assert.Equal(0, fallbackCalls);
    }

    [Fact]
    public async Task Streaming_EmitsUnreachable_WhenAllCandidatesFailBeforeFirstChunk()
    {
        var sink = new CapturingSink();
        var primary = new FakeChatClient(streamHandler: (_, _, ct) => ThrowBeforeFirstChunkAsync(true, ct));
        var fallback = new FakeChatClient(streamHandler: (_, _, ct) => ThrowBeforeFirstChunkAsync(true, ct));

        await Assert.ThrowsAsync<HttpRequestException>(() => CollectText(Client(sink, primary, fallback)));

        Assert.Contains(sink.Alerts, a => a.Category == AlertType.ProviderFailover);
        Assert.Contains(sink.Alerts, a => a.Category == AlertType.ProviderUnreachable);
    }

    [Fact]
    public async Task Streaming_SingleCandidateFailure_EmitsUnreachable_NotFailover()
    {
        var sink = new CapturingSink();
        var only = new FakeChatClient(streamHandler: (_, _, ct) => ThrowBeforeFirstChunkAsync(true, ct));

        await Assert.ThrowsAsync<HttpRequestException>(() => CollectText(Client(sink, only)));

        Assert.Contains(sink.Alerts, a => a.Category == AlertType.ProviderUnreachable);
        Assert.DoesNotContain(sink.Alerts, a => a.Category == AlertType.ProviderFailover);
    }

    [Fact]
    public async Task Streaming_DoesNotFailover_OnCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var sink = new CapturingSink();
        var fallbackCalls = 0;
        var primary = new FakeChatClient(streamHandler: (_, _, ct) => ThrowBeforeFirstChunkAsync(true, ct));
        var fallback = new FakeChatClient(streamHandler: (_, _, ct) =>
        {
            fallbackCalls++;
            return SingleTextUpdateAsync("fallback", ct);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Client(sink, primary, fallback).GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token)) { }
        });

        Assert.Equal(0, fallbackCalls);
        Assert.Empty(sink.Alerts);
    }

    private static async Task<List<string>> CollectText(IChatClient client)
    {
        var texts = new List<string>();
        await foreach (var u in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
        {
            foreach (var c in u.Contents)
                if (c is TextContent t) texts.Add(t.Text);
        }

        return texts;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowBeforeFirstChunkAsync(
        bool shouldThrow, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (shouldThrow)
            throw new HttpRequestException("primary stream failed before first chunk");

        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("unused")] };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldThenThrowAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("primary")] };
        throw new HttpRequestException("primary stream failed after first chunk");
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> SingleTextUpdateAsync(
        string text, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };
    }

    private sealed class StubRouter(IReadOnlyList<IChatClient> candidates) : IChatClientRouter
    {
        public IReadOnlyList<IChatClient> Route(ChatRoutingContext context) => candidates;
    }

    private sealed class CapturingSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];
        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }
}
