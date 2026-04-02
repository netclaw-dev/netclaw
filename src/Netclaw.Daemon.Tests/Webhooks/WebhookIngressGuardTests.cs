using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Daemon.Webhooks;
using Xunit;

namespace Netclaw.Daemon.Tests.Webhooks;

public sealed class WebhookIngressGuardTests
{
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-04-02T18:00:00Z"));

    [Fact]
    public void Duplicate_delivery_is_suppressed()
    {
        var sut = new WebhookIngressGuard(_timeProvider, NullLogger<WebhookIngressGuard>.Instance);

        var first = sut.CheckAndRecord("github-issues", "delivery-1", rateLimitPerMinute: 30);
        var second = sut.CheckAndRecord("github-issues", "delivery-1", rateLimitPerMinute: 30);

        Assert.Equal(WebhookIngressDecisionKind.Accepted, first.Kind);
        Assert.Equal(WebhookIngressDecisionKind.Duplicate, second.Kind);
    }

    [Fact]
    public void Rate_limit_rejects_requests_after_limit_is_reached()
    {
        var sut = new WebhookIngressGuard(_timeProvider, NullLogger<WebhookIngressGuard>.Instance);

        Assert.Equal(WebhookIngressDecisionKind.Accepted, sut.CheckAndRecord("github-issues", "1", 2).Kind);
        Assert.Equal(WebhookIngressDecisionKind.Accepted, sut.CheckAndRecord("github-issues", "2", 2).Kind);

        var third = sut.CheckAndRecord("github-issues", "3", 2);

        Assert.Equal(WebhookIngressDecisionKind.RateLimited, third.Kind);
        Assert.True(third.RetryAfterSeconds > 0);
    }
}
