// -----------------------------------------------------------------------
// <copyright file="SlackTargetResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using SlackNet;
using SlackNet.WebApi;
using Netclaw.Channels;
using ChannelType = Netclaw.Actors.Channels.ChannelType;

namespace Netclaw.Channels.Slack;

public sealed record SlackTargetResolutionResult(
    bool Success,
    string? ErrorMessage,
    string? ChannelId,
    string? UserId);

/// <summary>
/// Resolves human-friendly Slack targets (e.g. #channel, @user, email) into canonical IDs.
/// </summary>
public interface ISlackTargetResolver
{
    Task<SlackTargetResolutionResult> ResolveAsync(string target, CancellationToken ct = default);
}

public sealed record SlackChannelPage(IReadOnlyList<Conversation> Channels, string? NextCursor);

public sealed record SlackUserPage(IReadOnlyList<User> Users, string? NextCursor);

public interface ISlackTargetLookupClient
{
    Task<SlackChannelPage> ListChannelsAsync(string? cursor, CancellationToken ct = default);
    Task<SlackUserPage> ListUsersAsync(string? cursor, CancellationToken ct = default);

    /// <summary>
    /// Fetches metadata (name, archived state) for a single conversation via
    /// <c>conversations.info</c>. Throws <see cref="SlackException"/> when the
    /// channel is unknown or unreadable by the bot.
    /// </summary>
    Task<Conversation> GetChannelInfoAsync(string channelId, CancellationToken ct = default);
}

public sealed class SlackApiTargetLookupClient(ISlackApiClient slackApi) : ISlackTargetLookupClient
{
    public async Task<SlackChannelPage> ListChannelsAsync(string? cursor, CancellationToken ct = default)
    {
        // Archived channels are never deliverable, so they must not resolve
        // through name search either.
        var page = await slackApi.Conversations.List(
            excludeArchived: true,
            types: [ConversationType.PublicChannel, ConversationType.PrivateChannel],
            cursor: cursor,
            cancellationToken: ct);

        return new SlackChannelPage(page.Channels.ToList(), page.ResponseMetadata?.NextCursor);
    }

    public async Task<SlackUserPage> ListUsersAsync(string? cursor, CancellationToken ct = default)
    {
        var page = await slackApi.Users.List(cursor: cursor, cancellationToken: ct);
        return new SlackUserPage(page.Members.ToList(), page.ResponseMetadata?.NextCursor);
    }

    public Task<Conversation> GetChannelInfoAsync(string channelId, CancellationToken ct = default)
        => slackApi.Conversations.Info(channelId, cancellationToken: ct);
}

public sealed class SlackTargetResolver(
    ISlackTargetLookupClient lookupClient,
    SlackChannelOptions options,
    Func<SlackChannelId?> defaultChannelIdAccessor) : ISlackTargetResolver, IChannelAddressResolver
{
    private static readonly IReadOnlySet<ChannelAddressKind> SupportedAddressKinds = new HashSet<ChannelAddressKind>
    {
        ChannelAddressKind.Destination
    };

    public ChannelDescriptorKey Key { get; } = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);

    public IReadOnlySet<ChannelAddressKind> AddressKinds => SupportedAddressKinds;

    public async Task<SlackTargetResolutionResult> ResolveAsync(string target, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target))
            return new SlackTargetResolutionResult(false, "Target is required.", null, null);

        var raw = target.Trim();

        if (IsSlackChannelId(raw))
        {
            var channelId = new SlackChannelId(raw);
            return SlackAclPolicy.IsAllowedChannel(channelId, options, defaultChannelIdAccessor())
                ? new SlackTargetResolutionResult(true, null, raw, null)
                : new SlackTargetResolutionResult(false, $"Slack channel '{raw}' is not in the allowed channels list.", null, null);
        }

        if (IsSlackUserId(raw))
        {
            var userId = new SlackUserId(raw);
            return SlackAclPolicy.IsAllowedUser(userId, options)
                ? new SlackTargetResolutionResult(true, null, null, raw)
                : new SlackTargetResolutionResult(false, $"Slack user '{raw}' is not in the allowed users list.", null, null);
        }

        if (raw.StartsWith("#", StringComparison.Ordinal))
        {
            var channelName = raw[1..].Trim();
            if (string.IsNullOrWhiteSpace(channelName))
                return new SlackTargetResolutionResult(false, "Channel name is empty.", null, null);

            var channelId = await ResolveChannelByNameAsync(channelName, ct);
            return channelId is not null
                ? new SlackTargetResolutionResult(true, null, channelId, null)
                : new SlackTargetResolutionResult(false, $"Could not resolve Slack channel '{raw}'.", null, null);
        }

        if (raw.StartsWith("@", StringComparison.Ordinal))
        {
            var userQuery = raw[1..].Trim();
            if (string.IsNullOrWhiteSpace(userQuery))
                return new SlackTargetResolutionResult(false, "User name is empty.", null, null);

            var userId = await ResolveUserAsync(userQuery, ct);
            return userId is not null
                ? new SlackTargetResolutionResult(true, null, null, userId)
                : new SlackTargetResolutionResult(false, $"Could not resolve Slack user '{raw}'.", null, null);
        }

        var fallbackChannelId = await ResolveChannelByNameAsync(raw, ct);
        if (fallbackChannelId is not null)
            return new SlackTargetResolutionResult(true, null, fallbackChannelId, null);

        var fallbackUserId = await ResolveUserAsync(raw, ct);
        if (fallbackUserId is not null)
            return new SlackTargetResolutionResult(true, null, null, fallbackUserId);

        return new SlackTargetResolutionResult(
            false,
            $"Could not resolve Slack target '{target}'. Use #channel, @user, or a Slack ID (C..., G..., U...).",
            null,
            null);
    }

    public async ValueTask<ChannelAddressResolutionResult> ResolveAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.ChannelKey.Equals(Key))
            return ChannelAddressResolutionResult.Unsupported($"Slack resolver cannot resolve channel key '{request.ChannelKey}'.");

        if (request.AddressKind != ChannelAddressKind.Destination)
            return ChannelAddressResolutionResult.Unsupported($"Slack destination resolver does not support address kind '{request.AddressKind}'.");

        var raw = request.Query.Trim();
        if (raw.StartsWith('#'))
            raw = raw[1..].Trim();

        if (IsSlackChannelId(raw))
        {
            var channelId = new SlackChannelId(raw);
            return SlackAclPolicy.IsAllowedChannel(channelId, options, defaultChannelIdAccessor())
                ? ChannelAddressResolutionResult.Resolved(new ResolvedChannelAddress(Key, request.AddressKind, raw, raw))
                : ChannelAddressResolutionResult.NotFound($"Slack channel '{raw}' is not in the allowed channels list.");
        }

        var matches = await FindChannelMatchesAsync(raw, cancellationToken);
        return ToResolutionResult(request.AddressKind, matches, raw);
    }

    /// <summary>
    /// Blank-query listing derived from configuration, mirroring the
    /// Mattermost resolver: the deliverable destination set is exactly the
    /// runtime-resolved default channel plus
    /// <see cref="SlackChannelOptions.AllowedChannelIds"/> — the same
    /// allowlist <see cref="SlackAclPolicy.IsAllowedChannel"/> enforces — so
    /// no workspace-wide <c>conversations.list</c> pagination is needed
    /// (O(allowlist) instead of O(workspace) rate-limited calls). Display
    /// names are resolved per channel via <c>conversations.info</c>; channels
    /// whose info reports archived are skipped because archived channels are
    /// never deliverable. Membership is not required for a channel to appear
    /// here — the operator allowlisted it; a send to a channel the bot has
    /// not joined fails loudly at delivery time.
    /// </summary>
    public async ValueTask<ChannelAddressResolutionResult> ListDestinationsAsync(
        CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        if (defaultChannelIdAccessor() is { } defaultChannel)
            ids.Add(defaultChannel.Value);

        ids.AddRange(options.AllowedChannelIds);

        var destinations = new List<ResolvedChannelAddress>();
        foreach (var id in ids
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            Conversation? info;
            try
            {
                info = await lookupClient.GetChannelInfoAsync(id, cancellationToken);
            }
            catch (SlackException)
            {
                // Fall back to the raw ID as the display name (Mattermost
                // precedent): the operator configured this channel as
                // deliverable even if the bot cannot read its metadata.
                info = null;
            }

            // Archived channels are never deliverable — skip them.
            if (info?.IsArchived == true)
                continue;

            var displayName = string.IsNullOrWhiteSpace(info?.Name) ? id : $"#{info!.Name}";
            destinations.Add(new ResolvedChannelAddress(
                Key, ChannelAddressKind.Destination, id, displayName));
        }

        return ChannelAddressResolutionResult.Listed(destinations);
    }

    private async Task<string?> ResolveChannelByNameAsync(string channelName, CancellationToken ct)
    {
        var cursor = default(string);
        do
        {
            var page = await lookupClient.ListChannelsAsync(cursor, ct);

            var match = page.Channels.FirstOrDefault(c =>
                IsAllowedChannel(c)
                && (string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.NameNormalized, channelName, StringComparison.OrdinalIgnoreCase)));

            if (match is not null)
                return match.Id;

            cursor = page.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return null;
    }

    private async Task<List<ResolvedChannelAddress>> FindChannelMatchesAsync(string query, CancellationToken ct)
    {
        var exactMatches = new List<ResolvedChannelAddress>();
        var substringMatches = new List<ResolvedChannelAddress>();
        var cursor = default(string);

        do
        {
            var page = await lookupClient.ListChannelsAsync(cursor, ct);
            foreach (var channel in page.Channels)
            {
                if (!IsAllowedChannel(channel))
                    continue;

                var quality = GetMatchQuality(channel, query);
                if (quality == MatchQuality.None)
                    continue;

                var displayName = string.IsNullOrWhiteSpace(channel.Name)
                    ? channel.Id
                    : $"#{channel.Name}";
                var address = new ResolvedChannelAddress(Key, ChannelAddressKind.Destination, channel.Id, displayName);

                if (quality == MatchQuality.Exact)
                    exactMatches.Add(address);
                else
                    substringMatches.Add(address);
            }

            cursor = page.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return exactMatches.Count > 0 ? exactMatches : substringMatches;
    }

    private ChannelAddressResolutionResult ToResolutionResult(
        ChannelAddressKind addressKind,
        IReadOnlyList<ResolvedChannelAddress> matches,
        string query)
    {
        if (matches.Count == 0)
            return ChannelAddressResolutionResult.NotFound($"No Slack {addressKind} matched '{query}'.");

        if (matches.Count == 1)
            return ChannelAddressResolutionResult.Resolved(matches[0]);

        return ChannelAddressResolutionResult.Ambiguous(
            matches,
            $"Slack {addressKind} query '{query}' matched {matches.Count} destinations.");
    }

    private bool IsAllowedChannel(Conversation channel)
    {
        if (string.IsNullOrWhiteSpace(channel.Id))
            return false;

        return SlackAclPolicy.IsAllowedChannel(new SlackChannelId(channel.Id), options, defaultChannelIdAccessor());
    }

    private enum MatchQuality { None, Substring, Exact }

    private static MatchQuality GetMatchQuality(Conversation channel, string query)
    {
        var name = channel.Name ?? string.Empty;
        var normalizedName = channel.NameNormalized ?? string.Empty;

        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedName, query, StringComparison.OrdinalIgnoreCase))
            return MatchQuality.Exact;

        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return MatchQuality.Substring;

        return MatchQuality.None;
    }

    private static bool IsSlackChannelId(string value)
    {
        if (value.Length < 9)
            return false;
        if (!value.StartsWith("C", StringComparison.Ordinal) && !value.StartsWith("G", StringComparison.Ordinal))
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(value[i]))
                return false;
        }
        return true;
    }

    private static bool IsSlackUserId(string value)
    {
        if (value.Length < 9)
            return false;
        if (!value.StartsWith("U", StringComparison.Ordinal) && !value.StartsWith("W", StringComparison.Ordinal))
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(value[i]))
                return false;
        }
        return true;
    }

    private async Task<string?> ResolveUserAsync(string query, CancellationToken ct)
    {
        var cursor = default(string);
        var exactMatches = new List<User>();

        do
        {
            var response = await lookupClient.ListUsersAsync(cursor, ct);
            foreach (var user in response.Users)
            {
                if (user.IsBot || user.Deleted)
                    continue;

                if (!SlackAclPolicy.IsAllowedUser(new SlackUserId(user.Id), options))
                    continue;

                var displayName = user.Profile?.DisplayName ?? string.Empty;
                var realName = user.RealName ?? string.Empty;
                var username = user.Name ?? string.Empty;
                var email = user.Profile?.Email ?? string.Empty;

                if (string.Equals(email, query, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(displayName, query, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(realName, query, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(username, query, StringComparison.OrdinalIgnoreCase))
                {
                    exactMatches.Add(user);
                }
            }

            cursor = response.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        if (exactMatches.Count == 1)
            return exactMatches[0].Id;

        return null;
    }
}
