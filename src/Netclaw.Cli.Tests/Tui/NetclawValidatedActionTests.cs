// -----------------------------------------------------------------------
// <copyright file="NetclawValidatedActionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class NetclawValidatedActionTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Repeated_dynamic_failure_does_not_silently_save_anyway()
    {
        var file = Path.Combine(_dir.Path, "state.txt");
        File.WriteAllText(file, "before");
        var commit = new NetclawUiCommit<string>(
            Id: "test.action",
            Label: "Test action",
            ReadDraft: () => "after",
            WriteDraft: _ => { },
            Validate: _ => NetclawUiValidationResult.Passed(),
            DynamicCheck: NetclawUiDynamicCheck<string>.Required(
                (_, _) => ValueTask.FromResult(NetclawUiValidationResult.Warning("probe failed")),
                NetclawUiDynamicFailurePolicy.AllowSaveAnyway),
            PersistAsync: (value, _) =>
            {
                File.WriteAllText(file, value);
                return ValueTask.CompletedTask;
            },
            AfterCommit: _ => { });
        var action = new NetclawValidatedAction<string>(commit, new NetclawUiCommitPipeline(), NetclawUiCommitTrigger.AutoSave);

        var first = action.Invoke();
        var second = action.Invoke();

        Assert.False(first.Success);
        Assert.True(first.CanSaveAnyway);
        Assert.False(second.Success);
        Assert.True(second.CanSaveAnyway);
        Assert.Equal("before", File.ReadAllText(file));
    }
}
