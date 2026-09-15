// -----------------------------------------------------------------------
// <copyright file="TeamsIdentifiers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;

namespace Netclaw.Channels.Teams;

public enum TeamsConversationScope
{
    Personal = 0,
    Channel = 1,
    GroupChat = 2
}

public enum TeamsIdentifierValidationError
{
    None,
    MissingTenantId,
    MissingConversationId,
    MissingActivityId,
    UnsupportedScope,
    InvalidSessionId,
    OversizedIdentifier
}

public sealed record TeamsSessionIdentifier(
    string TenantId,
    TeamsConversationScope Scope,
    string ConversationId,
    string ThreadKey,
    string? RootActivityId);

/// <summary>
/// Builds and parses the Teams session format without exposing raw Teams values
/// in the slash-delimited generic channel grammar. The 1 KiB UTF-8 component
/// limit is local resource protection: ChannelGatewayActor currently has no
/// actor-name or persistence-key limit, so identifiers must be bounded before
/// they can become actor keys or persisted session IDs.
/// </summary>
public static class TeamsSessionIdentifierCodec
{
    public const int MaxRawIdentifierBytes = 1024;

    public const int MaxEncodedIdentifierLength = 1366;

    private const string Prefix = "teams";
    private const string PersonalThreadKey = "conversation";

    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool TryCreatePersonal(
        string tenantId,
        string conversationId,
        out SessionId sessionId,
        out TeamsIdentifierValidationError error)
        => TryCreate(tenantId, TeamsConversationScope.Personal, conversationId, null, out sessionId, out error);

    public static bool TryCreateChannel(
        string tenantId,
        string conversationId,
        string? rootActivityId,
        out SessionId sessionId,
        out TeamsIdentifierValidationError error)
        => TryCreate(tenantId, TeamsConversationScope.Channel, conversationId, rootActivityId, out sessionId, out error);

    public static bool TryCreateGroupChat(
        string tenantId,
        string conversationId,
        out SessionId sessionId,
        out TeamsIdentifierValidationError error)
        => TryCreate(tenantId, TeamsConversationScope.GroupChat, conversationId, null, out sessionId, out error);

    /// <summary>
    /// Checks the canonical Microsoft Teams GroupChat conversation form. This
    /// protects configuration and actor identities from display names.
    /// </summary>
    public static bool IsCanonicalGroupChatConversationId(string? conversationId)
        => conversationId is { } value
           && TryValidateRaw(value, TeamsIdentifierValidationError.MissingConversationId, out _)
           && value.StartsWith("19:", StringComparison.Ordinal)
           && value.EndsWith("@thread.v2", StringComparison.Ordinal);

    /// <summary>
    /// Validates an opaque activity ID before it is used for durable
    /// idempotency. This applies the same resource limit as session IDs.
    /// </summary>
    public static bool IsValidActivityIdentifier(string? activityId) =>
        TryValidateRaw(activityId, TeamsIdentifierValidationError.MissingActivityId, out _);

    public static bool TryParse(
        SessionId sessionId,
        out TeamsSessionIdentifier identifier,
        out TeamsIdentifierValidationError error)
    {
        identifier = null!;
        error = TeamsIdentifierValidationError.InvalidSessionId;

        if (!SessionIdFormat.TrySplit(sessionId, out var channelPart, out var threadKey)
            || threadKey.Contains('/', StringComparison.Ordinal))
            return false;

        var segments = channelPart.Split('~');
        if (segments.Length != 4 || !string.Equals(segments[0], Prefix, StringComparison.Ordinal))
            return false;

        if (!TryDecodeCanonical(segments[1], out var tenantId, out error)
            || !TryParseScope(segments[2], out var scope)
            || !TryDecodeCanonical(segments[3], out var conversationId, out error))
        {
            if (error == TeamsIdentifierValidationError.None)
                error = TeamsIdentifierValidationError.UnsupportedScope;
            return false;
        }

        if (scope == TeamsConversationScope.GroupChat
            && !IsCanonicalGroupChatConversationId(conversationId))
        {
            error = TeamsIdentifierValidationError.InvalidSessionId;
            return false;
        }

        if (scope is TeamsConversationScope.Personal or TeamsConversationScope.GroupChat)
        {
            if (!string.Equals(threadKey, PersonalThreadKey, StringComparison.Ordinal))
                return false;

            identifier = new TeamsSessionIdentifier(tenantId, scope, conversationId, threadKey, null);
            error = TeamsIdentifierValidationError.None;
            return true;
        }

        if (!TryDecodeCanonical(threadKey, out var rootActivityId, out error))
            return false;

        identifier = new TeamsSessionIdentifier(tenantId, scope, conversationId, threadKey, rootActivityId);
        error = TeamsIdentifierValidationError.None;
        return true;
    }

    private static bool TryCreate(
        string tenantId,
        TeamsConversationScope scope,
        string conversationId,
        string? rootActivityId,
        out SessionId sessionId,
        out TeamsIdentifierValidationError error)
    {
        sessionId = default;

        if (!TryValidateRaw(tenantId, TeamsIdentifierValidationError.MissingTenantId, out error)
            || !TryValidateRaw(conversationId, TeamsIdentifierValidationError.MissingConversationId, out error))
            return false;

        if (scope == TeamsConversationScope.GroupChat
            && !IsCanonicalGroupChatConversationId(conversationId))
        {
            error = TeamsIdentifierValidationError.InvalidSessionId;
            return false;
        }

        var threadKey = PersonalThreadKey;
        if (scope == TeamsConversationScope.Channel)
        {
            if (!TryValidateRaw(rootActivityId, TeamsIdentifierValidationError.MissingActivityId, out error))
                return false;

            threadKey = Encode(rootActivityId!);
        }
        else if (scope is not (TeamsConversationScope.Personal or TeamsConversationScope.GroupChat))
        {
            error = TeamsIdentifierValidationError.UnsupportedScope;
            return false;
        }

        sessionId = SessionIdFormat.Build(
            $"{Prefix}~{Encode(tenantId)}~{scope.ToString().ToLowerInvariant()}~{Encode(conversationId)}",
            threadKey);
        error = TeamsIdentifierValidationError.None;
        return true;
    }

    private static bool TryDecodeCanonical(
        string encoded,
        out string value,
        out TeamsIdentifierValidationError error)
    {
        value = string.Empty;
        error = TeamsIdentifierValidationError.InvalidSessionId;
        if (encoded.Length > MaxEncodedIdentifierLength)
        {
            error = TeamsIdentifierValidationError.OversizedIdentifier;
            return false;
        }

        if (string.IsNullOrWhiteSpace(encoded) || encoded.Contains("=", StringComparison.Ordinal) || encoded.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return false;

        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            value = Utf8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (!TryValidateRaw(value, TeamsIdentifierValidationError.InvalidSessionId, out error)
            || !string.Equals(Encode(value), encoded, StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool TryParseScope(string value, out TeamsConversationScope scope)
    {
        if (string.Equals(value, "personal", StringComparison.Ordinal))
        {
            scope = TeamsConversationScope.Personal;
            return true;
        }

        if (string.Equals(value, "channel", StringComparison.Ordinal))
        {
            scope = TeamsConversationScope.Channel;
            return true;
        }

        if (string.Equals(value, "groupchat", StringComparison.Ordinal))
        {
            scope = TeamsConversationScope.GroupChat;
            return true;
        }

        scope = default;
        return false;
    }

    private static bool TryValidateRaw(
        string? value,
        TeamsIdentifierValidationError missingValueError,
        out TeamsIdentifierValidationError error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = missingValueError;
            return false;
        }

        if (Utf8.GetByteCount(value) > MaxRawIdentifierBytes)
        {
            error = TeamsIdentifierValidationError.OversizedIdentifier;
            return false;
        }

        error = TeamsIdentifierValidationError.None;
        return true;
    }

    private static string Encode(string value)
        => Convert.ToBase64String(Utf8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
