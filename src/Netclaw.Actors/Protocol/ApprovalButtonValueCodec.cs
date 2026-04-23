namespace Netclaw.Actors.Protocol;

/// <summary>
/// Encodes and decodes the pipe-delimited value embedded in approval button
/// custom IDs / values. Used by both Slack and Discord channel adapters.
/// Format: <c>callId|optionKey|requesterSenderId</c>
/// </summary>
public static class ApprovalButtonValueCodec
{
    public static string Encode(ToolInteractionRequest request, ToolInteractionOption option)
        => string.Join("|",
        [
            request.CallId,
            option.Key,
            request.RequesterSenderId ?? string.Empty
        ]);

    public static bool TryDecode(string? value, out string? callId, out string? selectedKey, out string? requesterSenderId)
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
}
