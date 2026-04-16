using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Cli.Tests.Mcp;

public sealed class McpToolPermissionsViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public McpToolPermissionsViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-vm-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private McpToolPermissionsViewModel CreateVm()
    {
        var configuration = new ConfigurationBuilder().Build();
        var daemonPaths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-vm-daemon-{Guid.NewGuid():N}"));
        daemonPaths.EnsureDirectoriesExist();
        var daemonApi = new DaemonApi(new NoopHttpClientFactory(), configuration, daemonPaths);
        return new McpToolPermissionsViewModel(_paths, daemonApi);
    }

    [Fact]
    public void CycleServerDefault_StartingFromAuto_LandsOnDenyAfterTwoCycles()
    {
        var vm = CreateVm();
        vm.InitializeForTests(new McpServerName("notion"), new[] { "create-pages", "search" });
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        vm.CycleServerDefault();
        Assert.Equal(ToolApprovalMode.Approval, vm.GetServerDefault());

        vm.CycleServerDefault();
        Assert.Equal(ToolApprovalMode.Deny, vm.GetServerDefault());

        vm.CycleServerDefault();
        Assert.Equal(ToolApprovalMode.Auto, vm.GetServerDefault());
    }

    [Fact]
    public void CycleToolOverride_FromInherit_CyclesThroughAllModes()
    {
        var vm = CreateVm();
        vm.InitializeForTests(new McpServerName("notion"), new[] { "create-pages" });
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Initial: inherit (effective mode resolves from server default / global default).
        var (_, isInherited) = vm.GetEffectiveMode(new ToolName("create-pages"));
        Assert.True(isInherited);

        vm.CycleToolOverride(new ToolName("create-pages"));
        var step1 = vm.GetEffectiveMode(new ToolName("create-pages"));
        Assert.Equal(ToolApprovalMode.Auto, step1.Mode);
        Assert.False(step1.IsInherited);

        vm.CycleToolOverride(new ToolName("create-pages"));
        var step2 = vm.GetEffectiveMode(new ToolName("create-pages"));
        Assert.Equal(ToolApprovalMode.Approval, step2.Mode);
        Assert.False(step2.IsInherited);

        vm.CycleToolOverride(new ToolName("create-pages"));
        var step3 = vm.GetEffectiveMode(new ToolName("create-pages"));
        Assert.Equal(ToolApprovalMode.Deny, step3.Mode);
        Assert.False(step3.IsInherited);

        vm.CycleToolOverride(new ToolName("create-pages"));
        var step4 = vm.GetEffectiveMode(new ToolName("create-pages"));
        Assert.True(step4.IsInherited);
    }

    [Fact]
    public void Save_WritesServerDefaultsAndOverridesAndRemovesInheritedEntries()
    {
        var vm = CreateVm();
        vm.InitializeForTests(new McpServerName("notion"), new[] { "create-pages", "search" });
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        // Cycle server default twice: Auto → Approval → Deny.
        vm.CycleServerDefault();
        vm.CycleServerDefault();

        // Cycle create-pages once → Auto (explicit override).
        vm.CycleToolOverride(new ToolName("create-pages"));

        // Cycle search four times back to inherit.
        vm.CycleToolOverride(new ToolName("search"));
        vm.CycleToolOverride(new ToolName("search"));
        vm.CycleToolOverride(new ToolName("search"));
        vm.CycleToolOverride(new ToolName("search"));

        vm.Save();

        var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var approvalPolicy = doc.RootElement
            .GetProperty("Tools")
            .GetProperty("AudienceProfiles")
            .GetProperty("Personal")
            .GetProperty("ApprovalPolicy");

        Assert.Equal(
            "Deny",
            approvalPolicy.GetProperty("McpServerDefaults").GetProperty("notion").GetString());

        var toolOverrides = approvalPolicy.GetProperty("ToolOverrides");
        Assert.Equal("Auto", toolOverrides.GetProperty("notion/create-pages").GetString());

        // search was cycled back to inherit → it must NOT be persisted.
        Assert.False(toolOverrides.TryGetProperty("notion/search", out _));

        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void GetEffectiveMode_ReadsExistingMcpServerDefaultsFromConfig()
    {
        // Seed a netclaw.json with an existing McpServerDefaults entry.
        var configJson = """
        {
          "configVersion": 1,
          "Tools": {
            "AudienceProfiles": {
              "Personal": {
                "ApprovalPolicy": {
                  "DefaultMode": "Auto",
                  "McpServerDefaults": { "notion": "Approval" }
                }
              }
            }
          }
        }
        """;
        File.WriteAllText(_paths.NetclawConfigPath, configJson);

        var vm = CreateVm();
        vm.InitializeForTests(new McpServerName("notion"), new[] { "create-pages" });
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        var (mode, inherited) = vm.GetEffectiveMode(new ToolName("create-pages"));
        Assert.Equal(ToolApprovalMode.Approval, mode);
        Assert.True(inherited);
        Assert.Equal(ToolApprovalMode.Approval, vm.GetServerDefault());
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
