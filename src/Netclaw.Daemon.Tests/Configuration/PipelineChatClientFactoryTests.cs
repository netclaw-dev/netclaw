// -----------------------------------------------------------------------
// <copyright file="PipelineChatClientFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
