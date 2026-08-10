// -----------------------------------------------------------------------
// <copyright file="McpToolPermissionsViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Cli.Tests.Mcp;

public sealed class McpToolPermissionsViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public McpToolPermissionsViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    private McpToolPermissionsViewModel CreateVm(McpToolPermissionsNavigationState? navigationState = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        var daemonApi = new DaemonApi(new NoopHttpClientFactory(), configuration, _paths);
        return new McpToolPermissionsViewModel(_paths, daemonApi, navigationState);
    }

    [Fact]
    public void InitializeForTests_AppliesRequestedInitialAudience()
    {
        var navigationState = new McpToolPermissionsNavigationState();
        navigationState.RequestInitialAudience(TrustAudience.Team);
        var vm = CreateVm(navigationState);

        vm.InitializeForTests(new McpServerName("notion"), new[] { "create-pages" });

        Assert.Equal(TrustAudience.Team, vm.SelectedAudience);
    }

    [Fact]
    public void InitializeForTests_ThrowsForMalformedConfig()
    {
        var vm = CreateVm();
        File.WriteAllText(_paths.NetclawConfigPath, "{ not json");

        Assert.ThrowsAny<JsonException>(() =>
            vm.InitializeForTests(new McpServerName("notion"), new[] { "create-pages" }));
    }

    [Fact]
    public async Task LoadServers_NonObjectDaemonBody_SurfacesStatusInsteadOfThrowing()
    {
        // A 200 whose body is a JSON array (not the expected object map) makes EnumerateObject()
        // throw. LoadServersAsync runs fire-and-forget from OnActivated, so an unhandled throw
        // would fault page activation; the VM must instead surface a status message and not throw.
        var configuration = new ConfigurationBuilder().Build();
        var daemonApi = new DaemonApi(new StubStatusesHttpClientFactory("[]"), configuration, _paths);
        var vm = new McpToolPermissionsViewModel(_paths, daemonApi, navigationState: null);

        await vm.LoadServersAsync();

        Assert.Empty(vm.Servers);
        Assert.Contains("Could not read MCP server statuses", vm.StatusMessage.Value);
    }

    public static TheoryData<bool, ToolApprovalMode[]> ServerDefaultCycles => new()
    {
        { false, [ToolApprovalMode.Approval, ToolApprovalMode.Deny, ToolApprovalMode.Auto] },
        { true, [ToolApprovalMode.Deny, ToolApprovalMode.Approval, ToolApprovalMode.Auto] }
    };

    public static TheoryData<bool, ToolApprovalMode[]> ToolOverrideCycles => new()
    {
        { false, [ToolApprovalMode.Auto, ToolApprovalMode.Approval, ToolApprovalMode.Deny] },
        { true, [ToolApprovalMode.Deny, ToolApprovalMode.Approval, ToolApprovalMode.Auto] }
    };

    [Theory]
    [MemberData(nameof(ServerDefaultCycles))]
    public void CycleServerDefault_CyclesThroughModes(bool reverse, ToolApprovalMode[] expectedModes)
    {
        var vm = CreateVm();
        vm.InitializeForTests(new McpServerName("notion"), new[] { "create-pages", "search" });
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        foreach (var expectedMode in expectedModes)
        {
            CycleServerDefault(vm, reverse);
            Assert.Equal(expectedMode, vm.GetServerDefault());
        }
    }

    [Theory]
    [MemberData(nameof(ToolOverrideCycles))]
    public void CycleToolOverride_CyclesThroughModes(bool reverse, ToolApprovalMode[] expectedModes)
    {
        var vm = CreateVm();
        var toolName = new ToolName("create-pages");
        vm.InitializeForTests(new McpServerName("notion"), new[] { toolName.Value });
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        var (_, isInherited) = vm.GetEffectiveMode(toolName);
        Assert.True(isInherited);

        foreach (var expectedMode in expectedModes)
        {
            CycleToolOverride(vm, toolName, reverse);
            var step = vm.GetEffectiveMode(toolName);
            Assert.Equal(expectedMode, step.Mode);
            Assert.False(step.IsInherited);
        }

        CycleToolOverride(vm, toolName, reverse);
        var final = vm.GetEffectiveMode(toolName);
        Assert.True(final.IsInherited);
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

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
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

    [Fact]
    public void CycleToolOverride_ForwardThenBack_ReturnsToOriginalState()
    {
        var vm = CreateVm();
        vm.InitializeForTests(new McpServerName("notion"), new[] { "create-pages" });
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        vm.CycleToolOverride(new ToolName("create-pages"));
        vm.CycleToolOverride(new ToolName("create-pages"));

        vm.CycleToolOverrideBack(new ToolName("create-pages"));
        vm.CycleToolOverrideBack(new ToolName("create-pages"));

        var (_, isInherited) = vm.GetEffectiveMode(new ToolName("create-pages"));
        Assert.True(isInherited);
    }

    [Fact]
    public void ToggleServerAccess_GrantsAllDiscoveredTools()
    {
        var vm = CreateVm();
        var tools = new[] { "create-pages", "search", "list-databases" };
        vm.InitializeForTests(new McpServerName("notion"), tools);
        vm.SetSelectedAudienceForTests(TrustAudience.Team);

        Assert.False(vm.IsServerAllowedForSelectedAudience());

        vm.ToggleServerAccess();

        Assert.True(vm.IsServerAllowedForSelectedAudience());
        foreach (var tool in tools)
            Assert.True(vm.IsToolGranted(new ToolName(tool)));
    }

    [Fact]
    public void ToggleServerAccess_FirstEnableReplacesAStoredPartialGrantSet()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Team": {
                    "McpServersMode": "Allowlist",
                    "AllowedMcpServers": [],
                    "McpServerToolGrants": {
                      "notion": ["search"]
                    }
                  }
                }
              }
            }
            """);

        var vm = CreateVm();
        var tools = new[] { "create-pages", "search", "list-databases" };
        vm.InitializeForTests(new McpServerName("notion"), tools);
        vm.SetSelectedAudienceForTests(TrustAudience.Team);

        Assert.False(vm.IsServerAllowedForSelectedAudience());

        vm.ToggleServerAccess();

        Assert.True(vm.IsServerAllowedForSelectedAudience());
        foreach (var tool in tools)
            Assert.True(vm.IsToolGranted(new ToolName(tool)));

        vm.ToggleServerAccess();

        Assert.False(vm.IsServerAllowedForSelectedAudience());
        foreach (var tool in tools)
            Assert.False(vm.IsToolGranted(new ToolName(tool)));

        vm.ToggleServerAccess();

        Assert.True(vm.IsServerAllowedForSelectedAudience());
        foreach (var tool in tools)
            Assert.True(vm.IsToolGranted(new ToolName(tool)));
    }

    [Fact]
    public void ToggleServerAccess_DisablingClearsGrantedTools()
    {
        var vm = CreateVm();
        var tools = new[] { "create-pages", "search" };
        vm.InitializeForTests(new McpServerName("notion"), tools);
        vm.SetSelectedAudienceForTests(TrustAudience.Team);

        vm.ToggleServerAccess(); // enable
        Assert.True(vm.IsToolGranted(new ToolName("create-pages")));

        vm.ToggleServerAccess(); // disable
        Assert.False(vm.IsServerAllowedForSelectedAudience());
        foreach (var tool in tools)
            Assert.False(vm.IsToolGranted(new ToolName(tool)));
    }

    [Fact]
    public void Save_DisablingServerFromAllowlistPreservesOtherServers()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Team": {
                    "McpServersMode": "Allowlist",
                    "AllowedMcpServers": ["notion", "github"]
                  }
                }
              }
            }
            """);

        var vm = CreateVm();
        vm.Servers.Add(("notion", "running", 1));
        vm.Servers.Add(("github", "running", 1));
        vm.InitializeForTests(new McpServerName("notion"), new[] { "create-pages" });
        vm.SetSelectedAudienceForTests(TrustAudience.Team);

        vm.ToggleServerAccess();
        Assert.True(vm.Save());

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var team = GetAudienceProfile(doc, "Team");
        Assert.Equal("Allowlist", team.GetProperty("McpServersMode").GetString());

        var servers = ReadAllowedServers(team);
        Assert.DoesNotContain("notion", servers);
        Assert.Contains("github", servers);
    }

    [Fact]
    public void Save_DisablingServerFromAllProfileConvertsToAllowlist()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "McpServers": {
                "github": { "Transport": "stdio" }
              },
              "Tools": {
                "AudienceProfiles": {
                  "Personal": {
                    "McpServersMode": "All"
                  }
                }
              }
            }
            """);

        var vm = CreateVm();
        vm.Servers.Add(("notion", "running", 1));
        vm.InitializeForTests(new McpServerName("notion"), new[] { "create-pages" });
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);

        vm.ToggleServerAccess();
        Assert.True(vm.Save());

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var personal = GetAudienceProfile(doc, "Personal");
        Assert.Equal("Allowlist", personal.GetProperty("McpServersMode").GetString());

        var servers = ReadAllowedServers(personal);
        Assert.DoesNotContain("notion", servers);
        Assert.Contains("github", servers);

        var reloaded = CreateVm();
        reloaded.InitializeForTests(new McpServerName("notion"), new[] { "create-pages" });
        reloaded.SetSelectedAudienceForTests(TrustAudience.Personal);
        Assert.False(reloaded.IsServerAllowedForSelectedAudience());
    }

    [Fact]
    public void Save_DoesNotMutateTheLiveInMemoryProfile()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "McpServers": { "github": { "Transport": "stdio" } },
              "Tools": {
                "AudienceProfiles": {
                  "Personal": { "McpServersMode": "All" }
                }
              }
            }
            """);

        var vm = CreateVm();
        vm.Servers.Add(("notion", "running", 1));
        vm.InitializeForTests(new McpServerName("notion"), new[] { "create-pages" });
        vm.SetSelectedAudienceForTests(TrustAudience.Personal);
        Assert.Equal(ToolProfileMode.All, vm.Profiles.Personal.McpServersMode);

        vm.ToggleServerAccess(); // disable notion -> pending All->Allowlist conversion
        Assert.True(vm.Save());

        // The save writes the Allowlist conversion to disk, but must NOT coerce the live in-memory
        // profile that backs runtime ACL queries (IsServerAllowed, etc.). The prior code mutated it
        // mid-save, so a mid-save exception would leave the ACL in a post-save allowlist state.
        Assert.Equal(ToolProfileMode.All, vm.Profiles.Personal.McpServersMode);
        Assert.Empty(vm.Profiles.Personal.AllowedMcpServers);

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal("Allowlist", GetAudienceProfile(doc, "Personal").GetProperty("McpServersMode").GetString());
    }

    private static void CycleServerDefault(McpToolPermissionsViewModel vm, bool reverse)
    {
        if (reverse)
            vm.CycleServerDefaultBack();
        else
            vm.CycleServerDefault();
    }

    private static void CycleToolOverride(McpToolPermissionsViewModel vm, ToolName toolName, bool reverse)
    {
        if (reverse)
            vm.CycleToolOverrideBack(toolName);
        else
            vm.CycleToolOverride(toolName);
    }

    private static JsonElement GetAudienceProfile(JsonDocument doc, string audienceName)
        => doc.RootElement
            .GetProperty("Tools")
            .GetProperty("AudienceProfiles")
            .GetProperty(audienceName);

    private static string[] ReadAllowedServers(JsonElement profile)
        => profile.GetProperty("AllowedMcpServers")
            .EnumerateArray()
            .Select(static server => server.GetString() ?? string.Empty)
            .ToArray();

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Returns a 200 with a fixed body for every request, so the daemon-statuses call succeeds and
    // the VM exercises its response-shape handling rather than a connection failure.
    private sealed class StubStatusesHttpClientFactory(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(body));

        private sealed class StubHandler(string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage { Content = new StringContent(body) });
        }
    }
}
