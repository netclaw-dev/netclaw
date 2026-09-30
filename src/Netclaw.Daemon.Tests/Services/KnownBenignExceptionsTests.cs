// -----------------------------------------------------------------------
// <copyright file="KnownBenignExceptionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ModelContextProtocol.Client;
using Netclaw.Configuration.Http;
using Netclaw.Daemon.Services;
using Netclaw.Daemon.Tests.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class KnownBenignExceptionsTests
{
    // Shutdown case: McpSessionHandler.SendMessageAsync throws at ThrowIfCancellationRequested()
    // before any transport frame. Hard to produce for real without racing the session teardown.
    private const string SessionHandlerOnlyStack =
        "   at System.Threading.CancellationToken.ThrowOperationCanceledException()\n"
        + "   at ModelContextProtocol.McpSessionHandler.SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken)";

    [Fact]
    public async Task Real_sdk_send_failure_on_502_is_benign()
    {
        var exception = await CaptureSdkNotificationSendFailureAsync();

        Assert.True(KnownBenignExceptions.IsMcpSessionSendFailure(exception));
        Assert.True(KnownBenignExceptions.IsMcpSessionSendFailure(new AggregateException(exception)));
        Assert.True(KnownBenignExceptions.IsMcpSessionSendFailure(
            new AggregateException(new AggregateException(exception))));
    }

    [Fact]
    public async Task Aggregate_with_one_non_mcp_exception_is_not_benign()
    {
        var exception = await CaptureSdkNotificationSendFailureAsync();

        Assert.False(KnownBenignExceptions.IsMcpSessionSendFailure(
            new AggregateException(exception, new InvalidOperationException("daemon bug"))));
    }

    [Fact]
    public void Non_mcp_http_request_exception_with_real_stack_is_not_benign()
    {
        var exception = Assert.Throws<HttpRequestException>((Action)(() =>
            throw new HttpRequestException("Response status code does not indicate success: 502 (Bad Gateway).")));

        Assert.NotNull(exception.StackTrace);
        Assert.False(KnownBenignExceptions.IsMcpSessionSendFailure(exception));
        Assert.False(KnownBenignExceptions.IsMcpSessionSendFailure(new AggregateException(exception)));
    }

    [Fact]
    public void Cancellation_inside_session_handler_send_is_benign()
    {
        Assert.True(KnownBenignExceptions.IsMcpSessionSendFailure(
            new AggregateException(new FakeOperationCanceledException(SessionHandlerOnlyStack))));
    }

    [Fact]
    public void Non_transport_exception_type_with_session_handler_frame_is_not_benign()
    {
        Assert.False(KnownBenignExceptions.IsMcpSessionSendFailure(
            new AggregateException(new FakeInvalidOperationException(SessionHandlerOnlyStack))));
    }

    [Fact]
    public void Null_and_empty_aggregate_are_not_benign()
    {
        Assert.False(KnownBenignExceptions.IsMcpSessionSendFailure(null));
        Assert.False(KnownBenignExceptions.IsMcpSessionSendFailure(new AggregateException()));
    }

    /// <summary>
    /// Sends a notification through the real SDK to a server that answers 502, and returns
    /// the exception with its real SDK stack.
    /// </summary>
    internal static async Task<HttpRequestException> CaptureSdkNotificationSendFailureAsync()
    {
        var server = new ScriptedMcpHttpHandler(_ => ScriptedMcpHttpHandler.BadGateway());
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri("https://example.invalid/mcp"),
                    Name = "flaky-server",
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                McpHttpClientFactory.Create(server),
                ownsHttpClient: true),
            new McpClientOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        return await Assert.ThrowsAsync<HttpRequestException>(() => client.SendNotificationAsync(
            "notifications/netclaw-test",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed class FakeOperationCanceledException(string stackTrace) : OperationCanceledException
    {
        public override string? StackTrace => stackTrace;
    }

    private sealed class FakeInvalidOperationException(string stackTrace) : InvalidOperationException
    {
        public override string? StackTrace => stackTrace;
    }
}
