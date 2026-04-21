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

    private void CreateValidRoute(string routeName)
    {
        var route = new WebhookRouteConfig
        {
            Enabled = true,
            Prompt = "Test prompt",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.Hmac,
                Secret = new SensitiveString("test-secret")
            }
        };

        var store = new WebhookRouteStore(_paths);
        store.Save(routeName, route);
    }
}
