using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class InboundWebhookRoutesDoctorCheckTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public InboundWebhookRoutesDoctorCheckTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-inbound-webhook-doctor-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ReturnsPass_WhenNoRouteFilesExist()
    {
        var check = new InboundWebhookRoutesDoctorCheck(_paths);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("No inbound webhook route files", result.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("missing a Prompt", result.Message, StringComparison.OrdinalIgnoreCase);
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
