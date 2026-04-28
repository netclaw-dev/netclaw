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

        AppendAdoptedContextSummary(sb, request);

        sb.AppendLine();
        sb.AppendLine("Reply with:");
        sb.Append("A) ").AppendLine(ApprovalOptionKeys.ApproveOnceLabel);
        sb.Append("B) ").AppendLine(ApprovalOptionKeys.ApproveSessionLabel);
        sb.Append("C) ").AppendLine(ApprovalOptionKeys.ApproveAlwaysLabel);
        sb.Append("D) ").AppendLine(ApprovalOptionKeys.DenyLabel);
        return sb.ToString().TrimEnd();
    }

    public static (string Text, IReadOnlyList<DiscordButtonSpec> Buttons) BuildButtonPrompt(
        ToolInteractionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine(":lock: **Tool approval required**");
        AppendToolSummary(sb, request);

        sb.AppendLine();
        sb.Append("You can also reply with `A`, `B`, `C`, or `D` in this thread.");

        var buttons = request.Options
            .Select(option => new DiscordButtonSpec(
                CustomId: BuildButtonValue(request, option),
                Label: option.Label,
                Style: GetButtonStyle(option.Key)))
            .ToList();

        return (sb.ToString().TrimEnd(), buttons);
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
        sb.Append(" (by <@").Append(senderId).Append(">)");
        return sb.ToString();
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
                    sb.Append("  • `").Append(pattern).AppendLine("`");
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

    internal static string BuildButtonValue(ToolInteractionRequest request, ToolInteractionOption option)
        => ApprovalButtonValueCodec.Encode(request, option);

    internal static bool TryParseButtonValue(string? value, out string? callId, out string? selectedKey, out string? requesterSenderId)
        => ApprovalButtonValueCodec.TryDecode(value, out callId, out selectedKey, out requesterSenderId);

    private static DiscordButtonStyle GetButtonStyle(string optionKey)
        => optionKey switch
        {
            ApprovalOptionKeys.Deny => DiscordButtonStyle.Danger,
            ApprovalOptionKeys.ApproveOnce => DiscordButtonStyle.Success,
            _ => DiscordButtonStyle.Secondary
        };
}
