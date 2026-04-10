using Netclaw.Actors.Protocol;
using SlackNet.Blocks;

namespace Netclaw.Channels.Slack;

internal static class SlackApprovalBlockBuilder
{
    public const string ApprovalActionId = "tool_approval";

    public static string BuildApprovalText(ToolInteractionRequest request)
    {
        var lines = new List<string>
        {
            $":lock: *Tool approval required*",
            $"> `{request.ToolName}`: `{request.DisplayText}`"
        };

        if (request.Patterns.Count > 0)
        {
            if (request.Patterns.Count == 1)
            {
                lines.Add($"Pattern: `{request.Patterns[0]}`");
            }
            else
            {
                lines.Add("Patterns:");
                foreach (var pattern in request.Patterns)
                    lines.Add($"  • `{pattern}`");
            }
        }

        lines.Add("");
        lines.Add("Reply with:");
        lines.Add("  *A)* Approve once");
        lines.Add("  *B)* Approve for this chat");
        lines.Add("  *C)* Approve always");
        lines.Add("  *D)* Deny");

        return string.Join("\n", lines);
    }

    public static IReadOnlyList<Block> BuildApprovalBlocks(ToolInteractionRequest request)
    {
        var blocks = new List<Block>
        {
            new SectionBlock
            {
                Text = new Markdown(":lock: *Tool approval required*")
            },
            new SectionBlock
            {
                Text = new Markdown($"*Tool:* `{EscapeMarkdown(request.ToolName)}`\n*Request:* `{EscapeMarkdown(request.DisplayText)}`"),
                Expand = true
            }
        };

        if (request.Patterns.Count > 0)
        {
            var patternLines = request.Patterns.Select(pattern => $"• `{EscapeMarkdown(pattern)}`");
            blocks.Add(new SectionBlock
            {
                Text = new Markdown($"*Patterns*\n{string.Join("\n", patternLines)}")
            });
        }

        blocks.Add(new ActionsBlock
        {
            Elements = request.Options
                .Select(option => (IActionElement)new Button
                {
                    ActionId = BuildActionId(option.Key),
                    Text = new PlainText(option.Label),
                    Value = BuildButtonValue(request, option),
                    Style = GetButtonStyle(option.Key),
                    AccessibilityLabel = option.Label
                })
                .ToList()
        });

        blocks.Add(new SectionBlock
        {
            Text = new Markdown("You can also reply with `A`, `B`, `C`, or `D` in this thread.")
        });

        return blocks;
    }

    public static string BuildResolvedApprovalText(
        ToolInteractionRequest request,
        string selectedKey,
        string senderId)
    {
        var statusPrefix = selectedKey == SlackApprovalHandler.DenyKey
            ? ":no_entry:"
            : ":white_check_mark:";
        var decisionLabel = GetDecisionLabel(selectedKey);

        return string.Join("\n", new[]
        {
            $"{statusPrefix} *Tool approval resolved* by <@{EscapeMarkdown(senderId)}>",
            $"> `{request.ToolName}`: `{request.DisplayText}`",
            $"Decision: *{decisionLabel}*"
        });
    }

    public static IReadOnlyList<Block> BuildResolvedApprovalBlocks(
        ToolInteractionRequest request,
        string selectedKey,
        string senderId)
    {
        var statusPrefix = selectedKey == SlackApprovalHandler.DenyKey
            ? ":no_entry:"
            : ":white_check_mark:";
        var decisionLabel = GetDecisionLabel(selectedKey);

        var blocks = new List<Block>
        {
            new SectionBlock
            {
                Text = new Markdown($"{statusPrefix} *Tool approval resolved* by <@{EscapeMarkdown(senderId)}>")
            },
            new SectionBlock
            {
                Text = new Markdown(
                    $"*Tool:* `{EscapeMarkdown(request.ToolName)}`\n"
                    + $"*Request:* `{EscapeMarkdown(request.DisplayText)}`\n"
                    + $"*Decision:* *{EscapeMarkdown(decisionLabel)}*"),
                Expand = true
            }
        };

        if (request.Patterns.Count > 0)
        {
            var patternLines = request.Patterns.Select(pattern => $"• `{EscapeMarkdown(pattern)}`");
            blocks.Add(new SectionBlock
            {
                Text = new Markdown($"*Patterns*\n{string.Join("\n", patternLines)}")
            });
        }

        return blocks;
    }

    internal static string BuildButtonValue(ToolInteractionRequest request, ToolInteractionOption option)
        => string.Join("|",
        [
            request.CallId,
            option.Key,
            request.RequesterSenderId ?? string.Empty
        ]);

    internal static bool TryParseButtonValue(string? value, out string? callId, out string? selectedKey, out string? requesterSenderId)
    {
        callId = null;
        selectedKey = null;
        requesterSenderId = null;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('|');
        if (parts.Length < 2)
            return false;

        callId = string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0];
        selectedKey = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1];
        requesterSenderId = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])
            ? parts[2]
            : null;
        return callId is not null && selectedKey is not null;
    }

    private static ButtonStyle GetButtonStyle(string optionKey)
        => optionKey switch
        {
            SlackApprovalHandler.DenyKey => ButtonStyle.Danger,
            SlackApprovalHandler.ApproveOnceKey => ButtonStyle.Primary,
            _ => ButtonStyle.Default
        };

    internal static bool IsApprovalActionId(string? actionId)
        => !string.IsNullOrWhiteSpace(actionId)
           && actionId.StartsWith($"{ApprovalActionId}_", StringComparison.Ordinal);

    private static string BuildActionId(string optionKey)
        => $"{ApprovalActionId}_{optionKey}";

    private static string GetDecisionLabel(string optionKey)
        => optionKey switch
        {
            SlackApprovalHandler.ApproveOnceKey => "Approve once",
            SlackApprovalHandler.ApproveSessionKey => "Approve for this chat",
            SlackApprovalHandler.ApproveAlwaysKey => "Approve always",
            SlackApprovalHandler.DenyKey => "Deny",
            _ => "Deny"
        };

    private static string EscapeMarkdown(string value)
        => value.Replace("`", "'", StringComparison.Ordinal);
}
