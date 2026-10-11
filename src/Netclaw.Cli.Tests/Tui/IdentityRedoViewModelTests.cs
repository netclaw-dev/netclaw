// -----------------------------------------------------------------------
// <copyright file="IdentityRedoViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina.Reactive;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Behavioral coverage for the "redo identity setup" flow. The defining invariant
/// (simplify-netclaw-init) is that this flow rewrites the identity files WITHOUT
/// calling <c>WriteConfig</c>, so a redo must never clobber an existing
/// <c>netclaw.json</c> — security posture and configured providers must survive.
/// </summary>
public sealed class IdentityRedoViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly HealthCheckStepViewModel _readiness = new();

    public IdentityRedoViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() { _readiness.Dispose(); _dir.Dispose(); }

    [Fact]
    public void Redo_rewrites_identity_files_without_clobbering_config()
    {
        // A non-default config: a hardened posture plus a configured provider entry.
        // The redo must leave both untouched while (re)writing SOUL.md / TOOLING.md.
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team" },
              "Providers": { "openrouter": { "BaseUrl": "https://openrouter.ai/api/v1" } },
              "Identity": { "AgentName": "Existing", "UserTimezone": "UTC" }
            }
            """);
        var securityBefore = ReadSection(File.ReadAllText(_paths.NetclawConfigPath), "Security");
        var providersBefore = ReadSection(File.ReadAllText(_paths.NetclawConfigPath), "Providers");

        // Identity files do not exist before a redo run.
        Assert.False(File.Exists(_paths.SoulPath));
        Assert.False(File.Exists(_paths.ToolingPath));

        using var vm = new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness);
        DriveToSaved(vm);

        Assert.True(vm.IsSaved.Value);

        // The identity files were (re)written by the redo flow.
        Assert.True(File.Exists(_paths.SoulPath), "SOUL.md must be written by the redo flow.");
        Assert.True(File.Exists(_paths.ToolingPath), "TOOLING.md must be written by the redo flow.");
        Assert.NotEqual(0, new FileInfo(_paths.SoulPath).Length);
        Assert.NotEqual(0, new FileInfo(_paths.ToolingPath).Length);

        // Only the Identity section of netclaw.json changes; every other section is untouched.
        var configAfter = File.ReadAllText(_paths.NetclawConfigPath);
        Assert.Equal(securityBefore, ReadSection(configAfter, "Security"));
        Assert.Equal(providersBefore, ReadSection(configAfter, "Providers"));
    }

    [Fact]
    public void Redo_persists_the_new_identity_and_the_onboarding_trigger_uses_it()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team" },
              "Identity": { "AgentName": "Existing", "UserName": "Walter", "CommunicationStyle": "Concise & casual", "UserTimezone": "UTC" }
            }
            """);

        using var vm = new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness);
        vm.Step.UserName = "Pat";
        vm.Step.CommunicationStyle = "Detailed & formal";
        DriveToSaved(vm);

        Assert.True(vm.IsSaved.Value);
        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var identity = doc.RootElement.GetProperty("Identity");
        Assert.Equal("Pat", identity.GetProperty("UserName").GetString());
        Assert.Equal("Detailed & formal", identity.GetProperty("CommunicationStyle").GetString());
        Assert.Equal("UTC", identity.GetProperty("UserTimezone").GetString());
        Assert.Equal("Team", doc.RootElement.GetProperty("Security").GetProperty("DeploymentPosture").GetString());

        var trigger = ChatOnboarding.BuildTrigger(_paths);
        Assert.Contains("My name is Pat", trigger);
        Assert.Contains("\"Detailed & formal\"", trigger);
        Assert.DoesNotContain("Walter", trigger);
    }

    [Fact]
    public void GoBack_at_first_identity_field_routes_to_existing_install_menu()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
        using var vm = new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness);

        string? route = null;
        SetNavigate(vm, r => route = r);

        // Esc / GoBack at the very first identity sub-step exits the redo flow
        // back to the existing-install menu rather than swallowing the keystroke.
        vm.GoBack();

        Assert.Equal(InitExistingInstallViewModel.MenuRoute, route);
    }

    [Fact]
    public void Write_failure_names_the_file_even_when_the_path_contains_an_apostrophe()
    {
        var apostropheHome = Path.Combine(_dir.Path, "o'brien");
        var paths = new NetclawPaths(apostropheHome);
        paths.EnsureDirectoriesExist();
        // A directory where SOUL.md belongs makes the write fail with a permission error.
        Directory.CreateDirectory(paths.SoulPath);

        using var vm = new IdentityRedoViewModel(paths, new ChatNavigationState(), _readiness);
        DriveToSaved(vm);

        Assert.False(vm.IsSaved.Value);
        Assert.Equal(
            "Couldn't write SOUL.md: permission denied. Fix it and press Enter to retry.",
            vm.Context.StatusMessage.Value);
    }

    [Fact]
    public void Write_failure_that_is_not_a_permission_error_reports_write_failed()
    {
        // An exclusive handle on SOUL.md makes the write fail with an IOException.
        File.WriteAllText(_paths.SoulPath, "existing");
        using var held = new FileStream(_paths.SoulPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        using var vm = new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness);
        DriveToSaved(vm);

        Assert.False(vm.IsSaved.Value);
        Assert.Equal(
            "Couldn't write SOUL.md: write failed. Fix it and press Enter to retry.",
            vm.Context.StatusMessage.Value);
    }

    [Fact]
    public void Redo_keeps_an_owner_only_config_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return; // file modes are a POSIX concept

        File.WriteAllText(_paths.NetclawConfigPath, """{ "configVersion": 1, "Identity": { "AgentName": "Existing" } }""");
        File.SetUnixFileMode(_paths.NetclawConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using var vm = new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness);
        DriveToSaved(vm);

        Assert.True(vm.IsSaved.Value);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_paths.NetclawConfigPath));
    }

    // The daemon reads keys without case. A second "Identity" beside "identity" makes it stop at
    // startup with "A duplicate key 'Identity:AgentName' was found".
    [Theory]
    [InlineData("""{ "identity": { "AgentName": "Existing", "UserTimezone": "UTC" } }""")]
    [InlineData("""{ "IDENTITY": { "agentname": "Existing", "usertimezone": "UTC", "username": "Walter" } }""")]
    [InlineData("""{ "Identity": { "agentName": "Existing", "userTimezone": "UTC" } }""")]
    public void Redo_writes_into_the_identity_section_spelling_the_file_already_has(string config)
    {
        File.WriteAllText(_paths.NetclawConfigPath, config);

        using var vm = new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness);
        vm.Step.UserName = "Pat";
        DriveToSaved(vm);

        Assert.True(vm.IsSaved.Value);
        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var sections = doc.RootElement.EnumerateObject().Where(p => p.Name.Equals("Identity", StringComparison.OrdinalIgnoreCase)).ToList();
        var identity = Assert.Single(sections).Value;
        Assert.Equal(["AgentName", "CommunicationStyle", "UserName", "UserTimezone"],
            identity.EnumerateObject().Select(p => p.Name.ToUpperInvariant() switch
            {
                "AGENTNAME" => "AgentName", "COMMUNICATIONSTYLE" => "CommunicationStyle",
                "USERNAME" => "UserName", "USERTIMEZONE" => "UserTimezone", _ => p.Name
            }).Order(StringComparer.Ordinal));
        Assert.Equal(identity.EnumerateObject().Count(), identity.EnumerateObject().Select(p => p.Name.ToUpperInvariant()).Distinct().Count());

        // The merged result still loads the way the daemon loads it.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(_paths.NetclawConfigPath, optional: false).Build();
        Assert.Equal("Pat", configuration["Identity:UserName"]);
    }

    [Fact]
    public void Redo_keeps_a_symbolic_link_to_the_config_and_rewrites_the_file_it_points_at()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "symbolic links need a POSIX file system");
        var real = Path.Combine(_dir.Path, "dotfiles-netclaw.json");
        File.WriteAllText(real, """{ "configVersion": 1, "Security": { "DeploymentPosture": "Team" } }""");
        File.CreateSymbolicLink(_paths.NetclawConfigPath, real);

        using var vm = new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness);
        vm.Step.UserName = "Pat";
        DriveToSaved(vm);

        Assert.True(vm.IsSaved.Value);
        Assert.NotNull(new FileInfo(_paths.NetclawConfigPath).LinkTarget);
        Assert.Contains("\"UserName\": \"Pat\"", File.ReadAllText(real), StringComparison.Ordinal);
        Assert.Contains("Team", File.ReadAllText(real), StringComparison.Ordinal);
    }

    [Fact]
    public void Redo_on_a_config_with_comments_stops_before_writing_any_identity_file()
    {
        const string config = "{\n  // hand-edited\n  \"configVersion\": 1,\n}";
        File.WriteAllText(_paths.NetclawConfigPath, config);

        using var vm = new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness);
        DriveToSaved(vm);

        Assert.False(vm.IsSaved.Value);
        Assert.Equal(
            "Couldn't read netclaw.json: it has comments or is not valid JSON. Fix it and press Enter to retry.",
            vm.Context.StatusMessage.Value);
        Assert.False(File.Exists(_paths.SoulPath));
        Assert.Equal(config, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Redo_says_when_the_form_opens_that_netclaw_json_cannot_be_read()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ // note\n \"configVersion\": 1 }");

        using var vm = new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness);

        Assert.Equal(
            "Couldn't read netclaw.json: it has comments or is not valid JSON. Fix it and press Enter to retry.",
            vm.Context.StatusMessage.Value);
    }

    [Fact]
    public void Redo_names_secrets_json_when_that_is_the_file_that_cannot_be_read()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "configVersion": 1 }""");
        File.WriteAllText(_paths.SecretsPath, "{ \"Slack\": { \"BotToken\": ");

        using var vm = new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness);
        Assert.StartsWith("Couldn't read secrets.json", vm.Context.StatusMessage.Value, StringComparison.Ordinal);

        DriveToSaved(vm);

        Assert.False(vm.IsSaved.Value);
        Assert.StartsWith("Couldn't read secrets.json", vm.Context.StatusMessage.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Redo_does_not_touch_SOUL_when_netclaw_json_cannot_be_written()
    {
        File.WriteAllText(_paths.SoulPath, "original soul");
        Directory.CreateDirectory(_paths.NetclawConfigPath); // a directory where the file belongs

        using var vm = new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness);
        DriveToSaved(vm);

        Assert.False(vm.IsSaved.Value);
        Assert.Equal("original soul", File.ReadAllText(_paths.SoulPath));
    }

    // Drives the single-step identity flow forward until the redo reports IsSaved.
    // The orchestrator advances through the identity sub-steps; one extra GoNext past
    // the last sub-step finalizes (writes identity files, sets IsSaved). Guard the loop
    // so a flow that never completes fails loudly instead of hanging.
    private static void DriveToSaved(IdentityRedoViewModel vm)
    {
        for (var i = 0; i < 32 && !vm.IsSaved.Value; i++)
            vm.GoNext();
    }

    private static string ReadSection(string json, string section)
    {
        using var doc = JsonDocument.Parse(json);
        return System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetProperty(section).GetRawText())!.ToJsonString();
    }

    // The Navigate delegate is a protected, framework-wired member on ReactiveViewModel
    // (set via the internal WireUp during page binding, which tests cannot reach).
    // Inject it directly so we can observe the route the redo flow requests on exit.
    internal static void SetNavigate(ReactiveViewModel vm, Action<string> navigate)
    {
        var property = typeof(ReactiveViewModel).GetProperty(
            "Navigate",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(vm, navigate);
    }
}
