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
        => new(sessionId: null, sessionDirectory: null) { Audience = audience };

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
}
