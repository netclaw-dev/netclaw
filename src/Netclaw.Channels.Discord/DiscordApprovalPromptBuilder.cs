using System.Text;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Discord;

internal static class DiscordApprovalPromptBuilder
{
    public static string BuildTextPrompt(ToolInteractionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Netclaw approval required:");
        sb.Append("Tool: ").AppendLine(request.ToolName);
        sb.Append("Action: ").AppendLine(request.DisplayText);

        if (request.Patterns.Count > 0)
            sb.Append("Pattern: ").AppendLine(string.Join(", ", request.Patterns));

        sb.AppendLine();
        sb.AppendLine("Reply with:");
        sb.Append("A) ").AppendLine(ApprovalOptionKeys.ApproveOnceLabel);
        sb.Append("B) ").AppendLine(ApprovalOptionKeys.ApproveSessionLabel);
        sb.Append("C) ").AppendLine(ApprovalOptionKeys.ApproveAlwaysLabel);
        sb.Append("D) ").AppendLine(ApprovalOptionKeys.DenyLabel);
        return sb.ToString().TrimEnd();
    }

    public static string BuildDecisionStatus(string selectedKey)
    {
        var label = selectedKey switch
        {
            ApprovalOptionKeys.ApproveOnce => ApprovalOptionKeys.ApproveOnceLabel,
            ApprovalOptionKeys.ApproveSession => ApprovalOptionKeys.ApproveSessionLabel,
            ApprovalOptionKeys.ApproveAlways => ApprovalOptionKeys.ApproveAlwaysLabel,
            ApprovalOptionKeys.Deny => ApprovalOptionKeys.DenyLabel,
            _ => selectedKey
        };

        return $"Recorded approval decision: {label}.";
    }
}
