// -----------------------------------------------------------------------
// <copyright file="DiscordNetAddressLookupClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Discord.WebSocket;

namespace Netclaw.Channels.Discord.Transport;

internal sealed class DiscordNetAddressLookupClient(DiscordSocketClient client) : IDiscordAddressLookupClient
{
    public ValueTask<IReadOnlyList<DiscordLookupUser>> FindUsersAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var matches = client.Guilds
            .SelectMany(guild => guild.Users)
            .Where(user => MatchesUserQuery(user, query))
            .Select(user => new DiscordLookupUser(
                new DiscordUserId(user.Id.ToString()),
                user.Username,
                user.GlobalName,
                user.DisplayName,
                user.IsBot))
            .DistinctBy(user => user.UserId.Value)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<DiscordLookupUser>>(matches);
    }

    public ValueTask<IReadOnlyList<DiscordLookupDestination>> FindDestinationsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var matches = client.Guilds
            .SelectMany(guild => guild.TextChannels)
            .Where(channel => MatchesChannelQuery(channel, query))
            .Select(channel => new DiscordLookupDestination(
                new DiscordChannelId(channel.Id.ToString()),
                channel.Name))
            .DistinctBy(destination => destination.ChannelId.Value)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<DiscordLookupDestination>>(matches);
    }

    public ValueTask<IReadOnlyList<DiscordLookupDestination>> ListDestinationsAsync(
        CancellationToken cancellationToken = default)
    {
        var destinations = client.Guilds
            .SelectMany(guild => guild.TextChannels)
            .Select(channel => new DiscordLookupDestination(
                new DiscordChannelId(channel.Id.ToString()),
                channel.Name))
            .DistinctBy(destination => destination.ChannelId.Value)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<DiscordLookupDestination>>(destinations);
    }

    private static bool MatchesUserQuery(SocketGuildUser user, string query)
    {
        return string.Equals(user.Id.ToString(), query, StringComparison.Ordinal)
               || string.Equals(user.Username, query, StringComparison.OrdinalIgnoreCase)
               || string.Equals(user.GlobalName, query, StringComparison.OrdinalIgnoreCase)
               || string.Equals(user.DisplayName, query, StringComparison.OrdinalIgnoreCase)
               || string.Equals(user.Nickname, query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesChannelQuery(SocketTextChannel channel, string query)
    {
        return string.Equals(channel.Id.ToString(), query, StringComparison.Ordinal)
               || string.Equals(channel.Name, query, StringComparison.OrdinalIgnoreCase);
    }
}
