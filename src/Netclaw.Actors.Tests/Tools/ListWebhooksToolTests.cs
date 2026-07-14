// -----------------------------------------------------------------------
// <copyright file="ListWebhooksToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// The schema-advertised Filter parameter was previously a complete no-op
/// (silently discarded). It must now be honored: 'active' (default) excludes
/// disabled routes, 'all' includes everything, anything else rejects
/// (netclaw-tools spec: webhook listing honors its filter argument).
/// </summary>
public sealed class ListWebhooksToolTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly WebhookRouteStore _store;
    private readonly ListWebhooksTool _tool;

    public ListWebhooksToolTests()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        _store = new WebhookRouteStore(paths);
        _tool = new ListWebhooksTool(_store);

        _store.Save("enabled-route", new WebhookRouteConfig
        {
            Enabled = true,
            Prompt = "Handle inbound delivery."
        });
        _store.Save("disabled-route", new WebhookRouteConfig
        {
            Enabled = false,
            Prompt = "Dormant route."
        });
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Active_filter_excludes_disabled_routes_and_echoes_filter()
    {
        var result = await _tool.ExecuteAsync(
            ToolInput.Create("Filter", "active"), TestToolExecutionContext.CreateUnbound(), TestContext.Current.CancellationToken);

        Assert.Contains("enabled-route", result);
        Assert.DoesNotContain("disabled-route", result);
        Assert.Contains("filter: active", result);
        Assert.Contains("1 of 2", result);
    }

    [Fact]
    public async Task Absent_filter_defaults_to_active()
    {
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, object?>(), TestToolExecutionContext.CreateUnbound(), TestContext.Current.CancellationToken);

        Assert.Contains("enabled-route", result);
        Assert.DoesNotContain("disabled-route", result);
        Assert.Contains("filter: active", result);
    }

    [Fact]
    public async Task All_filter_includes_disabled_routes_with_enabled_state()
    {
        var result = await _tool.ExecuteAsync(
            ToolInput.Create("Filter", "all"), TestToolExecutionContext.CreateUnbound(), TestContext.Current.CancellationToken);

        Assert.Contains("enabled-route", result);
        Assert.Contains("disabled-route", result);
        Assert.Contains("Enabled: True", result);
        Assert.Contains("Enabled: False", result);
        Assert.Contains("filter: all", result);
        Assert.Contains("2 of 2", result);
    }

    [Fact]
    public async Task Unknown_filter_value_rejects_naming_supported_values()
    {
        var result = await _tool.ExecuteAsync(
            ToolInput.Create("Filter", "enabled"), TestToolExecutionContext.CreateUnbound(), TestContext.Current.CancellationToken);

        Assert.Contains("'Filter' value 'enabled' is not supported", result);
        Assert.Contains("active, all", result);
        Assert.Contains("NOT executed", result);
        Assert.DoesNotContain("enabled-route", result);
    }
}
