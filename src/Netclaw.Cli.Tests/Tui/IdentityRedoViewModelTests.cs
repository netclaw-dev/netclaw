// -----------------------------------------------------------------------
// <copyright file="IdentityRedoViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using System.Text.Json;
using Netclaw.Cli.Tui;
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

    public IdentityRedoViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

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
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var securityBefore = ReadSection(configBefore, "Security");
        var providersBefore = ReadSection(configBefore, "Providers");

        // Identity files do not exist before a redo run.
        Assert.False(File.Exists(_paths.SoulPath));
        Assert.False(File.Exists(_paths.ToolingPath));

        using var vm = new IdentityRedoViewModel(_paths);
        DriveToSaved(vm);

        Assert.True(vm.IsSaved.Value);

        // The identity files were (re)written by the redo flow.
        Assert.True(File.Exists(_paths.SoulPath), "SOUL.md must be written by the redo flow.");
        Assert.True(File.Exists(_paths.ToolingPath), "TOOLING.md must be written by the redo flow.");
        Assert.NotEqual(0, new FileInfo(_paths.SoulPath).Length);
        Assert.NotEqual(0, new FileInfo(_paths.ToolingPath).Length);

        // netclaw.json is byte-for-byte untouched: redo never calls WriteConfig.
        var configAfter = File.ReadAllText(_paths.NetclawConfigPath);
        Assert.Equal(configBefore, configAfter);
        Assert.Equal(securityBefore, ReadSection(configAfter, "Security"));
        Assert.Equal(providersBefore, ReadSection(configAfter, "Providers"));
    }

    [Fact]
    public void GoBack_at_first_identity_field_routes_to_existing_install_menu()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
        using var vm = new IdentityRedoViewModel(_paths);

        string? route = null;
        SetNavigate(vm, r => route = r);

        // Esc / GoBack at the very first identity sub-step exits the redo flow
        // back to the existing-install menu rather than swallowing the keystroke.
        vm.GoBack();

        Assert.Equal(InitExistingInstallViewModel.MenuRoute, route);
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
        return doc.RootElement.GetProperty(section).GetRawText();
    }

    // The Navigate delegate is a protected, framework-wired member on ReactiveViewModel
    // (set via the internal WireUp during page binding, which tests cannot reach).
    // Inject it directly so we can observe the route the redo flow requests on exit.
    private static void SetNavigate(ReactiveViewModel vm, Action<string> navigate)
    {
        var property = typeof(ReactiveViewModel).GetProperty(
            "Navigate",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(vm, navigate);
    }
}
