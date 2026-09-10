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
