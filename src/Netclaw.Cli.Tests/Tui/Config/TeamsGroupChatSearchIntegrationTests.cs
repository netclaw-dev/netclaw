// -----------------------------------------------------------------------
// <copyright file="TeamsGroupChatSearchIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Teams.Graph;
using Netclaw.Cli.Tests.Tui;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class TeamsGroupChatSearchIntegrationTests
{
    private const string TargetTitle = "BostonTech - AI Teams Test";
    private const string TargetId = "19:boston-ai-test@thread.v2";
    private const string AllowedUser = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    [Theory]
    [InlineData("boston")]
    [InlineData(TargetTitle)]
    public async Task One_search_follows_real_provider_cursors_and_saves_a_late_match(string query)
    {
        using var fixture = new SearchFixture(bufferedMatches: false);
        using var vm = fixture.CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Teams);
        vm.BeginGroupChatDiscovery();
        vm.GroupChatSearchInput = query;

        await vm.SearchGroupChatsFromInputAsync().WaitAsync(TestContext.Current.CancellationToken);

        var chat = Assert.Single(vm.GroupChatSearchResults);
        Assert.Equal(TargetId, chat.Id);
        Assert.Equal(TargetTitle, chat.Topic);
        Assert.False(vm.IsGroupChatSearchRunning);
        Assert.False(vm.HasGroupChatContinuation);
        Assert.Equal(71, fixture.Handler.Requests.Count);
        Assert.Equal(fixture.Handler.Requests.Count, fixture.Handler.Requests.Distinct().Count());
        Assert.All(fixture.Handler.Requests, uri =>
        {
            Assert.DoesNotContain("boston", uri.Query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("expand", uri.Query, StringComparison.OrdinalIgnoreCase);
        });

        vm.SelectGroupChatForReview();
        vm.ToggleGroupChats();
        vm.ApplyGroupChats();
        await vm.PendingConfigWrite;

        using var saved = JsonDocument.Parse(File.ReadAllText(fixture.Paths.NetclawConfigPath));
        var teams = saved.RootElement.GetProperty("Teams");
        Assert.Equal(TargetId, Assert.Single(teams.GetProperty("AllowedGroupChatIds").EnumerateArray()).GetString());
        Assert.Equal(AllowedUser, Assert.Single(teams.GetProperty("AllowedUserIds").EnumerateArray()).GetString());
        Assert.True(teams.GetProperty("AllowGroupChats").GetBoolean());
        Assert.DoesNotContain(TargetTitle, File.ReadAllText(fixture.Paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Automatic_search_retains_buffered_matches_without_requiring_more_network_requests()
    {
        using var fixture = new SearchFixture(bufferedMatches: true);
        using var vm = fixture.CreateViewModel();
        vm.OpenAdapterManagement(ChannelType.Teams);
        vm.BeginGroupChatDiscovery();
        vm.GroupChatSearchInput = "boston";

        await vm.SearchGroupChatsFromInputAsync().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(50, vm.GroupChatSearchResults.Count);
        Assert.Equal(50, vm.GroupChatSearchResults.Select(chat => chat.Id).Distinct().Count());
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.False(vm.HasGroupChatContinuation);
        Assert.False(vm.IsGroupChatSearchRunning);
        Assert.DoesNotContain("stalled", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SearchFixture : IDisposable
    {
        private readonly DisposableTempDir _directory = new();
        private readonly HttpClient _httpClient;
        private readonly GraphServiceClient _graph;
        private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 1_024 });
        public SearchHandler Handler { get; }
        public NetclawPaths Paths { get; }

        public SearchFixture(bool bufferedMatches)
        {
            Paths = new NetclawPaths(_directory.Path);
            Paths.EnsureDirectoriesExist();
            File.WriteAllText(Paths.NetclawConfigPath,
                $$"""
                {
                  "configVersion": 1,
                  "Teams": {
                    "Enabled": true,
                    "TenantId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "ClientId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                    "BotId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
                    "AllowedUserIds": ["{{AllowedUser}}"]
                  }
                }
                """);
            File.WriteAllText(Paths.SecretsPath,
                """{"configVersion":1,"Teams":{"ClientSecret":"synthetic-test-secret"}}""");
            Handler = new SearchHandler(bufferedMatches);
            _httpClient = new HttpClient(Handler);
            _graph = new GraphServiceClient(_httpClient, new AnonymousAuthenticationProvider(), "https://graph.test/v1.0");
        }

        public ChannelsConfigViewModel CreateViewModel()
            => new(Paths, new FakeSlackProbe(), new FakeDiscordProbe(), new FakeMattermostProbe(), TimeProvider.System,
                teamsDirectoryFactory: _ => new TeamsGraphDirectoryClient(_graph, "tenant-a", _cache, TimeProvider.System),
                environmentVariableReader: _ => null);

        public void Dispose()
        {
            _graph.Dispose();
            _httpClient.Dispose();
            _cache.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class SearchHandler(bool bufferedMatches) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Add(uri);
            object[] values;
            string? next = null;
            if (uri.AbsolutePath == "/v1.0/users")
            {
                var secondPage = uri.Query.Contains("next-users", StringComparison.Ordinal);
                var count = bufferedMatches ? 1 : 20;
                values = Enumerable.Range(secondPage ? 21 : 1, count)
                    .Select(index => (object)new { id = UserId(index) }).ToArray();
                if (!bufferedMatches && !secondPage)
                    next = "https://graph.test/v1.0/users?$skiptoken=next-users";
            }
            else
            {
                var userId = uri.AbsolutePath.Split('/')[3];
                var user = int.Parse(userId[^12..], System.Globalization.CultureInfo.InvariantCulture);
                if (bufferedMatches)
                {
                    values = Enumerable.Range(0, 50)
                        .Select(index => Chat($"19:boston-{index}@thread.v2", $"{TargetTitle} {index}", "group")).ToArray();
                }
                else if (user == 11)
                {
                    var page = string.IsNullOrEmpty(uri.Query) || !uri.Query.StartsWith("?page=", StringComparison.Ordinal)
                        ? 1 : int.Parse(uri.Query[6..], System.Globalization.CultureInfo.InvariantCulture);
                    values = [Chat($"19:meeting-{page}@thread.v2", "Scheduled meeting", "meeting")];
                    if (page < 30)
                        next = $"https://graph.test/v1.0/users/{userId}/chats?page={page + 1}";
                }
                else
                {
                    values = user is 37 or 38
                        ? [Chat(TargetId, TargetTitle, "group")]
                        : [Chat($"19:other-{user}@thread.v2", "Other project", "group")];
                }
            }

            var payload = new Dictionary<string, object?> { ["value"] = values };
            if (next is not null)
                payload["@odata.nextLink"] = next;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            });
        }

        private static string UserId(int index) => $"00000000-0000-0000-0000-{index:D12}";

        private static object Chat(string id, string topic, string chatType) => new { id, topic, chatType };
    }
}
