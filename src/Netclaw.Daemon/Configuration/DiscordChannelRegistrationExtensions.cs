using Netclaw.Channels;
using Netclaw.Channels.Discord;

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

        services.AddSingleton<IDiscordGatewayClient, UnconfiguredDiscordGatewayClient>();
        services.AddSingleton<IDiscordReplyClient, UnconfiguredDiscordReplyClient>();

        services.AddKeyedSingleton<IChannel, DiscordChannel>(DiscordChannelKey);
        services.AddSingleton<IChannel>(sp =>
            sp.GetRequiredKeyedService<IChannel>(DiscordChannelKey));

        services.AddSingleton<IHostedService>(sp =>
            (IHostedService)sp.GetRequiredKeyedService<IChannel>(DiscordChannelKey));
    }
}
