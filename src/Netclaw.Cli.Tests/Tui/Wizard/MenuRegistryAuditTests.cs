// -----------------------------------------------------------------------
// <copyright file="MenuRegistryAuditTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class MenuRegistryAuditTests
{
    [Fact]
    public void RegisteredLeafEditors_AreExpectedSet()
    {
        using var services = BuildServices();
        var registry = services.GetRequiredService<SectionEditorRegistry>();

        var ids = registry.Editors.Select(e => e.SectionId).OrderBy(static x => x).ToArray();
        Assert.Equal(["exposure-mode", "feature-selection", "identity", "provider", "security-posture"], ids);
    }

    [Fact]
    public void RegisteredLeafEditors_DeclareDoctorChecks_OrJustifiedExemption()
    {
        using var services = BuildServices();
        var registry = services.GetRequiredService<SectionEditorRegistry>();

        foreach (var editor in registry.Editors)
        {
            var hasChecks = editor.RelevantDoctorChecks.Count > 0;
            var justification = SectionEditorAudit.GetDoctorCheckJustification(editor);

            Assert.True(hasChecks || !string.IsNullOrWhiteSpace(justification),
                $"Section editor '{editor.SectionId}' must declare relevant doctor checks or a [NoDoctorChecks] justification.");
        }
    }

    [Fact]
    public void MenuHiddenLeafEditors_AreLimitedToKnownInitOwnedExemptions()
    {
        using var services = BuildServices();
        var registry = services.GetRequiredService<SectionEditorRegistry>();

        var hiddenEditors = registry.Editors.Where(e => !e.ShowInMenu).Select(e => e.SectionId).OrderBy(static x => x).ToArray();
        Assert.Equal(SectionEditorExemptions.ConfigSmokeExemptions.OrderBy(static x => x).ToArray(), hiddenEditors);
    }

    [Fact]
    public void RegisteredLeafEditors_HaveConcreteLeafTestClasses()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = nameof(ProviderSectionEditorTests),
            ["identity"] = nameof(IdentitySectionEditorTests),
            ["security-posture"] = nameof(SecurityPostureSectionEditorTests),
            ["feature-selection"] = nameof(FeatureSelectionSectionEditorTests),
            ["exposure-mode"] = nameof(ExposureModeSectionEditorTests)
        };

        using var services = BuildServices();
        var registry = services.GetRequiredService<SectionEditorRegistry>();
        var testTypeNames = typeof(MenuRegistryAuditTests).Assembly.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var editor in registry.Editors)
        {
            Assert.True(expected.TryGetValue(editor.SectionId, out var testTypeName),
                $"Add a concrete section-editor test mapping for '{editor.SectionId}'.");
            Assert.Contains(testTypeName, testTypeNames);
        }
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NetclawPaths());
        services.AddSingleton(ProviderCommand.CreateDefaultRegistry());
        services.AddSingleton<IProviderProbe, FakeProviderProbe>();
        services
            .AddSectionEditor<ProviderStepViewModel>()
            .AddSectionEditor<IdentityStepViewModel>()
            .AddSectionEditor<SecurityPostureStepViewModel>()
            .AddSectionEditor<FeatureSelectionStepViewModel>()
            .AddSectionEditor<ExposureModeStepViewModel>();
        return services.BuildServiceProvider();
    }

    private sealed class FakeProviderProbe : IProviderProbe
    {
        public Task<ProviderProbeResult> ProbeAsync(string providerType, string? endpoint, string? apiKey, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(true, null, []));

        public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(true, null, []));

        public Task<ProviderProbeResult> ProbeAsync(string providerType, string? endpoint, string? credential, AuthMethod authMethod, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(true, null, []));
    }
}
