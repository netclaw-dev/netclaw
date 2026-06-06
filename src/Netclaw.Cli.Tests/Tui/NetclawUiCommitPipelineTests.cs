// -----------------------------------------------------------------------
// <copyright file="NetclawUiCommitPipelineTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class NetclawUiCommitPipelineTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Static_validation_failure_does_not_call_dynamic_validation_or_persist()
    {
        var draft = "bad";
        var dynamicCalled = false;
        var file = SeedFile();
        NetclawUiCommitResult? observedResult = null;
        var commit = CreateCommit(
            readDraft: () => draft,
            staticValidate: _ => NetclawUiValidationResult.Failed("static failure"),
            dynamicValidate: (_, _) =>
            {
                dynamicCalled = true;
                return ValueTask.FromResult(NetclawUiValidationResult.Passed());
            },
            persist: (_, _) => WriteFile(file, "changed"),
            afterCommit: result => observedResult = result);

        var result = await new NetclawUiCommitPipeline().CommitAsync(
            commit,
            NetclawUiCommitTrigger.Enter,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(NetclawUiCommitStage.StaticValidation, result.Stage);
        Assert.False(dynamicCalled);
        Assert.Equal("before", File.ReadAllText(file));
        Assert.Same(result, observedResult);
    }

    [Fact]
    public async Task Dynamic_validation_failure_blocks_persistence_and_reports_save_anyway_when_allowed()
    {
        var draft = "https://skills.example.test";
        var file = SeedFile();
        var commit = CreateCommit(
            readDraft: () => draft,
            dynamicValidate: (_, _) => ValueTask.FromResult(NetclawUiValidationResult.Warning("probe failed")),
            failurePolicy: NetclawUiDynamicFailurePolicy.AllowSaveAnyway,
            persist: (_, _) => WriteFile(file, "changed"));

        var result = await new NetclawUiCommitPipeline().CommitAsync(
            commit,
            NetclawUiCommitTrigger.Enter,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(NetclawUiCommitStage.DynamicValidation, result.Stage);
        Assert.True(result.CanSaveAnyway);
        Assert.Equal("before", File.ReadAllText(file));
    }

    [Fact]
    public async Task Save_anyway_persists_after_static_validation_passes_and_policy_allows_override()
    {
        var draft = "https://skills.example.test";
        var file = SeedFile();
        var commit = CreateCommit(
            readDraft: () => draft,
            dynamicValidate: (_, _) => ValueTask.FromResult(NetclawUiValidationResult.Warning("probe failed")),
            failurePolicy: NetclawUiDynamicFailurePolicy.AllowSaveAnyway,
            persist: (value, _) => WriteFile(file, value));

        var result = await new NetclawUiCommitPipeline().CommitAsync(
            commit,
            NetclawUiCommitTrigger.SaveAnyway,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(NetclawUiCommitStage.Completed, result.Stage);
        Assert.Equal(draft, File.ReadAllText(file));
    }

    [Fact]
    public async Task Save_anyway_does_not_override_static_validation_failure()
    {
        var file = SeedFile();
        var commit = CreateCommit(
            readDraft: () => "bad",
            staticValidate: _ => NetclawUiValidationResult.Failed("static failure"),
            dynamicValidate: (_, _) => ValueTask.FromResult(NetclawUiValidationResult.Warning("probe failed")),
            failurePolicy: NetclawUiDynamicFailurePolicy.AllowSaveAnyway,
            persist: (_, _) => WriteFile(file, "changed"));

        var result = await new NetclawUiCommitPipeline().CommitAsync(
            commit,
            NetclawUiCommitTrigger.SaveAnyway,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(NetclawUiCommitStage.StaticValidation, result.Stage);
        Assert.Equal("before", File.ReadAllText(file));
    }

    [Fact]
    public void Not_applicable_dynamic_check_requires_non_empty_justification()
    {
        var ex = Assert.Throws<ArgumentException>(() => NetclawUiDynamicCheck<string>.NotApplicable(" "));

        Assert.Contains("non-empty justification", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Persistence_exception_surfaces_error_result()
    {
        var commit = CreateCommit(
            readDraft: () => "good",
            persist: (_, _) => throw new InvalidOperationException("disk full"));

        var result = await new NetclawUiCommitPipeline().CommitAsync(
            commit,
            NetclawUiCommitTrigger.Enter,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(NetclawUiCommitStage.Persistence, result.Stage);
        Assert.Contains("disk full", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private string SeedFile()
    {
        var file = Path.Combine(_dir.Path, $"state-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "before");
        return file;
    }

    private static ValueTask WriteFile(string file, string value)
    {
        File.WriteAllText(file, value);
        return ValueTask.CompletedTask;
    }

    private static NetclawUiCommit<string> CreateCommit(
        Func<string> readDraft,
        Func<string, NetclawUiValidationResult>? staticValidate = null,
        Func<string, CancellationToken, ValueTask<NetclawUiValidationResult>>? dynamicValidate = null,
        NetclawUiDynamicFailurePolicy failurePolicy = NetclawUiDynamicFailurePolicy.Block,
        Func<string, CancellationToken, ValueTask>? persist = null,
        Action<NetclawUiCommitResult>? afterCommit = null)
        => new(
            Id: "test.field",
            Label: "Test field",
            ReadDraft: readDraft,
            WriteDraft: _ => { },
            Validate: staticValidate ?? (_ => NetclawUiValidationResult.Passed()),
            DynamicCheck: dynamicValidate is null
                ? NetclawUiDynamicCheck<string>.NotApplicable("Pure in-memory test commit has no runtime dependency.")
                : NetclawUiDynamicCheck<string>.Required(dynamicValidate, failurePolicy),
            PersistAsync: persist ?? ((_, _) => ValueTask.CompletedTask),
            AfterCommit: afterCommit ?? (_ => { }));
}
