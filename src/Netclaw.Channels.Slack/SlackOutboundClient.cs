// -----------------------------------------------------------------------
// <copyright file="SlackOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using SlackNet;
using SlackNet.WebApi;

namespace Netclaw.Channels.Slack;

public sealed class SlackOutboundClient(ISlackApiClient slackApiClient) : ISlackOutboundClient
{
    public async Task<SlackChannelId> OpenDmChannelAsync(SlackUserId userId, CancellationToken ct = default)
    {
        try
        {
            var channelId = await slackApiClient.Conversations.Open(new[] { userId.Value }, ct);
            return new SlackChannelId(channelId);
        }
        catch (SlackException ex)
        {
            throw new SlackMessageDeliveryException(
                ex.ErrorCode,
                SlackReplyClient.MapFailureKind(ex.ErrorCode),
                ex.Message,
                ex);
        }
    }

    public async Task<SlackNewThread> PostNewThreadAsync(SlackChannelId channelId, string text, CancellationToken ct = default)
    {
        try
        {
            var blocks = SlackBlockConverter.Convert(text);

            var response = await slackApiClient.Chat.PostMessage(new Message
            {
                Channel = channelId.Value,
                Text = SlackTextProtector.ProtectUrls(text),
                Blocks = blocks
            }, ct);

            if (response is null || string.IsNullOrEmpty(response.Ts))
            {
                throw new SlackMessageDeliveryException(
                    "phantom_success",
                    DeliveryFailureKind.TransportFailure,
                    "Slack returned no message timestamp — the message was not delivered");
            }

            var threadTs = new SlackThreadTs(response.Ts);
            return new SlackNewThread(channelId, threadTs);
        }
        catch (SlackException ex)
        {
            throw new SlackMessageDeliveryException(
                ex.ErrorCode,
                SlackReplyClient.MapFailureKind(ex.ErrorCode),
                ex.Message,
                ex);
        }
    }
}
