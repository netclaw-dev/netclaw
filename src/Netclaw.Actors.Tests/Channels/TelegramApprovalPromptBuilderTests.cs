// -----------------------------------------------------------------------
// <copyright file="TelegramApprovalPromptBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telegram;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TelegramApprovalPromptBuilderTests
{
    [Fact]
    public void Prompt_contains_tool_action_and_instruction()
    {
        var prompt = TelegramApprovalPromptBuilder.BuildPrompt(Request());

        Assert.Contains("Tool approval required", prompt, StringComparison.Ordinal);
        Assert.Contains("shell_execute", prompt, StringComparison.Ordinal);
        Assert.Contains("git push origin main", prompt, StringComparison.Ordinal);
        Assert.Contains("Choose an action below", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ApprovalOptionKeys.ApproveOnce, "Once", "✅")]
    [InlineData(ApprovalOptionKeys.Deny, "Deny", "⛔")]
    public void Resolved_prompt_contains_decision(string key, string label, string marker)
    {
        var prompt = TelegramApprovalPromptBuilder.BuildResolvedPrompt(Request(), key, 123);

        Assert.Contains(label, prompt, StringComparison.Ordinal);
        Assert.Contains(marker, prompt, StringComparison.Ordinal);
        Assert.Contains("123", prompt, StringComparison.Ordinal);
    }

    private static ToolInteractionRequest Request() => new()
    {
        SessionId = new SessionId("123/chat"),
        CallId = new ToolCallId("call-1"),
        Kind = "approval",
        ToolName = new ToolName("shell_execute"),
        DisplayText = "git push origin main",
        Options =
        [
            new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
        ]
    };
}
