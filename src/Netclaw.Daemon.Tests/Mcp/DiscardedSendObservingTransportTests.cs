// -----------------------------------------------------------------------
// <copyright file="DiscardedSendObservingTransportTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Netclaw.Configuration.Http;
using Netclaw.Daemon.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class DiscardedSendObservingTransportTests
{
    private const string ServerName = "flaky-server";

    /// <summary>
    /// The #2258 production path through the real SDK: the server sends a request that the
    /// client cannot handle, the SDK's fire-and-forget handler sends a JSON-RPC error reply,
    /// and the server answers that POST with 502. The decorator logs and swallows it.
    /// </summary>
    [Fact]
    public async Task Sdk_error_reply_that_gets_502_is_logged_against_the_server_and_swallowed()
    {
        var logger = new CapturingLogger();
        var errorReplyPosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ScriptedMcpHttpHandler(body =>
        {
            if (body["method"]?.GetValue<string>() == "ping")
            {
                // Deliver a server-to-client request that the client does not support,
                // then the ping response.
                return ScriptedMcpHttpHandler.EventStream(
                    new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = "server-request-1",
                        ["method"] = "netclaw/unsupported",
                    },
                    ScriptedMcpHttpHandler.Result(body, new JsonObject()));
            }

            if (body["error"] is not null)
                errorReplyPosted.TrySetResult();

            return ScriptedMcpHttpHandler.BadGateway();
        });

        await using var client = await ConnectAsync(server, logger);
        await client.PingAsync(cancellationToken: TestContext.Current.CancellationToken);

        await errorReplyPosted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var entry = await logger.FirstWarning.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Contains($"MCP server '{ServerName}'", entry);
        Assert.Contains("JSON-RPC error reply", entry);
        Assert.Contains("502", entry);
        Assert.DoesNotContain('\n', entry);
    }

    [Fact]
    public async Task Awaited_request_send_failure_propagates_unchanged()
    {
        var logger = new CapturingLogger();
        var failure = new HttpRequestException("Response status code does not indicate success: 502 (Bad Gateway).");
        var transport = new DiscardedSendObservingTransport(new ThrowingTransport(failure), ServerName, logger);

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendMessageAsync(
            new JsonRpcRequest { Id = new RequestId(1), Method = RequestMethods.ToolsCall },
            TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Cancelled_notification_send_failure_is_logged_and_swallowed()
    {
        var logger = new CapturingLogger();
        var transport = new DiscardedSendObservingTransport(
            new ThrowingTransport(new IOException("Connection reset by peer.")),
            ServerName,
            logger);

        await transport.SendMessageAsync(
            new JsonRpcNotification { Method = NotificationMethods.CancelledNotification },
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Contains($"MCP server '{ServerName}'", entry);
        Assert.Contains(NotificationMethods.CancelledNotification, entry);
        Assert.Contains("IOException", entry);
    }

    private static async Task<McpClient> ConnectAsync(ScriptedMcpHttpHandler server, ILogger logger)
    {
        var transport = new DiscardedSendObservingClientTransport(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri("https://example.invalid/mcp"),
                    Name = ServerName,
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                McpHttpClientFactory.Create(server),
                ownsHttpClient: true),
            logger);

        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions(),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class ThrowingTransport(Exception failure) : ITransport
    {
        private readonly Channel<JsonRpcMessage> _messages = Channel.CreateUnbounded<JsonRpcMessage>();

        public string? SessionId => null;

        public ChannelReader<JsonRpcMessage> MessageReader => _messages.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
            => Task.FromException(failure);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly TaskCompletionSource<string> _firstWarning =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Entries { get; } = new();

        public Task<string> FirstWarning => _firstWarning.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning)
                return;

            var message = formatter(state, exception);
            Entries.Enqueue(message);
            _firstWarning.TrySetResult(message);
        }
    }
}
