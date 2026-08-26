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
        var receipt = category == ToolInvocationOutcomeCategory.RecoverableCorrection
            ? new ToolInvocationReceipt(
                category,
                remediationCode: ToolRemediationCode.SetWorkingDirectory)
            : new ToolInvocationReceipt(category);

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

        Assert.True(outputs.TryComplete(new ToolInvocationReceipt(ToolInvocationOutcomeCategory.AccessDenied)));
        Assert.False(outputs.TryComplete(Success(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "late.txt")),
            ToolFileActivityKind.Read)));
        Assert.Equal(ToolInvocationOutcomeCategory.AccessDenied, outputs.Receipt?.Category);
        Assert.Empty(outputs.Receipt?.FileActivity ?? []);
    }

    [Fact]
    public void Non_success_receipt_rejects_file_activity()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "invalid.txt"));

        var exception = Assert.Throws<ArgumentException>(() => new ToolInvocationReceipt(
            ToolInvocationOutcomeCategory.AccessDenied,
            [new ToolFileActivity(path, ToolFileActivityKind.Read)]));

        Assert.Contains("successful", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recoverable_receipt_requires_remediation()
    {
        Assert.Throws<ArgumentException>(() => new ToolInvocationReceipt(
            ToolInvocationOutcomeCategory.RecoverableCorrection));
    }

    [Fact]
    public void Non_corrective_receipt_rejects_remediation()
    {
        Assert.Throws<ArgumentException>(() => new ToolInvocationReceipt(
            ToolInvocationOutcomeCategory.AccessDenied,
            remediationCode: ToolRemediationCode.SetWorkingDirectory));
    }

    [Fact]
    public void Remediation_rejects_undefined_code()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolInvocationReceipt(
            ToolInvocationOutcomeCategory.RecoverableCorrection,
            remediationCode: (ToolRemediationCode)int.MaxValue));
    }

    private static ToolInvocationReceipt Success(string path, ToolFileActivityKind kind)
        => new(
            ToolInvocationOutcomeCategory.Success,
            [new ToolFileActivity(path, kind)]);

    private static SerializableChatMessage Result(string callId, string name, string content)
        => new()
        {
            Role = ChatRole.Tool,
            Name = name,
            ToolCallId = new ToolCallId(callId),
            Content = content
        };
}
