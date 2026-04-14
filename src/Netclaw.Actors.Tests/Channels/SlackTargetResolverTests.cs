using Netclaw.Channels.Slack;
using SlackNet;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackTargetResolverTests
{
    [Fact]
    public async Task Resolve_channel_name_with_hash_returns_channel_id()
    {
        var lookup = new FakeSlackTargetLookupClient
        {
            ChannelPages =
            [
                new SlackChannelPage(
                [
                    new Conversation { Id = "C1", Name = "openclaw", NameNormalized = "openclaw" }
                ],
                null)
            ]
        };

        var resolver = new SlackTargetResolver(lookup);
        var result = await resolver.ResolveAsync("#openclaw", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("C1", result.ChannelId);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task Resolve_user_mention_returns_user_id()
    {
        var lookup = new FakeSlackTargetLookupClient
        {
            UserPages =
            [
                new SlackUserPage(
                [
                    new User
                    {
                        Id = "U42",
                        Name = "aaron",
                        RealName = "Aaron Stannard",
                        Profile = new UserProfile
                        {
                            DisplayName = "aaron",
                            Email = "aaron@petabridge.com"
                        }
                    }
                ],
                null)
            ]
        };

        var resolver = new SlackTargetResolver(lookup);
        var result = await resolver.ResolveAsync("@aaron", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("U42", result.UserId);
        Assert.Null(result.ChannelId);
    }

    [Fact]
    public async Task Resolve_ambiguous_user_query_fails()
    {
        var lookup = new FakeSlackTargetLookupClient
        {
            UserPages =
            [
                new SlackUserPage(
                [
                    new User { Id = "U1", Name = "aaron" },
                    new User { Id = "U2", Name = "aaron" }
                ],
                null)
            ]
        };

        var resolver = new SlackTargetResolver(lookup);
        var result = await resolver.ResolveAsync("aaron", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Could not resolve", result.ErrorMessage);
        Assert.Null(result.ChannelId);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task Resolve_channel_name_without_hash_falls_back_to_channel_lookup()
    {
        var lookup = new FakeSlackTargetLookupClient
        {
            ChannelPages =
            [
                new SlackChannelPage(
                [
                    new Conversation { Id = "C777", Name = "openclaw" }
                ],
                null)
            ]
        };

        var resolver = new SlackTargetResolver(lookup);
        var result = await resolver.ResolveAsync("openclaw", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("C777", result.ChannelId);
    }

    [Fact]
    public async Task Resolve_raw_channel_id_skips_directory_lookup()
    {
        var lookup = new FakeSlackTargetLookupClient();
        var resolver = new SlackTargetResolver(lookup);

        var result = await resolver.ResolveAsync("C0123ABC", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("C0123ABC", result.ChannelId);
        Assert.Equal(0, lookup.ChannelListCallCount);
        Assert.Equal(0, lookup.UserListCallCount);
    }

    [Fact]
    public async Task Resolve_raw_user_id_skips_directory_lookup()
    {
        var lookup = new FakeSlackTargetLookupClient();
        var resolver = new SlackTargetResolver(lookup);

        var result = await resolver.ResolveAsync("U0456XYZ", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("U0456XYZ", result.UserId);
        Assert.Equal(0, lookup.ChannelListCallCount);
        Assert.Equal(0, lookup.UserListCallCount);
    }

    private sealed class FakeSlackTargetLookupClient : ISlackTargetLookupClient
    {
        public IReadOnlyList<SlackChannelPage> ChannelPages { get; init; } = [];
        public IReadOnlyList<SlackUserPage> UserPages { get; init; } = [];
        public int ChannelListCallCount { get; private set; }
        public int UserListCallCount { get; private set; }

        public Task<SlackChannelPage> ListChannelsAsync(string? cursor, CancellationToken ct = default)
        {
            ChannelListCallCount++;

            if (ChannelPages.Count == 0)
                return Task.FromResult(new SlackChannelPage([], null));

            if (!int.TryParse(cursor, out var index))
                index = 0;

            if (index >= ChannelPages.Count)
                return Task.FromResult(new SlackChannelPage([], null));

            var next = index + 1 < ChannelPages.Count ? (index + 1).ToString() : null;
            var page = ChannelPages[index] with { NextCursor = next };
            return Task.FromResult(page);
        }

        public Task<SlackUserPage> ListUsersAsync(string? cursor, CancellationToken ct = default)
        {
            UserListCallCount++;

            if (UserPages.Count == 0)
                return Task.FromResult(new SlackUserPage([], null));

            if (!int.TryParse(cursor, out var index))
                index = 0;

            if (index >= UserPages.Count)
                return Task.FromResult(new SlackUserPage([], null));

            var next = index + 1 < UserPages.Count ? (index + 1).ToString() : null;
            var page = UserPages[index] with { NextCursor = next };
            return Task.FromResult(page);
        }
    }
}
