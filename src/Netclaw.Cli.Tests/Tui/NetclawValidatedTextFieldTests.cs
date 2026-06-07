// -----------------------------------------------------------------------
// <copyright file="NetclawValidatedTextFieldTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Tests.Utilities;
using Termina.Input;
using Termina.Layout;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class NetclawValidatedTextFieldTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Enter_commits_typed_and_pasted_text_through_pipeline()
    {
        var draft = string.Empty;
        var file = SeedFile();
        var component = CreateComponent(
            readDraft: () => draft,
            writeDraft: value => draft = value,
            persist: (value, _) => WriteFile(file, value));

        component.HandleInput(Key('a'));
        component.HandlePaste(new PasteEvent("bc"));
        component.HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal("abc", draft);
        Assert.Equal("abc", File.ReadAllText(file));
        Assert.True(component.LastCommitResult?.Success);
    }

    [Fact]
    public void Enter_static_validation_failure_leaves_file_unchanged()
    {
        var draft = string.Empty;
        var file = SeedFile();
        var component = CreateComponent(
            readDraft: () => draft,
            writeDraft: value => draft = value,
            validate: value => value == "bad"
                ? NetclawUiValidationResult.Failed("bad input")
                : NetclawUiValidationResult.Passed(),
            persist: (value, _) => WriteFile(file, value));

        component.HandleInput(Key('b'));
        component.HandleInput(Key('a'));
        component.HandleInput(Key('d'));
        component.HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal("bad", draft);
        Assert.Equal("before", File.ReadAllText(file));
        Assert.False(component.LastCommitResult?.Success);
        Assert.Equal(NetclawUiCommitStage.StaticValidation, component.LastCommitResult?.Stage);
    }

    [Fact]
    public void Arrow_keys_edit_middle_of_text_before_commit()
    {
        var draft = string.Empty;
        var file = SeedFile();
        var component = CreateComponent(
            readDraft: () => draft,
            writeDraft: value => draft = value,
            persist: (value, _) => WriteFile(file, value));

        component.HandleInput(Key('a'));
        component.HandleInput(Key('b'));
        component.HandleInput(Key('c'));
        component.HandleInput(Key(ConsoleKey.LeftArrow));
        component.HandleInput(Key(ConsoleKey.LeftArrow));
        component.HandleInput(Key('X'));
        component.HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal("aXbc", draft);
        Assert.Equal("aXbc", File.ReadAllText(file));
        Assert.True(component.LastCommitResult?.Success);
    }

    [Fact]
    public void Enter_dynamic_failure_blocks_until_explicit_save_anyway()
    {
        var draft = string.Empty;
        var file = SeedFile();
        var component = CreateComponent(
            readDraft: () => draft,
            writeDraft: value => draft = value,
            dynamicValidate: (_, _) => ValueTask.FromResult(NetclawUiValidationResult.Warning("probe failed")),
            persist: (value, _) => WriteFile(file, value));

        component.HandleInput(Key('a'));
        component.HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal("before", File.ReadAllText(file));
        Assert.False(component.LastCommitResult?.Success);
        Assert.True(component.LastCommitResult?.CanSaveAnyway);

        component.HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal("before", File.ReadAllText(file));
        Assert.False(component.LastCommitResult?.Success);
        Assert.True(component.LastCommitResult?.CanSaveAnyway);

        component.Commit(NetclawUiCommitTrigger.SaveAnyway);

        Assert.Equal("a", File.ReadAllText(file));
        Assert.True(component.LastCommitResult?.Success);
    }

    [Fact]
    public void Draft_change_after_dynamic_failure_requires_validation_again()
    {
        var draft = string.Empty;
        var attempts = 0;
        var file = SeedFile();
        var component = CreateComponent(
            readDraft: () => draft,
            writeDraft: value => draft = value,
            dynamicValidate: (_, _) =>
            {
                attempts++;
                return ValueTask.FromResult(NetclawUiValidationResult.Warning("probe failed"));
            },
            persist: (value, _) => WriteFile(file, value));

        component.HandleInput(Key('a'));
        component.HandleInput(Key(ConsoleKey.Enter));
        component.HandleInput(Key('b'));
        component.HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal(2, attempts);
        Assert.Equal("before", File.ReadAllText(file));
        Assert.False(component.LastCommitResult?.Success);
        Assert.True(component.LastCommitResult?.CanSaveAnyway);
    }

    [Fact]
    public void Backspace_updates_draft_without_committing()
    {
        var draft = string.Empty;
        var file = SeedFile();
        var component = CreateComponent(
            readDraft: () => draft,
            writeDraft: value => draft = value,
            persist: (value, _) => WriteFile(file, value));

        component.HandleInput(Key('a'));
        component.HandleInput(Key('b'));
        component.HandleInput(Key(ConsoleKey.Backspace));

        Assert.Equal("a", draft);
        Assert.Equal("before", File.ReadAllText(file));
        Assert.Null(component.LastCommitResult);
    }

    private string SeedFile()
    {
        var file = Path.Combine(_dir.Path, $"state-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "before");
        return file;
    }

    private static NetclawValidatedTextField CreateComponent(
        Func<string> readDraft,
        Action<string> writeDraft,
        Func<string, NetclawUiValidationResult>? validate = null,
        Func<string, CancellationToken, ValueTask<NetclawUiValidationResult>>? dynamicValidate = null,
        Func<string, CancellationToken, ValueTask>? persist = null)
    {
        var commit = new NetclawUiCommit<string>(
            Id: "test.text",
            Label: "Test text",
            ReadDraft: readDraft,
            WriteDraft: writeDraft,
            Validate: validate ?? (_ => NetclawUiValidationResult.Passed()),
            DynamicCheck: dynamicValidate is null
                ? NetclawUiDynamicCheck<string>.NotApplicable("Text field test has no runtime dependency.")
                : NetclawUiDynamicCheck<string>.Required(dynamicValidate, NetclawUiDynamicFailurePolicy.AllowSaveAnyway),
            PersistAsync: persist ?? ((_, _) => ValueTask.CompletedTask),
            AfterCommit: _ => { });

        return new NetclawValidatedTextField(commit, new NetclawUiCommitPipeline(), "Type here...");
    }

    private static ValueTask WriteFile(string file, string value)
    {
        File.WriteAllText(file, value);
        return ValueTask.CompletedTask;
    }

    private static ConsoleKeyInfo Key(char key)
        => new(key, Enum.Parse<ConsoleKey>(char.ToUpperInvariant(key).ToString()), shift: false, alt: false, control: false);

    private static ConsoleKeyInfo Key(ConsoleKey key)
        => new('\0', key, shift: false, alt: false, control: false);
}
