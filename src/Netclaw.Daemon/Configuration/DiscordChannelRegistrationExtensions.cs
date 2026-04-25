using Discord.WebSocket;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Reminders;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Discord.Transport;

namespace Netclaw.Daemon.Configuration;

public static class DiscordChannelRegistrationExtensions
{
    private const string DiscordChannelKey = "discord";

    public static void AddDiscordChannelIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        var discordOptions = configuration.GetSection("Discord").Get<DiscordChannelOptions>() ?? new DiscordChannelOptions();
        services.AddSingleton(discordOptions);

        if (!discordOptions.Enabled)
            return;

        if (discordOptions.BotToken is null || string.IsNullOrWhiteSpace(discordOptions.BotToken.Value))
            throw new InvalidOperationException("Discord is enabled but Discord:BotToken is not configured.");

        services.AddSingleton(_ => new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = Discord.GatewayIntents.Guilds
                | Discord.GatewayIntents.GuildMessages
                | Discord.GatewayIntents.DirectMessages
                | Discord.GatewayIntents.MessageContent,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 100
        }));

        services.AddHttpClient("discord-files");
        services.AddSingleton<IDiscordGatewayClient, DiscordNetGatewayClient>();
        services.AddSingleton<IDiscordReplyClient, DiscordNetReplyClient>();
        services.AddSingleton<IThreadHistoryFetcher, DiscordThreadHistoryFetcher>();
        services.AddSingleton<IReminderTargetResolver, DiscordReminderTargetResolver>();

        services.AddKeyedSingleton<IChannel, DiscordChannel>(DiscordChannelKey);
        services.AddSingleton<IChannel>(sp =>
            sp.GetRequiredKeyedService<IChannel>(DiscordChannelKey));

        services.AddSingleton<IHostedService>(sp =>
            (IHostedService)sp.GetRequiredKeyedService<IChannel>(DiscordChannelKey));
    }
}
