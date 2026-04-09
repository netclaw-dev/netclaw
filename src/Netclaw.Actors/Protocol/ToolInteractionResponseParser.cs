namespace Netclaw.Actors.Protocol;

/// <summary>
/// Shared parser for text-based tool interaction responses.
/// Channels that do not have a richer UI can use this to map free-form text
/// like "a" or "approve once" into an interaction option key.
/// </summary>
public static class ToolInteractionResponseParser
{
    public static bool TryParseApprovalResponse(string text, out string? selectedKey)
    {
        selectedKey = null;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().ToLowerInvariant();

        selectedKey = trimmed switch
        {
            "a" or "1" or "approve" or "approve once" or "approve_once" or "once" or "yes" => "approve_once",
            "b" or "2" or "approve session" or "approve_session" or "session" or "approve for this chat" or "this chat" or "approve for this thread" or "this thread" => "approve_session",
            "c" or "3" or "approve always" or "approve_always" or "always" => "approve_always",
            "d" or "4" or "deny" or "no" or "reject" => "deny",
            _ => null
        };

        return selectedKey is not null;
    }
}
