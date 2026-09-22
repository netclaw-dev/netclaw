// -----------------------------------------------------------------------
// <copyright file="TelegramAclContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public sealed class TelegramAclContractTests : AclPolicyContractTests
{
    protected override string ExpectedSourceKind => "telegram";

    // Telegram intentionally has no default-channel allow exception: every
    // group chat must appear in AllowedChatIds, and TelegramAclDoctorCheck
    // enforces an explicit user or chat scope. The two DefaultChannelId
    // contract cases are capability-excluded, not skipped as failures.
    protected override bool SupportsDefaultChannelId => false;

    protected override IAclDecision EvaluateDm(string userId, ChannelOptionsBuilder options)
        => EvaluateMessage("dm-channel", userId, isDm: true, options);

    protected override IAclDecision EvaluateChannel(
        string channelId, string userId, ChannelOptionsBuilder options)
        => EvaluateMessage(channelId, userId, isDm: false, options);

    protected override IAclDecision EvaluateMessage(
        string channelId, string userId, bool isDm, ChannelOptionsBuilder options)
    {
        var telegramOptions = new TelegramChannelOptions
        {
            AllowDirectMessages = options.AllowDirectMessages,
            AllowedChatIds = options.AllowedChannelIds
                .Select(TelegramContractIds.LongId)
                .Select(id => id.ToString())
                .ToArray(),
            AllowedUserIds = options.AllowedUserIds
                .Select(TelegramContractIds.LongId)
                .Select(id => id.ToString())
                .ToArray(),
            ChatAudiences = MapAudiences(options.ChannelAudiences)
        };

        var message = new TelegramInboundMessage(
            ChatId: TelegramContractIds.LongId(channelId),
            UserId: string.IsNullOrEmpty(userId)
                ? null
                : TelegramContractIds.LongId(userId),
            MessageId: TelegramContractIds.IntId("evt-1"),
            Text: "test",
            IsDirectMessage: isDm);

        return TelegramAclPolicy.EvaluateInbound(message, telegramOptions);
    }

    // Audience keys are contract channel ids, except the literal "dm" key —
    // AudienceResult.Resolve matches "dm" by name for direct messages, and the
    // channel-id keys must map through the same transform as the message ids
    // so the override lookups line up.
    private static Dictionary<string, string> MapAudiences(
        Dictionary<string, string> channelAudiences) =>
        channelAudiences.ToDictionary(
            pair => pair.Key == "dm" ? "dm" : TelegramContractIds.LongId(pair.Key).ToString(),
            pair => pair.Value,
            StringComparer.Ordinal);
}
