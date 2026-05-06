// -----------------------------------------------------------------------
// <copyright file="MattermostApprovalPromptBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Mattermost;

internal static class MattermostApprovalPromptBuilder
{
    public static (string Text, IReadOnlyList<MattermostAttachment> Attachments) BuildButtonPrompt(
        ToolInteractionRequest request,
        string callbackUrl,
        string rootPostId)
    {
        var sb = new StringBuilder();
        sb.AppendLine(":lock: **Tool approval required**");
        AppendToolSummary(sb, request);
        sb.AppendLine();
        sb.Append("You can also reply with `A`, `B`, `C`, or `D` in this thread.");

        var actions = request.Options
            .Select(option => new MattermostAttachmentAction(
                Id: $"tool_approval_{option.Key}",
                Name: option.Label,
                IntegrationUrl: callbackUrl,
                Context: new Dictionary<string, string>
                {
                    ["call_id"] = request.CallId,
                    ["selected_key"] = option.Key,
                    ["requester_sender_id"] = request.RequesterSenderId ?? string.Empty,
                    ["root_post_id"] = rootPostId
                },
                Style: GetButtonStyle(option.Key)))
            .ToList();

        var attachment = new MattermostAttachment(
            Fallback: "Tool approval required — reply with A, B, C, or D",
            Color: "#3AA3E3",
            Actions: actions);

        return (sb.ToString().TrimEnd(), [attachment]);
    }

    public static string BuildTextPrompt(ToolInteractionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine(":lock: **Tool approval required**");
        AppendToolSummary(sb, request);

        sb.AppendLine();
        sb.AppendLine("Reply with:");
        sb.Append("**A)** ").AppendLine(ApprovalOptionKeys.ApproveOnceLabel);
        sb.Append("**B)** ").AppendLine(ApprovalOptionKeys.ApproveSessionLabel);
        sb.Append("**C)** ").AppendLine(ApprovalOptionKeys.ApproveAlwaysLabel);
        sb.Append("**D)** ").AppendLine(ApprovalOptionKeys.DenyLabel);
        return sb.ToString().TrimEnd();
    }

    public static string BuildDecisionStatus(string selectedKey)
    {
        var label = GetDecisionLabel(selectedKey);
        return $"Recorded approval decision: {label}.";
    }

    public static string BuildResolvedPromptText(
        ToolInteractionRequest request,
        string selectedKey,
        string senderId)
    {
        var statusEmoji = selectedKey == ApprovalOptionKeys.Deny
            ? ":no_entry:"
            : ":white_check_mark:";
        var decisionLabel = GetDecisionLabel(selectedKey);

        var sb = new StringBuilder();
        sb.Append(statusEmoji).AppendLine(" **Tool approval resolved**");
        AppendToolSummary(sb, request);

        sb.Append("**Decision:** ").Append(decisionLabel);
        sb.Append(" (by @").Append(senderId).Append(')');
        return sb.ToString();
    }

    public static MattermostAttachment BuildResolvedAttachment(
        ToolInteractionRequest request,
        string selectedKey,
        string senderId)
    {
        var resolvedText = BuildResolvedPromptText(request, selectedKey, senderId);
        var color = selectedKey == ApprovalOptionKeys.Deny ? "#CC0000" : "#2EA44F";

        return new MattermostAttachment(
            Fallback: resolvedText,
            Color: color,
            Text: resolvedText);
    }

    private static void AppendToolSummary(StringBuilder sb, ToolInteractionRequest request)
    {
        sb.Append("**Tool:** `").Append(request.ToolName).AppendLine("`");
        sb.Append("**Action:** `").Append(request.DisplayText).AppendLine("`");

        if (request.Patterns.Count > 0)
        {
            if (request.Patterns.Count == 1)
            {
                sb.Append("**Pattern:** `").Append(request.Patterns[0]).AppendLine("`");
            }
            else
            {
                sb.AppendLine("**Patterns:**");
                foreach (var pattern in request.Patterns)
                    sb.Append("  - `").Append(pattern).AppendLine("`");
            }
        }

        AppendAdoptedContextSummary(sb, request);
    }

    private static void AppendAdoptedContextSummary(StringBuilder sb, ToolInteractionRequest request)
    {
        if (!request.HasAdoptedContext)
            return;

        sb.Append("**Adopted context:** present").AppendLine();
        sb.Append("**Speakers:** `").Append(string.Join(", ", request.AdoptedSpeakerIds)).AppendLine("`");
    }

    private static string GetDecisionLabel(string selectedKey)
        => selectedKey switch
        {
            ApprovalOptionKeys.ApproveOnce => ApprovalOptionKeys.ApproveOnceLabel,
            ApprovalOptionKeys.ApproveSession => ApprovalOptionKeys.ApproveSessionLabel,
            ApprovalOptionKeys.ApproveAlways => ApprovalOptionKeys.ApproveAlwaysLabel,
            ApprovalOptionKeys.Deny => ApprovalOptionKeys.DenyLabel,
            _ => selectedKey
        };

    private static string GetButtonStyle(string optionKey)
        => optionKey switch
        {
            ApprovalOptionKeys.Deny => "danger",
            ApprovalOptionKeys.ApproveOnce => "primary",
            _ => "default"
        };
}
