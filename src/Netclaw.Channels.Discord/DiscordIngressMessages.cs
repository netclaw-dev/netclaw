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
    DiscordUserId? RequesterSenderId,
    DateTimeOffset RequestedAt);
