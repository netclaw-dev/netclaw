// -----------------------------------------------------------------------
// <copyright file="NetclawValidatedPickerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class NetclawValidatedPickerTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Enter_commits_selected_option_through_pipeline()
    {
        var draft = "first";
        var file = SeedFile();
        var component = CreateComponent(
            readDraft: () => draft,
            writeDraft: value => draft = value,
            persist: (value, _) => WriteFile(file, value));

        component.HandleInput(Key(ConsoleKey.DownArrow));
        component.HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal("second", draft);
        Assert.Equal("second", File.ReadAllText(file));
        Assert.True(component.LastCommitResult?.Success);
    }

    [Fact]
    public void Enter_dynamic_failure_blocks_then_second_enter_saves_anyway()
    {
        var draft = "first";
        var file = SeedFile();
        var component = CreateComponent(
            readDraft: () => draft,
            writeDraft: value => draft = value,
            dynamicValidate: (_, _) => ValueTask.FromResult(NetclawUiValidationResult.Warning("probe failed")),
            persist: (value, _) => WriteFile(file, value));

        component.HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal("before", File.ReadAllText(file));
        Assert.False(component.LastCommitResult?.Success);
        Assert.True(component.LastCommitResult?.CanSaveAnyway);

        component.HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal("first", File.ReadAllText(file));
        Assert.True(component.LastCommitResult?.Success);
    }

    private string SeedFile()
    {
        var file = Path.Combine(_dir.Path, $"state-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "before");
        return file;
    }

    private static NetclawValidatedPicker<string> CreateComponent(
        Func<string> readDraft,
        Action<string> writeDraft,
        Func<string, CancellationToken, ValueTask<NetclawUiValidationResult>>? dynamicValidate = null,
        Func<string, CancellationToken, ValueTask>? persist = null)
    {
        var commit = new NetclawUiCommit<string>(
            Id: "test.picker",
            Label: "Test picker",
            ReadDraft: readDraft,
            WriteDraft: writeDraft,
            Validate: _ => NetclawUiValidationResult.Passed(),
            DynamicCheck: dynamicValidate is null
                ? NetclawUiDynamicCheck<string>.NotApplicable("Picker test has no runtime dependency.")
                : NetclawUiDynamicCheck<string>.Required(dynamicValidate, NetclawUiDynamicFailurePolicy.AllowSaveAnyway),
            PersistAsync: persist ?? ((_, _) => ValueTask.CompletedTask),
            AfterCommit: _ => { });

        return new NetclawValidatedPicker<string>(
            commit,
            new NetclawUiCommitPipeline(),
            [new NetclawPickerOption<string>("first", "First"), new NetclawPickerOption<string>("second", "Second")]);
    }

    private static ValueTask WriteFile(string file, string value)
    {
        File.WriteAllText(file, value);
        return ValueTask.CompletedTask;
    }

    private static ConsoleKeyInfo Key(ConsoleKey key)
        => new('\0', key, shift: false, alt: false, control: false);
}
