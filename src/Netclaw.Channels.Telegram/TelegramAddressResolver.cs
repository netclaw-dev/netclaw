// -----------------------------------------------------------------------
// <copyright file="TelegramAddressResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;

namespace Netclaw.Channels.Telegram;

public sealed class TelegramAddressResolver(TelegramChannelOptions options) : IChannelAddressResolver
{
    private static readonly IReadOnlySet<ChannelAddressKind> AllKinds = new HashSet<ChannelAddressKind>
    {
        ChannelAddressKind.User,
        ChannelAddressKind.DirectMessage,
        ChannelAddressKind.Destination
    };

    private static readonly IReadOnlySet<ChannelAddressKind> GroupKinds = new HashSet<ChannelAddressKind>
    {
        ChannelAddressKind.User,
        ChannelAddressKind.Destination
    };

    public ChannelDescriptorKey Key { get; } = ChannelDescriptorKey.FromChannelType(ChannelType.Telegram);

    public IReadOnlySet<ChannelAddressKind> AddressKinds => options.AllowDirectMessages ? AllKinds : GroupKinds;

    public ValueTask<ChannelAddressResolutionResult> ResolveAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.ChannelKey.Equals(Key))
            return ValueTask.FromResult(ChannelAddressResolutionResult.Unsupported(
                $"Telegram resolver cannot resolve channel key '{request.ChannelKey}'."));

        var query = Normalize(request.Query);
        return request.AddressKind switch
        {
            ChannelAddressKind.User => ValueTask.FromResult(ResolveUser(request.AddressKind, query)),
            ChannelAddressKind.DirectMessage when options.AllowDirectMessages =>
                ValueTask.FromResult(ResolveUser(request.AddressKind, query)),
            ChannelAddressKind.DirectMessage => ValueTask.FromResult(ChannelAddressResolutionResult.Unsupported(
                "Telegram direct-message resolution is disabled in configuration.")),
            ChannelAddressKind.Destination => ValueTask.FromResult(ResolveChat(query)),
            _ => ValueTask.FromResult(ChannelAddressResolutionResult.Unsupported(
                $"Telegram resolver does not support address kind '{request.AddressKind}'."))
        };
    }

    public ValueTask<ChannelAddressResolutionResult> ListDestinationsAsync(
        CancellationToken cancellationToken = default)
    {
        var destinations = options.AllowedChatIds
            .Where(IsTelegramId)
            .Distinct(StringComparer.Ordinal)
            .Select(id => new ResolvedChannelAddress(Key, ChannelAddressKind.Destination, id, id))
            .ToArray();
        return ValueTask.FromResult(ChannelAddressResolutionResult.Listed(destinations));
    }

    private ChannelAddressResolutionResult ResolveUser(ChannelAddressKind kind, string query)
    {
        return IsTelegramId(query) && options.AllowedUserIds.Contains(query, StringComparer.Ordinal)
            ? ChannelAddressResolutionResult.Resolved(new ResolvedChannelAddress(Key, kind, query, query))
            : ChannelAddressResolutionResult.NotFound($"Telegram user '{query}' is not in the allowed users list.");
    }

    private ChannelAddressResolutionResult ResolveChat(string query)
    {
        return IsTelegramId(query) && options.AllowedChatIds.Contains(query, StringComparer.Ordinal)
            ? ChannelAddressResolutionResult.Resolved(
                new ResolvedChannelAddress(Key, ChannelAddressKind.Destination, query, query))
            : ChannelAddressResolutionResult.NotFound($"Telegram chat '{query}' is not in the allowed chats list.");
    }

    private static string Normalize(string query) => query.Trim().TrimStart('@', '#');

    internal static bool IsTelegramId(string value) =>
        long.TryParse(value, out var parsed) && parsed != 0;
}
