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
        string channelId,
        string rootPostId,
        MattermostCallbackActionStore? actionStore = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(":lock: **Tool approval required**");
        AppendToolSummary(sb, request);
        sb.AppendLine();
        sb.Append("You can also reply with `A`, `B`, `C`, or `D` in this thread.");

        var requesterSenderId = request.RequesterSenderId?.Value ?? string.Empty;
        var actions = request.Options
            .Select(option =>
            {
                var optionKey = option.Key.Value;
                var actionToken = actionStore?.CreateAction(
                    channelId,
                    request.CallId.Value,
                    optionKey,
                    rootPostId,
                    string.IsNullOrEmpty(requesterSenderId) ? null : requesterSenderId);
                var context = actionToken is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>
                    {
                        ["action_token"] = actionToken
                    };

                return new MattermostAttachmentAction(
                    Id: $"tool_approval_{optionKey}",
                    Name: option.Label,
                    IntegrationUrl: callbackUrl,
                    Context: context,
                    Style: GetButtonStyle(optionKey));
            })
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
        var label = ApprovalOptionKeys.LabelFor(selectedKey);
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
        var decisionLabel = ApprovalOptionKeys.LabelFor(selectedKey);

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

    private static string GetButtonStyle(string optionKey)
        => optionKey switch
        {
            ApprovalOptionKeys.Deny => "danger",
            ApprovalOptionKeys.ApproveOnce => "primary",
            _ => "default"
        };
}
