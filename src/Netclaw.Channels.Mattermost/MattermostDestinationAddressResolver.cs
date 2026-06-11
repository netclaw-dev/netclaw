// -----------------------------------------------------------------------
// <copyright file="MattermostDestinationAddressResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;

namespace Netclaw.Channels.Mattermost;

public sealed class MattermostDestinationAddressResolver(
    MattermostChannelOptions options,
    Func<MattermostChannelId?> defaultChannelIdAccessor) : IChannelAddressResolver
{
    private static readonly IReadOnlySet<ChannelAddressKind> SupportedAddressKinds = new HashSet<ChannelAddressKind>
    {
        ChannelAddressKind.Destination
    };

    public ChannelDescriptorKey Key { get; } = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);

    public IReadOnlySet<ChannelAddressKind> AddressKinds => SupportedAddressKinds;

    public ValueTask<ChannelAddressResolutionResult> ResolveAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.ChannelKey.Equals(Key))
            return ValueTask.FromResult(ChannelAddressResolutionResult.Unsupported($"Mattermost destination resolver cannot resolve channel key '{request.ChannelKey}'."));

        if (request.AddressKind != ChannelAddressKind.Destination)
            return ValueTask.FromResult(ChannelAddressResolutionResult.Unsupported($"Mattermost destination resolver does not support address kind '{request.AddressKind}'."));

        var channelId = NormalizeDestinationQuery(request.Query);
        if (!MattermostIdentifierFormat.IsMattermostId(channelId))
        {
            return ValueTask.FromResult(ChannelAddressResolutionResult.NotFound(
                $"Mattermost destination lookup requires an exact channel ID."));
        }

        var target = new MattermostChannelId(channelId);
        return ValueTask.FromResult(MattermostAclPolicy.IsAllowedChannel(target, options, defaultChannelIdAccessor())
            ? ChannelAddressResolutionResult.Resolved(new ResolvedChannelAddress(Key, request.AddressKind, channelId, channelId))
            : ChannelAddressResolutionResult.NotFound($"Mattermost channel '{channelId}' is not in the allowed channels list."));
    }

    /// <summary>
    /// Blank-query listing derived purely from configuration: Mattermost's
    /// channel ACL is a strict allowlist (no allow-all-when-empty), so the
    /// deliverable destination set is exactly the default channel plus
    /// AllowedChannelIds — no API client needed. Display names are the raw
    /// channel IDs because this resolver has no name-lookup surface
    /// (destination lookup here requires exact IDs for the same reason).
    /// An empty result is honest: nothing is configured as deliverable.
    /// </summary>
    public ValueTask<ChannelAddressResolutionResult> ListDestinationsAsync(
        CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        if (defaultChannelIdAccessor() is { } defaultChannel)
            ids.Add(defaultChannel.Value);

        ids.AddRange(options.AllowedChannelIds);

        var destinations = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(id => new ResolvedChannelAddress(Key, ChannelAddressKind.Destination, id, id))
            .ToArray();

        return ValueTask.FromResult(ChannelAddressResolutionResult.Listed(destinations));
    }

    private static string NormalizeDestinationQuery(string query)
    {
        var normalized = query.Trim();
        return normalized.StartsWith("channel:", StringComparison.OrdinalIgnoreCase)
            ? normalized[8..].Trim()
            : normalized;
    }
}
