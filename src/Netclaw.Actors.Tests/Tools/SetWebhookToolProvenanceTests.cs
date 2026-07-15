// -----------------------------------------------------------------------
// <copyright file="SetWebhookToolProvenanceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Webhook routes created via <c>set_webhook</c> inherit the creating context's
/// audience (transitive provenance, matching <c>set_reminder</c>) and cannot be
/// minted above the creator's authority (downgrade-only escalation guard).
/// </summary>
public sealed class SetWebhookToolProvenanceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly WebhookRouteStore _store;

    public SetWebhookToolProvenanceTests()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        _store = new WebhookRouteStore(paths);
    }

    public void Dispose() => _dir.Dispose();

    private static ToolExecutionContext Context(TrustAudience audience)
        => TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions { Audience = audience });

    private async Task<string> CreateRouteAsync(string routeName, TrustAudience creator, string? requestedAudience)
    {
        var tool = new SetWebhookTool(_store);
        var args = new Dictionary<string, object?>
        {
            ["RouteName"] = routeName,
            ["Prompt"] = "Handle inbound delivery.",
            ["VerificationKind"] = "Hmac",
            ["Secret"] = "test-secret",
        };
        if (requestedAudience is not null)
            args["Audience"] = requestedAudience;

        return await tool.ExecuteAsync(args, Context(creator), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(TrustAudience.Personal)]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    public async Task Omitted_audience_inherits_creating_context(TrustAudience creator)
    {
        var result = await CreateRouteAsync("inherit-route", creator, requestedAudience: null);

        Assert.DoesNotContain("Error", result);
        Assert.True(_store.TryGet("inherit-route", out var saved));
        Assert.Equal(creator, saved.Definition!.Audience);
    }

    [Fact]
    public async Task Explicit_downgrade_below_creator_is_allowed()
    {
        var result = await CreateRouteAsync("downgrade-route", TrustAudience.Personal, requestedAudience: "team");

        Assert.DoesNotContain("Error", result);
        Assert.True(_store.TryGet("downgrade-route", out var saved));
        Assert.Equal(TrustAudience.Team, saved.Definition!.Audience);
    }

    [Fact]
    public async Task Requested_audience_above_creator_is_rejected_and_not_persisted()
    {
        var result = await CreateRouteAsync("escalate-route", TrustAudience.Team, requestedAudience: "personal");

        Assert.Contains("exceeds creator authority", result);
        Assert.False(_store.TryGet("escalate-route", out _));
    }

    [Fact]
    public async Task Notify_instructions_require_notification_target()
    {
        var tool = new SetWebhookTool(_store);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["RouteName"] = "notify-without-target",
            ["Prompt"] = "Handle inbound delivery.",
            ["VerificationKind"] = "Hmac",
            ["Secret"] = "test-secret",
            ["NotifyInstructions"] = "Post a summary to the release channel.",
            ["DeliveryRequired"] = false
        }, Context(TrustAudience.Team), TestContext.Current.CancellationToken);

        Assert.Equal("Error: NotificationTarget is required when NotifyInstructions are provided.", result);
        Assert.False(_store.TryGet("notify-without-target", out _));
    }

    [Fact]
    public async Task Timestamped_hmac_settings_are_persisted()
    {
        var tool = new SetWebhookTool(_store);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["RouteName"] = "stripe-events",
            ["Prompt"] = "Handle Stripe delivery.",
            ["VerificationKind"] = "HmacTimestamped",
            ["Secret"] = "whsec_test",
            ["SignatureHeaderName"] = "Stripe-Signature",
            ["TimestampField"] = "timestamp",
            ["SignatureField"] = "signature",
            ["SignedPayloadSeparator"] = "::",
            ["ToleranceSeconds"] = 120
        }, Context(TrustAudience.Public), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Error", result);
        Assert.True(_store.TryGet("stripe-events", out var saved));
        var verification = saved.Definition!.Verification;
        Assert.Equal(WebhookVerifierKind.HmacTimestamped, verification.Kind);
        Assert.Equal("timestamp", verification.TimestampField);
        Assert.Equal("signature", verification.SignatureField);
        Assert.Equal("::", verification.SignedPayloadSeparator);
        Assert.Equal(120, verification.ToleranceSeconds);
    }

    [Fact]
    public async Task Timestamp_settings_are_rejected_for_body_hmac()
    {
        var tool = new SetWebhookTool(_store);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["RouteName"] = "invalid-route",
            ["Prompt"] = "Handle delivery.",
            ["VerificationKind"] = "Hmac",
            ["Secret"] = "test-secret",
            ["TimestampField"] = "t"
        }, Context(TrustAudience.Public), TestContext.Current.CancellationToken);

        Assert.Contains("require 'verificationKind' to be 'HmacTimestamped'", result);
        Assert.False(_store.TryGet("invalid-route", out _));
    }

    [Fact]
    public async Task Update_preserves_omitted_route_and_verification_settings()
    {
        var tool = new SetWebhookTool(_store);
        var createResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["RouteName"] = "stripe-events",
            ["Prompt"] = "Handle Stripe delivery.",
            ["VerificationKind"] = "HmacTimestamped",
            ["Secret"] = "old-secret",
            ["SignatureHeaderName"] = "Stripe-Signature",
            ["EventHeaderName"] = "Stripe-Event",
            ["DeliveryIdHeaderName"] = "Stripe-Delivery",
            ["TimestampField"] = "timestamp",
            ["SignatureField"] = "signature",
            ["SignedPayloadSeparator"] = "::",
            ["ToleranceSeconds"] = 120,
            ["Events"] = "payment.created,payment.failed",
            ["Audience"] = "team",
            ["NotifyInstructions"] = "Notify the payments channel.",
            ["DeliveryRequired"] = false,
            ["NotificationChannelId"] = "C-PAYMENTS",
            ["MaxBodyBytes"] = 4096,
            ["RateLimitPerMinute"] = 12,
            ["Enabled"] = false
        }, Context(TrustAudience.Personal), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Error", createResult);

        var updateResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["RouteName"] = "stripe-events",
            ["Prompt"] = "Handle and summarize Stripe delivery.",
            ["VerificationKind"] = "HmacTimestamped",
            ["Secret"] = "new-secret",
            ["RateLimitPerMinute"] = 24
        }, Context(TrustAudience.Team), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Error", updateResult);
        Assert.True(_store.TryGet("stripe-events", out var saved));
        var route = saved.Definition!;
        Assert.Equal("Handle and summarize Stripe delivery.", route.Prompt);
        Assert.Equal(new SensitiveString("new-secret"), route.Verification.Secret);
        Assert.Equal(24, route.RateLimitPerMinute);
        Assert.False(route.Enabled);
        Assert.Equal(4096, route.MaxBodyBytes);
        Assert.Equal(TrustAudience.Team, route.Audience);
        Assert.Equal(["payment.created", "payment.failed"], route.Events);
        Assert.Equal("Notify the payments channel.", route.NotifyInstructions);
        Assert.False(route.DeliveryRequired);
        Assert.Equal("C-PAYMENTS", route.NotificationTarget?.ChannelId);
        Assert.Equal("Stripe-Signature", route.Verification.SignatureHeaderName);
        Assert.Equal("Stripe-Event", route.Verification.EventHeaderName);
        Assert.Equal("Stripe-Delivery", route.Verification.DeliveryIdHeaderName);
        Assert.Equal("timestamp", route.Verification.TimestampField);
        Assert.Equal("signature", route.Verification.SignatureField);
        Assert.Equal("::", route.Verification.SignedPayloadSeparator);
        Assert.Equal(120, route.Verification.ToleranceSeconds);
    }

    [Fact]
    public async Task Lower_audience_cannot_update_higher_audience_route()
    {
        var createResult = await CreateRouteAsync("team-route", TrustAudience.Team, requestedAudience: null);
        Assert.DoesNotContain("Error", createResult);
        var tool = new SetWebhookTool(_store);

        var updateResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["RouteName"] = "team-route",
            ["Prompt"] = "Replace team instructions.",
            ["VerificationKind"] = "Hmac",
            ["Secret"] = "replacement-secret"
        }, Context(TrustAudience.Public), TestContext.Current.CancellationToken);

        Assert.Contains("exceeds creator authority", updateResult);
        Assert.True(_store.TryGet("team-route", out var saved));
        Assert.Equal("Handle inbound delivery.", saved.Definition!.Prompt);
        Assert.Equal(new SensitiveString("test-secret"), saved.Definition.Verification.Secret);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData(" timestamp")]
    [InlineData("time=stamp")]
    [InlineData("time\nstamp")]
    [InlineData("téstamp")]
    public async Task Unusable_timestamp_field_names_are_rejected_before_save(string timestampField)
    {
        var tool = new SetWebhookTool(_store);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["RouteName"] = "invalid-route",
            ["Prompt"] = "Handle delivery.",
            ["VerificationKind"] = "HmacTimestamped",
            ["Secret"] = "test-secret",
            ["TimestampField"] = timestampField
        }, Context(TrustAudience.Public), TestContext.Current.CancellationToken);

        Assert.Contains("Verification.TimestampField", result);
        Assert.False(_store.TryGet("invalid-route", out _));
    }
}
