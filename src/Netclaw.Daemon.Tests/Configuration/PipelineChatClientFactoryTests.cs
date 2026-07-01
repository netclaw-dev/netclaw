// -----------------------------------------------------------------------
// <copyright file="PipelineChatClientFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;
using LoggingChatClient = Netclaw.Daemon.Configuration.LoggingChatClient;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class PipelineChatClientFactoryTests
{
    private readonly RetryPolicy _policy = new()
    {
        MaxRetries = 3,
        BaseDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(10)
    };

    [Fact]
    public void Compose_puts_Logging_outermost()
    {
        var pipeline = PipelineChatClientFactory.Compose(
            new FakeChatClient(streaming: true), _policy, NullLoggerFactory.Instance, TimeProvider.System);

        // ChatClientBuilder applies the first-registered factory outermost. Logging must
        // wrap Retry so a single completion log spans the whole retried operation — guard
        // against the .Use() ordering silently flipping on a package bump.
        Assert.IsType<LoggingChatClient>(pipeline);
    }

    [Fact]
    public async Task Compose_streams_through_and_logs_completion()
    {
        var logs = new List<string>();
        var pipeline = PipelineChatClientFactory.Compose(
            new FakeChatClient(streaming: true), _policy, new ListLoggerFactory(logs), TimeProvider.System);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in pipeline.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(u);
        }

        Assert.Single(updates);                                              // leaf reached, output flows through
        Assert.Contains(logs, l => l.Contains("LLM streaming call completed")); // Logging middleware wired
    }

    [Fact]
    public async Task Compose_streaming_tags_SessionId_scope_through_pipeline()
    {
        // Cross-cutting invariant: SessionScopedChatOptions must survive *by reference*
        // through the composed Logging -> Retry pipeline (no decorator clones it down to a
        // base ChatOptions), so the streaming production path still surfaces SessionId as a
        // Seq scope. A future decorator that rebuilt options would break this test, not just
        // a unit decorator tested in isolation.
        var logger = new ScopeCapturingLogger();
        var pipeline = PipelineChatClientFactory.Compose(
            new FakeChatClient(streaming: true), _policy, new SingleLoggerFactory(logger), TimeProvider.System);

        await foreach (var _ in pipeline.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new SessionScopedChatOptions { SessionId = "ch/thread" },
            TestContext.Current.CancellationToken))
        {
            // Drain the stream to completion so the pipeline (scope/retry/logging) runs; updates aren't asserted here.
        }

        Assert.True(logger.HasSessionScope("ch/thread"));
    }

    [Fact]
    public async Task Compose_streaming_retry_warning_inherits_SessionId_scope()
    {
        // RetryingChatClient deliberately opens no SessionId scope of its own — the retry
        // warning must still be correlated, inheriting the enclosing LoggingChatClient
        // streaming scope. Prove it at the message level: the retry-warning line is emitted
        // while the session id is the active scope.
        var logger = new MessageScopeLogger();
        var attempts = 0;
        var leaf = new FakeChatClient(streamHandler: (_, _, ct) =>
        {
            attempts++;
            return ThrowFirstThenYield(shouldThrow: attempts < 2, ct);
        });
        var pipeline = PipelineChatClientFactory.Compose(
            leaf, _policy, new SingleLoggerFactory(logger), TimeProvider.System);

        await foreach (var _ in pipeline.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new SessionScopedChatOptions { SessionId = "ch/thread" },
            TestContext.Current.CancellationToken))
        {
            // Drain the stream to completion so the pipeline (scope/retry/logging) runs; updates aren't asserted here.
        }

        var retryWarning = Assert.Single(
            logger.Entries, e => e.Message.Contains("LLM call failed (attempt", StringComparison.Ordinal));
        Assert.Equal("ch/thread", retryWarning.SessionId);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowFirstThenYield(
        bool shouldThrow, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (shouldThrow)
            throw new HttpRequestException("transient", null, HttpStatusCode.TooManyRequests);

        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
    }

    // Records, for each log call, the session id on the active scope stack — so a test can
    // assert that a specific line (not just the call as a whole) was emitted in scope.
    private sealed class MessageScopeLogger : ILogger
    {
        private readonly List<object?> _scopes = [];
        public List<(string Message, string? SessionId)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            _scopes.Add(state);
            return new Pop(_scopes);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((formatter(state, exception), ActiveSessionId()));

        private string? ActiveSessionId()
        {
            for (var i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i] is IEnumerable<KeyValuePair<string, object>> kvps)
                    foreach (var kv in kvps)
                        if (kv.Key == Netclaw.Actors.Protocol.NetclawLogProperties.SessionId && kv.Value is string s)
                            return s;
            return null;
        }

        private sealed class Pop(List<object?> scopes) : IDisposable
        {
            public void Dispose()
            {
                if (scopes.Count > 0)
                    scopes.RemoveAt(scopes.Count - 1);
            }
        }
    }

    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class ListLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _logs;
        public ListLoggerFactory(List<string> logs) => _logs = logs;
        public ILogger CreateLogger(string categoryName) => new ListLogger(_logs);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class ListLogger(List<string> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => logs.Add(formatter(state, exception));
        }
    }
}
