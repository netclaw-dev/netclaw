// -----------------------------------------------------------------------
// <copyright file="SlackReplyClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Slack;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Events;
using SlackNet.WebApi;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackReplyClientTests
{
    private static SlackException CreateSlackException(string errorCode) =>
        new(new ErrorResponse { Error = errorCode });

    [Fact]
    public async Task PostThreadReplyAsync_null_response_throws_phantom_success()
    {
        var fakeChat = new FakeChatApi(response: null);
        var fakeClient = new FakeSlackApiClient(fakeChat);
        var client = new SlackReplyClient(fakeClient);

        var ex = await Assert.ThrowsAsync<SlackMessageDeliveryException>(() =>
            client.PostThreadReplyAsync(new SlackPostMessage(
                ChannelId: new SlackChannelId("C123"),
                ThreadTs: new SlackThreadTs("1234.5678"),
                Text: "hello"), TestContext.Current.CancellationToken));

        Assert.Equal("phantom_success", ex.ErrorCode);
        Assert.Equal(DeliveryFailureKind.TransportFailure, ex.FailureKind);
    }

    [Fact]
    public async Task PostThreadReplyAsync_empty_ts_throws_phantom_success()
    {
        var fakeChat = new FakeChatApi(response: new PostMessageResponse { Ts = "" });
        var fakeClient = new FakeSlackApiClient(fakeChat);
        var client = new SlackReplyClient(fakeClient);

        var ex = await Assert.ThrowsAsync<SlackMessageDeliveryException>(() =>
            client.PostThreadReplyAsync(new SlackPostMessage(
                ChannelId: new SlackChannelId("C123"),
                ThreadTs: new SlackThreadTs("1234.5678"),
                Text: "hello"), TestContext.Current.CancellationToken));

        Assert.Equal("phantom_success", ex.ErrorCode);
        Assert.Equal(DeliveryFailureKind.TransportFailure, ex.FailureKind);
    }

    [Fact]
    public async Task PostThreadReplyAsync_slack_exception_wraps_to_delivery_exception()
    {
        var fakeChat = new FakeChatApi(throwOnPost: CreateSlackException("msg_too_long"));
        var fakeClient = new FakeSlackApiClient(fakeChat);
        var client = new SlackReplyClient(fakeClient);

        var ex = await Assert.ThrowsAsync<SlackMessageDeliveryException>(() =>
            client.PostThreadReplyAsync(new SlackPostMessage(
                ChannelId: new SlackChannelId("C123"),
                ThreadTs: new SlackThreadTs("1234.5678"),
                Text: new string('x', 50_000)), TestContext.Current.CancellationToken));

        Assert.Equal("msg_too_long", ex.ErrorCode);
        Assert.Equal(DeliveryFailureKind.MessageTooLarge, ex.FailureKind);
    }

    [Fact]
    public async Task PostThreadReplyAsync_rate_limited_maps_to_transport_failure()
    {
        var fakeChat = new FakeChatApi(throwOnPost: CreateSlackException("rate_limited"));
        var fakeClient = new FakeSlackApiClient(fakeChat);
        var client = new SlackReplyClient(fakeClient);

        var ex = await Assert.ThrowsAsync<SlackMessageDeliveryException>(() =>
            client.PostThreadReplyAsync(new SlackPostMessage(
                ChannelId: new SlackChannelId("C123"),
                ThreadTs: new SlackThreadTs("1234.5678"),
                Text: "hello"), TestContext.Current.CancellationToken));

        Assert.Equal("rate_limited", ex.ErrorCode);
        Assert.Equal(DeliveryFailureKind.TransportFailure, ex.FailureKind);
    }

    [Fact]
    public async Task PostThreadReplyAsync_valid_response_succeeds()
    {
        var fakeChat = new FakeChatApi(response: new PostMessageResponse { Ts = "1234.5678", Channel = "C123" });
        var fakeClient = new FakeSlackApiClient(fakeChat);
        var client = new SlackReplyClient(fakeClient);

        await client.PostThreadReplyAsync(new SlackPostMessage(
            ChannelId: new SlackChannelId("C123"),
            ThreadTs: new SlackThreadTs("1234.5678"),
            Text: "hello"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PostThreadReplyAsync_uses_explicit_blocks_when_provided()
    {
        var fakeChat = new FakeChatApi(response: new PostMessageResponse { Ts = "1234.5678", Channel = "C123" });
        var fakeClient = new FakeSlackApiClient(fakeChat);
        var client = new SlackReplyClient(fakeClient);
        var blocks = new List<Block>
        {
            new SectionBlock { Text = new PlainText("custom approval") }
        };

        await client.PostThreadReplyAsync(new SlackPostMessage(
            ChannelId: new SlackChannelId("C123"),
            ThreadTs: new SlackThreadTs("1234.5678"),
            Text: "fallback text",
            Blocks: blocks), TestContext.Current.CancellationToken);

        Assert.NotNull(fakeChat.LastPostedMessage);
        Assert.Single(fakeChat.LastPostedMessage!.Blocks);
        var section = Assert.IsType<SectionBlock>(fakeChat.LastPostedMessage.Blocks.Single());
        Assert.Equal("custom approval", section.Text.ToString());
    }

    [Fact]
    public void Approval_block_builder_uses_unique_action_ids_per_button()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("D1/123.456"),
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId("call-1"),
            ToolName = new Netclaw.Tools.ToolName("shell_execute"),
            DisplayText = "git status",
            RequesterSenderId = new SenderId("U1"),
            Patterns = ["git status"],
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var actions = Assert.IsType<ActionsBlock>(blocks.Single(block => block is ActionsBlock));
        var actionIds = actions.Elements
            .Cast<Button>()
            .Select(button => button.ActionId)
            .ToList();

        Assert.Equal(4, actionIds.Count);
        Assert.Equal(4, actionIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(actionIds, actionId => Assert.True(SlackApprovalBlockBuilder.IsApprovalActionId(actionId)));
    }

    [Theory]
    [InlineData("invalid_blocks", DeliveryFailureKind.ContentRejected)]
    [InlineData("invalid_arguments", DeliveryFailureKind.ContentRejected)]
    [InlineData("msg_too_long", DeliveryFailureKind.MessageTooLarge)]
    [InlineData("too_many_attachments", DeliveryFailureKind.UnsupportedContent)]
    [InlineData("not_in_channel", DeliveryFailureKind.PermissionDenied)]
    [InlineData("channel_not_found", DeliveryFailureKind.PermissionDenied)]
    [InlineData("missing_scope", DeliveryFailureKind.PermissionDenied)]
    [InlineData("no_permission", DeliveryFailureKind.PermissionDenied)]
    [InlineData("rate_limited", DeliveryFailureKind.TransportFailure)]
    [InlineData("some_unknown_error", DeliveryFailureKind.Unknown)]
    public void MapFailureKind_classifies_error_codes_correctly(string errorCode, DeliveryFailureKind expected)
    {
        Assert.Equal(expected, SlackReplyClient.MapFailureKind(errorCode));
    }

    /// <summary>
    /// Minimal fake that only implements PostMessage for testing SlackReplyClient.
    /// </summary>
    private sealed class FakeChatApi(PostMessageResponse? response = null, SlackException? throwOnPost = null) : IChatApi
    {
        public Message? LastPostedMessage { get; private set; }

        public Task<PostMessageResponse> PostMessage(Message message, CancellationToken cancellationToken = default)
        {
            if (throwOnPost is not null)
                throw throwOnPost;
            LastPostedMessage = message;
            return Task.FromResult(response!);
        }

        public Task<MessageTsResponse> Delete(string ts, string channelId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<MessageTsResponse> MeMessage(string channel, string text, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ScheduleMessageResponse> ScheduleMessage(Message message, DateTime postAt, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteScheduledMessage(string messageId, string channelId, bool? asUser = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<PostEphemeralResponse> PostEphemeral(string userId, Message message, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task Unfurl(string channelId, string ts, IDictionary<string, Attachment>? unfurls = null, bool userAuthRequired = false, IEnumerable<Block>? userAuthBlocks = null, string? userAuthMessage = null, string? userAuthUrl = null, UnfurlMetadata? metadata = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task Unfurl(LinkSource source, string unfurlId, IDictionary<string, Attachment>? unfurls = null, bool userAuthRequired = false, IEnumerable<Block>? userAuthBlocks = null, string? userAuthMessage = null, string? userAuthUrl = null, UnfurlMetadata? metadata = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<MessageUpdateResponse> Update(MessageUpdate messageUpdate, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<PermalinkResponse> GetPermalink(string channelId, string messageTs, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ScheduledMessageListResponse> ScheduledMessagesList(string? channel = null, DateTime? latestDateTime = null, DateTime? oldestDateTime = null, string? cursor = null, int limit = 100, string? teamId = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<MessageTsResponse> StartStream(string channel, string threadTs, string? markdownText = null, string? recipientUserId = null, string? recipientTeamId = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<MessageTsResponse> AppendStream(string channel, string ts, string markdownText, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<PostMessageResponse> StopStream(string channel, string ts, string? markdownText = null, IEnumerable<Block>? blocks = null, object? metadataObject = null, MessageMetadata? metadataJson = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<PostMessageResponse> UpdateStream(string channel, string ts, IEnumerable<Block>? newBlocks = null, string? markdownText = null, object? metadataObject = null, MessageMetadata? metadataJson = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
