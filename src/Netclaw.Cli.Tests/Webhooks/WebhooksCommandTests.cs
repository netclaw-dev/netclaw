using System.Text.Json;
using Netclaw.Cli.Json;
using Netclaw.Cli.Webhooks;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Webhooks;

public sealed class WebhooksCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public WebhooksCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task List_NoRoutes_ReturnsZero()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "list"], _paths);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task List_WithRoutes_ReturnsZero()
    {
        CreateValidRoute("test-route");

        var result = await WebhooksCommand.RunAsync(["webhooks", "list"], _paths);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task List_InvalidRouteWithNullVerification_DoesNotCrash()
    {
        WriteRouteText("bad", """
{
  "enabled": true,
  "verification": null,
  "events": [],
  "audience": "Public",
  "prompt": "x",
  "notifyInstructions": "",
  "deliveryRequired": true,
  "notificationTarget": null,
  "maxBodyBytes": 1024,
  "rateLimitPerMinute": 1
}
""");

        var result = await WebhooksCommand.RunAsync(["webhooks", "list"], _paths);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task List_Json_MarksInvalidRouteAsInvalid()
    {
        WriteRouteText("bad", """
{
  "enabled": true,
  "verification": null,
  "events": [],
  "audience": "Public",
  "prompt": "x",
  "notifyInstructions": "",
  "deliveryRequired": true,
  "notificationTarget": null,
  "maxBodyBytes": 1024,
  "rateLimitPerMinute": 1
}
""");

        using var stdout = new StringWriter();
        var result = await WebhooksCommand.RunAsync(["webhooks", "list", "--json"], _paths, stdout);
        Assert.Equal(0, result);

        var list = JsonSerializer.Deserialize<List<RouteListItem>>(stdout.ToString(), JsonDefaults.ConfigRead);
        Assert.NotNull(list);
        var item = Assert.Single(list!);
        Assert.Equal("bad", item.Name);
        Assert.Equal("invalid", item.Status);
        Assert.Equal("unknown", item.Verification);
    }

    [Fact]
    public async Task Show_ExistingRoute_ReturnsZero()
    {
        CreateValidRoute("test-route");

        var result = await WebhooksCommand.RunAsync(["webhooks", "show", "test-route"], _paths);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Show_NonexistentRoute_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "show", "nonexistent"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Show_MissingRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "show"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Show_InvalidRouteWithNullVerification_ReturnsOne()
    {
        WriteRouteText("bad", """
{
  "enabled": true,
  "verification": null,
  "events": [],
  "audience": "Public",
  "prompt": "x",
  "notifyInstructions": "",
  "deliveryRequired": true,
  "notificationTarget": null,
  "maxBodyBytes": 1024,
  "rateLimitPerMinute": 1
}
""");

        var result = await WebhooksCommand.RunAsync(["webhooks", "show", "bad"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_NewRoute_CreatesFile()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "new-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret"
        ], _paths);

        Assert.Equal(0, result);
        Assert.True(File.Exists(Path.Combine(_paths.WebhooksDirectory, "new-route.json")));
    }

    [Fact]
    public async Task Set_WithUppercaseRoute_NormalizesToLowercase()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "GitHub-Issues",
            "--prompt", "Test prompt",
            "--secret", "test-secret"
        ], _paths);

        Assert.Equal(0, result);
        Assert.True(File.Exists(Path.Combine(_paths.WebhooksDirectory, "github-issues.json")));
    }

    [Fact]
    public async Task Set_MissingPrompt_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--secret", "test-secret"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_MissingSecret_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_DryRun_DoesNotCreateFile()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "dry-run-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret",
            "--dry-run"
        ], _paths);

        Assert.Equal(0, result);
        Assert.False(File.Exists(Path.Combine(_paths.WebhooksDirectory, "dry-run-route.json")));
    }

    [Fact]
    public async Task Set_CreateOnly_FailsIfExists()
    {
        CreateValidRoute("existing-route");

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "existing-route",
            "--prompt", "Updated prompt",
            "--secret", "updated-secret",
            "--create-only"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_UpdateOnly_FailsIfNotExists()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "nonexistent-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret",
            "--update-only"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_ConflictingCreateAndUpdateOnly_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret",
            "--create-only",
            "--update-only"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_ConflictingEnabledFlags_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret",
            "--enabled",
            "--disabled"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_ConflictingDeliveryFlags_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret",
            "--delivery-required",
            "--no-delivery-required"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_InvalidVerificationKind_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret",
            "--verification-kind", "invalid"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_InvalidAudience_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret",
            "--audience", "invalid"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_InvalidRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "../secrets",
            "--prompt", "Test prompt",
            "--secret", "test-secret"
        ], _paths);

        Assert.Equal(1, result);
        Assert.False(File.Exists(Path.Combine(_paths.ConfigDirectory, "secrets.json")));
    }

    [Fact]
    public async Task Set_MissingPromptValue_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt",
            "--secret", "test-secret"
        ], _paths);

        Assert.Equal(1, result);
        Assert.False(File.Exists(Path.Combine(_paths.WebhooksDirectory, "test-route.json")));
    }

    [Fact]
    public async Task Set_MissingSecretFlagValue_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret"
        ], _paths);

        Assert.Equal(1, result);
        Assert.False(File.Exists(Path.Combine(_paths.WebhooksDirectory, "test-route.json")));
    }

    [Fact]
    public async Task Set_MissingVerificationKindValue_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret",
            "--verification-kind"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_MissingNotificationChannelValue_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret",
            "--notification-channel"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_MissingPromptFile_DoesNotPartiallyUpdateExistingRoute()
    {
        CreateValidRoute("test-route", secret: "before-secret", prompt: "before-prompt");

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt-file", Path.Combine(_tempDir, "missing.txt"),
            "--secret", "after-secret"
        ], _paths);

        Assert.Equal(1, result);

        var route = ReadRoute("test-route");
        Assert.Equal("before-prompt", route.Prompt);
        Assert.Equal("before-secret", route.Verification.Secret!.Value);
    }

    [Fact]
    public async Task Set_MultipleSecretSources_ReturnsOne()
    {
        var secretFile = Path.Combine(_tempDir, "secret.txt");
        File.WriteAllText(secretFile, "file-secret");

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret", "inline-secret",
            "--secret-file", secretFile
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_MultiplePromptSources_ReturnsOne()
    {
        var promptFile = Path.Combine(_tempDir, "prompt.txt");
        File.WriteAllText(promptFile, "file prompt");

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "inline prompt",
            "--prompt-file", promptFile,
            "--secret", "test-secret"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_MissingSecretEnvVariable_ReturnsOne()
    {
        Environment.SetEnvironmentVariable("NETCLAW_WEBHOOK_TEST_MISSING_SECRET", null);

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret-env", "NETCLAW_WEBHOOK_TEST_MISSING_SECRET"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Delete_ExistingRoute_ReturnsZero()
    {
        CreateValidRoute("delete-me");

        var result = await WebhooksCommand.RunAsync(["webhooks", "delete", "delete-me", "--force"], _paths);

        Assert.Equal(0, result);
        Assert.False(File.Exists(Path.Combine(_paths.WebhooksDirectory, "delete-me.json")));
    }

    [Fact]
    public async Task Delete_NonexistentRoute_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "delete", "nonexistent", "--force"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Delete_MissingRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "delete"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Delete_InvalidRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "delete", "../secrets", "--force"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Validate_ValidRoute_ReturnsZero()
    {
        CreateValidRoute("valid-route");

        var result = await WebhooksCommand.RunAsync(["webhooks", "validate", "valid-route"], _paths);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Validate_NonexistentRoute_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "validate", "nonexistent"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Validate_MissingRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "validate"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Validate_InvalidRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "validate", "../secrets"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Validate_InvalidRouteWithNullVerification_ReturnsOne()
    {
        WriteRouteText("bad", """
{
  "enabled": true,
  "verification": null,
  "events": [],
  "audience": "Public",
  "prompt": "x",
  "notifyInstructions": "",
  "deliveryRequired": true,
  "notificationTarget": null,
  "maxBodyBytes": 1024,
  "rateLimitPerMinute": 1
}
""");

        var result = await WebhooksCommand.RunAsync(["webhooks", "validate", "bad"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Help_ReturnsZero()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "help"], _paths);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task HelpFlag_ReturnsZero()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "--help"], _paths);
        Assert.Equal(0, result);
    }

    private void CreateValidRoute(string routeName, string secret = "test-secret", string prompt = "Test prompt")
    {
        var route = new WebhookRouteConfig
        {
            Enabled = true,
            Prompt = prompt,
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.Hmac,
                Secret = new SensitiveString(secret)
            }
        };

        var store = new WebhookRouteStore(_paths);
        store.Save(routeName, route);
    }

    private WebhookRouteConfig ReadRoute(string routeName)
    {
        var store = new WebhookRouteStore(_paths);
        Assert.True(store.TryGet(routeName, out var match));
        Assert.NotNull(match.Definition);
        return match.Definition!;
    }

    private void WriteRouteText(string routeName, string text)
    {
        File.WriteAllText(Path.Combine(_paths.WebhooksDirectory, $"{routeName}.json"), text);
    }

    private sealed class RouteListItem
    {
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Verification { get; set; } = string.Empty;
    }
}
