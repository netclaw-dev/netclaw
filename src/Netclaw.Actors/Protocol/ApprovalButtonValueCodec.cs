// -----------------------------------------------------------------------
// <copyright file="ApprovalButtonValueCodec.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Encodes and decodes the pipe-delimited value embedded in approval button
/// custom IDs / values. Used by both Slack and Discord channel adapters.
/// Format: <c>callId|optionKey|requesterSenderId</c>
/// </summary>
public static class ApprovalButtonValueCodec
{
    public const int MaxEncodedLength = 100;

    public static string Encode(ToolInteractionRequest request, ToolInteractionOption option)
        => Encode(request.CallId.Value, option.Key, request.RequesterSenderId);

    public static string Encode(string callId, string optionKey, string? requesterSenderId)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(optionKey);
        if (callId.Contains('|', StringComparison.Ordinal))
            throw new ArgumentException("callId must not contain the pipe delimiter '|'.", nameof(callId));
        if (optionKey.Contains('|', StringComparison.Ordinal))
            throw new ArgumentException("optionKey must not contain the pipe delimiter '|'.", nameof(optionKey));

        var suffix = $"|{optionKey}|{requesterSenderId ?? string.Empty}";
        var maxCallIdLength = MaxEncodedLength - suffix.Length;
        var truncatedCallId = callId.Length > maxCallIdLength
            ? callId[..maxCallIdLength]
            : callId;
        return $"{truncatedCallId}{suffix}";
    }

    /// <summary>
    /// Whether <paramref name="approvingSenderId"/> is allowed to approve a request
    /// with the given requester identity. VerifiedAutomation requests can be approved by anyone.
    /// </summary>
    public static bool CanApprove(
        PrincipalClassification? requesterPrincipal,
        string? requesterSenderId,
        string approvingSenderId)
    {
        if (requesterPrincipal is PrincipalClassification.VerifiedAutomation)
            return true;
        if (string.IsNullOrWhiteSpace(requesterSenderId))
            return true;
        return string.Equals(requesterSenderId, approvingSenderId, StringComparison.Ordinal);
    }

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
