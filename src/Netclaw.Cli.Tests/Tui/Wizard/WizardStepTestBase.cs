// -----------------------------------------------------------------------
// <copyright file="WizardStepTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public abstract class WizardStepTestBase : IDisposable
{
    private readonly string _tempDir;
    protected readonly WizardContext Context;

    protected WizardStepTestBase()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        var paths = new NetclawPaths(_tempDir);
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
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }
}
