// -----------------------------------------------------------------------
// <copyright file="ToolRemediationPresenterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class ToolRemediationPresenterTests
{
    private static readonly (ToolRemediationCode Code, string ExpectedAction)[] SupportedRemediations =
    {
        (
            ToolRemediationCode.SetWorkingDirectory,
            "Next action: call set_working_directory with the project directory from this result, then retry the failed tool call."
        ),
        (
            ToolRemediationCode.UseSessionScratch,
            "Next action: use the session scratch directory from this result for disposable files, or retry unchanged for exact platform paths."
        ),
        (
            ToolRemediationCode.ProvideUniqueOldString,
            "Next action: retry file_edit with a unique OldString, or set ReplaceAll=true when every match should change."
        )
    };

    [Fact]
    public void Presenter_appends_one_action_for_each_supported_remediation()
    {
        foreach (var (code, expectedAction) in SupportedRemediations)
        {
            var receipt = new ToolInvocationReceipt(
                ToolInvocationOutcomeCategory.RecoverableCorrection,
                remediationCode: code);

            var result = ToolRemediationPresenter.Present(
                Message("bounded failure"),
                receipt,
                setWorkingDirectoryAvailable: true);

            Assert.Equal($"bounded failure\n{expectedAction}", result.Content);
            Assert.Equal(1, CountOccurrences(result.Content, "Next action:"));
        }
    }

    [Fact]
    public void Presenter_suppresses_hidden_working_directory_tool()
    {
        var receipt = new ToolInvocationReceipt(
            ToolInvocationOutcomeCategory.RecoverableCorrection,
            remediationCode: ToolRemediationCode.SetWorkingDirectory);

        var result = ToolRemediationPresenter.Present(
            Message("bounded failure"),
            receipt,
            setWorkingDirectoryAvailable: false);

        Assert.Equal("bounded failure", result.Content);
        Assert.DoesNotContain("set_working_directory", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Presenter_leaves_non_corrective_result_unchanged()
    {
        var message = Message("denied");
        var receipt = new ToolInvocationReceipt(ToolInvocationOutcomeCategory.AccessDenied);

        Assert.Same(message, ToolRemediationPresenter.Present(message, receipt, true));
        Assert.Same(message, ToolRemediationPresenter.Present(message, null, true));
    }

    private static SerializableChatMessage Message(string content)
        => new() { Role = ChatRole.Tool, Content = content };

    private static int CountOccurrences(string value, string term)
        => value.Split(term, StringSplitOptions.None).Length - 1;
}
