using Netclaw.Actors.Protocol;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Discord;

public sealed record DiscordThreadInbound(
    SessionId SessionId,
    DiscordChannelId ChannelId,
    DiscordReplyChannelId ReplyChannelId,
    DiscordThreadOrMessageId ThreadOrMessageId,
    DiscordMessageId? RootMessageId,
    DiscordEventId EventId,
    DiscordUserId SenderId,
    TrustAudience Audience,
    PrincipalClassification Principal,
    SourceProvenance Provenance,
    string Text,
    DateTimeOffset ReceivedAt);

public sealed record DiscordApprovalResponse(
    DiscordChannelId ChannelId,
    DiscordThreadOrMessageId ThreadOrMessageId,
    string CallId,
    string SelectedKey,
    DiscordUserId SenderId,
    DiscordUserId? RequesterSenderId = null);

internal sealed record PendingApprovalRequest(
    string CallId,
    DiscordUserId? RequesterSenderId);

/// <summary>
/// Sent by a session binding actor to its parent conversation actor when the
/// transport creates a thread from a root message. The conversation actor
/// registers an alias so that subsequent messages/interactions arriving on the
/// new thread channel ID route to the original session binding.
/// </summary>
internal sealed record ThreadPromoted(
    DiscordThreadOrMessageId OriginalThreadOrMessageId,
    DiscordReplyChannelId ThreadChannelId);
