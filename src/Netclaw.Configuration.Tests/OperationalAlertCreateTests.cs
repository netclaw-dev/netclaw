// -----------------------------------------------------------------------
// <copyright file="OperationalAlertCreateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class OperationalAlertCreateTests
{
    [Fact]
    public void Create_sets_all_properties_correctly()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero));
        var context = new Dictionary<string, string> { ["error"] = "timeout" };

        var alert = OperationalAlert.Create(
            fakeTime,
            type: "provider.unreachable",
            category: AlertType.ProviderUnreachable,
            summary: "LLM provider unreachable",
            severity: AlertSeverity.Critical,
            source: "anthropic",
            context: context);

        Assert.Equal("provider.unreachable", alert.Type);
        Assert.Equal(AlertType.ProviderUnreachable, alert.Category);
        Assert.Equal("LLM provider unreachable", alert.Summary);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("anthropic", alert.Source);
        Assert.Same(context, alert.Context);
        Assert.Equal(fakeTime.GetUtcNow(), alert.Timestamp);
    }

    [Fact]
    public void Create_generates_12_char_AlertId()
    {
        var alert = OperationalAlert.Create(
            TimeProvider.System,
            type: "test.alert",
            category: AlertType.DaemonStarted,
            summary: "Test",
            severity: AlertSeverity.Info);

        Assert.Equal(12, alert.AlertId.Length);
        Assert.Matches("^[0-9a-f]{12}$", alert.AlertId);
    }

    [Fact]
    public void Create_defaults_source_and_context_to_null()
    {
        var alert = OperationalAlert.Create(
            TimeProvider.System,
            type: "test.alert",
            category: AlertType.DaemonStarted,
            summary: "Test",
            severity: AlertSeverity.Info);

        Assert.Null(alert.Source);
        Assert.Null(alert.Context);
    }
}
