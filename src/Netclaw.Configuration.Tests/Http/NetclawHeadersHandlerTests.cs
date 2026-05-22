// -----------------------------------------------------------------------
// <copyright file="NetclawHeadersHandlerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Netclaw.Configuration.Http;
using Xunit;

namespace Netclaw.Configuration.Tests.Http;

public sealed class NetclawHeadersHandlerTests
{
    [Fact]
    public async Task Stamps_user_agent_and_component_when_request_has_neither()
    {
        var captured = await SendAsync(component: "test-component",
            configure: _ => { });

        Assert.Equal(NetclawUserAgent.Value, captured.UserAgent);
        Assert.Equal("test-component", captured.Component);
    }

    [Fact]
    public async Task Does_not_overwrite_caller_supplied_user_agent()
    {
        var captured = await SendAsync(component: "test-component", configure: r =>
            r.Headers.UserAgent.ParseAdd("CustomAgent/9.9"));

        Assert.Equal("CustomAgent/9.9", captured.UserAgent);
    }

    [Fact]
    public async Task Does_not_overwrite_user_agent_set_via_untyped_TryAddWithoutValidation()
    {
        // Mirrors what HttpClient.DefaultRequestHeaders.TryAddWithoutValidation
        // produces by the time the handler runs: header is in the raw bag, not
        // in the typed UserAgent collection. A Count==0 check would miss this
        // and double-stamp.
        var captured = await SendAsync(component: "test-component", configure: r =>
            r.Headers.TryAddWithoutValidation("User-Agent", "OperatorAgent/1.0"));

        Assert.Equal("OperatorAgent/1.0", captured.UserAgent);
    }

    [Fact]
    public async Task Does_not_overwrite_caller_supplied_component_header()
    {
        var captured = await SendAsync(component: "default-component", configure: r =>
            r.Headers.TryAddWithoutValidation(NetclawUserAgent.ComponentHeader, "override"));

        Assert.Equal("override", captured.Component);
    }

    [Fact]
    public async Task Re_running_handler_on_same_request_does_not_duplicate_headers()
    {
        // Polly retry shape: the same HttpRequestMessage is fed through the
        // handler chain a second time. The handler must be idempotent on the
        // request it already stamped. HttpMessageInvoker (rather than
        // HttpClient) is used because HttpClient rejects resending the same
        // HttpRequestMessage, but DelegatingHandlers in real retry policies
        // do see the same request twice.
        var captured = new CapturingHandler();
        var handler = new NetclawHeadersHandler("test-component")
        {
            InnerHandler = captured,
        };

        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");

        await invoker.SendAsync(request, TestContext.Current.CancellationToken);
        await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        // captured.UserAgent reads raw header values via TryGetValues; if the
        // handler had stamped twice, this would be the UA string doubled.
        Assert.Equal(NetclawUserAgent.Value, captured.UserAgent);
        Assert.Equal("test-component", captured.Component);
    }

    [Fact]
    public void Constructor_rejects_empty_component()
    {
        Assert.Throws<ArgumentException>(() => new NetclawHeadersHandler(""));
    }

    private static async Task<CapturedHeaders> SendAsync(string component, Action<HttpRequestMessage> configure)
    {
        var captured = new CapturingHandler();
        var handler = new NetclawHeadersHandler(component)
        {
            InnerHandler = captured,
        };

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        configure(request);
        await client.SendAsync(request, TestContext.Current.CancellationToken);

        var seen = captured.LastRequest!;
        return new CapturedHeaders(
            seen.Headers.TryGetValues("User-Agent", out var ua) ? string.Join(" ", ua) : "",
            seen.Headers.TryGetValues(NetclawUserAgent.ComponentHeader, out var c) ? string.Join(",", c) : "");
    }

    private sealed record CapturedHeaders(string UserAgent, string Component);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? UserAgent => LastRequest is null
            ? null
            : LastRequest.Headers.TryGetValues("User-Agent", out var v) ? string.Join(" ", v) : "";
        public string? Component => LastRequest is null
            ? null
            : LastRequest.Headers.TryGetValues(NetclawUserAgent.ComponentHeader, out var v) ? string.Join(",", v) : "";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
