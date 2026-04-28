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

        AppendAdoptedContextSummary(lines, request);

        lines.Add("");
        lines.Add("Reply with:");
        lines.Add($"  *A)* {ApprovalOptionKeys.ApproveOnceLabel}");
        lines.Add($"  *B)* {ApprovalOptionKeys.ApproveSessionLabel}");
        lines.Add($"  *C)* {ApprovalOptionKeys.ApproveAlwaysLabel}");
        lines.Add($"  *D)* {ApprovalOptionKeys.DenyLabel}");

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

        if (request.HasAdoptedContext)
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(BuildAdoptedContextMarkdown(request))
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
        var statusPrefix = selectedKey == ApprovalOptionKeys.Deny
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
        var statusPrefix = selectedKey == ApprovalOptionKeys.Deny
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

        if (request.HasAdoptedContext)
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(BuildAdoptedContextMarkdown(request))
            });
        }

        return blocks;
    }

    private static void AppendAdoptedContextSummary(List<string> lines, ToolInteractionRequest request)
    {
        if (!request.HasAdoptedContext)
            return;

        lines.Add($"Adopted context: present ({string.Join(", ", request.AdoptedSpeakerIds)})");
    }

    private static string BuildAdoptedContextMarkdown(ToolInteractionRequest request)
        => $"*Adopted context:* present\n*Speakers:* `{EscapeMarkdown(string.Join(", ", request.AdoptedSpeakerIds))}`";

    internal static string BuildButtonValue(ToolInteractionRequest request, ToolInteractionOption option)
        => ApprovalButtonValueCodec.Encode(request, option);

    internal static bool TryParseButtonValue(string? value, out string? callId, out string? selectedKey, out string? requesterSenderId)
        => ApprovalButtonValueCodec.TryDecode(value, out callId, out selectedKey, out requesterSenderId);

    private static ButtonStyle GetButtonStyle(string optionKey)
        => optionKey switch
        {
            ApprovalOptionKeys.Deny => ButtonStyle.Danger,
            ApprovalOptionKeys.ApproveOnce => ButtonStyle.Primary,
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
            ApprovalOptionKeys.ApproveOnce => ApprovalOptionKeys.ApproveOnceLabel,
            ApprovalOptionKeys.ApproveSession => ApprovalOptionKeys.ApproveSessionLabel,
            ApprovalOptionKeys.ApproveAlways => ApprovalOptionKeys.ApproveAlwaysLabel,
            ApprovalOptionKeys.Deny => ApprovalOptionKeys.DenyLabel,
            _ => ApprovalOptionKeys.DenyLabel
        };

    private static string EscapeMarkdown(string value)
        => value.Replace("`", "'", StringComparison.Ordinal);
}
