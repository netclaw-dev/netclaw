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
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

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
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

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
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

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
            [new ChatMessage(ChatRole.User, "hi")])) { }

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
            });

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
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Contains(logs, l => l.Contains("LLM prompt dump:"));
        Assert.Contains(logs, l => l.Contains("role=user"));
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
