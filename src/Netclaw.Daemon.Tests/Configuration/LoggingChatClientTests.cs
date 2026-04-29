// -----------------------------------------------------------------------
// <copyright file="LoggingChatClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class LoggingChatClientTests
{
    [Fact]
    public async Task LogsCompletionTime()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger(logs);
        var fake = new FakeChatClient();

        var client = new LoggingChatClient(fake, logger);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Contains(logs, l => l.Contains("LLM call completed"));
    }

    [Fact]
    public async Task LogsTokenUsage()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger(logs);
        var fake = new FakeChatClient((_,_,_) =>
        {
            var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            {
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 20 }
            };
            return Task.FromResult(response);
        });

        var client = new LoggingChatClient(fake, logger);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(logs, l => l.Contains("input: 10") && l.Contains("output: 20"));
    }

    [Fact]
    public async Task LogsErrorOnFailure()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger(logs);
        var fake = new FakeChatClient((_,_,_) =>
            throw new HttpRequestException("boom"));

        var client = new LoggingChatClient(fake, logger);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(logs, l => l.Contains("LLM call failed"));
    }

    [Fact]
    public async Task Streaming_LogsCompletion()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger(logs);
        var fake = new FakeChatClient(streaming: true);

        var client = new LoggingChatClient(fake, logger);
        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken)) { }

        Assert.Contains(logs, l => l.Contains("LLM streaming call completed"));
    }

    [Fact]
    public async Task LogsPromptSummaryInDebugMode()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger(logs);
        var fake = new FakeChatClient();

        var client = new LoggingChatClient(fake, logger);
        await client.GetResponseAsync(
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
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(logs, l => l.Contains("LLM prompt summary"));
        Assert.Contains(logs, l => l.Contains("promptSha256="));
        Assert.Contains(logs, l => l.Contains("browserToolOptions=1"));
    }

    [Fact]
    public async Task LogsPromptDumpWhenTraceEnabled()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger(logs);
        var fake = new FakeChatClient();

        var client = new LoggingChatClient(fake, logger);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(logs, l => l.Contains("LLM prompt dump:"));
        Assert.Contains(logs, l => l.Contains("role=user"));
    }

    [Fact]
    public async Task LogsTokenDeltaAcrossMultipleCalls()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger(logs);
        var callCount = 0;

        var fake = new FakeChatClient((_, _, _) =>
        {
            callCount++;
            // Return increasing input tokens: 100, 150, 200
            var inputTokens = callCount * 50;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            {
                Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = 10 }
            });
        });

        var client = new LoggingChatClient(fake, logger);

        // First call: 50 tokens
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);
        // Second call: 100 tokens
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);
        // Third call: 150 tokens
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        // First call has no previous, so delta is N/A
        Assert.Contains(logs, l => l.Contains("delta: N/A"));
        // Second and third calls have delta of +50 each
        Assert.Equal(2, logs.Count(l => l.Contains("delta: +50")));
    }

    [Fact]
    public async Task LogsCumulativeTokensAcrossMultipleCalls()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger(logs);
        var callCount = 0;

        var fake = new FakeChatClient((_, _, _) =>
        {
            callCount++;
            // Return input tokens: 50, 100, 150
            var inputTokens = callCount * 50;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            {
                Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = 10 }
            });
        });

        var client = new LoggingChatClient(fake, logger);

        // First call: 50 tokens
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);
        // Second call: 100 tokens
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);
        // Third call: 150 tokens
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        // Verify cumulative: 50, 150, 300
        Assert.Contains(logs, l => l.Contains("cumulative: 50"));
        Assert.Contains(logs, l => l.Contains("cumulative: 150"));
        Assert.Contains(logs, l => l.Contains("cumulative: 300"));
    }

    [Fact]
    public async Task Streaming_LogsTokenDeltaAndCumulative()
    {
        var logs = new List<string>();
        var logger = new CapturingLogger(logs);
        var callCount = 0;

        var fake = new FakeChatClient(streaming: true, streamHandler: (messages, options, ct) =>
        {
            callCount++;
            // Return 100 input tokens on each call
            return StreamWithUsage(messages, 100, 10);
        });

        var client = new LoggingChatClient(fake, logger);

        // First streaming call
        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken)) { }
        // Second streaming call
        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken)) { }

        // Verify delta and cumulative appear in streaming logs
        Assert.Contains(logs, l => l.Contains("delta:") && l.Contains("cumulative:"));
        // First call should have delta N/A (no previous), cumulative 100
        Assert.Contains(logs, l => l.Contains("delta: N/A") && l.Contains("cumulative: 100"));
        // Second call should have delta 0, cumulative 200
        Assert.Contains(logs, l => l.Contains("delta: 0") && l.Contains("cumulative: 200"));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithUsage(
        IEnumerable<ChatMessage> messages, int inputTokens, int outputTokens)
    {
        await Task.CompletedTask;
        // Yield a text update
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("response")]
        };
        // Yield usage details
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
