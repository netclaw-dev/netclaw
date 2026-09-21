// -----------------------------------------------------------------------
// <copyright file="TeamsGraphDirectoryClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Teams.Graph;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class TeamsGraphDirectoryClientTests
{
    [Fact]
    public async Task Team_search_keeps_a_short_query_cache_and_seeds_the_long_lived_team_record()
    {
        using var handler = new TeamsDirectoryHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var graphClient = new GraphServiceClient(
            httpClient,
            new AnonymousAuthenticationProvider(),
            "https://graph.test/v1.0");
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        using var directory = new TeamsGraphDirectoryClient(graphClient, "tenant-a", cache, TimeProvider.System);

        var first = await directory.SearchTeamsAsync("Op", 25, TestContext.Current.CancellationToken);
        var second = await directory.SearchTeamsAsync("Op", 25, TestContext.Current.CancellationToken);
        var cachedRecord = await directory.GetTeamAsync("team-1", TestContext.Current.CancellationToken);
        var differentBound = await directory.SearchTeamsAsync("Op", 26, TestContext.Current.CancellationToken);

        Assert.True(first.IsAvailable);
        Assert.True(second.IsAvailable);
        Assert.True(cachedRecord.IsAvailable);
        Assert.True(differentBound.IsAvailable);
        Assert.Equal(2, handler.TeamListRequests);
        Assert.Equal(0, handler.TeamRecordRequests);
    }

    [Fact]
    public async Task Channel_list_omits_the_unsupported_top_query_option()
    {
        using var handler = new TeamsDirectoryHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var graphClient = new GraphServiceClient(
            httpClient,
            new AnonymousAuthenticationProvider(),
            "https://graph.test/v1.0");
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        using var directory = new TeamsGraphDirectoryClient(graphClient, "tenant-a", cache, TimeProvider.System);

        var result = await directory.GetChannelsAsync("team-1", 25, TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Single(result.Value!);
        Assert.NotNull(handler.ChannelListRequestUri);
        Assert.DoesNotContain("top=", handler.ChannelListRequestUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "teams_directory_authentication_failed")]
    [InlineData(HttpStatusCode.Forbidden, "teams_directory_permission_denied")]
    public async Task Directory_request_classifies_authentication_and_permission_failures(HttpStatusCode statusCode, string reasonCode)
    {
        using var httpClient = new HttpClient(new StatusCodeHandler(statusCode));
        using var graphClient = new GraphServiceClient(
            httpClient,
            new AnonymousAuthenticationProvider(),
            "https://graph.test/v1.0");
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        using var directory = new TeamsGraphDirectoryClient(graphClient, "tenant-a", cache, TimeProvider.System);

        var result = await directory.GetTeamAsync("team-1", TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.Equal(reasonCode, result.ReasonCode);
    }

    [Fact]
    public async Task Group_chat_discovery_uses_a_selected_user_page_and_hides_raw_next_links()
    {
        using var handler = new TeamsDirectoryHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var graphClient = new GraphServiceClient(
            httpClient,
            new AnonymousAuthenticationProvider(),
            "https://graph.test/v1.0");
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        using var directory = new TeamsGraphDirectoryClient(graphClient, "tenant-a", cache, TimeProvider.System);

        var first = await directory.GetGroupChatsAsync("user-1", 25, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first.IsAvailable);
        var chat = Assert.Single(first.Value!.Chats);
        Assert.Equal("19:group-one@thread.v2", chat.Id);
        Assert.Equal("Operations", chat.Topic);
        Assert.Equal(["Maya King"], chat.ParticipantPreview);
        Assert.NotNull(first.Value.Continuation);
        Assert.DoesNotContain("https://", first.Value.Continuation, StringComparison.Ordinal);
        Assert.NotNull(handler.GroupChatListRequestUri);
        Assert.Contains("top=25", handler.GroupChatListRequestUri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("select=", handler.GroupChatListRequestUri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("search=", handler.GroupChatListRequestUri.Query, StringComparison.OrdinalIgnoreCase);

        var second = await directory.GetGroupChatsAsync(
            "user-1",
            25,
            first.Value.Continuation,
            TestContext.Current.CancellationToken);

        Assert.True(second.IsAvailable);
        Assert.Equal("19:group-two@thread.v2", Assert.Single(second.Value!.Chats).Id);
        Assert.Null(second.Value.Continuation);
    }

    [Fact]
    public async Task Group_chat_continuation_cannot_cross_selected_users()
    {
        using var handler = new TeamsDirectoryHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var graphClient = new GraphServiceClient(
            httpClient,
            new AnonymousAuthenticationProvider(),
            "https://graph.test/v1.0");
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        using var directory = new TeamsGraphDirectoryClient(graphClient, "tenant-a", cache, TimeProvider.System);

        var first = await directory.GetGroupChatsAsync("user-1", 25, cancellationToken: TestContext.Current.CancellationToken);
        var invalid = await directory.GetGroupChatsAsync(
            "user-2",
            25,
            first.Value!.Continuation,
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDirectoryOperationStatus.InvalidRequest, invalid.Status);
        Assert.Equal("teams_directory_invalid_continuation", invalid.ReasonCode);
    }

    [Fact]
    public async Task Saved_group_chat_lookup_loads_member_display_names_for_the_management_label()
    {
        using var handler = new TeamsDirectoryHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var graphClient = new GraphServiceClient(
            httpClient,
            new AnonymousAuthenticationProvider(),
            "https://graph.test/v1.0");
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        using var directory = new TeamsGraphDirectoryClient(graphClient, "tenant-a", cache, TimeProvider.System);

        var result = await directory.GetGroupChatAsync("19:saved-chat@thread.v2", TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Equal(["Maya King", "Ari Stone"], result.Value!.ParticipantPreview);
        Assert.NotNull(handler.GroupChatRecordRequestUri);
        Assert.Contains("expand=members", handler.GroupChatRecordRequestUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StatusCodeHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class TeamsDirectoryHttpHandler : HttpMessageHandler
    {
        public int TeamListRequests { get; private set; }

        public int TeamRecordRequests { get; private set; }

        public Uri? ChannelListRequestUri { get; private set; }

        public Uri? GroupChatListRequestUri { get; private set; }

        public Uri? GroupChatRecordRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/teams", StringComparison.Ordinal))
            {
                TeamListRequests++;
                return Task.FromResult(Json("""{ "value": [{ "id": "team-1", "displayName": "Operations" }] }"""));
            }

            if (path.EndsWith("/teams/team-1", StringComparison.Ordinal))
            {
                TeamRecordRequests++;
                return Task.FromResult(Json("""{ "id": "team-1", "displayName": "Operations" }"""));
            }

            if (path.EndsWith("/teams/team-1/channels", StringComparison.Ordinal))
            {
                ChannelListRequestUri = request.RequestUri;
                return Task.FromResult(Json("""{ "value": [{ "id": "channel-1", "displayName": "General" }] }"""));
            }

            if (path.EndsWith("/users/user-1/chats", StringComparison.Ordinal))
            {
                GroupChatListRequestUri = request.RequestUri;
                if (request.RequestUri?.Query.Contains("skiptoken=next", StringComparison.Ordinal) == true)
                {
                    return Task.FromResult(Json(
                        """{ "value": [{ "id": "19:group-two@thread.v2", "chatType": "group", "topic": "Later" }] }"""));
                }

                return Task.FromResult(Json(
                    """{ "value": [{ "id": "19:group-one@thread.v2", "chatType": "group", "topic": "Operations", "members": [{ "displayName": "Maya King" }] }, { "id": "19:one-to-one@thread.v2", "chatType": "oneOnOne" }], "@odata.nextLink": "https://graph.test/v1.0/users/user-1/chats?$skiptoken=next" }"""));
            }

            if (path.Contains("/chats/", StringComparison.Ordinal))
            {
                GroupChatRecordRequestUri = request.RequestUri;
                return Task.FromResult(Json(
                    """{ "id": "19:saved-chat@thread.v2", "chatType": "group", "members": [{ "displayName": "Maya King" }, { "displayName": "Ari Stone" }] }"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string content)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                }
            };
    }
}
