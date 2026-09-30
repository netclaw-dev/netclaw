// -----------------------------------------------------------------------
// <copyright file="TelegramApprovalPromptBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Telegram;

internal static class TelegramApprovalPromptBuilder
{
    internal const int MaxDisplayTextChars = 3000;

    public static string BuildPrompt(ToolInteractionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("🔒 Tool approval required");
        sb.Append("Tool: ").AppendLine(request.ToolName.Value);
        sb.Append(request.ToolName.IsMcp ? "Invocation: " : "Action: ")
            .AppendLine(ApprovalDisplayTextFormatter.Truncate(request.DisplayText, MaxDisplayTextChars));
        sb.Append("Choose an action below.");
        return sb.ToString();
    }

    public static string BuildResolvedPrompt(
        ToolInteractionRequest request,
        string selectedKey,
        string senderId)
    {
        var label = ApprovalOptionKeys.LabelFor(selectedKey);
        var marker = selectedKey == ApprovalOptionKeys.Deny ? "⛔" : "✅";
        return $"{marker} Tool approval resolved\n"
               + $"Tool: {request.ToolName.Value}\n"
               + $"Decision: {label}\n"
               + $"By: {senderId}";
    }

    /// <summary>
    /// Builds the resolved-state text when the binding holds no original
    /// request, for example after a cold spawn recovered the prompt from the
    /// journal. Pre-field journal entries fall back to an unknown tool.
    /// </summary>
    public static string BuildResolvedPromptWithoutRequest(
        string selectedKey,
        string senderId,
        string? toolName)
    {
        var label = ApprovalOptionKeys.LabelFor(selectedKey);
        var marker = selectedKey == ApprovalOptionKeys.Deny ? "⛔" : "✅";
        var tool = string.IsNullOrEmpty(toolName) ? "unknown" : toolName;
        return $"{marker} Tool approval resolved\n"
               + $"Tool: {tool}\n"
               + $"Decision: {label}\n"
               + $"By: {senderId}";
    }
}
