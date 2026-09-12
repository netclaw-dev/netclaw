// -----------------------------------------------------------------------
// <copyright file="DiscordAddressResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;

namespace Netclaw.Channels.Discord;

public sealed record DiscordLookupUser(
    DiscordUserId UserId,
    string Username,
    string? GlobalName,
    string? DisplayName,
    bool IsBot);

public sealed record DiscordLookupDestination(
    DiscordChannelId ChannelId,
    string Name);

public interface IDiscordAddressLookupClient
{
    ValueTask<IReadOnlyList<DiscordLookupUser>> FindUsersAsync(string query, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DiscordLookupDestination>> FindDestinationsAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every text channel across the guilds the bot has joined, unfiltered.
    /// ACL filtering happens in <see cref="DiscordAddressResolver"/> — the
    /// client only reflects the gateway cache.
    /// </summary>
    ValueTask<IReadOnlyList<DiscordLookupDestination>> ListDestinationsAsync(CancellationToken cancellationToken = default);
}

public sealed class DiscordAddressResolver(
    IDiscordAddressLookupClient lookupClient,
    DiscordChannelOptions options,
    Func<DiscordChannelId?> defaultChannelIdAccessor) : IChannelAddressResolver
{
    private static readonly IReadOnlySet<ChannelAddressKind> UserAndDestinationKinds = new HashSet<ChannelAddressKind>
    {
        ChannelAddressKind.User,
        ChannelAddressKind.Destination
    };

    private static readonly IReadOnlySet<ChannelAddressKind> UserDmAndDestinationKinds = new HashSet<ChannelAddressKind>
    {
        ChannelAddressKind.User,
        ChannelAddressKind.DirectMessage,
        ChannelAddressKind.Destination
    };

    public ChannelDescriptorKey Key { get; } = ChannelDescriptorKey.FromChannelType(ChannelType.Discord);

    public IReadOnlySet<ChannelAddressKind> AddressKinds => options.AllowDirectMessages
        ? UserDmAndDestinationKinds
        : UserAndDestinationKinds;

    public async ValueTask<ChannelAddressResolutionResult> ResolveAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.ChannelKey.Equals(Key))
            return ChannelAddressResolutionResult.Unsupported($"Discord resolver cannot resolve channel key '{request.ChannelKey}'.");

        return request.AddressKind switch
        {
            ChannelAddressKind.User => await ResolveUserAsync(request, cancellationToken),
            ChannelAddressKind.DirectMessage => options.AllowDirectMessages
                ? await ResolveUserAsync(request, cancellationToken)
                : ChannelAddressResolutionResult.Unsupported("Discord direct-message resolution is disabled in configuration."),
            ChannelAddressKind.Destination => await ResolveDestinationAsync(request, cancellationToken),
            _ => ChannelAddressResolutionResult.Unsupported($"Discord resolver does not support address kind '{request.AddressKind}'.")
        };
    }

    private async ValueTask<ChannelAddressResolutionResult> ResolveUserAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var query = NormalizeUserQuery(request.Query);
        if (IsDiscordSnowflake(query))
        {
            var userId = new DiscordUserId(query);
            return DiscordAclPolicy.IsAllowedUser(userId, options)
                ? ChannelAddressResolutionResult.Resolved(new ResolvedChannelAddress(Key, request.AddressKind, query, query))
                : ChannelAddressResolutionResult.NotFound($"Discord user '{query}' is not in the allowed users list.");
        }

        var matches = (await lookupClient.FindUsersAsync(query, cancellationToken))
            .Where(user => !user.IsBot && DiscordAclPolicy.IsAllowedUser(user.UserId, options))
            .Where(user => MatchesUserQuery(user, query))
            .Select(user => new ResolvedChannelAddress(
                Key,
                request.AddressKind,
                user.UserId.Value,
                GetUserDisplayName(user)))
            .DistinctBy(address => address.StableId)
            .ToArray();

        return ToResolutionResult(request.AddressKind, matches, query);
    }

    private async ValueTask<ChannelAddressResolutionResult> ResolveDestinationAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var query = NormalizeDestinationQuery(request.Query);
        if (IsDiscordSnowflake(query))
        {
            var channelId = new DiscordChannelId(query);
            return DiscordAclPolicy.IsAllowedChannel(channelId, options, defaultChannelIdAccessor())
                ? ChannelAddressResolutionResult.Resolved(new ResolvedChannelAddress(Key, request.AddressKind, query, query))
                : ChannelAddressResolutionResult.NotFound($"Discord channel '{query}' is not in the allowed channels list.");
        }

        var matches = (await lookupClient.FindDestinationsAsync(query, cancellationToken))
            .Where(destination => DiscordAclPolicy.IsAllowedChannel(destination.ChannelId, options, defaultChannelIdAccessor()))
            .Where(destination => MatchesDestinationQuery(destination, query))
            .Select(destination => new ResolvedChannelAddress(
                Key,
                request.AddressKind,
                destination.ChannelId.Value,
                string.IsNullOrWhiteSpace(destination.Name) ? destination.ChannelId.Value : $"#{destination.Name}"))
            .DistinctBy(address => address.StableId)
            .ToArray();

        return ToResolutionResult(request.AddressKind, matches, query);
    }

    /// <summary>
    /// Blank-query listing: every guild text channel the bot can see that
    /// passes the same <see cref="DiscordAclPolicy.IsAllowedChannel"/> gate
    /// the search path applies.
    /// </summary>
    public async ValueTask<ChannelAddressResolutionResult> ListDestinationsAsync(
        CancellationToken cancellationToken = default)
    {
        var destinations = (await lookupClient.ListDestinationsAsync(cancellationToken))
            .Where(destination => DiscordAclPolicy.IsAllowedChannel(destination.ChannelId, options, defaultChannelIdAccessor()))
            .Select(destination => new ResolvedChannelAddress(
                Key,
                ChannelAddressKind.Destination,
                destination.ChannelId.Value,
                string.IsNullOrWhiteSpace(destination.Name) ? destination.ChannelId.Value : $"#{destination.Name}"))
            .DistinctBy(address => address.StableId)
            .ToArray();

        return ChannelAddressResolutionResult.Listed(destinations);
    }

    private static ChannelAddressResolutionResult ToResolutionResult(
        ChannelAddressKind addressKind,
        IReadOnlyList<ResolvedChannelAddress> matches,
        string query)
    {
        if (matches.Count == 0)
            return ChannelAddressResolutionResult.NotFound($"No Discord {addressKind} matched '{query}'.");

        if (matches.Count == 1)
            return ChannelAddressResolutionResult.Resolved(matches[0]);

        return ChannelAddressResolutionResult.Ambiguous(
            matches,
            $"Discord {addressKind} query '{query}' matched {matches.Count} destinations.");
    }

    private static string NormalizeUserQuery(string query)
    {
        var normalized = query.Trim();
        if (normalized.StartsWith("<@", StringComparison.Ordinal) && normalized.EndsWith('>'))
        {
            normalized = normalized[2..^1];
            if (normalized.StartsWith('!'))
                normalized = normalized[1..];

            // A mention tag never carries stray whitespace, so the shared
            // strip-at helper's extra Trim() would be a no-op here. Kept
            // inline (not delegated) so a malformed tag with embedded
            // whitespace still matches today's untrimmed fallback exactly.
            return normalized.StartsWith('@') ? normalized[1..].Trim() : normalized;
        }

        return UserQueryNormalizer.StripLeadingAt(normalized);
    }

    private static string NormalizeDestinationQuery(string query)
    {
        var normalized = query.Trim();
        if (normalized.StartsWith("channel:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[8..].Trim();

        if (normalized.StartsWith("<#", StringComparison.Ordinal) && normalized.EndsWith('>'))
            normalized = normalized[2..^1];

        return normalized.StartsWith('#') ? normalized[1..].Trim() : normalized;
    }

    private static bool MatchesUserQuery(DiscordLookupUser user, string query)
    {
        return string.Equals(user.UserId.Value, query, StringComparison.Ordinal)
               || string.Equals(user.Username, query, StringComparison.OrdinalIgnoreCase)
               || string.Equals(user.GlobalName, query, StringComparison.OrdinalIgnoreCase)
               || string.Equals(user.DisplayName, query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesDestinationQuery(DiscordLookupDestination destination, string query)
    {
        return string.Equals(destination.ChannelId.Value, query, StringComparison.Ordinal)
               || string.Equals(destination.Name, query, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUserDisplayName(DiscordLookupUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            return user.DisplayName;

        if (!string.IsNullOrWhiteSpace(user.GlobalName))
            return user.GlobalName;

        if (!string.IsNullOrWhiteSpace(user.Username))
            return $"@{user.Username}";

        return user.UserId.Value;
    }

    internal static bool IsDiscordSnowflake(string value)
        => value.Length is >= 17 and <= 20 && value.All(char.IsAsciiDigit);
}
