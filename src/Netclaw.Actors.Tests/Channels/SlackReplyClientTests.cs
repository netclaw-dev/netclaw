using Netclaw.Actors.Protocol;
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
                Text: "hello")));

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
                Text: "hello")));

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
                Text: new string('x', 50_000))));

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
                Text: "hello")));

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
            Text: "hello"));
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
        public Task<PostMessageResponse> PostMessage(Message message, CancellationToken cancellationToken = default)
        {
            if (throwOnPost is not null)
                throw throwOnPost;
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

    /// <summary>
    /// Minimal fake ISlackApiClient that exposes only Chat property for testing.
    /// </summary>
    private sealed class FakeSlackApiClient(IChatApi chat) : ISlackApiClient
    {
        public IChatApi Chat => chat;

        public IApiApi Api => throw new NotImplementedException();
        public IAppsConnectionsApi AppsConnectionsApi => throw new NotImplementedException();
        public IAppsEventAuthorizationsApi AppsEventAuthorizations => throw new NotImplementedException();
        public IAssistantSearchApi AssistantSearch => throw new NotImplementedException();
        public IAssistantThreadsApi AssistantThreads => throw new NotImplementedException();
        public IAuthApi Auth => throw new NotImplementedException();
        public IBookmarksApi Bookmarks => throw new NotImplementedException();
        public IBotsApi Bots => throw new NotImplementedException();
        public ICallParticipantsApi CallParticipants => throw new NotImplementedException();
        public ICallsApi Calls => throw new NotImplementedException();
        public ICanvasesApi Canvases => throw new NotImplementedException();
        public IConversationsApi Conversations => throw new NotImplementedException();
        public IDialogApi Dialog => throw new NotImplementedException();
        public IDndApi Dnd => throw new NotImplementedException();
        public IEmojiApi Emoji => throw new NotImplementedException();
        public IEntityApi Entity => throw new NotImplementedException();
        public IExternalTeamsApi ExternalTeams => throw new NotImplementedException();
        public IFileCommentsApi FileComments => throw new NotImplementedException();
        public IFilesApi Files => throw new NotImplementedException();
        public IListApi List => throw new NotImplementedException();
        public IListDownloadApi ListDownload => throw new NotImplementedException();
        public IListItemsApi ListItems => throw new NotImplementedException();
        public IListAccessApi ListAccess => throw new NotImplementedException();
        public IMigrationApi Migration => throw new NotImplementedException();
        public IOAuthApi OAuth => throw new NotImplementedException();
        public IOAuthV2Api OAuthV2 => throw new NotImplementedException();
        public IOpenIdApi OpenIdApi => throw new NotImplementedException();
        public IPinsApi Pins => throw new NotImplementedException();
        public IReactionsApi Reactions => throw new NotImplementedException();
        public IRemindersApi Reminders => throw new NotImplementedException();
        public IRemoteFilesApi RemoteFiles => throw new NotImplementedException();
        public IRtmApi Rtm => throw new NotImplementedException();
        public IScheduledMessagesApi ScheduledMessages => throw new NotImplementedException();
        public ISearchApi Search => throw new NotImplementedException();
        public ITeamApi Team => throw new NotImplementedException();
        public ITeamBillingApi TeamBilling => throw new NotImplementedException();
        public ITeamPreferencesApi TeamPreferences => throw new NotImplementedException();
        public ITeamProfileApi TeamProfile => throw new NotImplementedException();
        public IToolingApi Tooling => throw new NotImplementedException();
        public IUserGroupsApi UserGroups => throw new NotImplementedException();
        public IUserGroupUsersApi UserGroupUsers => throw new NotImplementedException();
        public IUserProfileApi UserProfile => throw new NotImplementedException();
        public IUsersApi Users => throw new NotImplementedException();
        public IViewsApi Views => throw new NotImplementedException();
        public bool DisableRetryOnRateLimit { get; set; }
        public bool WarningsAsErrors { get; set; }
        public ISlackApiClient WithAccessToken(string accessToken) => throw new NotImplementedException();
        public Task Get(string apiMethod, Dictionary<string, object> args, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<T> Get<T>(string apiMethod, Dictionary<string, object> args, CancellationToken cancellationToken) where T : class => throw new NotImplementedException();
        public Task Post(string apiMethod, Dictionary<string, object> args, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<T> Post<T>(string apiMethod, Dictionary<string, object> args, CancellationToken cancellationToken) where T : class => throw new NotImplementedException();
        public Task Post(string apiMethod, Dictionary<string, object> args, HttpContent content, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<T> Post<T>(string apiMethod, Dictionary<string, object> args, HttpContent content, CancellationToken cancellationToken) where T : class => throw new NotImplementedException();
        public Task Respond(string responseUrl, IReadOnlyMessage message, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task PostToWebhook(string webhookUrl, Message message, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
