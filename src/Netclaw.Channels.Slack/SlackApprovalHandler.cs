namespace Netclaw.Channels.Slack;

/// <summary>
/// Constants and utilities for Slack approval prompt formatting.
/// Approval prompts are posted as text messages with letter-keyed options.
/// User responses are parsed from regular Slack messages by the conversation actor.
/// </summary>
public static class SlackApprovalHandler
{
    public const string ApproveOnceKey = "approve_once";
    public const string ApproveAlwaysKey = "approve_always";
    public const string DenyKey = "deny";

    /// <summary>
    /// Attempts to parse a user message as an approval response.
    /// Matches: "a", "b", "c", "approve once", "approve always", "deny",
    /// and common variations.
    /// </summary>
    public static (bool IsApproval, string? SelectedKey) TryParseApprovalResponse(string text)
    {
        var trimmed = text.Trim().ToLowerInvariant();

        return trimmed switch
        {
            "a" or "1" or "approve" or "approve once" or "yes" => (true, ApproveOnceKey),
            "b" or "2" or "approve always" or "always" => (true, ApproveAlwaysKey),
            "c" or "3" or "deny" or "no" or "reject" => (true, DenyKey),
            _ => (false, null)
        };
    }
}
