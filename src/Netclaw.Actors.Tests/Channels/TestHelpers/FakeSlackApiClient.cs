// -----------------------------------------------------------------------
// <copyright file="FakeSlackApiClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using SlackNet;
using SlackNet.WebApi;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

/// <summary>
/// Minimal fake <see cref="ISlackApiClient"/> that exposes only the sub-APIs a
/// test supplies (Chat and/or Auth); every other member throws so unexpected
/// API usage fails loud instead of silently succeeding.
/// </summary>
internal sealed class FakeSlackApiClient(IChatApi? chat = null, IAuthApi? auth = null) : ISlackApiClient
{
    public IChatApi Chat => chat ?? throw new NotImplementedException();
    public IAuthApi Auth => auth ?? throw new NotImplementedException();

    public IApiApi Api => throw new NotImplementedException();
    public IAppsConnectionsApi AppsConnectionsApi => throw new NotImplementedException();
    public IAppsEventAuthorizationsApi AppsEventAuthorizations => throw new NotImplementedException();
    public IAssistantSearchApi AssistantSearch => throw new NotImplementedException();
    public IAssistantThreadsApi AssistantThreads => throw new NotImplementedException();
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
