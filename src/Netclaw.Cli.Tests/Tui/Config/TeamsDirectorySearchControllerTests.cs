// -----------------------------------------------------------------------
// <copyright file="TeamsDirectorySearchControllerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Channels.Teams;
using Netclaw.Cli.Tui.Config;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class TeamsDirectorySearchControllerTests
{
    [Fact]
    public async Task Group_chat_search_uses_the_name_and_continuation_without_a_user_search()
    {
        var time = new FakeTimeProvider();
        var chat = new TeamsDirectoryGroupChat("19:boston@thread.v2", "BostonTech Operations", ["Ada", "Grace"]);
        var directory = new GroupChatNameSearchDirectory
        {
            SearchHandler = (_, _) => ValueTask.FromResult(
                TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.Available(new([chat], null, 10, 0, 11, 20)))
        };
        using var controller = new TeamsDirectorySearchController(directory, time);
        var search = controller.SearchGroupChatsAsync("BostonTech Operations", "opaque-cursor", TestContext.Current.CancellationToken).AsTask();

        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Empty(directory.SearchCalls);
        time.Advance(TimeSpan.FromMilliseconds(700));
        var response = await search;
        Assert.Single(directory.SearchCalls);

        Assert.True(response.IsCurrent);
        Assert.Equal(("BostonTech Operations", "opaque-cursor"), Assert.Single(directory.SearchCalls));
        Assert.Equal(0, directory.UserSearchCalls);
        Assert.Equal(chat, Assert.Single(response.Result.Value!.Chats));
    }

    [Fact]
    public async Task Changed_chat_name_rejects_an_old_page_when_the_directory_ignores_cancellation()
    {
        var time = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var directory = new GroupChatNameSearchDirectory
        {
            SearchHandler = (query, _) =>
            {
                if (query == "old")
                {
                    started.TrySetResult();
                    return new ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>>(release.Task);
                }

                return ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.Available(new([], null, 1, 0, 2, 0)));
            }
        };
        using var controller = new TeamsDirectorySearchController(directory, time);
        var oldSearch = controller.SearchGroupChatsAsync("old", "old-page", TestContext.Current.CancellationToken).AsTask();
        time.Advance(TimeSpan.FromSeconds(1));
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        controller.Invalidate();
        var currentSearch = controller.SearchGroupChatsAsync("new", null, TestContext.Current.CancellationToken).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(300));
        release.SetResult(TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.Available(
            new([new("19:old@thread.v2", "Old chat", ["Ada"])], "stale-page", 5, 0, 10, 20)));

        var oldResponse = await oldSearch;
        var currentResponse = await currentSearch;
        Assert.False(oldResponse.IsCurrent);
        Assert.False(controller.IsCurrent(oldResponse.Generation));
        Assert.True(currentResponse.IsCurrent);
        Assert.Empty(currentResponse.Result.Value!.Chats);
    }

    [Fact]
    public async Task Search_waits_for_the_debounce_before_it_calls_the_directory()
    {
        var time = new FakeTimeProvider();
        var directory = new RecordingDirectory();
        using var controller = new TeamsDirectorySearchController(directory, time);

        var search = controller.SearchTeamsAsync("Operations", TestContext.Current.CancellationToken).AsTask();

        Assert.Equal(0, directory.TeamSearchCount);
        time.Advance(TimeSpan.FromMilliseconds(300));
        var response = await search;

        Assert.True(response.IsCurrent);
        Assert.True(response.Result.IsAvailable);
        Assert.Equal(1, directory.TeamSearchCount);
        Assert.Equal("team-1", Assert.Single(response.Result.Value!).Id);
    }

    [Fact]
    public async Task A_superseded_search_cannot_publish_stale_results()
    {
        var time = new FakeTimeProvider();
        var directory = new RecordingDirectory();
        using var controller = new TeamsDirectorySearchController(directory, time);

        var first = controller.SearchTeamsAsync("Old query", TestContext.Current.CancellationToken).AsTask();
        var second = controller.SearchTeamsAsync("Current query", TestContext.Current.CancellationToken).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(300));

        var firstResponse = await first;
        var secondResponse = await second;

        Assert.False(firstResponse.IsCurrent);
        Assert.True(secondResponse.IsCurrent);
        Assert.Equal(1, directory.TeamSearchCount);
    }

    [Fact]
    public async Task An_input_invalidation_rejects_a_completion_from_a_directory_that_ignores_cancellation()
    {
        var time = new FakeTimeProvider();
        var directory = new CancellationResistantDirectory();
        using var controller = new TeamsDirectorySearchController(directory, time);

        var search = controller.SearchTeamsAsync("old", TestContext.Current.CancellationToken).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(300));
        await directory.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        controller.Invalidate();
        directory.Complete();

        var response = await search;

        Assert.False(response.IsCurrent);
        Assert.False(controller.IsCurrent(response.Generation));
    }

    private sealed class RecordingDirectory : ITeamsDirectory
    {
        public int TeamSearchCount { get; private set; }

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>> SearchTeamsAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default)
        {
            TeamSearchCount++;
            return ValueTask.FromResult(
                TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>.Available(
                    [new TeamsDirectoryTeam("team-1", "Operations", null)]));
        }

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryTeam>> GetTeamAsync(
            string teamId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryTeam>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>> GetChannelsAsync(
            string teamId,
            int maximumResults,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryChannel>> GetChannelAsync(
            string teamId,
            string channelId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryChannel>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>> SearchUsersAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>> SearchGroupsAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroup>> GetGroupAsync(
            string groupId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryGroup>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryUser>> GetUserAsync(
            string userId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryUser>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlySet<string>>> CheckUserGroupMembershipAsync(
            string userId,
            IReadOnlyCollection<string> groupIds,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                TeamsDirectoryOperationResult<IReadOnlySet<string>>.Available(new HashSet<string>(StringComparer.Ordinal)));
    }

    private sealed class CancellationResistantDirectory : ITeamsDirectory
    {
        private readonly TaskCompletionSource<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>> _result = new();

        public TaskCompletionSource Started { get; } = new();

        public void Complete() => _result.SetResult(
            TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>.Available(
                [new TeamsDirectoryTeam("team-1", "Operations", null)]));

        public async ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>> SearchTeamsAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            return await _result.Task;
        }

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryTeam>> GetTeamAsync(string teamId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryTeam>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>> GetChannelsAsync(string teamId, int maximumResults, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryChannel>> GetChannelAsync(string teamId, string channelId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryChannel>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>> SearchUsersAsync(string query, int maximumResults, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>> SearchGroupsAsync(string query, int maximumResults, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroup>> GetGroupAsync(string groupId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryGroup>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryUser>> GetUserAsync(string userId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryUser>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlySet<string>>> CheckUserGroupMembershipAsync(string userId, IReadOnlyCollection<string> groupIds, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlySet<string>>.Unavailable("not_used"));
    }
}
