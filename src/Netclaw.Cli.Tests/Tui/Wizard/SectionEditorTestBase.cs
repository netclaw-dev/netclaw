// -----------------------------------------------------------------------
// <copyright file="SectionEditorTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public abstract class SectionEditorTestBase<TEditor> : WizardStepTestBase
    where TEditor : class, IWizardStepViewModel, ISectionEditor
{
    protected ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Context.Paths);
        services.AddSingleton(new ProviderDescriptorRegistry([]));
        services.AddSingleton<IProviderProbe, FakeProviderProbe>();
        services.AddTransient<TEditor>();
        return services.BuildServiceProvider();
    }

    protected TEditor CreateEditor()
    {
        using var services = BuildServices();
        return ActivatorUtilities.CreateInstance<TEditor>(services);
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
