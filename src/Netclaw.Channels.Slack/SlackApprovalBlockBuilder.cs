using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Builds the approval prompt text for Slack messages.
/// Uses text-based option list (works on all channels, no Block Kit interactivity required).
/// Block Kit buttons can be added later via <c>ReplaceBlockActionHandling</c>.
/// </summary>
internal static class SlackApprovalBlockBuilder
{
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
        lines.Add("  *B)* Approve always");
        lines.Add("  *C)* Deny");

        return string.Join("\n", lines);
    }
}
