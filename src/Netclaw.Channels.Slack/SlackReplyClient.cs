// -----------------------------------------------------------------------
// <copyright file="SlackReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using SlackNet;
using SlackNet.Blocks;
using SlackNet.WebApi;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Slack;

public sealed class SlackReplyClient(ISlackApiClient slackApiClient) : ISlackReplyClient
{
    public Task PostThreadReplyAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
        => PostThreadReplyWithTsAsync(message, cancellationToken);

    public async Task<string> PostThreadReplyWithTsAsync(SlackPostMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var blocks = message.Blocks?.Count > 0
                ? [.. message.Blocks]
                : SlackBlockConverter.Convert(message.Text);

            var response = await slackApiClient.Chat.PostMessage(new Message
            {
                Channel = message.ChannelId.Value,
                ThreadTs = message.ThreadTs.Value,
                Text = SlackTextProtector.ProtectUrls(message.Text),
                Blocks = blocks
            }, cancellationToken);

            if (response is null || string.IsNullOrEmpty(response.Ts))
            {
                throw new SlackMessageDeliveryException(
                    "phantom_success",
                    DeliveryFailureKind.TransportFailure,
                    "Slack returned no message timestamp — the message was not delivered");
            }

            return response.Ts;
        }
        catch (SlackException ex)
        {
            throw new SlackMessageDeliveryException(
                ex.ErrorCode,
                MapFailureKind(ex.ErrorCode),
                ex.Message,
                ex);
        }
    }

    public async Task UpdateThreadMessageAsync(
        SlackChannelId channelId,
        SlackEventTs messageTs,
        string text,
        IReadOnlyList<Block>? blocks = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await slackApiClient.Chat.Update(new MessageUpdate
            {
                ChannelId = channelId.Value,
                Ts = messageTs.Value,
                Text = SlackTextProtector.ProtectUrls(text),
                Blocks = blocks?.ToList()
            }, cancellationToken);
        }
        catch (SlackException ex)
        {
            throw new SlackMessageDeliveryException(
                ex.ErrorCode,
                MapFailureKind(ex.ErrorCode),
                ex.Message,
                ex);
        }
    }

    internal static DeliveryFailureKind MapFailureKind(string? errorCode)
        => errorCode switch
        {
            "invalid_blocks" or "invalid_arguments" => DeliveryFailureKind.ContentRejected,
            "msg_too_long" => DeliveryFailureKind.MessageTooLarge,
            "too_many_attachments" => DeliveryFailureKind.UnsupportedContent,
            "not_in_channel" or "channel_not_found" or "missing_scope" or "no_permission" => DeliveryFailureKind.PermissionDenied,
            "rate_limited" => DeliveryFailureKind.TransportFailure,
            _ => DeliveryFailureKind.Unknown
        };

    public async Task UploadFileToThreadAsync(
        SlackChannelId channelId,
        SlackThreadTs threadTs,
        string filePath,
        string? filename = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolvedFilename = filename ?? Path.GetFileName(filePath);
            await using var stream = System.IO.File.OpenRead(filePath);
            var upload = new FileUpload(resolvedFilename, stream);

            var response = await slackApiClient.Files.Upload(
                upload,
                channelId.Value,
                threadTs.Value,
                initialComment: null,
                cancellationToken);

            if (response is null)
            {
                throw new SlackMessageDeliveryException(
                    "phantom_success",
                    DeliveryFailureKind.TransportFailure,
                    "Slack returned no file reference — the upload was not delivered");
            }
        }
        catch (SlackException ex)
        {
            throw new SlackMessageDeliveryException(
                ex.ErrorCode,
                MapFailureKind(ex.ErrorCode),
                ex.Message,
                ex);
        }
    }
}
