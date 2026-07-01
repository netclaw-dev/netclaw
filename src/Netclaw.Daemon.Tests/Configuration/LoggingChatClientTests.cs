// -----------------------------------------------------------------------
// <copyright file="LoggingChatClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Sessions;
using Netclaw.Daemon.Configuration;
using Xunit;
// Netclaw's LoggingChatClient collides with Microsoft.Extensions.AI.LoggingChatClient
// (the core package's own logging decorator) now that the core package is referenced.
using LoggingChatClient = Netclaw.Daemon.Configuration.LoggingChatClient;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class LoggingChatClientTests
{
    [Fact]
    public async Task Streaming_LogsCompletion()
    {
        var logs = new List<string>();
        var client = new LoggingChatClient(new FakeChatClient(streaming: true), new CapturingLogger(logs));

        await Drain(client);

        Assert.Contains(logs, l => l.Contains("LLM streaming call completed"));
    }

    [Fact]
    public async Task Streaming_LogsTokenUsageWithoutDeltaOrCumulative()
    {
        var logs = new List<string>();
        var client = new LoggingChatClient(
            new FakeChatClient(streamHandler: (_, _, _) => StreamWithUsage(100, 20)),
            new CapturingLogger(logs));

        await Drain(client);

        Assert.Contains(logs, l => l.Contains("input: 100") && l.Contains("output: 20"));
        // The cross-call delta/cumulative counters were removed (stateless client).
        Assert.DoesNotContain(logs, l => l.Contains("delta:") || l.Contains("cumulative:"));
    }

    [Fact]
    public async Task Streaming_LogsErrorOnInitFailure()
    {
        var logs = new List<string>();
        var client = new LoggingChatClient(
            new FakeChatClient(streamHandler: (_, _, _) => throw new HttpRequestException("boom")),
            new CapturingLogger(logs));

        await Assert.ThrowsAsync<HttpRequestException>(() => Drain(client));

        Assert.Contains(logs, l => l.Contains("LLM streaming call failed"));
    }

    [Fact]
    public async Task Streaming_LogsPromptSummaryInDebugMode()
    {
        var logs = new List<string>();
        var client = new LoggingChatClient(new FakeChatClient(streaming: true), new CapturingLogger(logs));

        await foreach (var _ in client.GetStreamingResponseAsync(
            [
                new ChatMessage(ChatRole.System, "sys"),
                new ChatMessage(ChatRole.User, "hello")
            ],
            new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create((string query) => query, "search_tools"),
                    AIFunctionFactory.Create((string url) => url, "browser_playwright/browser_navigate")
                ]
            }, TestContext.Current.CancellationToken))
        {
            // Drain the stream to completion so the pipeline (scope/retry/logging) runs; updates aren't asserted here.
        }

        Assert.Contains(logs, l => l.Contains("LLM prompt summary"));
        Assert.Contains(logs, l => l.Contains("promptSha256="));
        Assert.Contains(logs, l => l.Contains("browserToolOptions=1"));
    }

    [Fact]
    public async Task Streaming_LogsPromptDumpWhenTraceEnabled()
    {
        var logs = new List<string>();
        var client = new LoggingChatClient(new FakeChatClient(streaming: true), new CapturingLogger(logs));

        await Drain(client);

        Assert.Contains(logs, l => l.Contains("LLM prompt dump:"));
        Assert.Contains(logs, l => l.Contains("role=user"));
    }

    [Fact]
    public async Task Streaming_attaches_SessionId_scope_from_options()
    {
        // The decorator is session-agnostic; it learns the session from the call's
        // SessionScopedChatOptions, replacing the deleted AsyncLocal diagnostics context.
        var logger = new ScopeCapturingLogger();
        var client = new LoggingChatClient(new FakeChatClient(streaming: true), logger);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new SessionScopedChatOptions { SessionId = "ch/thread" },
            TestContext.Current.CancellationToken))
        {
            // Drain the stream to completion so the pipeline (scope/retry/logging) runs; updates aren't asserted here.
        }

        Assert.True(logger.HasSessionScope("ch/thread"));
    }

    [Fact]
    public async Task Streaming_with_plain_options_attaches_no_scope()
    {
        // A sidecar/session-agnostic call carries a plain ChatOptions: no session scope.
        var logger = new ScopeCapturingLogger();
        var client = new LoggingChatClient(new FakeChatClient(streaming: true), logger);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions(), TestContext.Current.CancellationToken))
        {
            // Drain the stream to completion so the pipeline (scope/retry/logging) runs; updates aren't asserted here.
        }

        Assert.False(logger.HasAnySessionScope());
    }

    private static async Task Drain(IChatClient client)
    {
        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken))
        {
            // Drain the stream to completion so the pipeline (scope/retry/logging) runs; updates aren't asserted here.
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithUsage(int inputTokens, int outputTokens)
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("response")] };
        yield return new ChatResponseUpdate
        {
            Contents = [new UsageContent(new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens })]
        };
    }

    /// <summary>
    /// Minimal logger that captures formatted messages for assertion.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _messages;

        public CapturingLogger(List<string> messages)
        {
            _messages = messages;
        }

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
