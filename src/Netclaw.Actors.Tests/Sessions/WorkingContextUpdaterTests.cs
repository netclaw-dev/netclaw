// -----------------------------------------------------------------------
// <copyright file="WorkingContextUpdaterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public class WorkingContextUpdaterTests
{
    public static TheoryData<string> NonSuccessCategories { get; } = new()
    {
        nameof(ToolInvocationOutcomeCategory.InvalidInput),
        nameof(ToolInvocationOutcomeCategory.AccessDenied),
        nameof(ToolInvocationOutcomeCategory.NotFound),
        nameof(ToolInvocationOutcomeCategory.TransientFailure),
        nameof(ToolInvocationOutcomeCategory.RecoverableCorrection)
    };

    [Fact]
    public void Successful_receipts_apply_canonical_activity_in_result_order()
    {
        var first = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "receipt-first.txt"));
        var second = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "receipt-second.txt"));
        var results = new[]
        {
            Result("call-1", "file_read", "presentation is irrelevant"),
            Result("call-2", "file_edit", "Error-looking presentation is still not authority"),
            Result("call-3", "file_read", "same file again")
        };
        var receipts = new Dictionary<string, ToolInvocationReceipt>(StringComparer.Ordinal)
        {
            ["call-1"] = Success(first, ToolFileActivityKind.Read),
            ["call-2"] = Success(second, ToolFileActivityKind.Changed),
            ["call-3"] = Success(first, ToolFileActivityKind.Read)
        };

        var updated = WorkingContextUpdater.UpdateFromToolReceipts(
            WorkingContext.Empty,
            results,
            receipts);

        Assert.Equal([first, second], updated.RecentFiles);
    }

    [Theory]
    [MemberData(nameof(NonSuccessCategories))]
    public void Failed_or_corrective_receipts_cannot_add_recent_files(string categoryName)
    {
        var category = Enum.Parse<ToolInvocationOutcomeCategory>(categoryName);
        var result = Result("call-1", "file_read", "successful-looking presentation");
        ToolInvocationReceipt receipt = category == ToolInvocationOutcomeCategory.RecoverableCorrection
            ? new ToolInvocationReceipt.Correction(ToolRemediationCode.SetWorkingDirectory)
            : new ToolInvocationReceipt.OtherOutcome(category);

        var updated = WorkingContextUpdater.UpdateFromToolReceipts(
            WorkingContext.Empty,
            [result],
            new Dictionary<string, ToolInvocationReceipt>(StringComparer.Ordinal)
            {
                ["call-1"] = receipt
            });

        Assert.Empty(updated.RecentFiles);
    }

    [Fact]
    public void Missing_receipt_cannot_claim_activity_from_arguments_or_result()
    {
        var updated = WorkingContextUpdater.UpdateFromToolReceipts(
            WorkingContext.Empty,
            [Result("call-1", "mcp_file_tool", "Successfully wrote /outside/file.txt")],
            new Dictionary<string, ToolInvocationReceipt>(StringComparer.Ordinal));

        Assert.Empty(updated.RecentFiles);
    }

    [Fact]
    public void Accepted_result_uses_canonical_file_activity_from_a_successful_receipt()
    {
        // Arrange
        var canonicalPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "canonical.txt"));
        var message = Result("call-1", "file_read", "Read /misleading.txt");
        var receipt = Success(canonicalPath, ToolFileActivityKind.Read);

        // Act
        var updated = WorkingContextUpdater.AcceptToolResult(WorkingContext.Empty, message, receipt);

        // Assert
        Assert.Equal([canonicalPath], updated.RecentFiles);
        Assert.Null(updated.ProjectDirectory);
    }

    [Theory]
    [MemberData(nameof(NonSuccessCategories))]
    public void Unsuccessful_result_cannot_change_project_or_file_context(string categoryName)
    {
        // Arrange
        var current = new WorkingContext { ProjectDirectory = "/existing/project" };
        var message = Result("call-1", "set_working_directory", "/misleading/project");
        var category = Enum.Parse<ToolInvocationOutcomeCategory>(categoryName);
        ToolInvocationReceipt receipt = category == ToolInvocationOutcomeCategory.RecoverableCorrection
            ? new ToolInvocationReceipt.Correction(ToolRemediationCode.SetWorkingDirectory)
            : new ToolInvocationReceipt.OtherOutcome(category);

        // Act
        var updated = WorkingContextUpdater.AcceptToolResult(current, message, receipt);

        // Assert
        Assert.Same(current, updated);
    }

    [Fact]
    public void Only_successful_working_directory_tool_can_declare_project_scope()
    {
        // Arrange
        var declaredPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "declared-project"));
        var receipt = new ToolInvocationReceipt.Succeeded([], declaredPath);
        var wrongTool = Result("call-1", "file_read", declaredPath);
        var rightTool = Result("call-2", "set_working_directory", "/misleading/project");

        // Act
        var rejected = WorkingContextUpdater.AcceptToolResult(WorkingContext.Empty, wrongTool, receipt);
        var accepted = WorkingContextUpdater.AcceptToolResult(WorkingContext.Empty, rightTool, receipt);

        // Assert
        Assert.Null(rejected.ProjectDirectory);
        Assert.Equal(declaredPath, accepted.ProjectDirectory);
    }

    [Fact]
    public void Missing_receipt_cannot_change_project_scope_from_presentation_text()
    {
        // Arrange
        var current = new WorkingContext { ProjectDirectory = "/existing/project" };
        var message = Result("call-1", "set_working_directory", "/misleading/project");

        // Act
        var updated = WorkingContextUpdater.AcceptToolResult(current, message, null);

        // Assert
        Assert.Same(current, updated);
    }

    [Fact]
    public void Receipt_is_terminal_and_cannot_be_replaced()
    {
        var outputs = new ToolExecutionOutputs();

        Assert.True(outputs.TryComplete(new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.AccessDenied)));
        Assert.False(outputs.TryComplete(Success(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "late.txt")),
            ToolFileActivityKind.Read)));
        Assert.Equal(ToolInvocationOutcomeCategory.AccessDenied, outputs.Receipt?.Category);
        Assert.False(outputs.Receipt is ToolInvocationReceipt.Succeeded { FileActivity.Count: > 0 });
    }

    [Fact]
    public void Other_outcome_rejects_success_category()
        => Assert.Throws<ArgumentException>(() => new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.Success));

    [Fact]
    public void Other_outcome_rejects_correction_without_remediation()
        => Assert.Throws<ArgumentException>(() => new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.RecoverableCorrection));

    [Fact]
    public void Other_outcome_rejects_undefined_category()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ToolInvocationReceipt.OtherOutcome((ToolInvocationOutcomeCategory)int.MaxValue));

    [Fact]
    public void Remediation_rejects_undefined_code()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolInvocationReceipt.Correction((ToolRemediationCode)int.MaxValue));
    }

    private static ToolInvocationReceipt Success(string path, ToolFileActivityKind kind)
        => new ToolInvocationReceipt.Succeeded([new ToolFileActivity(path, kind)], null);

    private static SerializableChatMessage Result(string callId, string name, string content)
        => new()
        {
            Role = ChatRole.Tool,
            Name = name,
            ToolCallId = new ToolCallId(callId),
            Content = content
        };
}
