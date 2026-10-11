// -----------------------------------------------------------------------
// <copyright file="InitStartRouteTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Where <c>netclaw init</c> starts: any <c>netclaw.json</c> opens the existing-install menu
/// except the seed <c>install.sh</c> / <c>install.ps1</c> write for <c>--channel</c> (issue #2084).
/// </summary>
public sealed class InitStartRouteTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public InitStartRouteTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    private string RouteFor(string configJson)
    {
        File.WriteAllText(_paths.NetclawConfigPath, configJson);
        return InitExistingInstallViewModel.ResolveStartRoute(_paths);
    }

    [Fact]
    public void Missing_config_starts_the_wizard()
    {
        Assert.False(File.Exists(_paths.NetclawConfigPath));
        Assert.Equal(InitExistingInstallViewModel.WizardRoute, InitExistingInstallViewModel.ResolveStartRoute(_paths));
    }

    [Theory]
    [InlineData("""{"configVersion":1,"Daemon":{"UpdateChannel":"beta"}}""")] // install.sh and install.ps1
    [InlineData("""{"Daemon":{"UpdateChannel":"beta"}}""")]
    [InlineData("""{"configVersion":1}""")]
    [InlineData("""{"configVersion":1,"Daemon":{}}""")]
    [InlineData("""{"ConfigVersion":1,"daemon":{"updateChannel":"beta"}}""")]
    [InlineData("""{"CONFIGVERSION":1,"DAEMON":{"UPDATECHANNEL":"beta"}}""")]
    public void Installer_seed_starts_the_wizard(string json)
        => Assert.Equal(InitExistingInstallViewModel.WizardRoute, RouteFor(json));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"configVersion":1,"Daemon":{"UpdateChannel":"beta","Port":5199}}""")]
    [InlineData("""{"configVersion":1,"Daemon":{"UpdateChannel":"beta"},"Workspaces":{}}""")]
    [InlineData("""{"configVersion":1,"Daemon":{"UpdateChannel":"beta"},"Providers":{"ollama":{"BaseUrl":"http://localhost:11434"}}}""")]
    [InlineData("""{"configVersion":1,"Daemon":{"UpdateChannel":"beta"},"Security":{"DeploymentPosture":"Team"}}""")]
    [InlineData("""{"Daemon":{"Port":5199}}""")]
    [InlineData("""{"Daemon":"beta"}""")]
    [InlineData("[]")]
    public void Config_with_anything_else_opens_the_menu(string json)
        => Assert.Equal(InitExistingInstallViewModel.MenuRoute, RouteFor(json));

    [Fact]
    public void Hand_written_team_config_without_secrets_opens_the_menu()
    {
        const string json = """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team" },
              "Tools": { "AudienceProfiles": { "Team": { "ShellMode": "Off" } } },
              "Models": { "Roles": { "Main": { "Provider": "ollama", "Model": "llama3" } } }
            }
            """;

        Assert.Equal(InitExistingInstallViewModel.MenuRoute, RouteFor(json));
        Assert.False(File.Exists(_paths.SecretsPath));
    }

    [Fact]
    public void Malformed_config_opens_the_menu()
        => Assert.Equal(InitExistingInstallViewModel.MenuRoute, RouteFor("{ not json"));
}
