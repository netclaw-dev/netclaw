// -----------------------------------------------------------------------
// <copyright file="RetryingChatClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RetryingChatClientTests
{
    private readonly RetryPolicy _policy = new()
    {
        MaxRetries = 3,
        BaseDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(10)
    };

    public static TheoryData<string, Func<Exception>, int, int> RetryableTransientFailureCases { get; } = new()
    {
        {
            "429",
            () => new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests),
            2,
            3
        },
        {
            "500",
            () => new HttpRequestException("server error", null, HttpStatusCode.InternalServerError),
            1,
            2
        },
        {
            "StatuslessHttpRequestException",
            () => new HttpRequestException("connection reset"),
            1,
            2
        },
        {
            "TaskCanceledTimeout",
            () => new TaskCanceledException("request timed out"),
            1,
            2
        }
    };

    [Theory]
    [MemberData(nameof(RetryableTransientFailureCases))]
    public async Task RetriesOnTransientFailure_ThenSucceeds(
        string name,
        Func<Exception> makeException,
        int failuresBeforeSuccess,
        int expectedAttempts)
    {
        var attempts = 0;
        var fake = new FakeChatClient((_, _, _) =>
        {
            attempts++;
            if (attempts <= failuresBeforeSuccess)
                throw makeException();
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        });

        var client = new RetryingChatClient(fake, _policy, NullLogger.Instance);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Messages[0].Text);
        Assert.True(attempts == expectedAttempts, $"case {name}: expected {expectedAttempts} attempts, got {attempts}");
    }

    [Fact]
    public async Task StopsAfterMaxRetries()
    {
        var attempts = 0;
        var fake = new FakeChatClient((_,_,_) =>
        {
            attempts++;
            throw new HttpRequestException("always fails", null, HttpStatusCode.TooManyRequests);
        });

        var client = new RetryingChatClient(fake, _policy, NullLogger.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));

        // 1 initial + 3 retries = 4 total
        Assert.Equal(4, attempts);
    }

    // NOTE: RetryingChatClient deliberately does NOT open its own SessionId scope — it
    // inherits the enclosing LoggingChatClient scope in the composed pipeline. The retry
    // warning's session correlation is therefore covered by
    // PipelineChatClientFactoryTests.Compose_streaming_retry_warning_inherits_SessionId_scope,
    // which exercises the real composition, not this decorator in isolation.

    [Fact]
    public async Task DoesNotRetryNonTransientErrors()
    {
        var attempts = 0;
        var fake = new FakeChatClient((_,_,_) =>
        {
            attempts++;
            throw new HttpRequestException("bad request", null, HttpStatusCode.BadRequest);
        });

        var client = new RetryingChatClient(fake, _policy, NullLogger.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts); // No retries for 400
    }

    [Fact]
    public async Task DoesNotRetry_WhenCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var attempts = 0;
        var fake = new FakeChatClient((_,_,_) =>
        {
            attempts++;
            throw new HttpRequestException("server error", null, HttpStatusCode.InternalServerError);
        });

        var client = new RetryingChatClient(fake, _policy, NullLogger.Instance);

        // Should propagate the exception without retrying since CT is already cancelled
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token));

        Assert.Equal(1, attempts); // No retries when cancelled
    }

    [Fact]
    public async Task StreamingRetriesPreFirstChunk_ThenSucceeds()
    {
        var attempts = 0;
        var fake = new FakeChatClient(streamHandler: (_, _, ct) =>
        {
            attempts++;
            return ThrowBeforeChunkThenYield(attempts, failUntil: 3, ct);
        });
        var client = new RetryingChatClient(fake, _policy, NullLogger.Instance);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(u);
        }

        Assert.Single(updates);     // only the successful attempt yields
        Assert.Equal(3, attempts);  // 2 pre-chunk failures + 1 success, each re-initiates the stream
    }

    [Fact]
    public async Task StreamingDoesNotRetryAfterFirstChunk()
    {
        var attempts = 0;
        var fake = new FakeChatClient(streamHandler: (_, _, ct) =>
        {
            attempts++;
            return YieldThenThrow(ct);
        });
        var client = new RetryingChatClient(fake, _policy, NullLogger.Instance);

        var updates = new List<ChatResponseUpdate>();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var u in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
            {
                updates.Add(u);
            }
        });

        // The 500 is retryable by policy, but a chunk was already emitted, so the
        // failure propagates instead of restarting (no duplicate output).
        Assert.Single(updates);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task StreamingStopsAfterMaxRetries()
    {
        var attempts = 0;
        var fake = new FakeChatClient(streamHandler: (_, _, ct) =>
        {
            attempts++;
            return ThrowBeforeChunkThenYield(attempts, failUntil: 100, ct);
        });
        var client = new RetryingChatClient(fake, _policy, NullLogger.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken)) { }
        });

        Assert.Equal(4, attempts); // 1 initial + 3 retries
    }

    [Fact]
    public async Task StreamingDoesNotRetry_WhenCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var attempts = 0;
        var fake = new FakeChatClient(streamHandler: (_, _, ct) =>
        {
            attempts++;
            return ThrowBeforeChunkThenYield(attempts, failUntil: 100, ct);
        });
        var client = new RetryingChatClient(fake, _policy, NullLogger.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token)) { }
        });

        Assert.Equal(1, attempts); // cancellation is not retried
    }

    [Fact]
    public async Task StreamingRetries_ProviderException5xx_ThenSucceeds()
    {
        // Curated provider errors carry the status on a ProviderException, not a raw
        // HttpRequestException — the transport must still recognize them as transient.
        var attempts = 0;
        var fake = new FakeChatClient(streamHandler: (_, _, ct) =>
        {
            attempts++;
            return ThrowProviderExceptionThenYield(attempts, failUntil: 3, ct);
        });
        var client = new RetryingChatClient(fake, _policy, NullLogger.Instance);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(u);
        }

        Assert.Single(updates);
        Assert.Equal(3, attempts); // 2 ProviderException(502) failures + 1 success
    }

    // Throws a retryable 429 before yielding any chunk while attemptNumber < failUntil,
    // otherwise yields one chunk. The runtime-dependent condition keeps the yield
    // reachable (no CS0162) so no warning suppression is needed.
    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowBeforeChunkThenYield(
        int attemptNumber, int failUntil,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (attemptNumber < failUntil)
            throw new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);

        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
    }

    // Same shape as ThrowBeforeChunkThenYield but throws a curated ProviderException(502).
    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowProviderExceptionThenYield(
        int attemptNumber, int failUntil,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (attemptNumber < failUntil)
            throw new ProviderException("server error (502)", "HTTP 502", statusCode: 502);

        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldThenThrow(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("partial")] };
        throw new HttpRequestException("mid-stream failure", null, HttpStatusCode.InternalServerError);
    }
}

/// <summary>
/// Minimal IChatClient test double.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? _handler;
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? _streamHandler;

    public FakeChatClient(
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? handler = null,
        bool streaming = false,
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? streamHandler = null)
    {
        _ = streaming;
        _handler = handler;
        _streamHandler = streamHandler;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_handler is not null)
            return _handler(messages, options, cancellationToken);

        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "default")]));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_streamHandler is not null)
            return _streamHandler(messages, options, cancellationToken);

        return StreamAsync();
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync()
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("streamed")]
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
