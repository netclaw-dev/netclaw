// -----------------------------------------------------------------------
// <copyright file="WizardStepTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public abstract class WizardStepTestBase : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    protected readonly WizardContext Context;

    protected WizardStepTestBase()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        Context = new WizardContext
        {
            Paths = paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };
    }

    public virtual void Dispose()
    {
        Context.Dispose();
        _dir.Dispose();
    }

    protected static void AssertSectionEnabled(Dictionary<string, object> config, string sectionKey, bool expected)
    {
        Assert.True(config.ContainsKey(sectionKey), $"Config should contain '{sectionKey}' section");
        var section = (Dictionary<string, object>)config[sectionKey];
        Assert.Equal(expected, section["Enabled"]);
    }

    protected static void AssertNoEnabledKey(Dictionary<string, object> config, string sectionKey)
    {
        if (config.TryGetValue(sectionKey, out var obj) && obj is Dictionary<string, object> section)
        {
            Assert.False(section.ContainsKey("Enabled"),
                $"Section '{sectionKey}' should not have an 'Enabled' key");
        }
    }
}
