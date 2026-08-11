// -----------------------------------------------------------------------
// <copyright file="SlackTargetResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using SlackNet;
using Xunit;
using ChannelType = Netclaw.Actors.Channels.ChannelType;

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

        var resolver = CreateResolver(lookup, new SlackChannelOptions { AllowedChannelIds = ["C1"] });
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

        var resolver = CreateResolver(lookup);
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

        var resolver = CreateResolver(lookup);
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

        var resolver = CreateResolver(lookup, new SlackChannelOptions { AllowedChannelIds = ["C777"] });
        var result = await resolver.ResolveAsync("openclaw", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("C777", result.ChannelId);
    }

    [Fact]
    public async Task Resolve_raw_channel_id_skips_directory_lookup()
    {
        var lookup = new FakeSlackTargetLookupClient();
        var resolver = CreateResolver(lookup, new SlackChannelOptions { AllowedChannelIds = ["C0123ABCDEF"] });

        var result = await resolver.ResolveAsync("C0123ABCDEF", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("C0123ABCDEF", result.ChannelId);
        Assert.Equal(0, lookup.ChannelListCallCount);
        Assert.Equal(0, lookup.UserListCallCount);
    }

    [Fact]
    public async Task Resolve_raw_user_id_skips_directory_lookup()
    {
        var lookup = new FakeSlackTargetLookupClient();
        var resolver = CreateResolver(lookup);

        var result = await resolver.ResolveAsync("U0456XYZABC", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("U0456XYZABC", result.UserId);
        Assert.Equal(0, lookup.ChannelListCallCount);
        Assert.Equal(0, lookup.UserListCallCount);
    }

    [Fact]
    public async Task Channel_address_resolver_returns_ambiguous_destination_candidates()
    {
        var lookup = new FakeSlackTargetLookupClient
        {
            ChannelPages =
            [
                new SlackChannelPage(
                [
                    new Conversation { Id = "C1", Name = "general-public" },
                    new Conversation { Id = "C2", Name = "general-private" }
                ],
                null)
            ]
        };
        var resolver = CreateResolver(lookup, new SlackChannelOptions { AllowedChannelIds = ["C1", "C2"] });
        var request = new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Slack),
            ChannelAddressKind.Destination,
            "general");

        var result = await resolver.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(["C1", "C2"], result.Candidates.Select(candidate => candidate.StableId).ToArray());
    }

    [Fact]
    public async Task Channel_address_resolver_filters_disallowed_destination_ids()
    {
        var lookup = new FakeSlackTargetLookupClient();
        var resolver = CreateResolver(lookup, new SlackChannelOptions { AllowedChannelIds = ["C012345AAAA"] });
        var request = new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Slack),
            ChannelAddressKind.Destination,
            "C012345BBBB");

        var result = await resolver.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.NotFound, result.Status);
        Assert.Contains("not in the allowed channels list", result.Error);
        Assert.Equal(0, lookup.ChannelListCallCount);
    }

    [Fact]
    public async Task List_destinations_derives_from_allowlist_without_workspace_listing()
    {
        var lookup = new FakeSlackTargetLookupClient
        {
            ChannelInfos = new Dictionary<string, Conversation>(StringComparer.Ordinal)
            {
                ["C0DEFAULT99"] = new() { Id = "C0DEFAULT99", Name = "ops" },
                ["C1"] = new() { Id = "C1", Name = "openclaw" },
                ["C3"] = new() { Id = "C3", Name = "general" },
                // Visible to the bot but NOT allowlisted: must never be
                // listed nor even queried.
                ["C9"] = new() { Id = "C9", Name = "random" }
            }
        };

        // The default channel id also appears in AllowedChannelIds: the union
        // must deduplicate (3 candidates, 3 info calls — not 4).
        var resolver = CreateResolver(
            lookup,
            new SlackChannelOptions { AllowedChannelIds = ["C0DEFAULT99", "C1", "C3"] },
            defaultChannelId: new SlackChannelId("C0DEFAULT99"));

        var result = await resolver.ListDestinationsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Listed, result.Status);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.StableId == "C0DEFAULT99" && c.DisplayName == "#ops");
        Assert.Contains(result.Candidates, c => c.StableId == "C1" && c.DisplayName == "#openclaw");
        Assert.Contains(result.Candidates, c => c.StableId == "C3" && c.DisplayName == "#general");
        Assert.DoesNotContain(result.Candidates, c => c.StableId == "C9");
        Assert.Equal(0, lookup.ChannelListCallCount);
        Assert.Equal(3, lookup.InfoCallCount);
    }

    [Fact]
    public async Task List_destinations_includes_runtime_resolved_default_channel()
    {
        // Simulates a name-only configuration: options.DefaultChannelId is
        // null and the accessor supplies the channel-resolved ID.
        var lookup = new FakeSlackTargetLookupClient
        {
            ChannelInfos = new Dictionary<string, Conversation>(StringComparer.Ordinal)
            {
                ["C0RUNTIME99"] = new() { Id = "C0RUNTIME99", Name = "resolved-default" }
            }
        };

        var resolver = CreateResolver(
            lookup,
            new SlackChannelOptions(),
            defaultChannelId: new SlackChannelId("C0RUNTIME99"));

        var result = await resolver.ListDestinationsAsync(TestContext.Current.CancellationToken);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("C0RUNTIME99", candidate.StableId);
        Assert.Equal("#resolved-default", candidate.DisplayName);
    }

    [Fact]
    public async Task List_destinations_skips_archived_channels()
    {
        var lookup = new FakeSlackTargetLookupClient
        {
            ChannelInfos = new Dictionary<string, Conversation>(StringComparer.Ordinal)
            {
                ["C1"] = new() { Id = "C1", Name = "retired", IsArchived = true },
                ["C2"] = new() { Id = "C2", Name = "active" }
            }
        };

        var resolver = CreateResolver(lookup, new SlackChannelOptions { AllowedChannelIds = ["C1", "C2"] });
        var result = await resolver.ListDestinationsAsync(TestContext.Current.CancellationToken);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("C2", candidate.StableId);
        Assert.Equal("#active", candidate.DisplayName);
    }

    [Fact]
    public async Task List_destinations_falls_back_to_raw_id_when_info_fails()
    {
        // C1 has no info entry, so the fake throws SlackException for it —
        // the listing keeps the channel and uses the raw ID as display name.
        var lookup = new FakeSlackTargetLookupClient
        {
            ChannelInfos = new Dictionary<string, Conversation>(StringComparer.Ordinal)
            {
                ["C2"] = new() { Id = "C2", Name = "two" }
            }
        };

        var resolver = CreateResolver(lookup, new SlackChannelOptions { AllowedChannelIds = ["C1", "C2"] });
        var result = await resolver.ListDestinationsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.StableId == "C1" && c.DisplayName == "C1");
        Assert.Contains(result.Candidates, c => c.StableId == "C2" && c.DisplayName == "#two");
    }

    [Fact]
    public async Task List_destinations_returns_empty_listing_when_nothing_is_allowed()
    {
        var lookup = new FakeSlackTargetLookupClient
        {
            ChannelInfos = new Dictionary<string, Conversation>(StringComparer.Ordinal)
            {
                ["C9"] = new() { Id = "C9", Name = "random" }
            }
        };

        var resolver = CreateResolver(lookup, new SlackChannelOptions { AllowedChannelIds = [] });
        var result = await resolver.ListDestinationsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Listed, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Equal(0, lookup.InfoCallCount);
    }

    private static SlackTargetResolver CreateResolver(
        ISlackTargetLookupClient lookup,
        SlackChannelOptions? options = null,
        SlackChannelId? defaultChannelId = null)
    {
        return new SlackTargetResolver(lookup, options ?? new SlackChannelOptions(), () => defaultChannelId);
    }

    private sealed class FakeSlackTargetLookupClient : ISlackTargetLookupClient
    {
        public IReadOnlyList<SlackChannelPage> ChannelPages { get; init; } = [];
        public IReadOnlyList<SlackUserPage> UserPages { get; init; } = [];
        public Dictionary<string, Conversation> ChannelInfos { get; init; } = new(StringComparer.Ordinal);
        public int ChannelListCallCount { get; private set; }
        public int UserListCallCount { get; private set; }
        public int InfoCallCount { get; private set; }

        public Task<Conversation> GetChannelInfoAsync(string channelId, CancellationToken ct = default)
        {
            InfoCallCount++;
            return ChannelInfos.TryGetValue(channelId, out var conversation)
                ? Task.FromResult(conversation)
                : throw new SlackException(new SlackNet.WebApi.ErrorResponse { Error = "channel_not_found" });
        }

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
