// -----------------------------------------------------------------------
// <copyright file="MattermostNetReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mattermost;

namespace Netclaw.Channels.Mattermost.Transport;

internal sealed class MattermostNetReplyClient : IMattermostReplyClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MattermostClient _client;
    private readonly HttpClient _httpClient;

    public MattermostNetReplyClient(MattermostClient client, HttpClient httpClient)
    {
        _client = client;
        _httpClient = httpClient;
    }

    public async Task<MattermostPostResult> PostReplyAsync(MattermostPostMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Attachments is { Count: > 0 })
            return await PostWithAttachmentsAsync(message, cancellationToken);

        var post = await _client.CreatePostAsync(
            channelId: message.ChannelId.Value,
            message: message.Text,
            replyToPostId: message.RootPostId?.Value ?? string.Empty,
            files: message.FileIds);

        return new MattermostPostResult(
            PostId: new MattermostPostId(post.Id));
    }

    public async Task UpdatePostAsync(
        MattermostPostId postId,
        string text,
        CancellationToken cancellationToken = default)
    {
        await _client.UpdatePostAsync(postId.Value, text);
    }

    public async Task UpdatePostAsync(
        MattermostPostId postId,
        string text,
        IReadOnlyList<MattermostAttachment>? attachments,
        CancellationToken cancellationToken = default)
    {
        if (attachments is null or { Count: 0 })
        {
            await _client.UpdatePostAsync(postId.Value, text);
            return;
        }

        var attachmentPayloads = MapAttachments(attachments);

        var payload = new UpdatePostPayload
        {
            Id = postId.Value,
            Message = text,
            Props = new PropsPayload { Attachments = attachmentPayloads }
        };

        var response = await _httpClient.PutAsJsonAsync(
            $"/api/v4/posts/{postId.Value}",
            payload,
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<MattermostPostResult> PostWithAttachmentsAsync(
        MattermostPostMessage message,
        CancellationToken cancellationToken)
    {
        var attachments = MapAttachments(message.Attachments!);

        var payload = new CreatePostPayload
        {
            ChannelId = message.ChannelId.Value,
            Message = message.Text,
            RootId = message.RootPostId?.Value,
            Props = new PropsPayload
            {
                Attachments = attachments
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            "/api/v4/posts",
            payload,
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var postId = doc.RootElement.GetProperty("id").GetString()!;

        return new MattermostPostResult(PostId: new MattermostPostId(postId));
    }

    private static List<AttachmentPayload> MapAttachments(IReadOnlyList<MattermostAttachment> source)
        => source
            .Select(a => new AttachmentPayload
            {
                Fallback = a.Fallback,
                Color = a.Color,
                Text = a.Text,
                Actions = a.Actions?.Select(act => new ActionPayload
                {
                    Id = act.Id,
                    Name = act.Name,
                    Type = "button",
                    Style = act.Style,
                    Integration = new IntegrationPayload
                    {
                        Url = act.IntegrationUrl,
                        Context = act.Context
                    }
                }).ToList()
            })
            .ToList();

    private sealed class UpdatePostPayload
    {
        public string Id { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public PropsPayload? Props { get; init; }
    }

    private sealed class CreatePostPayload
    {
        public string ChannelId { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string? RootId { get; init; }
        public PropsPayload? Props { get; init; }
    }

    private sealed class PropsPayload
    {
        public List<AttachmentPayload>? Attachments { get; init; }
    }

    private sealed class AttachmentPayload
    {
        public string? Fallback { get; init; }
        public string? Color { get; init; }
        public string? Text { get; init; }
        public List<ActionPayload>? Actions { get; init; }
    }

    private sealed class ActionPayload
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = "button";
        public string? Style { get; init; }
        public IntegrationPayload? Integration { get; init; }
    }

    private sealed class IntegrationPayload
    {
        public string Url { get; init; } = string.Empty;
        public Dictionary<string, string>? Context { get; init; }
    }
}
