// -----------------------------------------------------------------------
// <copyright file="ToolInteractionResponseParser.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Shared parser for text-based tool interaction responses.
/// Channels that do not have a richer UI can use this to map free-form text
/// like "a" or "approve once" into an interaction option key.
/// </summary>
public static class ToolInteractionResponseParser
{
    public static bool LooksLikeApprovalResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().ToLowerInvariant();

        if (trimmed.Length == 1)
        {
            var ch = trimmed[0];
            if (ch is >= 'a' and <= 'e')
                return true;
        }

        if (int.TryParse(trimmed, out var numericIndex)
            && numericIndex >= 1
            && numericIndex <= 5)
        {
            return true;
        }

        return TryParseNamedSelection(trimmed, out _);
    }

    public static bool TryParseApprovalResponse(
        string text,
        IReadOnlyList<ToolInteractionOption> options,
        out string? selectedKey)
    {
        selectedKey = null;

        if (string.IsNullOrWhiteSpace(text) || options.Count == 0)
            return false;

        var trimmed = text.Trim().ToLowerInvariant();

        if (TryParseIndexedSelection(trimmed, options, out selectedKey))
            return true;

        if (!TryParseNamedSelection(trimmed, out var parsedKey))
            return false;

        if (!options.Any(option => string.Equals(option.Key.Value, parsedKey, StringComparison.Ordinal)))
            return false;

        selectedKey = parsedKey;
        return true;
    }

    private static bool TryParseIndexedSelection(
        string trimmed,
        IReadOnlyList<ToolInteractionOption> options,
        out string? selectedKey)
    {
        selectedKey = null;

        if (trimmed.Length == 1)
        {
            var ch = trimmed[0];
            if (ch is >= 'a' and <= 'z')
            {
                var index = ch - 'a';
                if (index < options.Count)
                {
                    selectedKey = options[index].Key.Value;
                    return true;
                }
            }
        }

        if (int.TryParse(trimmed, out var numericIndex)
            && numericIndex >= 1
            && numericIndex <= options.Count)
        {
            selectedKey = options[numericIndex - 1].Key.Value;
            return true;
        }

        return false;
    }

    private static bool TryParseNamedSelection(string trimmed, out string? selectedKey)
    {
        selectedKey = trimmed switch
        {
            "a" or "1" or "approve" or "approve once" or "approve_once" or "once" or "yes" => ApprovalOptionKeys.ApproveOnce,
            "b" or "2" or "approve session" or "approve_session" or "session" or "approve for this chat" or "this chat" or "approve for this thread" or "this thread" => ApprovalOptionKeys.ApproveSession,
            "approve always" or "approve_always" or "always" or "always here" => ApprovalOptionKeys.ApproveAlways,
            "approve everywhere" or "approve_everywhere" or "everywhere" or "always anywhere" => ApprovalOptionKeys.ApproveEverywhere,
            "deny" or "no" or "reject" => ApprovalOptionKeys.Deny,
            _ => null
        };

        return selectedKey is not null;
    }
}
