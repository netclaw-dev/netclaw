// -----------------------------------------------------------------------
// <copyright file="ToolCorrectionDeliveryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ToolCorrectionDeliveryTests
{
    private static readonly ToolCorrection.NativeToolSuggested Native = new(new ToolName("file_write"));
    private static readonly ToolCorrection.ManagedTemporaryDirectorySuggested Temporary = new(
        new ManagedTemporaryCorrectionTarget("/session/tmp", "/tmp"));
    private static readonly ToolCorrection.ProjectDirectorySuggested Project = new("/project");
    private static readonly ManagedTemporaryCallSemantics.ShellCall Call = new(
        ApprovalShell.Bash, "printf result > /tmp/result.txt", "/tmp", false, TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Native_and_temporary_advice_preserve_both_targets_without_a_shell_retry(bool reverseOrder, bool includeCall)
    {
        var corrections = new ToolCorrectionCollection(reverseOrder ? [Temporary, Native] : [Native, Temporary]);

        var delivery = ToolCorrectionDelivery.Create(corrections, includeCall ? Call : null);

        Assert.Equal(Native.ToolName, delivery.NativeTool);
        Assert.Contains(Native.ToolName.Value, delivery.Content, StringComparison.Ordinal);
        Assert.Contains(Temporary.Target.ManagedTemporaryDirectory, delivery.Content, StringComparison.Ordinal);
        Assert.Equal(ToolRemediationCode.UseNativeTool, delivery.Receipt.RemediationCode);
        Assert.Null(delivery.ManagedTemporaryStateChange);
    }

    [Fact]
    public void Native_only_advice_does_not_arm_a_shell_retry()
    {
        var delivery = ToolCorrectionDelivery.Create(new ToolCorrectionCollection([Native]), Call);

        Assert.Equal(Native.ToolName, delivery.NativeTool);
        Assert.Equal(ToolRemediationCode.UseNativeTool, delivery.Receipt.RemediationCode);
        Assert.Null(delivery.ManagedTemporaryStateChange);
    }

    [Fact]
    public void Project_advice_does_not_arm_a_shell_retry()
    {
        var delivery = ToolCorrectionDelivery.Create(new ToolCorrectionCollection([Project]), Call);

        Assert.Equal(ToolRemediationCode.SetWorkingDirectory, delivery.Receipt.RemediationCode);
        Assert.Null(delivery.NativeTool);
        Assert.Null(delivery.ManagedTemporaryStateChange);
    }

    [Fact]
    public void Temporary_only_advice_arms_the_exact_call_and_target()
    {
        var delivery = ToolCorrectionDelivery.Create(new ToolCorrectionCollection([Temporary]), Call);

        var arm = Assert.IsType<ManagedTemporaryCorrectionChange.Arm>(delivery.ManagedTemporaryStateChange);
        Assert.Equal(Call, arm.Key.Call);
        Assert.Equal(Temporary.Target, arm.Key.Target);
        Assert.Equal(ToolRemediationCode.UseManagedTemporaryDirectory, delivery.Receipt.RemediationCode);
        Assert.Null(delivery.NativeTool);
    }

    [Fact]
    public void Temporary_only_advice_rejects_missing_call_semantics()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ToolCorrectionDelivery.Create(new ToolCorrectionCollection([Temporary]), managedTemporaryCall: null));
    }

    [Fact]
    public void Delivery_rejects_conflicting_targets_and_duplicate_categories()
    {
        IReadOnlyList<ToolCorrection>[] invalidCombinations =
        [
            [Project, Native],
            [Native, Project],
            [Project, Temporary],
            [Temporary, Project],
            [Native, Temporary, Project],
            [Native, new ToolCorrection.NativeToolSuggested(new ToolName("file_read"))],
            [Temporary, new ToolCorrection.ManagedTemporaryDirectorySuggested(new ManagedTemporaryCorrectionTarget("/other/tmp", "/tmp"))],
            [Project, new ToolCorrection.ProjectDirectorySuggested("/other/project")]
        ];

        foreach (var corrections in invalidCombinations)
        {
            var collection = new ToolCorrectionCollection(corrections);
            Assert.Throws<InvalidOperationException>(() => ToolCorrectionDelivery.Create(collection, Call));
        }
    }
}
