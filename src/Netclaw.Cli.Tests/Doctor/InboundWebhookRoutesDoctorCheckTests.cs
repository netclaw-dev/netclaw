// -----------------------------------------------------------------------
// <copyright file="InboundWebhookRoutesDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class InboundWebhookRoutesDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public InboundWebhookRoutesDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ReturnsPass_WhenNoRouteFilesExist()
    {
        var check = new InboundWebhookRoutesDoctorCheck(_paths);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("No inbound webhook route files", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsWarning_WhenInboundWebhooksEnabledWithoutRoutes()
    {
        // Enable-first is a valid setup order: `Webhooks.Enabled` is only the feature
        // toggle and the gateway is inert (404s) until routes are added — advisory, not error.
        File.WriteAllText(_paths.NetclawConfigPath, "{\"configVersion\":1,\"Webhooks\":{\"Enabled\":true}}");
        var check = new InboundWebhookRoutesDoctorCheck(_paths);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("enabled but no route files", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("netclaw webhooks set", result.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsWarning_WhenInboundWebhooksEnabledButAllRoutesDisabled()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{\"configVersion\":1,\"Webhooks\":{\"Enabled\":true}}");
        WriteRouteFile("github-issues", new WebhookRouteConfig
        {
            Enabled = false,
            Prompt = "triage this event",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.Hmac,
                Secret = new SensitiveString("secret")
            }
        });
        var check = new InboundWebhookRoutesDoctorCheck(_paths);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("no valid enabled route", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsPass_WhenRouteFileIsValid()
    {
        WriteRouteFile("github-issues", new WebhookRouteConfig
        {
            Prompt = "triage this event",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.Hmac,
                Secret = new SensitiveString("secret")
            }
        });

        var check = new InboundWebhookRoutesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Validated 1 inbound webhook route file", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsError_WhenRouteFileHasInvalidJson()
    {
        File.WriteAllText(Path.Combine(_paths.WebhooksDirectory, "github-issues.json"), "{ not valid json");

        var check = new InboundWebhookRoutesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("github-issues.json", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task ReturnsError_WhenRouteFileFailsValidation()
    {
        WriteRouteFile("github-issues", new WebhookRouteConfig
        {
            Prompt = "",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.Hmac,
                Secret = new SensitiveString("secret")
            }
        });

        var check = new InboundWebhookRoutesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Prompt is required", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsError_WhenRouteNameIsInvalid()
    {
        File.WriteAllText(Path.Combine(_paths.WebhooksDirectory, "Bad_Route.json"), """
{
  "prompt": "triage this event",
  "verification": {
    "kind": "Hmac",
    "secret": "secret"
  }
}
""");

        var check = new InboundWebhookRoutesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("lowercase kebab-case", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsError_WhenVerificationIsNull()
    {
        File.WriteAllText(Path.Combine(_paths.WebhooksDirectory, "github-issues.json"), """
{
  "enabled": true,
  "verification": null,
  "events": [],
  "audience": "Public",
  "prompt": "triage",
  "notifyInstructions": "",
  "deliveryRequired": true,
  "notificationTarget": null,
  "maxBodyBytes": 1024,
  "rateLimitPerMinute": 1
}
""");

        var check = new InboundWebhookRoutesDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Verification settings are required", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private void WriteRouteFile(string routeName, WebhookRouteConfig route)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        File.WriteAllText(
            Path.Combine(_paths.WebhooksDirectory, $"{routeName}.json"),
            JsonSerializer.Serialize(route, options));
    }
}
