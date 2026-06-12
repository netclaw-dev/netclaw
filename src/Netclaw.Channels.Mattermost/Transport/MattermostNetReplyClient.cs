// -----------------------------------------------------------------------
// <copyright file="MattermostNetReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost;
using Mattermost.Models.Posts;

namespace Netclaw.Channels.Mattermost.Transport;

internal sealed class MattermostNetReplyClient : IMattermostReplyClient
{
    private readonly MattermostClient _client;

    public MattermostNetReplyClient(MattermostClient client)
    {
        _client = client;
    }

    public async Task<MattermostPostResult> PostReplyAsync(MattermostPostMessage message, CancellationToken cancellationToken = default)
    {
        var props = BuildProps(message.Attachments);

        var post = await _client.CreatePostAsync(
            channelId: message.ChannelId.Value,
            message: message.Text,
            replyToPostId: message.RootPostId?.Value ?? string.Empty,
            files: message.FileIds,
            props: props);

        return new MattermostPostResult(PostId: new MattermostPostId(post.Id));
    }

    public async Task UpdatePostAsync(
        MattermostPostId postId,
        string text,
        IReadOnlyList<MattermostAttachment>? attachments,
        CancellationToken cancellationToken = default)
    {
        await _client.UpdatePostAsync(postId.Value, text, BuildProps(attachments));
    }

    public async Task<string> UploadFileAsync(
        MattermostChannelId channelId,
        string filePath,
        string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedFileName = fileName ?? Path.GetFileName(filePath);
        await using var stream = File.OpenRead(filePath);
        var details = await _client.UploadFileAsync(channelId.Value, resolvedFileName, stream, progressChanged: _ => { });

        if (details is null || string.IsNullOrWhiteSpace(details.Id))
            throw new InvalidOperationException("Mattermost returned no file ID — the upload was not delivered.");

        return details.Id;
    }

    private static PostProps? BuildProps(IReadOnlyList<MattermostAttachment>? attachments)
    {
        if (attachments is null or { Count: 0 })
            return null;

        var props = new PostProps();
        foreach (var attachment in attachments)
            props.Attachments.Add(MapAttachment(attachment));
        return props;
    }

    private static PostPropsAttachment MapAttachment(MattermostAttachment attachment)
    {
        var sdkAttachment = new PostPropsAttachment
        {
            Fallback = attachment.Fallback ?? string.Empty,
            Color = attachment.Color,
            Text = attachment.Text ?? string.Empty
        };

        if (attachment.Actions is { Count: > 0 })
        {
            foreach (var action in attachment.Actions)
                sdkAttachment.Actions.Add(MapAction(action));
        }

        return sdkAttachment;
    }

    private static PostPropsAction MapAction(MattermostAttachmentAction action)
    {
        var sdkAction = new PostPropsButtonAction
        {
            Id = action.Id,
            Name = action.Name,
            Integration = new Integration
            {
                Url = action.IntegrationUrl,
                Context = action.Context.ToDictionary(
                    static kv => kv.Key,
                    static kv => (object)kv.Value)
            }
        };

        if (Enum.TryParse<ActionStyle>(action.Style, ignoreCase: true, out var style))
            sdkAction.Style = style;

        return sdkAction;
    }
}
