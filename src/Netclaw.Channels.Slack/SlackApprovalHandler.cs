// -----------------------------------------------------------------------
// <copyright file="SlackApprovalHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using SlackNet.Blocks;
using SlackNet.Interaction;
using SlackNet.Interaction.Experimental;

namespace Netclaw.Channels.Slack;

#pragma warning disable CS0618
public sealed class SlackApprovalHandler : IAsyncBlockActionHandler
{
    private readonly SlackChannel _channel;
    private readonly ILogger<SlackApprovalHandler> _logger;

    public SlackApprovalHandler(SlackChannel channel, ILogger<SlackApprovalHandler> logger)
    {
        _channel = channel;
        _logger = logger;
    }

    public Task Handle(BlockActionRequest request, Responder respond)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(respond);

        if (request.Action is not ButtonAction action)
        {
            _logger.LogDebug("Ignoring non-button Slack block action");
            return respond();
        }

        if (!SlackApprovalBlockBuilder.IsApprovalActionId(action.ActionId))
            return respond();

        if (!SlackApprovalBlockBuilder.TryParseButtonValue(action.Value, out var callId, out var selectedKey, out var requesterSenderId))
        {
            _logger.LogWarning("Ignoring Slack approval button click with malformed value");
            return respond();
        }

        var channelId = request.Channel?.Id ?? request.Container?.ChannelId;
        var threadTs = request.Container?.ThreadTs
            ?? request.Container?.MessageTs
            ?? request.Message?.ThreadTs
            ?? request.Message?.Ts;
        // Timestamp of the message that holds the clicked button — needed to
        // chat.update the prompt back to its resolved state, including the
        // cold-spawn case where the binding has lost its in-memory pending
        // approval entry. Prefer Container.MessageTs because Slack populates
        // that envelope field for every block action; fall back to Message.Ts
        // for completeness.
        var promptMessageTs = request.Container?.MessageTs ?? request.Message?.Ts;
        var senderId = request.User?.Id;

        if (string.IsNullOrWhiteSpace(channelId)
            || string.IsNullOrWhiteSpace(threadTs)
            || string.IsNullOrWhiteSpace(senderId)
            || callId is null
            || selectedKey is null)
        {
            _logger.LogWarning(
                "Ignoring Slack approval button click with missing routing data channel={ChannelId} thread={ThreadTs} sender={SenderId}",
                channelId,
                threadTs,
                senderId);
            return respond();
        }

        _channel.HandleApprovalResponse(
            new SlackChannelId(channelId),
            new SlackThreadTs(threadTs),
            callId,
            selectedKey,
            senderId,
            requesterSenderId,
            !string.IsNullOrWhiteSpace(promptMessageTs) ? new SlackEventTs(promptMessageTs) : null);

        return respond();
    }
}
#pragma warning restore CS0618
